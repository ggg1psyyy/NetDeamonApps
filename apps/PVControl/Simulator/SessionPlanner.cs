using System;
using System.Collections.Generic;
using System.Linq;

namespace NetDeamon.apps.PVControl.Simulator;

/// <summary>
/// Price-aware session search for schedulable loads. Pure functions over EnergySimulator, so
/// they're testable without Home Assistant.
/// </summary>
public enum StartChoice
{
  /// <summary>Earliest start that passes the price check.</summary>
  Earliest,
  /// <summary>A later start that passes the price check and is cheaper by at least the minimum saving.</summary>
  Cheaper,
  /// <summary>No start passes the price check before the deadline — cheapest start that still finishes in time.</summary>
  DeadlineCheapest,
  /// <summary>Even starting now can't finish by the deadline — start now.</summary>
  DeadlineNow,
}

/// <param name="SavingPercent">For <see cref="StartChoice.Cheaper"/>: saving vs the earliest start, in %.</param>
public readonly record struct PlannedStart(DateTime Start, StartChoice Choice, float SavingPercent = 0);

public static class SessionPlanner
{
  /// <summary>
  /// A later start must be at least this much cheaper than the earliest one to be worth waiting
  /// for — keeps the plan from waiting on savings within forecast noise.
  /// </summary>
  public const float DefaultMinSavingFraction = 0.01f;

  /// <summary>Without a deadline, a session must end this long before the simulation ends.</summary>
  public const int ConsequenceMarginHours = 12;

  /// <summary>
  /// PriorityPlus's price scan. Simulates the load at every 15-min start from minStart — day or
  /// night, no PV-period gating. A start passes when it needs no new grid vs. baseline, or new
  /// grid only at/under the price threshold. Of the passing starts, the earliest wins unless a
  /// later one is cheaper overall (<see cref="SimulationResult.NetGridCostVs"/>) by at least
  /// <paramref name="minSavingFraction"/> — waiting for a tiny saving isn't worth leaving the
  /// load idle. Every start is re-simulated because moving it changes what NeedToCharge does.
  ///
  /// Without a deadline, starts are bounded by horizonEnd (the published price horizon); null
  /// when none passes. With a deadline, only starts that finish by it are considered, and the
  /// deadline beats the price threshold: if none passes, the cheapest start that still finishes
  /// in time is taken, and if even starting now is too late, now.
  /// </summary>
  public static PlannedStart? FindBestPriceAwareStart(
    DateTime minStart, DateTime horizonEnd, int durationMinutes,
    Func<DateTime, DateTime, SimulationResult> runSim, SimulationResult baseline,
    DateTime? deadline = null, float minSavingFraction = DefaultMinSavingFraction)
  {
    var lastStart = deadline is null ? horizonEnd : deadline.Value.AddMinutes(-durationMinutes);
    if (deadline is not null && lastStart < minStart)
      return new PlannedStart(minStart, StartChoice.DeadlineNow);

    // A session near the simulation's end isn't fully costed or price-checked: the part after
    // the end looks free, and the grid need it causes (e.g. the following night) falls outside
    // the horizon. Without a deadline, only compare sessions that end ConsequenceMarginHours
    // before the end; with one, the deadline decides and only the end itself caps. At least
    // minStart, for sessions longer than the horizon.
    var simEnd = baseline.Slots[^1].Time.AddMinutes(15);
    var lastFullStart = simEnd.AddMinutes(-durationMinutes - (deadline is null ? ConsequenceMarginHours * 60 : 0));
    if (lastStart > lastFullStart) lastStart = lastFullStart < minStart ? minStart : lastFullStart;

    (DateTime Start, float Cost)? earliest = null, cheapest = null, cheapestAny = null;
    for (var candidate = minStart; candidate <= lastStart; candidate = candidate.AddMinutes(15))
    {
      var sim = runSim(candidate, candidate.AddMinutes(durationMinutes));
      float cost = sim.NetGridCostVs(baseline);
      if (cheapestAny is null || cost < cheapestAny.Value.Cost) cheapestAny = (candidate, cost);
      if (sim.HasNewGridVs(baseline) && !sim.IsGridCheapVs(baseline)) continue;

      earliest ??= (candidate, cost);
      if (cheapest is null || cost < cheapest.Value.Cost) cheapest = (candidate, cost);
    }

    if (earliest is { } e && cheapest is { } c)
    {
      float saving = e.Cost - c.Cost;
      return e.Cost > 0 && saving >= minSavingFraction * e.Cost
        ? new PlannedStart(c.Start, StartChoice.Cheaper, saving / e.Cost * 100)
        : new PlannedStart(e.Start, StartChoice.Earliest);
    }
    if (deadline is not null && cheapestAny is { } a)
      return new PlannedStart(a.Start, StartChoice.DeadlineCheapest);
    return null;
  }

  /// <summary>
  /// Plans follow-up sessions after <paramref name="currentSessions"/> (the load's current or
  /// next session as decided this cycle) until <paramref name="energyNeededWh"/> is covered,
  /// using the same price scan (and deadline, if any). Each follow-up is judged against a
  /// baseline that already contains every earlier session, so it is only charged for the grid
  /// it adds itself. Preview only: the result is never fed into the control simulation — every
  /// cycle re-decides ChargeNow from scratch, so a planned session starts when its cycle comes.
  /// </summary>
  public static List<ExtraLoad> PlanFollowUpSessions(
    SimulationInput baseInput, IReadOnlyList<ExtraLoad> currentSessions, int energyNeededWh,
    DateTime horizonEnd, DateTime? deadline = null, int maxSessions = 3)
  {
    var planned = new List<ExtraLoad>();
    if (currentSessions.Count == 0) return planned;

    var template = currentSessions[^1];
    if (template.PowerW <= 0) return planned;

    var sessions = currentSessions.ToList();
    for (int i = 0; i < maxSessions; i++)
    {
      int plannedWh = (int)sessions.Sum(e => (e.EndTime - e.StartTime).TotalHours * e.PowerW);
      int remainingMinutes = Math.Max(0, energyNeededWh - plannedWh) * 60 / template.PowerW;
      if (remainingMinutes < 15) break;

      var input = baseInput.WithExtraLoads([.. baseInput.ExtraLoads, .. sessions]);
      var baseline = EnergySimulator.Simulate(input);

      var lastEnd = sessions[^1].EndTime;
      var from = lastEnd.RoundToNearestQuarterHour();
      if (from < lastEnd) from = from.AddMinutes(15);

      var start = FindBestPriceAwareStart(from, horizonEnd, remainingMinutes,
        (s, e) => EnergySimulator.Simulate(input.WithExtraLoads([.. input.ExtraLoads, Session(template, s, e)])),
        baseline, deadline);
      if (start is null) break;

      var next = Session(template, start.Value.Start, start.Value.Start.AddMinutes(remainingMinutes));
      sessions.Add(next);
      planned.Add(next);
    }
    return planned;
  }

  private static ExtraLoad Session(ExtraLoad template, DateTime start, DateTime end) =>
    new() { Name = template.Name, Priority = template.Priority, StartTime = start, EndTime = end, PowerW = template.PowerW };
}
