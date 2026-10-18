using System;
using System.Collections.Generic;
using System.Linq;

namespace NetDeamon.apps.PVControl.Simulator;

/// <summary>
/// Price-aware session search for schedulable loads. Pure functions over EnergySimulator, so
/// they're testable without Home Assistant.
/// </summary>
public static class SessionPlanner
{
  /// <summary>
  /// PriorityPlus's price-only scan. Steps FORWARD from minStart in 15-min steps — day or night,
  /// no PV-period gating — for the EARLIEST candidate where running the load for durationMinutes
  /// either needs no new grid at all vs. baseline, or needs new grid only at a price at/under
  /// the configured threshold. Delaying the candidate changes what the simulator's NeedToCharge
  /// logic computes for it (a different deadline, possibly a cheaper slot), so every candidate
  /// is re-simulated rather than checked once. Bounded by horizonEnd (the published price
  /// horizon): past it there's nothing to price, so the scan gives up and returns null.
  /// </summary>
  public static DateTime? FindEarliestPriceAwareStart(
    DateTime minStart, DateTime horizonEnd, int durationMinutes,
    Func<DateTime, DateTime, SimulationResult> runSim, SimulationResult baseline)
  {
    for (var candidate = minStart; candidate <= horizonEnd; candidate = candidate.AddMinutes(15))
    {
      var sim = runSim(candidate, candidate.AddMinutes(durationMinutes));
      if (!sim.HasNewGridVs(baseline) || sim.IsGridCheapVs(baseline))
        return candidate;
    }
    return null;
  }

  /// <summary>
  /// Plans follow-up sessions after <paramref name="currentSessions"/> (the load's current or
  /// next session as decided this cycle) until <paramref name="energyNeededWh"/> is covered,
  /// using the same price-only scan. Each follow-up is judged against a baseline that already
  /// contains every earlier session, so it is only charged for the grid it adds itself.
  /// Preview only: the result is never fed into the control simulation — every cycle re-decides
  /// ChargeNow from scratch, so a planned session actually starts when its cycle comes.
  /// </summary>
  public static List<ExtraLoad> PlanFollowUpSessions(
    SimulationInput baseInput, IReadOnlyList<ExtraLoad> currentSessions, int energyNeededWh,
    DateTime horizonEnd, int maxSessions = 3)
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

      var start = FindEarliestPriceAwareStart(from, horizonEnd, remainingMinutes,
        (s, e) => EnergySimulator.Simulate(input.WithExtraLoads([.. input.ExtraLoads, Session(template, s, e)])),
        baseline);
      if (start is null) break;

      var next = Session(template, start.Value, start.Value.AddMinutes(remainingMinutes));
      sessions.Add(next);
      planned.Add(next);
    }
    return planned;
  }

  private static ExtraLoad Session(ExtraLoad template, DateTime start, DateTime end) =>
    new() { Name = template.Name, Priority = template.Priority, StartTime = start, EndTime = end, PowerW = template.PowerW };
}
