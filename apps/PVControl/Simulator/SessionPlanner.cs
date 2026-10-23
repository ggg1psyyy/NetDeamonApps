using System;
using System.Collections.Generic;
using System.Linq;

namespace NetDeamon.apps.PVControl.Simulator;

/// <summary>Why <see cref="SessionPlanner.FindBestPriceAwareStart"/> chose a start.</summary>
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

/// <summary>A chosen session start.</summary>
/// <param name="Start">Start time.</param>
/// <param name="Choice">Why this start.</param>
/// <param name="SavingPercent">For <see cref="StartChoice.Cheaper"/>: saving vs the earliest start, in %.</param>
public readonly record struct PlannedStart(DateTime Start, StartChoice Choice, float SavingPercent = 0);

/// <summary>Price-aware session search for schedulable loads, over EnergySimulator (free of HA).</summary>
public static class SessionPlanner
{
  /// <summary>Saving a later start needs over the earliest one, as a fraction (keeps the plan from waiting on forecast noise).</summary>
  public const float DefaultMinSavingFraction = 0.01f;

  /// <summary>Without a deadline, a session must end this long before the simulation ends.</summary>
  public const int ConsequenceMarginHours = 12;

  /// <summary>
  /// PriorityPlus price scan: simulates the load at every 15-min start and picks one. A start passes
  /// if it needs no new grid or only at/under the price limit; the earliest passing start wins unless a
  /// later one is cheaper (<see cref="SimulationResult.NetGridCostVs"/>) by the minimum saving. A
  /// deadline beats the price limit.
  /// </summary>
  /// <param name="minStart">Earliest start.</param>
  /// <param name="horizonEnd">Latest start without a deadline (end of the published prices).</param>
  /// <param name="durationMinutes">Length of the session.</param>
  /// <param name="runSim">Simulates the load from start to end.</param>
  /// <param name="baseline">Simulation without the load.</param>
  /// <param name="deadline">Session must end by then; null = none.</param>
  /// <param name="minSavingFraction">Saving a later start needs over the earliest one (0.01 = 1 %).</param>
  /// <param name="preferredStart">Last cycle's planned start; kept while it passes and is cheaper than the earliest (stops flipping).</param>
  /// <returns>The start and why; null if none passes (no deadline).</returns>
  public static PlannedStart? FindBestPriceAwareStart(
    DateTime minStart, DateTime horizonEnd, int durationMinutes,
    Func<DateTime, DateTime, SimulationResult> runSim, SimulationResult baseline,
    DateTime? deadline = null, float minSavingFraction = DefaultMinSavingFraction,
    DateTime? preferredStart = null)
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

    (DateTime Start, float Cost)? earliest = null, cheapest = null, cheapestAny = null, preferred = null;
    for (var candidate = minStart; candidate <= lastStart; candidate = candidate.AddMinutes(15))
    {
      var sim = runSim(candidate, candidate.AddMinutes(durationMinutes));
      float cost = sim.NetGridCostVs(baseline);
      if (cheapestAny is null || cost < cheapestAny.Value.Cost) cheapestAny = (candidate, cost);
      if (sim.HasNewGridVs(baseline) && !sim.IsGridCheapVs(baseline)) continue;

      earliest ??= (candidate, cost);
      if (cheapest is null || cost < cheapest.Value.Cost) cheapest = (candidate, cost);
      if (candidate == preferredStart) preferred = (candidate, cost);
    }

    if (earliest is { } e && cheapest is { } c)
    {
      if (preferred is { } p && p.Start != e.Start && e.Cost > 0 && p.Cost < e.Cost
          && c.Cost > p.Cost - minSavingFraction * e.Cost)
        return new PlannedStart(p.Start, StartChoice.Cheaper, (e.Cost - p.Cost) / e.Cost * 100);

      float saving = e.Cost - c.Cost;
      return e.Cost > 0 && saving >= minSavingFraction * e.Cost
        ? new PlannedStart(c.Start, StartChoice.Cheaper, saving / e.Cost * 100)
        : new PlannedStart(e.Start, StartChoice.Earliest);
    }
    if (deadline is not null && cheapestAny is { } a)
      return new PlannedStart(a.Start, StartChoice.DeadlineCheapest);
    return null;
  }

  /// <summary>Preview of the sessions after the current one until the energy is covered (same price scan, each judged against a baseline with all earlier sessions).</summary>
  /// <param name="baseInput">Simulation input without the load.</param>
  /// <param name="currentSessions">The load's sessions decided this cycle; the last one is the template.</param>
  /// <param name="energyNeededWh">Energy still needed in total, Wh.</param>
  /// <param name="horizonEnd">Latest start without a deadline.</param>
  /// <param name="deadline">Sessions must end by then; null = none.</param>
  /// <param name="maxSessions">Most follow-ups to plan.</param>
  /// <returns>The follow-up sessions; never fed into the control simulation.</returns>
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
      int plannedWh = (int)sessions.Sum(e => e.EnergyWh);
      int remainingMinutes = Energy.Minutes(Math.Max(0, energyNeededWh - plannedWh), template.PowerW);
      if (remainingMinutes < 15) break;

      var input = baseInput.WithExtraLoads([.. baseInput.ExtraLoads, .. sessions]);
      var baseline = EnergySimulator.Simulate(input);

      var lastEnd = sessions[^1].EndTime;
      var from = lastEnd.Ceiling(TimeRange.SlotMinutes);

      var start = FindBestPriceAwareStart(from, horizonEnd, remainingMinutes,
        (s, e) => input.SimulateWith(template.Between(s, e)),
        baseline, deadline);
      if (start is null) break;

      var next = template.Between(start.Value.Start, start.Value.Start.AddMinutes(remainingMinutes));
      sessions.Add(next);
      planned.Add(next);
    }
    return planned;
  }
}
