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
  /// The DP plan as simulation slots: the DP's mode (reason <see cref="ForceChargeReasons.DpPlan"/>), path, SoC and
  /// flows; PV, load and extra loads of the quarter, and whether export and battery charging are allowed (negative
  /// prices), from the greedy simulation of the same quarter — the DP plans on its input.
  /// </summary>
  /// <param name="dp">The DP plan.</param>
  /// <param name="greedy">The greedy simulation the plan was computed on (or a later one).</param>
  public static List<SimulationSlot> ToTimeline(DpPlan dp, SimulationResult greedy)
  {
    var bySlot = greedy.Slots.ToDictionary(s => s.Time);
    return dp.Slots.Select(s =>
    {
      bySlot.TryGetValue(s.Time, out var g);
      var state = new InverterState(s.Mode, s.Mode == InverterModes.normal ? ForceChargeReasons.None : ForceChargeReasons.DpPlan,
        g?.State.BatteryChargeEnable ?? true, g?.State.ExportAllowed ?? true) { Bulk = s.Bulk };
      return new SimulationSlot(s.Time, s.SocPct, state, g?.PVWh ?? 0, g?.LoadWh ?? 0, g?.ExtraLoadWh ?? 0,
        s.BatteryChargeWh, s.BatteryDischargeWh, s.GridImportWh, s.GridExportWh);
    }).ToList();
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
