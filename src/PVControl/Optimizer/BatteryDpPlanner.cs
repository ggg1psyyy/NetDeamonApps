using NetDeamon.apps.PVControl.Simulator;
using System;
using System.Collections.Generic;
using System.Linq;

namespace NetDeamon.apps.PVControl.Optimizer;

/// <summary>Settings of the DP shadow planner (<see cref="BatteryDpPlanner"/>).</summary>
/// <param name="BandPenaltyPerPctQuarter">Penalty per % below the preferred minimum per quarter, € (without EnforcePreferredSoc).
/// 0.0005 ≈ 1.7 ct per kWh of reserve and hour (11.5 kWh battery): the reserve bridges an expensive time to a clearly
/// cheaper charge (an evening peak at 0.51 before a night at 0.39), but isn't used for fractions of a cent.</param>
/// <param name="FloorPenaltyPerPctQuarter">Penalty per % below the hard floor per quarter, € — practically forbidden.</param>
/// <param name="StatesPerPercent">SoC grid of the program: states per % (4 = 0.25 % steps).</param>
/// <param name="ChargeStepPct">Charge targets a grid charge can stop at within a quarter, every this many %.</param>
/// <param name="TieEur">Actions whose rest costs differ by no more than this are a tie, €: the earlier action in the list
/// wins (normal, feedin_priority, grid_only, then each charge target VPP before bulk) — VPP where it charges as
/// cheaply, bulk only where it saves something; 0.01 ct covers the interpolation's noise.</param>
public sealed record DpOptions(
  float BandPenaltyPerPctQuarter = 0.0005f,
  float FloorPenaltyPerPctQuarter = 1f,
  int StatesPerPercent = 4,
  int ChargeStepPct = 2,
  float TieEur = 0.0001f);

/// <summary>A quarter of a DP plan.</summary>
/// <param name="Time">Slot start.</param>
/// <param name="SocPct">SoC at the slot start, % (rounded down, like the simulation's).</param>
/// <param name="Mode">Inverter mode.</param>
/// <param name="Bulk">A grid charge's path: bulk (true) or VPP.</param>
/// <param name="TargetSocPct">Target of a grid charge, %; 0 otherwise.</param>
/// <param name="GridImportWh">Grid import, Wh.</param>
/// <param name="GridExportWh">Grid export, Wh.</param>
/// <param name="BatteryChargeWh">Battery charge, Wh.</param>
/// <param name="BatteryDischargeWh">Battery discharge, Wh.</param>
public sealed record DpSlot(DateTime Time, int SocPct, InverterModes Mode, bool Bulk, int TargetSocPct,
  int GridImportWh, int GridExportWh, int BatteryChargeWh, int BatteryDischargeWh);

/// <summary>What a plan costs over the horizon, €: <see cref="Total"/> = grid cost + penalties − the value of the energy left at the end.</summary>
/// <param name="GridCost">Import cost − export earnings.</param>
/// <param name="BandPenalty">Quarters below the preferred minimum.</param>
/// <param name="FloorPenalty">Quarters below the hard floor.</param>
/// <param name="EndValue">Energy above the hard floor at the end, at what refilling it would cost (<see cref="PlanEvaluation.EndValue"/>).</param>
/// <param name="EndShortfall">Ending the horizon under the preferred minimum (<see cref="PlanEvaluation.EndShortfall"/>).</param>
public sealed record PlanCost(float GridCost, float BandPenalty, float FloorPenalty, float EndValue, float EndShortfall = 0)
{
  /// <summary>The objective, €.</summary>
  public float Total => GridCost + BandPenalty + FloorPenalty - EndValue + EndShortfall;
}

/// <summary>A DP plan: the quarters from now to the simulation's end and its cost.</summary>
/// <param name="Slots">The quarters.</param>
/// <param name="Cost">What it costs (<see cref="PlanEvaluation"/>).</param>
/// <param name="EndSocPct">SoC at the end, %.</param>
public sealed record DpPlan(IReadOnlyList<DpSlot> Slots, PlanCost Cost, int EndSocPct);

