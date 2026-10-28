using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace NetDeamon.apps.PVControl.Simulator;

/// <summary>Why <see cref="SessionPlanner.FindBestPriceAwareStart"/> chose a start.</summary>
public enum StartChoice
{
  /// <summary>Earliest start that passes the price check.</summary>
  Earliest,
  /// <summary>A later start that passes the price check and is cheaper by at least the minimum saving.</summary>
  Cheaper,
  /// <summary>No start passes the price check before the deadline — a later start that still finishes in time and is cheaper by at least the minimum saving.</summary>
  DeadlineCheapest,
  /// <summary>No start passes the price check before the deadline — the earliest (no later start saves enough, or the session already runs).</summary>
  DeadlineEarliest,
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
/// <param name="Scan">The scan's candidates in one line (earliest / cheapest / last plan with their extra cost) — diagnostics.</param>
public readonly record struct PlannedStart(DateTime Start, StartChoice Choice, float SavingPercent = 0, DateTime? End = null, string? Scan = null);

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
  /// <param name="energyWh">Energy the session delivers (its length from <paramref name="powerW"/>, <see cref="EndForEnergy"/>).</param>
  /// <param name="powerW">The load's power, W.</param>
  /// <param name="runSim">Simulates the load from start to end.</param>
  /// <param name="baseline">Simulation without the load.</param>
  /// <param name="deadline">Session must end by then; null = none.</param>
  /// <param name="minSavingFraction">Saving a later start needs over the earliest one (0.01 = 1 %).</param>
  /// <param name="preferredStart">Last cycle's planned start; kept while it passes and is cheaper than the earliest (stops flipping).</param>
  /// <param name="minWindowMinutes">Shortest session worth starting (<see cref="StartChoice.Partial"/>); at least one slot.</param>
  /// <param name="now">Current time: a session starting in the running slot delivers its energy from now
  /// (<see cref="EndForEnergy"/>); null = from its start.</param>
  /// <returns>The start and why; null if none passes, not even shortened (no deadline).</returns>
  public static PlannedStart? FindBestPriceAwareStart(
    DateTime minStart, DateTime horizonEnd, int energyWh, int powerW,
    Func<DateTime, DateTime, SimulationResult> runSim, SimulationResult baseline,
    DateTime? deadline = null, float minSavingFraction = DefaultMinSavingFraction,
    DateTime? preferredStart = null, int minWindowMinutes = 0, DateTime? now = null)
  {
    // Every candidate delivers the same energy: its end from the energy, not from start + duration.
    int durationMinutes = Energy.Minutes(energyWh, powerW);
    DateTime End(DateTime start) => EndForEnergy(start, energyWh, powerW, now);
    // Even the earliest start can't finish in time (in the running slot it delivers from now, so its end
    // is later than start + duration): start now.
    if (deadline is { } by && End(minStart) > by)
      return new PlannedStart(minStart, StartChoice.DeadlineNow);
    var lastStart = deadline is null ? horizonEnd : deadline.Value.AddMinutes(-durationMinutes);

    // A session near the simulation's end isn't fully costed or price-checked: the part after
    // the end looks free, and the grid need it causes (e.g. the following night) falls outside
    // the horizon. Without a deadline, only compare sessions that end ConsequenceMarginHours
    // before the end; with one, the deadline decides and only the end itself caps. At least
    // minStart, for sessions longer than the horizon.
    var simEnd = baseline.Slots[^1].Time.AddMinutes(15);
    var lastFullStart = simEnd.AddMinutes(-durationMinutes - (deadline is null ? ConsequenceMarginHours * 60 : 0));
    if (lastStart > lastFullStart) lastStart = lastFullStart < minStart ? minStart : lastFullStart;

    (DateTime Start, float Cost)? earliest = null, cheapest = null, preferred = null, earliestAny = null, cheapestAny = null, preferredAny = null;
    for (var candidate = minStart; candidate <= lastStart; candidate = candidate.AddMinutes(15))
    {
      var sim = runSim(candidate, End(candidate));
      float cost = sim.NetGridCostVs(baseline);
      earliestAny ??= (candidate, cost);
      if (cheapestAny is null || cost < cheapestAny.Value.Cost) cheapestAny = (candidate, cost);
      if (candidate == preferredStart) preferredAny = (candidate, cost);
      if (!PassesPriceCheck(sim, baseline)) continue;

      earliest ??= (candidate, cost);
      if (cheapest is null || cost < cheapest.Value.Cost) cheapest = (candidate, cost);
      if (candidate == preferredStart) preferred = (candidate, cost);
    }

    string F((DateTime Start, float Cost)? x) => x is { } v ? string.Create(CultureInfo.InvariantCulture, $"{v.Start:HH:mm} {v.Cost:F2} €") : "–";
    string scan = $"earliest {F(earliestAny)} · cheapest {F(cheapestAny)} · last plan {F(preferredAny)}"
                  + (earliest is null ? " · none under the price limit" : $" · under the limit from {F(earliest)}");

    if (earliest is { } e && cheapest is { } c)
      return Choose(e, c, preferred, minSavingFraction) is { Later: true } later
        ? new PlannedStart(later.Start, StartChoice.Cheaper, later.SavingPercent, Scan: scan)
        : new PlannedStart(e.Start, StartChoice.Earliest, Scan: scan);
    // Nothing under the price limit, a deadline: the same choice among all starts that finish in time —
    // a running session isn't stopped for a later start, last cycle's plan stays unless clearly beaten
    // (live 2026-10-27/28: the cheapest start alone flipped with forecast noise between 23:00 and 01:45
    // and stopped the running EV after every 15-min min-on, ten times in one night).
    if (deadline is not null && earliestAny is { } ea && cheapestAny is { } ca)
      return Choose(ea, ca, preferredAny, minSavingFraction) is { Later: true } dl
        ? new PlannedStart(dl.Start, StartChoice.DeadlineCheapest, dl.SavingPercent, Scan: scan)
        : new PlannedStart(ea.Start, StartChoice.DeadlineEarliest, Scan: scan);

    int minMinutes = Math.Max(TimeRange.SlotMinutes, minWindowMinutes);
    for (var candidate = minStart; candidate <= lastStart; candidate = candidate.AddMinutes(15))
    {
      // Monotone: if the shortest session fails, every longer one from here does too.
      var from = candidate;
      if (minMinutes >= durationMinutes || !PassesPriceCheck(runSim(from, EndAfter(from, minMinutes, now)), baseline)) continue;
      var end = FindMaxSessionEnd(from, End(from), e => runSim(from, e), sim => PassesPriceCheck(sim, baseline));
      if (end is { } partialEnd && (partialEnd - from).TotalMinutes >= minMinutes)
        return new PlannedStart(from, StartChoice.Partial, End: partialEnd, Scan: scan);
    }
    return null;
  }

