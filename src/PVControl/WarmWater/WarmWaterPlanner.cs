using System;
using System.Collections.Generic;
using System.Linq;
using NetDeamon.apps.PVControl.Simulator;

namespace NetDeamon.apps.PVControl.WarmWater;

/// <summary>A time the tank must be warm.</summary>
/// <param name="Start">Window start.</param>
/// <param name="End">Window end (inclusive).</param>
/// <param name="MinTemp">Minimum tank temperature, °C.</param>
public readonly record struct ComfortWindow(DateTime Start, DateTime End, double MinTemp);

/// <summary>One planned heating run.</summary>
/// <param name="Start">Heating starts.</param>
/// <param name="End">Expected end (tank reaches <paramref name="TargetTemp"/>).</param>
/// <param name="StartTemp">Tank temperature at the start, °C.</param>
/// <param name="TargetTemp">Tank temperature at the end, °C.</param>
/// <param name="PowerW">Heating power, W.</param>
public readonly record struct PlannedRun(DateTime Start, DateTime End, double StartTemp, double TargetTemp, int PowerW);

/// <summary>A plan or a forecast of the tank.</summary>
/// <param name="Runs">Heating runs.</param>
/// <param name="Temps">Tank temperature at every slot start, °C.</param>
/// <param name="CostEur">Heating energy cost (without run start costs), €.</param>
/// <param name="ComfortDeficitK">Largest shortfall below a comfort minimum, K (0 = all met).</param>
public sealed record WarmWaterPlan(IReadOnlyList<PlannedRun> Runs, IReadOnlyList<(DateTime Time, double Temp)> Temps, float CostEur, double ComfortDeficitK)
{
  public static WarmWaterPlan Empty { get; } = new([], [], 0, 0);
}

/// <summary>Everything a plan is made from.</summary>
/// <param name="Now">Current time (inside the first slot).</param>
/// <param name="StartTemp">Tank temperature now, °C.</param>
/// <param name="Slots">15-min slot starts of the horizon.</param>
/// <param name="PricePerKwh">Cost of one kWh of heating per slot, €/kWh (<see cref="WarmWaterPlanner.HeatingPricesPerKwh"/>).</param>
/// <param name="Model">Tank model (heating rate, standby loss, power).</param>
/// <param name="Comfort">Comfort windows.</param>
/// <param name="MaxTemp">Highest tank temperature, °C.</param>
/// <param name="ForcedTarget">A running heating or a boost continues to this temperature; null = none.</param>
/// <param name="RunStartCostEur">Added per run start, €, so one longer run beats several short ones.</param>
/// <param name="StartHysteresisK">The heat pump starts a one-time heating only when the tank is at least this far below
/// its setpoint (<see cref="WarmWaterConfig.OneTimeHysteresisK"/>): a planned run rises at least this much, and a forced
/// target that isn't heating yet waits until the tank has cooled to it minus this. 0 = starts at once.</param>
/// <param name="HeatingNow">The heat pump heats warm water now (a forced target then continues without waiting).</param>
/// <param name="MinOffQuarters">After a run stops, this many whole quarters without heating before the next start (the
/// control's <see cref="WarmWaterControl.MinOffTime"/>: 4); 0 = none.</param>
/// <param name="NoStartBefore">No start before this time (the last real stop plus the off time); null = none.</param>
public sealed record PlanRequest(
  DateTime Now,
  double StartTemp,
  IReadOnlyList<DateTime> Slots,
  IReadOnlyList<float> PricePerKwh,
  TankModel Model,
  IReadOnlyList<ComfortWindow> Comfort,
  double MaxTemp,
  double? ForcedTarget = null,
  float RunStartCostEur = 0.02f,
  double StartHysteresisK = 0,
  bool HeatingNow = false,
  int MinOffQuarters = 0,
  DateTime? NoStartBefore = null);

/// <summary>
/// Plans warm-water heating at the lowest cost that keeps the comfort windows warm: a dynamic
/// program over the 15-min slots and the tank temperature (0.1 K steps); the standby loss of a
/// hotter tank is in the model, so early heating only wins when the price difference beats it.
/// </summary>
public static class WarmWaterPlanner
{
  /// <summary>Temperature resolution of the dynamic program, in K.</summary>
  public const double Resolution = 0.1;
  /// <summary>Penalty per K below a comfort minimum, per slot. Far above any heating cost, so a window is only missed when it can't be met.</summary>
  public const float DeficitPenaltyPerK = 10f;
  const int SlotMinutes = TimeRange.SlotMinutes;

