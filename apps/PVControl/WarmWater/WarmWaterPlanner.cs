using System;
using System.Collections.Generic;
using System.Linq;
using NetDeamon.apps.PVControl.Simulator;

namespace NetDeamon.apps.PVControl.WarmWater;

/// <summary>The tank must stay at or above <see cref="MinTemp"/> from <see cref="Start"/> to <see cref="End"/>.</summary>
public readonly record struct ComfortWindow(DateTime Start, DateTime End, double MinTemp);

/// <summary>One planned heating run: heat from <see cref="Start"/> until the tank reaches <see cref="TargetTemp"/> (expected at <see cref="End"/>).</summary>
public readonly record struct PlannedRun(DateTime Start, DateTime End, double StartTemp, double TargetTemp, int PowerW);

/// <summary>A plan or a forecast: heating runs, the tank temperature at every slot start, and the heating energy cost (without the run start cost).</summary>
/// <param name="ComfortDeficitK">Largest shortfall below a comfort window's minimum (0 = all met).</param>
public sealed record WarmWaterPlan(IReadOnlyList<PlannedRun> Runs, IReadOnlyList<(DateTime Time, double Temp)> Temps, float CostEur, double ComfortDeficitK)
{
  public static WarmWaterPlan Empty { get; } = new([], [], 0, 0);
}

/// <summary>Everything a plan is made from. <see cref="Slots"/> are 15-min slot starts, the first one containing <see cref="Now"/>.</summary>
/// <param name="PricePerKwh">What one kWh of heating costs in each slot (see <see cref="WarmWaterPlanner.HeatingPricesPerKwh"/>).</param>
/// <param name="ForcedTarget">A heating that is going (never stopped) or a boost: it continues until the tank reaches this temperature.</param>
/// <param name="RunStartCostEur">Added per run start, so the plan prefers one longer run over several short ones.</param>
public sealed record PlanRequest(
  DateTime Now,
  double StartTemp,
  IReadOnlyList<DateTime> Slots,
  IReadOnlyList<float> PricePerKwh,
  TankModel Model,
  IReadOnlyList<ComfortWindow> Comfort,
  double MaxTemp,
  double? ForcedTarget = null,
  float RunStartCostEur = 0.02f);

/// <summary>
/// Plans warm-water heating at the lowest cost that keeps the comfort windows warm. Pure
/// functions, so they're testable without Home Assistant.
///
/// The tank is a store that loses heat all the time, unlike the EV whose level stays where it is
/// left. So instead of "reach the target by a time" the question is "be above the minimum in every
/// comfort window": a dynamic program over the 15-min slots and the tank temperature (0.1 K steps)
/// that decides per slot whether to heat. Heating early is only worth it when the price difference
/// beats the extra standby loss, which grows with the temperature — the model has that loss, so
/// the trade-off falls out of the cost comparison.
/// </summary>
public static class WarmWaterPlanner
{
  /// <summary>Temperature resolution of the dynamic program, in K.</summary>
  public const double Resolution = 0.1;
  /// <summary>Penalty per K below a comfort minimum, per slot. Far above any heating cost, so a window is only missed when it can't be met.</summary>
  public const float DeficitPenaltyPerK = 10f;
  const int SlotMinutes = 15;

  /// <summary>
  /// What one kWh of heating costs in each slot, from the house simulation: the heat pump drawing
  /// <paramref name="powerW"/> during just that slot, as extra grid cost vs <paramref name="baseline"/>
  /// (<see cref="SimulationResult.NetGridCostVs"/> — battery use is priced by the grid import or lost
  /// export it causes later). PV surplus that would be exported costs the export price; energy the
  /// battery would deliver costs what refilling it costs. Never below 0.
  ///
  /// Probed in blocks of <paramref name="probeSlots"/> slots, each block's price applying to all
  /// its slots: one simulation per slot took ~1 s per plan (192 simulations), and the prices
  /// (hourly) and the battery hardly change within half an hour.
  /// </summary>
  public static float[] HeatingPricesPerKwh(SimulationInput baseInput, SimulationResult baseline, IReadOnlyList<DateTime> slots, DateTime now, int powerW, int probeSlots = 2)
  {
    var prices = new float[slots.Count];
    for (int i = 0; i < slots.Count; i += probeSlots)
    {
      int last = Math.Min(i + probeSlots, slots.Count) - 1;
      var start = slots[i] < now ? now : slots[i];
      var end = slots[last].AddMinutes(SlotMinutes);
      double kwh = powerW * (end - start).TotalHours / 1000;
      if (kwh <= 0) continue;
      var load = new ExtraLoad { Name = "Warm water (price probe)", StartTime = start, EndTime = end, PowerW = powerW };
      var sim = EnergySimulator.Simulate(baseInput.WithExtraLoads([.. baseInput.ExtraLoads, load]));
      float price = (float)Math.Max(0, sim.NetGridCostVs(baseline) / kwh);
      for (int j = i; j <= last; j++) prices[j] = price;
    }
    return prices;
  }

  /// <summary>
  /// The heat pump's efficiency depends on the outside temperature (air/water): each slot's heating
  /// price × (1 + <paramref name="changePerK"/> · (mean − outside)), mean = the mean forecast
  /// temperature of the horizon — so a warmer afternoon gets cheaper than a cold night and the cost
  /// level stays the same. Slots without a forecast temperature are unchanged; factor 0.5…1.5.
  /// </summary>
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