/// <summary>
/// The one cost model of a plan — the DP's objective and the yardstick both plans are compared with: grid cost per slot
/// (<see cref="SimulationInput.GridCostOf"/>), a penalty per % and quarter below the preferred minimum (only without
/// EnforcePreferredSoc: there the band is a reserve the plan keeps) and below the hard floor, and the energy left above
/// the hard floor at the end, valued at what refilling it would cost — the cheapest import price of the horizon's last
/// 24 h, for what it delivers after the discharge loss. Without a value a plan would empty the battery at the horizon's end for free; at the mean
/// price (peaks included) it bought energy for after the horizon (live 2026-10-30: 5.5 kWh at 0.41 at the very end).
/// </summary>
public static class PlanEvaluation
{
  /// <summary>Penalty of a quarter starting at a SoC, €.</summary>
  /// <param name="input">Simulation input (floors).</param>
  /// <param name="socPct">SoC at the slot start, % (fractional in the DP's grid).</param>
  /// <param name="o">Options.</param>
  public static (float Band, float Floor) Penalty(SimulationInput input, double socPct, DpOptions o)
  {
    int floor = input.GetEffectiveMinSoC();
    float band = !input.EnforcePreferredSoc && socPct < input.GetPreferredMinSoC()
      ? (float)(input.GetPreferredMinSoC() - socPct) * o.BandPenaltyPerPctQuarter : 0f;
    float under = socPct < floor ? (float)(floor - socPct) * o.FloorPenaltyPerPctQuarter : 0f;
    return (band, under);
  }

  /// <summary>
  /// Penalty for ending the horizon under the preferred minimum, €: per % missing at the end at the hard floor's rate —
  /// practically a rule (the user, 2026-10-31: the horizon ends at least at the preferred minimum; the plan is redone
  /// every few minutes and the horizon moves on at midnight, but the plan up to there doesn't count on an empty
  /// battery), still solvable where it can't be reached. Applies with EnforcePreferredSoc as well. Measured from the
  /// exact SoC, not whole %: rounded down, a plan ending at 19.9 % paid the full 1 € — and the DP, whose states lie
  /// between whole %, expected only part of it (live 2026-11-02: plans a minute apart differed by 1 €).
  /// </summary>
  /// <param name="input">Simulation input.</param>
  /// <param name="endEnergyWh">Energy at the end, Wh.</param>
  /// <param name="o">Options (the floor's rate).</param>
  public static float EndShortfall(SimulationInput input, int endEnergyWh, DpOptions o) =>
    (float)Math.Max(0, input.GetPreferredMinSoC() - Soc.FromWhExact(endEnergyWh, input.BatteryCapacityWh)) * o.FloorPenaltyPerPctQuarter;

  /// <summary>Value of the energy in the battery at the end, €: above the hard floor, what it delivers (after the discharge
  /// loss — the charge loss is in the flows) at <see cref="RefillPrice"/>.</summary>
  /// <param name="input">Simulation input.</param>
  /// <param name="endEnergyWh">Energy at the end, Wh.</param>
  public static float EndValue(SimulationInput input, int endEnergyWh) => EndValue(input, endEnergyWh, RefillPrice(input));

  /// <summary>Value of the energy in the battery at the end at a given refill price, € (the planner computes the price once).</summary>
  /// <param name="input">Simulation input.</param>
  /// <param name="endEnergyWh">Energy at the end, Wh.</param>
  /// <param name="refillPrice"><see cref="RefillPrice"/>, €/kWh.</param>
  public static float EndValue(SimulationInput input, int endEnergyWh, float refillPrice) =>
    input.DischargeAcForWh(endEnergyWh - input.SocToWh(input.GetEffectiveMinSoC())) / 1000f * refillPrice;

