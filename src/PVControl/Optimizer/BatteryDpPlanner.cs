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
/// <param name="KeepModeEur">In the current quarter the mode running now (normal, feedin_priority or grid_only) stays
/// while the best action is cheaper by no more than this, € — no switching every minute between options worth less than
/// 0.1 ct (2026-11-03: flat 0.50 prices, 9 switches in 1.5 h).</param>
/// <param name="ChargeNowEur">A grid charge the plan has later in the current price block starts (or keeps running) now
/// while that costs no more than this, € — within one price block the start is a tie, and the minute's replans moved it
/// (2026-11-04 12:00–12:08: VPP, normal, bulk, normal).</param>
/// <param name="ChargeStartEur">Cost of starting a grid charge after a quarter without one, €: one long charge beats a
/// block plus top-ups in quarters a fraction of a cent dearer (2026-11-07: 11:00 bulk, then five VPP top-ups at
/// +0.1 ct/kWh). A running charge (the current mode) continues without it. It also drops a whole charge that saves less
/// than this — 1 ct keeps a night charge worth 2 ct (2 ct dropped it), the 7 live snapshots of 2026-11-05…07 gave the same
/// blocks at 1, 2 and 5 ct. 0 = off — the DP then has no charging flag in its state.</param>
public sealed record DpOptions(
  float BandPenaltyPerPctQuarter = 0.0005f,
  float FloorPenaltyPerPctQuarter = 1f,
  int StatesPerPercent = 4,
  int ChargeStepPct = 2,
  float TieEur = 0.0001f,
  float KeepModeEur = 0.001f,
  float ChargeNowEur = 0.01f,
  float ChargeStartEur = 0.01f);

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
/// <param name="BatteryValue">What a kWh the battery delivers is worth at the end of the quarter, €/kWh: how much cheaper
/// the rest of the plan gets with it (<see cref="BatteryDpPlanner.BatteryValueOf"/>). Above it the battery covers the
/// house, below it the plan buys from the grid and keeps the battery — the line that explains the plan's holds.</param>
public sealed record DpSlot(DateTime Time, int SocPct, InverterModes Mode, bool Bulk, int TargetSocPct,
  int GridImportWh, int GridExportWh, int BatteryChargeWh, int BatteryDischargeWh, float BatteryValue = 0);

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
/// EnforcePreferredSoc: there the band is a reserve the plan keeps; only where the battery delivers,
/// <see cref="PlanEvaluation.BandApplies"/>) and below the hard floor, and the energy left above
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
  /// <param name="mode">The quarter's mode: the band only costs where the battery delivers (<see cref="BandApplies"/>).</param>
  public static (float Band, float Floor) Penalty(SimulationInput input, double socPct, DpOptions o, InverterModes mode)
  {
    int floor = input.GetEffectiveMinSoC();
    float band = BandApplies(mode) && !input.EnforcePreferredSoc && socPct < input.GetPreferredMinSoC()
      ? (float)(input.GetPreferredMinSoC() - socPct) * o.BandPenaltyPerPctQuarter : 0f;
    float under = socPct < floor ? (float)(floor - socPct) * o.FloorPenaltyPerPctQuarter : 0f;
    return (band, under);
  }

  /// <summary>
  /// Whether the preferred minimum's band costs in a quarter: only where the battery delivers (normal, feedin_priority,
  /// force_discharge). The band is a reserve for a load above the forecast; during a hold or a charge the grid covers
  /// such a load and the reserve isn't at risk — charging it there kept the plan at the preferred minimum and buying in
  /// an expensive hour rather than holding below it until a cheaper charge (the user, 2026-11-05: 22:00 at 0.487 held at
  /// 16 % for 20 quarters until the 03:00 charge instead of running to 23:00).
  /// </summary>
  /// <param name="mode">The quarter's mode.</param>
  public static bool BandApplies(InverterModes mode) =>
    mode is InverterModes.normal or InverterModes.feedin_priority or InverterModes.force_discharge or InverterModes.automatic;

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
  /// <param name="slots">(slot start, SoC % at the start, import Wh, export Wh, mode) of each quarter.</param>
  /// <param name="endEnergyWh">Energy at the end, Wh.</param>
  /// <param name="o">Options.</param>
  public static PlanCost Evaluate(SimulationInput input, IEnumerable<(DateTime Time, double SocPct, int ImportWh, int ExportWh, InverterModes Mode)> slots,
    int endEnergyWh, DpOptions o)
  {
    float grid = 0, band = 0, floor = 0;
    foreach (var s in slots)
    {
      grid += input.GridCostOf(s.Time, s.ImportWh, s.ExportWh);
      var (b, f) = Penalty(input, s.SocPct, o, s.Mode);
      band += b;
      floor += f;
    }
    return new PlanCost(grid, band, floor, EndValue(input, endEnergyWh), EndShortfall(input, endEnergyWh, o));
  }

  /// <summary>Cost of the greedy simulation's plan under the one model: its flows against a battery with the conversion
  /// losses (<see cref="SimulationInput.WithBatteryLosses"/>; the simulation's own unless a test ran it lossless) — the SoC
  /// path and the end those decisions lead to, so both plans are judged alike.</summary>
  /// <param name="result">The simulation.</param>
  /// <param name="o">Options.</param>
  public static PlanCost Evaluate(SimulationResult result, DpOptions o)
  {
    var input = result.Input.WithBatteryLosses();
    var path = result.EnergyPathWh(input);
    var slots = result.Slots.Select((s, i) => (s.Time, (double)input.WhToSoc(path[i]), s.GridImportWh, s.GridExportWh, s.State.Mode));
    return Evaluate(input, slots, path[^1], o);
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
    var o = options ?? new DpOptions();
    input = input.WithBatteryLosses();   // the DP plans with the battery's conversion losses
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
    var holdOks = times.Select(t => HoldAllowed(input, t)).ToArray();   // per whole quarter, the running one too

    // value[i][c][s]: cheapest cost from slot i in state s to the end, c = 1 if the quarter before charged from the grid
    // (only with a ChargeStartEur; else one layer); policy[i][c][s]: the action taken
    int layers = o.ChargeStartEur > 0 ? 2 : 1;
    int LayerAfter(Action a) => layers > 1 && a.Mode == InverterModes.force_charge ? 1 : 0;
    float StartCost(Action a, int c) => a.Mode == InverterModes.force_charge && c == 0 ? o.ChargeStartEur : 0;
    var value = new float[n + 1][][];
    var policy = new Action[n][][];
    value[n] = new float[layers][];
    float refill = PlanEvaluation.RefillPrice(input);
    // The end rule aimed half a state above the preferred minimum: the plan is followed with exact energies from the
    // nearest state's policy and drifts by up to half a step — aimed at exactly 20 % it ended at 19.9x % and paid the
    // rule's 1 €/% (live 2026-11-07: 5 of 119 quarters, 1.7–9.5 ct). PlanEvaluation judges at exactly the minimum.
    int endMarginWh = (int)Math.Ceiling(whPerState / 2);
    for (int c = 0; c < layers; c++)
    {
      value[n][c] = new float[states];
      for (int s = 0; s < states; s++)
        value[n][c][s] = -PlanEvaluation.EndValue(input, WhOf(s), refill) + PlanEvaluation.EndShortfall(input, WhOf(s) - endMarginWh, o);
    }

    var bestRestOf = new float[2];
    var bestActionOf = new Action[2];
    for (int i = n - 1; i >= 0; i--)
    {
      value[i] = new float[layers][];
      policy[i] = new Action[layers][];
      for (int c = 0; c < layers; c++) { value[i][c] = new float[states]; policy[i][c] = new Action[states]; }
      var (pv, load, extra) = energies[i];
      int totalLoad = load + extra;
      bool held = input.BatteryHeldForLoad(times[i], pv, totalLoad);
      bool holdOk = holdOks[i];
      var actions = Actions(input, o, held, holdOk);
      for (int s = 0; s < states; s++)
      {
        int wh = WhOf(s);
        int pct = input.WhToSoc(wh);   // the whole % the simulation and the evaluation use
        var (band, floor) = PlanEvaluation.Penalty(input, pct, o, InverterModes.normal);
        bestRestOf[0] = bestRestOf[1] = float.MaxValue;
        bestActionOf[0] = bestActionOf[1] = actions[0];
        foreach (var a in actions)
        {
          if (a.Mode == InverterModes.force_charge && a.TargetSocPct <= pct) continue;   // nothing to charge
          var (ch, dis, imp, exp) = Flows(input, a, pv, totalLoad, wh, fractions[i]);
          // A "charge" that stores under 1 % is a hold under another name (the battery doesn't discharge): the hold rule
          // applies to it (2026-11-03: 1–34 Wh force_charge quarters in daylight where the hold was ruled out).
          if (a.Mode == InverterModes.force_charge && !holdOk && StoresUnderOnePercent(input, ch, fractions[i])) continue;
          int next = input.NextEnergyWh(wh, ch, dis);
          float rest = input.GridCostOf(times[i], imp, exp) + (PlanEvaluation.BandApplies(a.Mode) ? band : 0) + floor + ValueAt(value[i + 1][LayerAfter(a)], next);
          for (int c = 0; c < layers; c++)
          {
            float total = rest + StartCost(a, c);
            if (total < bestRestOf[c] - o.TieEur) { bestRestOf[c] = total; bestActionOf[c] = a; }   // a tie goes to the earlier action
          }
        }
        for (int c = 0; c < layers; c++) { value[i][c][s] = bestRestOf[c]; policy[i][c][s] = bestActionOf[c]; }
      }
    }

    // Forwards from the real SoC with exact energies (the policy by the nearest state)
    int startEnergy = input.SocToWh(input.StartSocPercent);
    var (pv0, load0, extra0) = energies[0];
    // A charge running now continues without a start cost.
    int layer0 = layers > 1 && input.CurrentMode.Mode == InverterModes.force_charge ? 1 : 0;
    float RestAt(int i, Action a, int energyAfter) => StartCost(a, i == 0 ? layer0 : 0) + ValueAt(value[i + 1][LayerAfter(a)], energyAfter);
    (List<DpSlot> Slots, int End) Forward(Action? first)
    {
      var list = new List<DpSlot>(n);
      int energy = startEnergy, layer = layer0;
      for (int i = 0; i < n; i++)
      {
        var (pv, load, extra) = energies[i];
        var a = i == 0 ? first ?? FirstAction(input, o, policy[0][layer0][StateOf(energy)], times[0], pv, load + extra, energy, fractions[0], (act, e) => RestAt(0, act, e))
          : policy[i][layer][StateOf(energy)];
        var (ch, dis, imp, exp) = Flows(input, a, pv, load + extra, energy, fractions[i]);
        int next = input.NextEnergyWh(energy, ch, dis);
        list.Add(new DpSlot(times[i], input.WhToSoc(energy), a.Mode, a.Bulk, a.Mode == InverterModes.force_charge ? a.TargetSocPct : 0, imp, exp, ch, dis,
          BatteryValueOf(input, value[i + 1][LayerAfter(a)], next, ValueAt)));
        energy = next;
        layer = LayerAfter(a);
      }
      return (list, energy);
    }
    PlanCost CostOf((List<DpSlot> Slots, int End) f) =>
      PlanEvaluation.Evaluate(input, f.Slots.Select(s => (s.Time, (double)s.SocPct, s.GridImportWh, s.GridExportWh, s.Mode)), f.End, o);

    var plan = Forward(null);
    var cost = CostOf(plan);
    // A charge the plan has later in the current price block starts — or keeps running — now: within one block its start
    // is a tie the minute's replans kept moving (2026-11-04 12:00–12:08: VPP, normal, bulk, normal).
    if (plan.Slots[0].Mode != InverterModes.force_charge)
    {
      float price0 = input.ImportPrices.GetPriceOrMean(times[0]);
      int blockEnd = 1;
      while (blockEnd < n && input.ImportPrices.GetPriceOrMean(times[blockEnd]) == price0) blockEnd++;
      if (plan.Slots.Take(blockEnd).Skip(1).Any(s => s.Mode == InverterModes.force_charge))
      {
        bool holdOk = holdOks[0];
        bool bulk = input.CurrentMode is { Mode: InverterModes.force_charge, Bulk: true };
        Action? best = null;
        float bestRest = float.MaxValue;
        foreach (var a in Actions(input, o, input.BatteryHeldForLoad(times[0], pv0, load0 + extra0), holdOk))
        {
          if (a.Mode != InverterModes.force_charge || a.Bulk != bulk || a.TargetSocPct <= input.WhToSoc(startEnergy)) continue;
          var (ch, dis, imp, exp) = Flows(input, a, pv0, load0 + extra0, startEnergy, fractions[0]);
          if (!holdOk && StoresUnderOnePercent(input, ch, fractions[0])) continue;
          var (band, floor) = PlanEvaluation.Penalty(input, input.WhToSoc(startEnergy), o, a.Mode);
          float rest = input.GridCostOf(times[0], imp, exp) + band + floor + RestAt(0, a, input.NextEnergyWh(startEnergy, ch, dis));
          if (rest < bestRest - o.TieEur) (best, bestRest) = (a, rest);
        }
        if (best is { } charge)
        {
          var now = Forward(charge);
          var nowCost = CostOf(now);
          if (nowCost.Total <= cost.Total + o.ChargeNowEur) (plan, cost) = (now, nowCost);
        }
      }
    }
    return new DpPlan(plan.Slots, cost, input.WhToSoc(plan.End));
  }

  /// <summary>
  /// The current quarter's action at the exact energy now: the best one, except that the mode running now (normal,
  /// feedin_priority, grid_only) stays while the best is cheaper by no more than <see cref="DpOptions.KeepModeEur"/> — a
  /// hysteresis on the current quarter only, the rest of the plan stays free.
  /// </summary>
  private static Action FirstAction(SimulationInput input, DpOptions o, Action planned, DateTime time, int pv, int totalLoad,
    int energy, double fraction, Func<Action, int, float> restValue)
  {
    var current = input.CurrentMode.Mode;
    if (current is not (InverterModes.normal or InverterModes.feedin_priority or InverterModes.grid_only) || planned.Mode == current)
      return planned;
    var keep = new Action(current, false, 0);
    bool held = input.BatteryHeldForLoad(time, pv, totalLoad);
    if (!Actions(input, o, held, HoldAllowed(input, time)).Contains(keep))
      return planned;
    float Rest(Action a)
    {
      var (ch, dis, imp, exp) = Flows(input, a, pv, totalLoad, energy, fraction);
      var (band, floor) = PlanEvaluation.Penalty(input, input.WhToSoc(energy), o, a.Mode);
      return input.GridCostOf(time, imp, exp) + band + floor + restValue(a, input.NextEnergyWh(energy, ch, dis));
    }
    return Rest(keep) <= Rest(planned) + o.KeepModeEur ? keep : planned;
  }

  /// <summary>
  /// What a delivered kWh from the battery is worth at an energy, €/kWh: the slope of the rest's cost over ±1 % SoC
  /// (the DP's table: one kWh more in the battery makes the rest of the plan that much cheaper — a shadow price), per
  /// stored kWh and then per delivered kWh (÷ the discharge efficiency), so it compares with the import price. Near the
  /// floor the floor's penalty makes it huge: limited to 0…1 €/kWh.
  /// </summary>
  /// <param name="input">Simulation input (with the DP's losses).</param>
  /// <param name="rest">Cost of the rest by state (the DP's table for the next quarter).</param>
  /// <param name="energyWh">Energy at the end of the quarter, Wh.</param>
  /// <param name="valueAt">The table's interpolation.</param>
  internal static float BatteryValueOf(SimulationInput input, float[] rest, int energyWh, Func<float[], int, float> valueAt)
  {
    int step = input.SocToWh(1);
    int lo = Math.Max(0, energyWh - step), hi = Math.Min(input.BatteryCapacityWh, energyWh + step);
    if (hi <= lo) return 0;
    double perStoredKwh = (valueAt(rest, lo) - valueAt(rest, hi)) / ((hi - lo) / 1000.0);
    double perDeliveredKwh = perStoredKwh * input.DrawnWh(1000) / 1000.0;
    return (float)Math.Round(Math.Clamp(perDeliveredKwh, 0, 1), 4);
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
  /// Whether the DP may hold the battery (grid_only) in a quarter: without PV always (everything comes from the grid or
  /// the battery anyway); with any PV forecast only where the house's deficit under PV would take at least 1 % SoC out of
  /// the battery in the quarter (the user, 2026-11-02). Where PV covers the house or nearly does, a hold only buys PV's
  /// dips within the quarter from the grid (live 2026-11-02 08:30/14:15: PV 2 Wh short of the load, held at 0.53);
  /// feedin_priority exports the same surplus and lets the battery cover the dips. "Any PV", not the PV period
  /// (<see cref="SimulationInput.IsInPVPeriod"/>: PV above the load): on a dull day there is none, yet PV produces and
  /// dips (2026-11-03: forecast 330 W against a 700-W load, real 570 W against 515 W).
  /// </summary>
  /// <param name="input">Simulation input (capacity, losses).</param>
  /// <param name="pvWh">PV of the quarter, Wh.</param>
  /// <param name="totalLoadWh">Load incl. extra loads, Wh.</param>
  public static bool HoldAllowed(SimulationInput input, int pvWh, int totalLoadWh) =>
    pvWh <= 0 || input.DrawnWh(Math.Max(0, totalLoadWh - pvWh)) >= input.SocToWh(1);

  /// <summary>
  /// <see cref="HoldAllowed(SimulationInput, int, int)"/> of a whole quarter, the running one too: the rule is per quarter.
  /// On the running quarter's rest it fell under 1 % a few minutes in and the minute's replan dropped the hold for the
  /// rest of the quarter (live 2026-11-08 07:15–08:45, 14:15–14:45: grid_only at :00, normal 3–7 minutes later, every quarter).
  /// </summary>
  /// <param name="input">Simulation input (forecasts, capacity, losses).</param>
  /// <param name="slot">Start of the quarter.</param>
  public static bool HoldAllowed(SimulationInput input, DateTime slot)
  {
    var (pv, load, extra) = input.QuarterEnergyWh(slot);
    return HoldAllowed(input, pv, load + extra);
  }

  /// <summary>
  /// Whether a grid charge stores under 1 % SoC per quarter — a hold under another name where holds are ruled out
  /// (2026-11-03). On the running quarter's rest the 1 % shrinks with it: the charge's rate decides, not how many minutes
  /// are left (live 2026-11-08 10:13, 10:44, 12:29, 13:14: a charge to 99 % stopped late in its quarters).
  /// </summary>
  /// <param name="input">Simulation input (capacity, losses).</param>
  /// <param name="chargeWh">The charge into the battery in the slot (or its rest), Wh.</param>
  /// <param name="fraction">Share of the quarter the charge runs (<see cref="SimulationInput.SlotFraction"/>).</param>
  internal static bool StoresUnderOnePercent(SimulationInput input, int chargeWh, double fraction) =>
    input.StoredWh(chargeWh) < input.SocToWh(1) * fraction;

  /// <summary>The flows of an action, by the simulation's own function.</summary>
  private static (int ch, int dis, int imp, int exp) Flows(SimulationInput input, Action a, int pv, int totalLoad, int wh, double fraction)
  {
    var state = new InverterState(a.Mode) { Bulk = a.Bulk };
    return EnergySimulator.ComputeEnergyFlows(state, pv, totalLoad, wh, input, a.TargetSocPct, fraction);
  }
}