  /// <summary>The cheapest heating plan that meets the comfort windows (see class summary).</summary>
  public static WarmWaterPlan Plan(PlanRequest r)
  {
    int n = r.Slots.Count;
    if (n == 0) return WarmWaterPlan.Empty;

    // A run that is already going isn't stopped: heat until its target first, then optimize.
    var actions = new double[n];
    int first = 0;
    double temp = r.StartTemp;
    if (r.ForcedTarget is { } forced && temp < forced - Resolution / 2)
    {
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
    // State = (temperature bucket, heated in the previous slot). Each state keeps the cheapest
    // path reaching it and that path's exact temperature, so rounding never accumulates.
    var cost = new double[2 * buckets];
    var exact = new double[2 * buckets];
    var back = new int[n][];
    var level = new byte[n][];
    Array.Fill(cost, double.PositiveInfinity);
    int s0 = State(Bucket(temp), heating, buckets);
    cost[s0] = 0;
    exact[s0] = temp;

    for (int i = first; i < n; i++)
    {
      var nextCost = new double[2 * buckets];
      var nextExact = new double[2 * buckets];
      var prev = back[i] = new int[2 * buckets];
      var lvl = level[i] = new byte[2 * buckets];
      Array.Fill(nextCost, double.PositiveInfinity);
      var slotTime = SlotStart(r, i);
      double hours = SlotHours(r, i);

      for (int s = 0; s < cost.Length; s++)
      {
        if (double.IsPositiveInfinity(cost[s])) continue;
        double t = exact[s];
        bool wasHeating = s >= buckets;
        double baseCost = cost[s] + Penalty(r.Comfort, slotTime, t);
        for (int a = 0; a <= HeatLevels; a++)
        {
          bool heat = a > 0;
          if (heat && t >= r.MaxTemp - Resolution / 2) break;
          double next = Step(r, i, t, (double)a / HeatLevels, out double heatCost, out double heated, out _);
          double c = baseCost + heatCost + (heat && !wasHeating ? r.RunStartCostEur : 0);
          // Still running at the slot's end only when it heated the whole slot — a run that
          // stopped within it is over, and heating again is a new start.
          int ns = State(Bucket(next), heated >= hours - 1e-9, buckets);
          if (c < nextCost[ns] - 1e-12 || (Math.Abs(c - nextCost[ns]) <= 1e-12 && next > nextExact[ns]))
          {
            nextCost[ns] = c;
            nextExact[ns] = next;
            prev[ns] = s;
            lvl[ns] = (byte)a;
          }
        }
      }
      cost = nextCost;
      exact = nextExact;
    }

    // Cheapest end state, then walk back to read off the actions.
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

  /// <summary>
  /// What the heat pump's own automatic does: heat to <paramref name="setpoint"/> whenever the tank
  /// falls to setpoint − <paramref name="hysteresisK"/> (measured: 45 °C setpoint starts at 39.9 °C).
  /// Used as the forecast while PVControl doesn't control the tank, and to show what the plan saves.
  /// </summary>
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

  /// <summary>
  /// Heating choices per slot besides "off": heat for 1/10 … 10/10 of the slot. Lets a run stop
  /// between slot boundaries (the heat pump stops at its setpoint), so the plan overshoots by at
  /// most ~0.4 K instead of up to a whole slot of heating (~4 K).
  /// </summary>
  const int HeatLevels = 10;

  /// <summary>Runs the given per-slot heating fractions (0 = off, 1 = whole slot) forward and collects runs, temperatures and cost.</summary>
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

  /// <summary>
  /// One slot forward: heat for <paramref name="fraction"/> of it (at the model's rate, stopping
  /// at MaxTemp — only the heated time costs), then cool for the rest. <paramref name="peak"/> is
  /// the temperature when the heating stops.
  /// </summary>
  static double Step(PlanRequest r, int i, double temp, double fraction, out double heatCost, out double heatedHours, out double peak)
  {
    var start = SlotStart(r, i);
    double hours = SlotHours(r, i);
    peak = Math.Max(temp, Math.Min(r.MaxTemp, temp + r.Model.HeatingRateKPerHour * hours * fraction));
    heatedHours = (peak - temp) / r.Model.HeatingRateKPerHour;
    heatCost = r.Model.PowerW((temp + peak) / 2) * heatedHours / 1000 * r.PricePerKwh[i];
    double rest = hours - heatedHours;
    return rest > 1e-9 ? r.Model.Cool(peak, start, rest) : peak;
  }

  static DateTime SlotStart(PlanRequest r, int i) => i == 0 && r.Now > r.Slots[0] ? r.Now : r.Slots[i];
  static double SlotHours(PlanRequest r, int i) => (r.Slots[i].AddMinutes(SlotMinutes) - SlotStart(r, i)).TotalHours;

  static double Deficit(IReadOnlyList<ComfortWindow> comfort, DateTime time, double temp)
  {
    double deficit = 0;
    foreach (var w in comfort)
      if (time >= w.Start && time <= w.End)
        deficit = Math.Max(deficit, w.MinTemp - temp);
    return deficit;
  }

  static double Penalty(IReadOnlyList<ComfortWindow> comfort, DateTime time, double temp) =>
    Deficit(comfort, time, temp) * DeficitPenaltyPerK;

  static int Bucket(double temp) => Math.Max(0, (int)Math.Round(temp / Resolution));
  static int State(int bucket, bool heated, int buckets) => Math.Min(bucket, buckets - 1) + (heated ? buckets : 0);
}