  /// <summary>What refilling the battery after the horizon costs: the cheapest import price of its last 24 h, €/kWh
  /// (<see cref="PriceList.GetPriceOrMean"/>: estimated where no price is published yet).</summary>
  /// <param name="input">Simulation input.</param>
  public static float RefillPrice(SimulationInput input)
  {
    var end = BatteryDpPlanner.HorizonEnd(input);
    float cheapest = float.MaxValue;
    for (var t = end.AddHours(-24); t < end; t = t.AddMinutes(TimeRange.SlotMinutes))
      cheapest = Math.Min(cheapest, input.ImportPrices.GetPriceOrMean(t));
    return cheapest;
  }

  /// <summary>Cost of a planned run (the greedy simulation or a DP plan) under the one model.</summary>
  /// <param name="input">Simulation input.</param>
  /// <param name="slots">(slot start, SoC % at the start, import Wh, export Wh) of each quarter.</param>
  /// <param name="endEnergyWh">Energy at the end, Wh.</param>
  /// <param name="o">Options.</param>
  public static PlanCost Evaluate(SimulationInput input, IEnumerable<(DateTime Time, double SocPct, int ImportWh, int ExportWh)> slots,
    int endEnergyWh, DpOptions o)
  {
    float grid = 0, band = 0, floor = 0;
    foreach (var s in slots)
    {
      grid += input.GridCostOf(s.Time, s.ImportWh, s.ExportWh);
      var (b, f) = Penalty(input, s.SocPct, o);
      band += b;
      floor += f;
    }
    return new PlanCost(grid, band, floor, EndValue(input, endEnergyWh), EndShortfall(input, endEnergyWh, o));
  }

  /// <summary>Cost of the greedy simulation's plan under the one model: its flows (the greedy simulation's own, lossless)
  /// against a battery with the conversion losses (<see cref="SimulationInput.WithBatteryLosses"/>) — the SoC path and the
  /// end those decisions really lead to, so both plans are judged alike.</summary>
  /// <param name="result">The simulation.</param>
  /// <param name="o">Options.</param>
  public static PlanCost Evaluate(SimulationResult result, DpOptions o)
  {
    var input = result.Input.WithBatteryLosses();
    int energy = input.SocToWh(input.StartSocPercent);
    var slots = new List<(DateTime, double, int, int)>(result.Slots.Count);
    foreach (var s in result.Slots)
    {
      slots.Add((s.Time, input.WhToSoc(energy), s.GridImportWh, s.GridExportWh));
      energy = input.NextEnergyWh(energy, s.BatteryChargeWh, s.BatteryDischargeWh);
    }
    return Evaluate(input, slots, energy, o);
  }
}

/// <summary>
/// Global battery plan by dynamic programming (shadow: computed and published next to the greedy simulation, never
/// steering). States: the battery's energy on a grid of <see cref="DpOptions.StatesPerPercent"/> per %; per quarter
/// the actions normal, feedin_priority, grid_only and grid charges to targets every <see cref="DpOptions.ChargeStepPct"/> % on the VPP and
/// the bulk path. Each transition's flows come from the simulation's own <see cref="EnergySimulator.ComputeEnergyFlows"/>
/// (same limits, efficiency, import cap); a quarter with a load that never draws the battery holds it as the simulation
/// does. Backwards from the end (<see cref="PlanEvaluation.EndValue"/>) the cheapest rest is computed for every state,
/// then the plan is followed forwards from the real SoC with exact energies.
/// </summary>
public static class BatteryDpPlanner
{
  private const int SlotMinutes = TimeRange.SlotMinutes;

  /// <summary>End of the plan's horizon: the simulation's (tomorrow's midnight).</summary>
  /// <param name="input">Simulation input.</param>
  public static DateTime HorizonEnd(SimulationInput input) => input.StartTime.RoundToNearestQuarterHour().Date.AddDays(2);