  /// <summary>Cost of one kWh of heating per slot: what heating in that slot adds (<see cref="ILoadPricer"/>; the greedy
  /// simulation's <see cref="SimulationResult.NetGridCostVs"/> without one), never below 0.</summary>
  /// <param name="baseInput">House simulation input without the heating.</param>
  /// <param name="baseline">Simulation of <paramref name="baseInput"/>.</param>
  /// <param name="slots">Slot starts to price.</param>
  /// <param name="now">Current time (the first slot from here on).</param>
  /// <param name="powerW">Heating power, W.</param>
  /// <param name="probeSlots">Slots priced by one simulation (speed).</param>
  /// <param name="pricer">The pricer; null: the greedy simulation's (each probe then runs one).</param>
  /// <returns>€/kWh per slot.</returns>
  public static float[] HeatingPricesPerKwh(SimulationInput baseInput, SimulationResult baseline, IReadOnlyList<DateTime> slots, DateTime now, int powerW,
    int probeSlots = 2, ILoadPricer? pricer = null)
  {
    var prices = new float[slots.Count];
    for (int i = 0; i < slots.Count; i += probeSlots)
    {
      int last = Math.Min(i + probeSlots, slots.Count) - 1;
      var start = slots[i] < now ? now : slots[i];
      var end = slots[last].AddMinutes(SlotMinutes);
      double kwh = Energy.Wh(powerW, end - start) / 1000;
      if (kwh <= 0) continue;
      var probe = new ExtraLoad { Name = "Warm water (price probe)", StartTime = start, EndTime = end, PowerW = powerW };
      float cost = pricer?.Cost([probe], () => baseInput.SimulateWith(probe)) ?? baseInput.SimulateWith(probe).NetGridCostVs(baseline);
      float price = (float)Math.Max(0, cost / kwh);
      for (int j = i; j <= last; j++) prices[j] = price;
    }
    return prices;
  }

  /// <summary>Scales heating prices by the heat pump's efficiency at the outside temperature: × (1 + changePerK · (mean − outside)), limited to 0.5…1.5.</summary>
  /// <param name="prices">€/kWh per slot.</param>
  /// <param name="outsideTemps">Forecast outside temperature per slot, °C; null = unchanged.</param>
  /// <param name="changePerK">Price change per K below the horizon's mean; ≤ 0 = off.</param>
  /// <returns>New prices.</returns>
  public static float[] WithOutsideEfficiency(float[] prices, IReadOnlyList<double?> outsideTemps, double changePerK)
  {
    var known = outsideTemps.Where(t => t is not null).Select(t => t!.Value).ToList();
    if (known.Count == 0 || changePerK <= 0) return (float[])prices.Clone();
    double mean = known.Average();
    var result = new float[prices.Length];
    for (int i = 0; i < prices.Length; i++)
      result[i] = outsideTemps[i] is { } t ? prices[i] * (float)Math.Clamp(1 + changePerK * (mean - t), 0.5, 1.5) : prices[i];
    return result;
  }