  /// <summary>
  /// The one conversion from a load's energy to its window: the end of a session that puts
  /// <paramref name="energyWh"/> in at <paramref name="powerW"/> from <paramref name="start"/> — from now if
  /// it starts in the running slot (the simulation counts only the rest of that slot; "start + duration"
  /// delivered less and looked cheaper: live 2026-10-28 21:28 "start 21:15" simulated ~77 of 90 min,
  /// 0.4 kWh less, and won the scan at the evening's highest price).
  /// </summary>
  /// <param name="start">Session start (a slot).</param>
  /// <param name="energyWh">Energy to deliver, Wh.</param>
  /// <param name="powerW">The load's power, W.</param>
  /// <param name="now">Current time; null = the start.</param>
  /// <returns>The end; the start if there's no power.</returns>
  public static DateTime EndForEnergy(DateTime start, int energyWh, int powerW, DateTime? now) =>
    ChargeFrom(start, now).AddHours(powerW > 0 ? (double)energyWh / powerW : 0);

  /// <summary>End of a window of a fixed length (a minimum window), counted from now in the running slot (<see cref="EndForEnergy"/>).</summary>
  /// <param name="start">Window start (a slot).</param>
  /// <param name="minutes">Length.</param>
  /// <param name="now">Current time; null = the start.</param>
  /// <returns>The end.</returns>
  public static DateTime EndAfter(DateTime start, int minutes, DateTime? now) => ChargeFrom(start, now).AddMinutes(minutes);