  /// <summary>Plans the battery over the simulation's horizon (the running quarter through the end of tomorrow).</summary>
  /// <param name="input">The simulation input (the greedy run's: same forecasts, prices, extra loads).</param>
  /// <param name="options">Settings; defaults if null.</param>
  /// <returns>The plan.</returns>
  public static DpPlan Plan(SimulationInput input, DpOptions? options = null)
  {
    input = input.WithBatteryLosses();   // the DP plans with the battery's conversion losses
    var o = options ?? new DpOptions();
    var start = input.StartTime.RoundToNearestQuarterHour();
    var end = HorizonEnd(input);
    var times = new List<DateTime>();
    for (var t = start; t < end; t = t.AddMinutes(SlotMinutes)) times.Add(t);
    int n = times.Count;
    int states = 100 * o.StatesPerPercent + 1;
    double whPerState = input.BatteryCapacityWh / (double)(states - 1);
    int StateOf(int wh) => Math.Clamp((int)Math.Round(wh / whPerState), 0, states - 1);
    int WhOf(int state) => (int)Math.Round(state * whPerState);
    // The rest's cost at an exact energy: linear between the two grid states around it. Rounding to the nearest state
    // instead biased every quarter's small flow (100 Wh = 3.47 steps → 3) and the plan was dearer than it believed.
    float ValueAt(float[] v, int wh)
    {
      double x = Math.Clamp(wh / whPerState, 0, states - 1);
      int lo = (int)Math.Floor(x), hi = Math.Min(lo + 1, states - 1);
      double w = x - lo;
      return (float)(v[lo] * (1 - w) + v[hi] * w);
    }

    var energies = times.Select(t => input.SlotEnergyWh(t)).ToArray();
    var fractions = times.Select(t => input.SlotFraction(t)).ToArray();

    // value[i][s]: cheapest cost from slot i in state s to the end; policy[i][s]: the action taken
    var value = new float[n + 1][];
    var policy = new Action[n][];
    value[n] = new float[states];
    float refill = PlanEvaluation.RefillPrice(input);
    for (int s = 0; s < states; s++)
      value[n][s] = -PlanEvaluation.EndValue(input, WhOf(s), refill) + PlanEvaluation.EndShortfall(input, WhOf(s), o);

    for (int i = n - 1; i >= 0; i--)
    {
      value[i] = new float[states];
      policy[i] = new Action[states];
      var (pv, load, extra) = energies[i];
      int totalLoad = load + extra;
      bool held = input.BatteryHeldForLoad(times[i], pv, totalLoad);
      var actions = Actions(input, o, held, HoldAllowed(input, times[i], pv, totalLoad));
      for (int s = 0; s < states; s++)
      {
        int wh = WhOf(s);
        int pct = input.WhToSoc(wh);   // the whole % the simulation and the evaluation use
        var (band, floor) = PlanEvaluation.Penalty(input, pct, o);
        float best = float.MaxValue;
        Action bestAction = actions[0];
        foreach (var a in actions)
        {
          if (a.Mode == InverterModes.force_charge && a.TargetSocPct <= pct) continue;   // nothing to charge
          var (ch, dis, imp, exp) = Flows(input, a, pv, totalLoad, wh, fractions[i]);
          int next = input.NextEnergyWh(wh, ch, dis);
          float rest = input.GridCostOf(times[i], imp, exp) + band + floor + ValueAt(value[i + 1], next);
          if (rest < best - o.TieEur) { best = rest; bestAction = a; }   // a tie goes to the earlier action
        }
        value[i][s] = best;
        policy[i][s] = bestAction;
      }
    }

    // Forwards from the real SoC with exact energies (the policy by the nearest state)
    var slots = new List<DpSlot>(n);
    int energy = input.SocToWh(input.StartSocPercent);
    for (int i = 0; i < n; i++)
    {
      var (pv, load, extra) = energies[i];
      var a = policy[i][StateOf(energy)];
      var (ch, dis, imp, exp) = Flows(input, a, pv, load + extra, energy, fractions[i]);
      slots.Add(new DpSlot(times[i], input.WhToSoc(energy), a.Mode, a.Bulk, a.Mode == InverterModes.force_charge ? a.TargetSocPct : 0, imp, exp, ch, dis));
      energy = input.NextEnergyWh(energy, ch, dis);
    }
    var cost = PlanEvaluation.Evaluate(input, slots.Select(s => (s.Time, (double)s.SocPct, s.GridImportWh, s.GridExportWh)), energy, o);
    return new DpPlan(slots, cost, input.WhToSoc(energy));
  }