  /// <summary>The cheapest heating plan that meets the comfort windows.</summary>
  /// <param name="r">Plan input.</param>
  /// <returns>The plan.</returns>
  public static WarmWaterPlan Plan(PlanRequest r)
  {
    int n = r.Slots.Count;
    if (n == 0) return WarmWaterPlan.Empty;

    // A run that is already going isn't stopped: heat until its target first, then optimize. One that is set but not
    // heating yet starts only once the tank has cooled to its target minus the hysteresis (the heat pump waits; live
    // 2026-11-20: a boost to 47 °C at 44.3 °C started at 41.9 °C, two hours later).
    var actions = new double[n];
    int first = 0;
    double temp = r.StartTemp;
    if (r.ForcedTarget is { } forced && temp < forced - Resolution / 2)
    {
      if (!r.HeatingNow)
        while (first < n && temp > forced - r.StartHysteresisK + 1e-9)
          temp = Step(r, first++, temp, 0, out _, out _, out _);
      var capped = r with { MaxTemp = Math.Min(r.MaxTemp, forced) };
      while (first < n)
      {
        temp = Step(capped, first, temp, 1, out _, out double heated, out _);
        actions[first] = heated / SlotHours(r, first);
        if (actions[first++] < 1 - 1e-9) break; // reached the target within this slot
      }
    }
    bool heating = first > 0 && actions[first - 1] >= 1 - 1e-9;

    int buckets = Bucket(r.MaxTemp) + 2;
    // State = (temperature bucket, phase): 0 off and free to start, 1 heating and free to stop, 2 heating that may not
    // stop yet — a run must rise at least StartHysteresisK above its start (the heat pump doesn't start a smaller one) —,
    // and 3 … 3+gap−1 off since a stop for 0 … gap−1 complete quarters: no start before MinOffQuarters off-quarters
    // (the control's minimum off time, WarmWaterControl.MinOffTime — live 2026-11-23 16:45: the plan split one heating
    // into 16:45 → 37.7 °C and 17:15 → 45.3 °C, the heat pump stopped at 38 °C, the second run was refused for an hour
    // and the 17:30 comfort missed). A stop within a quarter (heated part of it) leaves that quarter incomplete. Each
    // state keeps the cheapest path reaching it, that path's exact temperature and, in phase 2, its run's start
    // temperature; a path that may stop or start is never displaced by one that may not (separate phases).
    double h = Math.Max(0, r.StartHysteresisK);
    int gap = Math.Max(0, r.MinOffQuarters);
    int phases = 3 + gap;
    bool CanStop(double t, double runStart) => t >= runStart + h - 1e-9 || t >= r.MaxTemp - Resolution / 2;
    int Idx(int bucket, int phase) => Math.Min(bucket, buckets - 1) + phase * buckets;
    int OffPhase(int completeQuarters) => completeQuarters >= gap ? 0 : 3 + completeQuarters;
    var cost = new double[phases * buckets];
    var exact = new double[phases * buckets];
    var runStart = new double[phases * buckets];
    var back = new int[n][];
    var level = new byte[n][];
    Array.Fill(cost, double.PositiveInfinity);
    // Where the plan starts: a heating that runs on (forced, free to stop), a forced run that just ended (a stop: the
    // off time counts from it), otherwise off — free, unless the last real stop was less than the off time ago.
    bool forcedEnded = first > 0 && !heating;
    int phase0 = heating ? 1 : forcedEnded ? OffPhase(0) : 0;
    int s0 = Idx(Bucket(temp), phase0);
    cost[s0] = 0;
    exact[s0] = temp;

    for (int i = first; i < n; i++)
    {
      var nextCost = new double[phases * buckets];
      var nextExact = new double[phases * buckets];
      var nextRunStart = new double[phases * buckets];
      var prev = back[i] = new int[phases * buckets];
      var lvl = level[i] = new byte[phases * buckets];
      Array.Fill(nextCost, double.PositiveInfinity);
      var slotTime = SlotStart(r, i);
      double hours = SlotHours(r, i);
      // the last real stop's off time: quarters that end before it (the one it ends in may start — the control holds
      // the exact minute, the plan's start isn't a quarter later)
      bool startBlocked = r.NoStartBefore is { } nsb && r.Slots[i] + TimeRange.Slot <= nsb;

      for (int s = 0; s < cost.Length; s++)
      {
        if (double.IsPositiveInfinity(cost[s])) continue;
        double t = exact[s];
        int phase = s / buckets;
        bool wasHeating = phase is 1 or 2;
        int offQuarters = phase >= 3 ? phase - 3 : -1;   // complete off quarters since a stop; -1: free (or heating)
        double baseCost = cost[s] + Penalty(r.Comfort, slotTime, t);
        for (int a = 0; a <= HeatLevels; a++)
        {
          bool heat = a > 0;
          if (!heat && phase == 2 && !CanStop(t, runStart[s])) continue;   // a run too small to stop
          if (heat && t >= r.MaxTemp - Resolution / 2) break;
          if (heat && !wasHeating && (offQuarters >= 0 || startBlocked)) break;   // within the off time: no start
          if (heat && !wasHeating && t > r.MaxTemp - h + 1e-9) break;      // no target the hysteresis lets start
          double next = Step(r, i, t, (double)a / HeatLevels, out double heatCost, out double heated, out _);
          double rs = !heat ? 0 : wasHeating && phase == 2 ? runStart[s] : wasHeating ? double.NegativeInfinity : t;
          // Still running at the slot's end only when it heated the whole slot — a run that
          // stopped within it is over, and heating again is a new start.
          bool running = heated >= hours - 1e-9;
          if (heat && !running && !CanStop(next, rs)) continue;           // stopping within the slot below the rise
          int nextPhase =
            heat && running ? (CanStop(next, rs) ? 1 : 2)
            : heat ? OffPhase(0)                                         // stopped within this quarter: incomplete
            : wasHeating ? OffPhase(1)                                   // stopped at its start: one whole quarter off
            : offQuarters >= 0 ? OffPhase(offQuarters + 1)
            : 0;
          double c = baseCost + heatCost + (heat && !wasHeating ? r.RunStartCostEur : 0);
          int ns = Idx(Bucket(next), nextPhase);
          if (c < nextCost[ns] - 1e-12 || (Math.Abs(c - nextCost[ns]) <= 1e-12 && next > nextExact[ns]))
          {
            nextCost[ns] = c;
            nextExact[ns] = next;
            nextRunStart[ns] = rs;
            prev[ns] = s;
            lvl[ns] = (byte)a;
          }
        }
      }
      cost = nextCost;
      exact = nextExact;
      runStart = nextRunStart;
    }

    // Cheapest end state (a run may still be going at the horizon's end), then walk back to read off the actions.
    int best = 0;
    for (int s = 1; s < cost.Length; s++)
      if (cost[s] < cost[best]) best = s;
    for (int i = n - 1, s = best; i >= first; i--)
    {
      actions[i] = (double)level[i][s] / HeatLevels;
      s = back[i][s];
    }
    return Simulate(r, actions);
  }