  /// <summary>When a session starting at a slot really begins: now, if the slot is already running.</summary>
  /// <param name="start">Session start (a slot).</param>
  /// <param name="now">Current time; null = the start.</param>
  private static DateTime ChargeFrom(DateTime start, DateTime? now) => now is { } n && n > start ? n : start;

  /// <summary>
  /// The start choice of a scan: the earliest unless a later start saves at least
  /// <paramref name="minSavingFraction"/> of its cost; last cycle's plan (<paramref name="preferred"/>)
  /// stays while it's cheaper than the earliest and nothing beats it by that much.
  /// </summary>
  /// <param name="earliest">Earliest start and its cost.</param>
  /// <param name="cheapest">Cheapest start and its cost.</param>
  /// <param name="preferred">Last cycle's planned start and its cost; null = none.</param>
  /// <param name="minSavingFraction">Saving a later start needs (∞: never later — a running session).</param>
  /// <returns>The start, whether it's later than the earliest, and its saving in %.</returns>
  private static (DateTime Start, bool Later, float SavingPercent) Choose(
    (DateTime Start, float Cost) earliest, (DateTime Start, float Cost) cheapest, (DateTime Start, float Cost)? preferred, float minSavingFraction)
  {
    var e = earliest;
    if (preferred is { } p && p.Start != e.Start && e.Cost > 0 && p.Cost < e.Cost
        && cheapest.Cost > p.Cost - minSavingFraction * e.Cost)
      return (p.Start, true, (e.Cost - p.Cost) / e.Cost * 100);
    float saving = e.Cost - cheapest.Cost;
    return e.Cost > 0 && saving >= minSavingFraction * e.Cost
      ? (cheapest.Start, true, saving / e.Cost * 100)
      : (e.Start, false, 0);
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
    if (maxEnd <= currentSlot) return null;

    // Fast path: the full session satisfies — most common case. The exact end (an energy's end
    // isn't on a quarter: rounding it down to one dropped up to 14 min of the energy).
    if (predicate(runSim(maxEnd)))
      return maxEnd;

    // Binary search: find rightmost whole-quarter step before maxEnd where predicate is true.
    bool aligned = currentSlot.AddMinutes(maxSteps * 15) == maxEnd;
    int lo = 1, hi = aligned ? maxSteps - 1 : maxSteps, best = -1;
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
  /// <param name="keepFromGrid">The session runs directly from the grid already: it stays so (live 2026-10-28
  /// 03:53–05:00 the choice flipped every ~10 min and the EV drew the battery a night charge had just filled).</param>
  /// <returns>The session as supplied and its simulation.</returns>
  public static (ExtraLoad Load, SimulationResult Sim) ChooseSupply(Func<ExtraLoad, SimulationResult> simulate, ExtraLoad session, SimulationResult baseline,
    bool keepFromGrid = false)
  {
    if (keepFromGrid)
    {
      var kept = session.WithFromGrid(true);
      return (kept, simulate(kept));
    }
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
      // What the sessions so far really still deliver (one that began in the running slot only from now).
      int plannedWh = (int)sessions.Sum(e => e.EnergyFromWh(baseInput.StartTime));
      int remainingWh = Math.Max(0, energyNeededWh - plannedWh);
      if (Energy.Minutes(remainingWh, template.PowerW) < 15) break;

      var input = baseInput.WithExtraLoads([.. baseInput.ExtraLoads, .. sessions]);
      var baseline = EnergySimulator.Simulate(input);

      var lastEnd = sessions[^1].EndTime;
      var from = lastEnd.Ceiling(TimeRange.SlotMinutes);

      var start = FindBestPriceAwareStart(from, horizonEnd, remainingWh, template.PowerW,
        (s, e) => ChooseSupply(l => input.SimulateWith(l), template.Between(s, e), baseline).Sim,
        baseline, deadline, now: baseInput.StartTime);
      if (start is null) break;

      var next = ChooseSupply(l => input.SimulateWith(l),
        template.Between(start.Value.Start, start.Value.End ?? EndForEnergy(start.Value.Start, remainingWh, template.PowerW, baseInput.StartTime)), baseline).Load;
      sessions.Add(next);
      planned.Add(next);
    }
    return planned;
  }
}