  /// <summary>An action of a quarter: a mode, and for a grid charge its path and target.</summary>
  private readonly record struct Action(InverterModes Mode, bool Bulk, int TargetSocPct);

  /// <summary>
  /// The actions of a quarter: normal (grid_only where a load never draws the battery), feedin_priority, grid_only (only
  /// where <see cref="HoldAllowed"/>),
  /// and grid charges to every <see cref="DpOptions.ChargeStepPct"/> % target on both paths. The order breaks ties
  /// (the first of equally cheap actions wins): with PV covering the house, feedin_priority and grid_only have the same
  /// quarter flows (the surplus exported, nothing from the battery), but feedin_priority lets the battery cover the
  /// dips within the quarter that grid_only buys from the grid (live 2026-11-02 08:45/14:00: grid_only held the battery
  /// at 0.51 for 0.1 ct); with PV under the house load feedin_priority is normal, which comes first.
  /// </summary>
  private static List<Action> Actions(SimulationInput input, DpOptions o, bool held, bool holdAllowed)
  {
    var list = new List<Action> { new(held ? InverterModes.grid_only : InverterModes.normal, false, 0) };
    if (!held)
    {
      list.Add(new(InverterModes.feedin_priority, false, 0));
      if (holdAllowed) list.Add(new(InverterModes.grid_only, false, 0));
    }
    for (int target = o.ChargeStepPct; target <= 100; target += o.ChargeStepPct)
    {
      list.Add(new(InverterModes.force_charge, false, target));
      list.Add(new(InverterModes.force_charge, true, target));
    }
    return list;
  }

  /// <summary>
  /// Whether the DP may hold the battery (grid_only) in a quarter: outside the PV period always (everything comes from
  /// the grid or the battery anyway); within it only where the house's deficit under PV would take at least 1 % SoC out
  /// of the battery in the quarter (the user, 2026-11-02). Where PV covers the house or nearly does, a hold only buys the
  /// dips within the quarter from the grid (live 2026-11-02 08:30/14:15: PV 2 Wh short of the load, held at 0.53);
  /// feedin_priority exports the same surplus and lets the battery cover the dips.
  /// </summary>
  /// <param name="input">Simulation input (capacity, losses, PV period).</param>
  /// <param name="slot">Quarter start.</param>
  /// <param name="pvWh">PV of the quarter, Wh.</param>
  /// <param name="totalLoadWh">Load incl. extra loads, Wh.</param>
  public static bool HoldAllowed(SimulationInput input, DateTime slot, int pvWh, int totalLoadWh) =>
    !input.IsInPVPeriod(slot) || input.DrawnWh(Math.Max(0, totalLoadWh - pvWh)) >= input.SocToWh(1);

  /// <summary>The flows of an action, by the simulation's own function.</summary>
  private static (int ch, int dis, int imp, int exp) Flows(SimulationInput input, Action a, int pv, int totalLoad, int wh, double fraction)
  {
    var state = new InverterState(a.Mode) { Bulk = a.Bulk };
    return EnergySimulator.ComputeEnergyFlows(state, pv, totalLoad, wh, input, a.TargetSocPct, fraction);
  }
}