  /// <summary>Forecast of the heat pump's own automatic: heat to the setpoint whenever the tank falls to setpoint − hysteresis.</summary>
  /// <param name="r">Plan input (prices for the cost).</param>
  /// <param name="setpoint">Automatic setpoint, °C.</param>
  /// <param name="hysteresisK">Drop below the setpoint that starts heating, K.</param>
  /// <param name="heatingNow">The heat pump heats now.</param>
  /// <returns>The forecast as a plan.</returns>
  public static WarmWaterPlan PredictAutomatic(PlanRequest r, double setpoint, double hysteresisK, bool heatingNow)
  {
    var actions = new double[r.Slots.Count];
    var auto = r with { MaxTemp = setpoint };
    double temp = r.StartTemp;
    bool heating = heatingNow && temp < setpoint;
    for (int i = 0; i < actions.Length; i++)
    {
      if (!heating && temp <= setpoint - hysteresisK) heating = true;
      temp = Step(auto, i, temp, heating ? 1 : 0, out _, out double heated, out _);
      actions[i] = heated / SlotHours(auto, i);
      if (actions[i] < 1 - 1e-9) heating = false; // reached the setpoint within this slot
    }
    return Simulate(auto, actions);
  }

  /// <summary>Heating fractions per slot besides off (1/10 … 10/10), so a run can stop between slot boundaries.</summary>
  const int HeatLevels = 10;

