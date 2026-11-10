using System;

namespace NetDeamon.apps.PVControl.Simulator;

/// <summary>
/// The battery top-up (<c>switch.pv_control_force_charge_at_cheapest_period</c>, one-shot): the target SoC by a deadline,
/// bought only in quarters at or under the price limit.
/// </summary>
public static class TopUp
{
  /// <summary>
  /// The deadline of a top-up armed at a time: the first time after arming that the ready-by time comes round; without
  /// one the next start of PV after arming; without PV in the forecast the end of the day after arming.
  /// </summary>
  /// <param name="armedAt">When the switch went on.</param>
  /// <param name="readyBy">Ready-by time of day; null = the next start of PV.</param>
  /// <param name="nextPvStart">The next start of relevant PV after a time (<see cref="SimulationInput.NextPVStart"/>).</param>
  /// <returns>The deadline, on the quarter grid.</returns>
  public static DateTime Deadline(DateTime armedAt, TimeOnly? readyBy, Func<DateTime, DateTime?> nextPvStart)
  {
    DateTime deadline;
    if (readyBy is { } t)
    {
      deadline = armedAt.Date + t.ToTimeSpan();
      if (deadline <= armedAt) deadline = deadline.AddDays(1);
    }
    else
      deadline = nextPvStart(armedAt) ?? armedAt.Date.AddDays(2);
    return deadline.Floor(TimeRange.SlotMinutes);
  }

  /// <summary>Whether the one-shot switch turns itself off now: armed and its deadline reached.</summary>
  /// <param name="deadline">The top-up's deadline; null when not armed.</param>
  /// <param name="now">Current time.</param>
  public static bool Expired(DateTime? deadline, DateTime now) => deadline is { } d && now >= d;
}
