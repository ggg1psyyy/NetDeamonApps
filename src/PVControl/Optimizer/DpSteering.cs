using NetDeamon.apps.PVControl.Simulator;
using System;
using System.Collections.Generic;
using System.Linq;

namespace NetDeamon.apps.PVControl.Optimizer;

/// <summary>Which plan steers the battery (select.pv_control_planner).</summary>
public enum Planner
{
  /// <summary>The greedy simulation (EnergySimulator's rules).</summary>
  greedy,
  /// <summary>The DP plan (<see cref="BatteryDpPlanner"/>), while it is fresh; otherwise greedy.</summary>
  dp,
}

/// <summary>
/// The DP plan as the steering plan: the inverter command (the current quarter, the next grid charge) is derived from a
/// list of simulation slots — the same chain (override, minimum run, import cap, the HA reconciler) acts on either plan.
/// A DP plan older than <see cref="MaxPlanAge"/> (or none) never steers: the greedy plan takes over.
/// </summary>
public static class DpSteering
{
  /// <summary>A DP plan older than this doesn't steer (the DP runs every minute while it does).</summary>
  public static readonly TimeSpan MaxPlanAge = TimeSpan.FromMinutes(10);

  /// <summary>
  /// The DP plan as simulation slots: the DP's mode (its reason by <see cref="ReasonOf"/>), path, SoC and flows; PV,
  /// load and extra loads of the quarter, and whether export and battery charging are allowed (negative prices), from
  /// the greedy simulation of the same quarter — the DP plans on its input.
  /// </summary>
  /// <param name="dp">The DP plan.</param>
  /// <param name="greedy">The greedy simulation the plan was computed on (or a later one).</param>
  public static List<SimulationSlot> ToTimeline(DpPlan dp, SimulationResult greedy)
  {
    var bySlot = greedy.Slots.ToDictionary(s => s.Time);
    return dp.Slots.Select((s, i) =>
    {
      bySlot.TryGetValue(s.Time, out var g);
      var state = new InverterState(s.Mode, ReasonOf(dp, i, greedy.Input, bySlot),
        g?.State.BatteryChargeEnable ?? true, g?.State.ExportAllowed ?? true) { Bulk = s.Bulk };
      return new SimulationSlot(s.Time, s.SocPct, state, g?.PVWh ?? 0, g?.LoadWh ?? 0, g?.ExtraLoadWh ?? 0,
        s.BatteryChargeWh, s.BatteryDischargeWh, s.GridImportWh, s.GridExportWh);
    }).ToList();
  }

  /// <summary>
  /// Why the DP charges or holds in a quarter, read from its own plan: a hold while a load that never draws the battery
  /// runs is <see cref="ForceChargeReasons.LoadFromGrid"/>. Otherwise the rest of the charge or hold (the quarters of
  /// the same mode from here — a single quarter of a block never decides) is taken off the battery: its charge, or for
  /// a hold the house's draw it spares. If the plan's low after it, until the battery is filled again (the next grid
  /// charge, PV raising it, or the horizon's end), would then lie under the absolute minimum:
  /// <see cref="ForceChargeReasons.DpAbsoluteMinimum"/>; under the preferred one:
  /// <see cref="ForceChargeReasons.DpPreferredMinimum"/>; else it is a price decision
  /// (<see cref="ForceChargeReasons.DpCheaperThanLater"/> / <see cref="ForceChargeReasons.DpSavedForLater"/>).
  /// </summary>
  /// <param name="dp">The DP plan.</param>
  /// <param name="i">The quarter's index in it.</param>
  /// <param name="input">The input it was planned on.</param>
  /// <param name="greedyBySlot">The greedy simulation's quarters by start (their PV and load).</param>
  public static ForceChargeReasons ReasonOf(DpPlan dp, int i, SimulationInput input, IReadOnlyDictionary<DateTime, SimulationSlot> greedyBySlot)
  {
    var slots = dp.Slots;
    var s = slots[i];
    if (s.Mode == InverterModes.normal) return ForceChargeReasons.None;
    bool HeldForLoad(DpSlot x) => x.Mode == InverterModes.grid_only && greedyBySlot.TryGetValue(x.Time, out var g)
      && input.BatteryHeldForLoad(x.Time, g.PVWh, g.LoadWh + g.ExtraLoadWh);
    if (HeldForLoad(s)) return ForceChargeReasons.LoadFromGrid;
    int addedWh = 0, end = i;
    for (; end < slots.Count && slots[end].Mode == s.Mode && !HeldForLoad(slots[end]); end++)
      addedWh += s.Mode == InverterModes.force_charge ? slots[end].BatteryChargeWh
        : greedyBySlot.TryGetValue(slots[end].Time, out var g) ? Math.Max(0, g.LoadWh - g.PVWh) : 0;
    int low = dp.EndSocPct;   // the horizon's end, if nothing fills the battery before
    for (int j = end; j < slots.Count; j++)
    {
      bool refilled = slots[j].Mode == InverterModes.force_charge
        || j > end && slots[j].SocPct > slots[j - 1].SocPct && slots[j - 1].Mode != InverterModes.force_charge;
      if (refilled)
      {
        low = slots[j].SocPct;
        for (int k = end; k < j; k++) low = Math.Min(low, slots[k].SocPct);
        break;
      }
      low = Math.Min(low, slots[j].SocPct);
    }
    double without = low - addedWh * 100.0 / input.BatteryCapacityWh;
    if (without < input.AbsoluteMinSocPercent) return ForceChargeReasons.DpAbsoluteMinimum;
    if (without < input.GetPreferredMinSoC()) return ForceChargeReasons.DpPreferredMinimum;
    return s.Mode == InverterModes.force_charge ? ForceChargeReasons.DpCheaperThanLater : ForceChargeReasons.DpSavedForLater;
  }

  /// <summary>Which plan steers now.</summary>
  /// <param name="selected">The planner the user selected.</param>
  /// <param name="plan">The last DP plan; null if none yet.</param>
  /// <param name="computedAt">When it was computed.</param>
  /// <param name="now">Current time.</param>
  /// <param name="greedy">This cycle's greedy simulation.</param>
  /// <returns>The DP timeline to steer by (null: the greedy plan steers), the planner in effect, and whether the DP
  /// was selected but couldn't steer (no plan, or older than <see cref="MaxPlanAge"/>).</returns>
  public static (IReadOnlyList<SimulationSlot>? Timeline, Planner Effective, bool Stale) Choose(
    Planner selected, DpPlan? plan, DateTime? computedAt, DateTime now, SimulationResult? greedy)
  {
    if (selected != Planner.dp) return (null, Planner.greedy, false);
    if (plan is null || greedy is null || computedAt is not { } at || now - at > MaxPlanAge)
      return (null, Planner.greedy, true);
    return (ToTimeline(plan, greedy), Planner.dp, false);
  }
}