  /// <summary>Runs per-slot heating fractions forward.</summary>
  /// <param name="r">Plan input.</param>
  /// <param name="actions">Heating fraction per slot (0 = off, 1 = whole slot).</param>
  /// <returns>Runs, temperatures and cost.</returns>
  internal static WarmWaterPlan Simulate(PlanRequest r, IReadOnlyList<double> actions)
  {
    var temps = new List<(DateTime, double)>(r.Slots.Count);
    var runs = new List<PlannedRun>();
    double temp = r.StartTemp, cost = 0, deficit = 0;
    DateTime? runStart = null;
    double runStartTemp = 0, runHours = 0, runPeak = 0;

    void EndRun()
    {
      if (runStart is not { } start) return;
      runs.Add(new PlannedRun(start, start.AddHours(runHours), runStartTemp, runPeak, (int)Math.Round(r.Model.PowerW((runStartTemp + runPeak) / 2))));
      runStart = null;
    }

    for (int i = 0; i < r.Slots.Count; i++)
    {
      var slotTime = SlotStart(r, i);
      temps.Add((slotTime, Math.Round(temp, 2)));
      deficit = Math.Max(deficit, Deficit(r.Comfort, slotTime, temp));
      bool heat = actions[i] > 0 && temp < r.MaxTemp - Resolution / 2;
      if (!heat) EndRun();
      if (heat && runStart is null)
      {
        runStart = slotTime;
        runStartTemp = temp;
        runHours = 0;
      }
      temp = Step(r, i, temp, heat ? actions[i] : 0, out double heatCost, out double heated, out double peak);
      cost += heatCost;
      runHours += heated;
      runPeak = peak;
      // A run that stops within the slot is over; the next heating is a new run.
      if (heat && heated < SlotHours(r, i) - 1e-9) EndRun();
    }
    EndRun();
    return new WarmWaterPlan(runs, temps, (float)cost, deficit);
  }

  /// <summary>One slot forward: heat for a fraction of it (stops at MaxTemp; only the heated time costs), then cool.</summary>
  /// <param name="r">Plan input.</param>
  /// <param name="i">Slot index.</param>
  /// <param name="temp">Tank temperature at the slot start, °C.</param>
  /// <param name="fraction">Heated share of the slot, 0…1.</param>
  /// <param name="heatCost">Cost of the heating, €.</param>
  /// <param name="heatedHours">Hours actually heated.</param>
  /// <param name="peak">Temperature when the heating stops, °C.</param>
  /// <returns>Temperature at the slot end, °C.</returns>
  static double Step(PlanRequest r, int i, double temp, double fraction, out double heatCost, out double heatedHours, out double peak)
  {
    var start = SlotStart(r, i);
    double hours = SlotHours(r, i);
    peak = Math.Max(temp, r.Model.Heat(temp, hours * fraction, r.MaxTemp));
    heatedHours = r.Model.HoursToHeat(temp, peak);
    heatCost = r.Model.PowerW((temp + peak) / 2) * heatedHours / 1000 * r.PricePerKwh[i];
    double rest = hours - heatedHours;
    return rest > 1e-9 ? r.Model.Cool(peak, start, rest) : peak;
  }

  /// <summary>Start of a slot; the first one from now on.</summary>
  /// <param name="r">Plan input.</param>
  /// <param name="i">Slot index.</param>
  static DateTime SlotStart(PlanRequest r, int i) => i == 0 && r.Now > r.Slots[0] ? r.Now : r.Slots[i];
  /// <summary>Length of a slot from <see cref="SlotStart"/> on, hours.</summary>
  /// <param name="r">Plan input.</param>
  /// <param name="i">Slot index.</param>
  static double SlotHours(PlanRequest r, int i) => TimeRange.Overlap(r.Slots[i], r.Slots[i] + TimeRange.Slot, r.Now, DateTime.MaxValue).TotalHours;

  /// <summary>Largest shortfall below the comfort windows active at a time.</summary>
  /// <param name="comfort">Comfort windows.</param>
  /// <param name="time">Time to check.</param>
  /// <param name="temp">Tank temperature, °C.</param>
  /// <returns>K; 0 if met.</returns>
  static double Deficit(IReadOnlyList<ComfortWindow> comfort, DateTime time, double temp)
  {
    double deficit = 0;
    foreach (var w in comfort)
      if (time >= w.Start && time <= w.End)
        deficit = Math.Max(deficit, w.MinTemp - temp);
    return deficit;
  }

  /// <summary>Penalty of a comfort shortfall (<see cref="DeficitPenaltyPerK"/> per K).</summary>
  /// <param name="comfort">Comfort windows.</param>
  /// <param name="time">Time to check.</param>
  /// <param name="temp">Tank temperature, °C.</param>
  static double Penalty(IReadOnlyList<ComfortWindow> comfort, DateTime time, double temp) =>
    Deficit(comfort, time, temp) * DeficitPenaltyPerK;

  /// <summary>Temperature bucket of the dynamic program.</summary>
  /// <param name="temp">Temperature, °C.</param>
  static int Bucket(double temp) => Math.Max(0, (int)Math.Round(temp / Resolution));
}
