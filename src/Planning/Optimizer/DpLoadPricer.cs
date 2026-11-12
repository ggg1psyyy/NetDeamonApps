using NetDeamon.apps.PVControl.Simulator;
using System;
using System.Collections.Generic;
using System.Linq;

namespace NetDeamon.apps.PVControl.Optimizer;

/// <summary>A quarter of a <see cref="LoadReplay"/>: the plan's mode as it runs with the loads and its flows.</summary>
/// <param name="Time">Slot start.</param>
/// <param name="Mode">The mode (the plan's; grid_only where a load from the grid holds the battery).</param>
/// <param name="ImportWh">Grid import, Wh.</param>
/// <param name="ExportWh">Grid export, Wh.</param>
/// <param name="ChargeWh">Battery charge, Wh.</param>
/// <param name="DischargeWh">Battery discharge, Wh.</param>
/// <param name="EnergyWh">Battery energy at the start, Wh.</param>
public sealed record ReplayQuarter(DateTime Time, InverterModes Mode, int ImportWh, int ExportWh, int ChargeWh, int DischargeWh, int EnergyWh);

/// <summary>The loads' quarters replayed without and with them on the DP plan, and what they cost.</summary>
/// <param name="Without">The quarters without the loads.</param>
/// <param name="With">The same quarters with them.</param>
/// <param name="GridEur">The grid cost the loads add in their quarters, €.</param>
/// <param name="RestEur">What the battery's energy difference after them costs the rest of the plan, € (the DP's table).</param>
public sealed record LoadReplay(IReadOnlyList<ReplayQuarter> Without, IReadOnlyList<ReplayQuarter> With, float GridEur, float RestEur)
{
  /// <summary>The loads' cost, €.</summary>
  public float CostEur => GridEur + RestEur;
}

/// <summary>
/// The DP's answer to "what do these loads cost" (<see cref="ILoadPricer"/>), from a DP plan of the house without them:
/// 1. the loads' quarters replayed with the plan's modes held — the simulation's flows, so a load the battery covers in
///    "normal" draws it, one from the grid holds it (with the house: grid_only, as live), the import cap trims a charge —
///    their grid cost against the same quarters without the loads;
/// 2. the battery's energy difference after them at the DP's cost of the rest (<see cref="DpRestCost"/>): what the DP
///    would do with a kWh more or less from there, not what a rule-based plan would re-decide.
/// Nothing in it re-plans the night, so a 1-% shift in the battery can't flip the price of a start (live 2026-11-09: the
/// greedy simulation's charges jumped and priced the EV's 23:15 start 22 ct under 23:00, which was 2 ct cheaper).
/// </summary>
public sealed class DpLoadPricer : ILoadPricer
{
  private readonly SimulationInput _base;   // with the DP's battery losses

  /// <summary>Plans the house without the loads (one DP run) and prices against it.</summary>
  /// <param name="baseInput">The house without the loads to price (with the mode running now, <see cref="DpSteering.RunningInput"/>).</param>
  /// <param name="options">The DP's options.</param>
  public DpLoadPricer(SimulationInput baseInput, DpOptions options) : this(baseInput, BatteryDpPlanner.Plan(baseInput, options)) { }

  /// <summary>Prices against a given plan of the house without the loads.</summary>
  /// <param name="baseInput">The house without the loads.</param>
  /// <param name="plan">The DP plan on it.</param>
  public DpLoadPricer(SimulationInput baseInput, DpPlan plan)
  {
    _base = baseInput.WithBatteryLosses();
    Plan = plan;
  }

  /// <summary>
  /// The pricer of loads planned against a base: the DP's while its plan steers (<paramref name="dpOptions"/> set — a
  /// fresh, selected DP plan, <see cref="DpSteering.Choose"/>), planned from the mode running now; else null — the greedy
  /// simulation's, as for the plan that then steers.
  /// </summary>
  /// <param name="dpOptions">The DP's options while it steers; null: the greedy plan steers (or the DP plan is stale).</param>
  /// <param name="baseInput">The house without the loads.</param>
  /// <param name="running">The mode running now; null: the input's.</param>
  public static ILoadPricer? For(DpOptions? dpOptions, SimulationInput baseInput, InverterState? running) =>
    dpOptions is { } o ? new DpLoadPricer(DpSteering.RunningInput(baseInput, running), o) : null;

  /// <summary>The DP plan of the house without the loads.</summary>
  public DpPlan Plan { get; }

  /// <inheritdoc/>
  public string Name => "dp";

  /// <inheritdoc/>
  public float Cost(IReadOnlyList<ExtraLoad> added, Func<SimulationResult> simulated) => Replay(added).CostEur;

  /// <summary>The loads' quarters without and with them, and their cost.</summary>
  /// <param name="added">The loads (as supplied).</param>
  public LoadReplay Replay(IReadOnlyList<ExtraLoad> added)
  {
    var slots = Plan.Slots;
    if (added.Count == 0 || slots.Count == 0 || Plan.Rest is not { } rest) return new LoadReplay([], [], 0, 0);
    var from = added.Min(l => l.StartTime);
    var to = added.Max(l => l.EndTime);
    int first = Math.Max(0, slots.Count(s => s.Time.AddMinutes(TimeRange.SlotMinutes) <= from));
    int end = slots.Count(s => s.Time < to);
    if (end <= first) return new LoadReplay([], [], 0, 0);

    var with = _base.WithExtraLoads([.. _base.ExtraLoads, .. added]);
    var without = new List<ReplayQuarter>(end - first);
    var withLoads = new List<ReplayQuarter>(end - first);
    int e0 = slots[first].EnergyWh, e1 = e0;
    float grid = 0;
    for (int k = first; k < end; k++)
    {
      var s = slots[k];
      double fraction = _base.SlotFraction(s.Time);
      var q0 = Step(_base, s, e0, fraction);
      var q1 = Step(with, s, e1, fraction);
      without.Add(q0);
      withLoads.Add(q1);
      grid += with.GridCostOf(s.Time, q1.ImportWh, q1.ExportWh) - _base.GridCostOf(s.Time, q0.ImportWh, q0.ExportWh);
      e0 = _base.NextEnergyWh(e0, q0.ChargeWh, q0.DischargeWh);
      e1 = with.NextEnergyWh(e1, q1.ChargeWh, q1.DischargeWh);
    }
    bool chargedBefore = slots[end - 1].Mode == InverterModes.force_charge;
    float restEur = rest.At(end, chargedBefore, e1) - rest.At(end, chargedBefore, e0);
    return new LoadReplay(without, withLoads, grid, restEur);
  }

  /// <summary>A quarter of the plan at an energy: its mode — grid_only where a load from the grid holds the battery, as
  /// the simulation and the DP do (<see cref="SimulationInput.BatteryHeldForLoad"/>) — and the flows.</summary>
  private static ReplayQuarter Step(SimulationInput input, DpSlot s, int energyWh, double fraction)
  {
    var (pv, load, extra) = input.SlotEnergyWh(s.Time);
    int total = load + extra;
    var mode = s.Mode is InverterModes.normal or InverterModes.feedin_priority && input.BatteryHeldForLoad(s.Time, pv, total)
      ? InverterModes.grid_only : s.Mode;
    var (ch, dis, imp, exp) = BatteryDpPlanner.Flows(input, mode, s.Bulk, s.TargetSocPct, pv, total, energyWh, fraction);
    return new ReplayQuarter(s.Time, mode, imp, exp, ch, dis, energyWh);
  }
}
