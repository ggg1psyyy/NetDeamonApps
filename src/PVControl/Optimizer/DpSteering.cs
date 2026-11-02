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
    var dpInput = greedy.Input.WithBatteryLosses();   // the input the DP planned with
    var bulk = BlockPaths(dp.Slots, greedy.Input.CurrentMode, s => dpInput.ChargeLimitWh(false, s.SocPct, dpInput.SlotFraction(s.Time)), dpInput.SocToWh(1));
    return dp.Slots.Select((s, i) =>
    {
      bySlot.TryGetValue(s.Time, out var g);
      var state = new InverterState(s.Mode, ReasonOf(dp, i, dpInput, bySlot),
        g?.State.BatteryChargeEnable ?? true, g?.State.ExportAllowed ?? true) { Bulk = bulk[i] };
      return new SimulationSlot(s.Time, s.SocPct, state, g?.PVWh ?? 0, g?.LoadWh ?? 0, g?.ExtraLoadWh ?? 0,
        s.BatteryChargeWh, s.BatteryDischargeWh, s.GridImportWh, s.GridExportWh);
    }).ToList();
  }

  /// <summary>
  /// One path per grid charge: every contiguous block of force_charge quarters runs entirely as bulk or entirely as VPP
  /// (a switch between Manual Mode and VPP writes EEPROM every time; the DP chooses per quarter and may alternate). Bulk
  /// only where VPP falls short of the block's planned charge by more than a tolerance (1 % SoC; the user, 2026-11-02: no
  /// bulk if VPP is enough) — the DP's choice per quarter tips to bulk for a few Wh (2026-10-31 04:45: 1213 Wh planned,
  /// VPP 1196), the next replan takes up such a rest. A charge that runs now keeps its path for its block (as in the greedy simulation). The import cap
  /// only throttles a bulk charge's current, never switches the path.
  /// </summary>
  /// <param name="slots">The DP plan's quarters.</param>
  /// <param name="current">The mode running now.</param>
  /// <param name="vppLimitWh">VPP's charge limit in a quarter, Wh (<see cref="SimulationInput.ChargeLimitWh"/>).</param>
  /// <param name="toleranceWh">How much of the block's charge VPP may miss and still run it, Wh.</param>
  /// <returns>Per quarter: bulk.</returns>
  public static bool[] BlockPaths(IReadOnlyList<DpSlot> slots, InverterState current, Func<DpSlot, int> vppLimitWh, int toleranceWh)
  {
    var bulk = new bool[slots.Count];
    for (int i = 0; i < slots.Count;)
    {
      if (slots[i].Mode != InverterModes.force_charge) { i++; continue; }
      int end = i;
      while (end < slots.Count && slots[end].Mode == InverterModes.force_charge) end++;
      var block = Enumerable.Range(i, end - i).Select(k => slots[k]).ToList();
      bool path = i == 0 && current.Mode == InverterModes.force_charge
        ? current.Bulk
        : block.Any(s => s.Bulk) && block.Sum(s => s.BatteryChargeWh) > block.Sum(vppLimitWh) + toleranceWh;
      for (int k = i; k < end; k++) bulk[k] = path;
      i = end;
    }
    return bulk;
  }

  /// <summary>
  /// Why the DP charges or holds in a quarter, read from its own plan: a hold while a load that never draws the battery
  /// runs is <see cref="ForceChargeReasons.LoadFromGrid"/>. Otherwise the rest of the charge or hold (the quarters of
  /// the same mode from here — a single quarter of a block never decides) is taken off the battery: its charge, or for
  /// a hold the house's draw it spares. If the plan's low after it, until the battery is filled again (the next grid
  /// charge, PV raising it, or the horizon's end), would then lie under the absolute minimum:
  /// <see cref="ForceChargeReasons.AbsoluteMinimum"/>; under the preferred one:
  /// <see cref="ForceChargeReasons.PreferredMinimum"/>; else it is a price decision
  /// (<see cref="ForceChargeReasons.CheaperThanLater"/> / <see cref="ForceChargeReasons.SavedForLater"/>).
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
    if (s.Mode == InverterModes.feedin_priority) return ForceChargeReasons.FeedInFirst;
    bool HeldForLoad(DpSlot x) => x.Mode == InverterModes.grid_only && greedyBySlot.TryGetValue(x.Time, out var g)
      && input.BatteryHeldForLoad(x.Time, g.PVWh, g.LoadWh + g.ExtraLoadWh);
    if (HeldForLoad(s)) return ForceChargeReasons.LoadFromGrid;
    int addedWh = 0, end = i;
    for (; end < slots.Count && slots[end].Mode == s.Mode && !HeldForLoad(slots[end]); end++)
      addedWh += s.Mode == InverterModes.force_charge ? input.StoredWh(slots[end].BatteryChargeWh)
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
    double without = low - Soc.FromWhExact(addedWh, input.BatteryCapacityWh);
    if (without < input.AbsoluteMinSocPercent) return ForceChargeReasons.AbsoluteMinimum;
    if (without < input.GetPreferredMinSoC()) return ForceChargeReasons.PreferredMinimum;
    return s.Mode == InverterModes.force_charge ? ForceChargeReasons.CheaperThanLater : ForceChargeReasons.SavedForLater;
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
