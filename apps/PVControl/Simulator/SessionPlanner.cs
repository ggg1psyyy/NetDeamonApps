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
  /// <summary>No full session passes the price check — earliest shortened one that does (the cheap window is shorter than the session).</summary>
  Partial,
}

/// <summary>A chosen session start.</summary>
/// <param name="Start">Start time.</param>
/// <param name="Choice">Why this start.</param>
/// <param name="SavingPercent">For <see cref="StartChoice.Cheaper"/>: saving vs the earliest start, in %.</param>
/// <param name="End">For <see cref="StartChoice.Partial"/>: end of the shortened session; null = full length.</param>
public readonly record struct PlannedStart(DateTime Start, StartChoice Choice, float SavingPercent = 0, DateTime? End = null);

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
  /// deadline beats the price limit. If no full session passes, the earliest shortened one that does
  /// (at least <paramref name="minWindowMinutes"/>): the part beyond the cheap window would come from
  /// the battery and be bought back later above the limit.
  /// </summary>
  /// <param name="minStart">Earliest start.</param>
  /// <param name="horizonEnd">Latest start without a deadline (end of the published prices).</param>
  /// <param name="durationMinutes">Length of the session.</param>
  /// <param name="runSim">Simulates the load from start to end.</param>
  /// <param name="baseline">Simulation without the load.</param>
  /// <param name="deadline">Session must end by then; null = none.</param>
  /// <param name="minSavingFraction">Saving a later start needs over the earliest one (0.01 = 1 %).</param>
  /// <param name="preferredStart">Last cycle's planned start; kept while it passes and is cheaper than the earliest (stops flipping).</param>
  /// <param name="minWindowMinutes">Shortest session worth starting (<see cref="StartChoice.Partial"/>); at least one slot.</param>
  /// <returns>The start and why; null if none passes, not even shortened (no deadline).</returns>
  public static PlannedStart? FindBestPriceAwareStart(
    DateTime minStart, DateTime horizonEnd, int durationMinutes,
    Func<DateTime, DateTime, SimulationResult> runSim, SimulationResult baseline,
    DateTime? deadline = null, float minSavingFraction = DefaultMinSavingFraction,
    DateTime? preferredStart = null, int minWindowMinutes = 0)
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
      if (!PassesPriceCheck(sim, baseline)) continue;

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

    int minMinutes = Math.Max(TimeRange.SlotMinutes, minWindowMinutes);
    for (var candidate = minStart; candidate <= lastStart; candidate = candidate.AddMinutes(15))
    {
      // Monotone: if the shortest session fails, every longer one from here does too.
      var from = candidate;
      if (minMinutes >= durationMinutes || !PassesPriceCheck(runSim(from, from.AddMinutes(minMinutes)), baseline)) continue;
      var end = FindMaxSessionEnd(from, from.AddMinutes(durationMinutes), e => runSim(from, e), sim => PassesPriceCheck(sim, baseline));
      if (end is { } partialEnd && (partialEnd - from).TotalMinutes >= minMinutes)
        return new PlannedStart(from, StartChoice.Partial, End: partialEnd);
    }
    return null;
  }

  /// <summary>PriorityPlus price check: the load needs no new grid, or only at/under the price limit — and never over the import limit.</summary>
  /// <param name="sim">Simulation with the load.</param>
  /// <param name="baseline">Simulation without it.</param>
  private static bool PassesPriceCheck(SimulationResult sim, SimulationResult baseline) =>
    (!sim.HasNewGridVs(baseline) || sim.IsGridCheapVs(baseline)) && !sim.ExceedsImportCapVs(baseline);

  /// <summary>Longest session from the current slot whose simulation passes a predicate (binary search; the predicate must be monotone).</summary>
  /// <param name="currentSlot">Session start.</param>
  /// <param name="maxEnd">Latest end.</param>
  /// <param name="runSim">Simulates a session ending at the given time.</param>
  /// <param name="predicate">Condition on the simulation.</param>
  /// <returns>The end; null if even one slot fails.</returns>
  public static DateTime? FindMaxSessionEnd(
    DateTime currentSlot, DateTime maxEnd,
    Func<DateTime, SimulationResult> runSim,
    Func<SimulationResult, bool> predicate)
  {
    int maxSteps = (int)(maxEnd - currentSlot).TotalMinutes / 15;
    if (maxSteps <= 0) return null;

    // Fast path: longest session satisfies — most common case.
    if (predicate(runSim(currentSlot.AddMinutes(maxSteps * 15))))
      return currentSlot.AddMinutes(maxSteps * 15);

    // Binary search: find rightmost step in [1, maxSteps-1] where predicate is true.
    int lo = 1, hi = maxSteps - 1, best = -1;
    while (lo <= hi)
    {
      int mid = (lo + hi) / 2;
      if (predicate(runSim(currentSlot.AddMinutes(mid * 15))))
      { best = mid; lo = mid + 1; }
      else
      { hi = mid - 1; }
    }
    return best >= 0 ? currentSlot.AddMinutes(best * 15) : null;
  }


  /// <summary>
  /// How a session is supplied: from PV and the house battery if that needs no extra grid (like
  /// Priority), otherwise directly from the grid (<see cref="ExtraLoad.FromGrid"/>: battery held while
  /// it runs without enough PV) — grid energy doesn't take the detour through the battery.
  /// </summary>
  /// <param name="simulate">Simulates with the session added.</param>
  /// <param name="session">The session (its <see cref="ExtraLoad.FromGrid"/> is ignored).</param>
  /// <param name="baseline">Simulation without it.</param>
  /// <returns>The session as supplied and its simulation.</returns>
  public static (ExtraLoad Load, SimulationResult Sim) ChooseSupply(Func<ExtraLoad, SimulationResult> simulate, ExtraLoad session, SimulationResult baseline)
  {
    var viaBattery = session.WithFromGrid(false);
    var sim = simulate(viaBattery);
    if (!sim.HasNewGridVs(baseline)) return (viaBattery, sim);
    var direct = session.WithFromGrid(true);
    return (direct, simulate(direct));
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
        (s, e) => ChooseSupply(l => input.SimulateWith(l), template.Between(s, e), baseline).Sim,
        baseline, deadline);
      if (start is null) break;

      var next = ChooseSupply(l => input.SimulateWith(l),
        template.Between(start.Value.Start, start.Value.End ?? start.Value.Start.AddMinutes(remainingMinutes)), baseline).Load;
      sessions.Add(next);
      planned.Add(next);
    }
    return planned;
  }
}
