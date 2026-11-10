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
    if (readyBy is not { } t)
      return (nextPvStart(armedAt) ?? armedAt.Date.AddDays(2)).Floor(TimeRange.SlotMinutes);
    // On the quarter grid first, then the next one after arming: 07:10 armed at 07:03 is 07:00 — passed, so tomorrow's.
    var deadline = (armedAt.Date + t.ToTimeSpan()).Floor(TimeRange.SlotMinutes);
    return deadline <= armedAt ? deadline.AddDays(1) : deadline;
  }

  /// <summary>
  /// When an armed top-up was armed, read back after a restart: the switch's <c>armed_at</c> attribute (PVControl writes
  /// it when the switch goes on); without it the switch's last change — which an HA restart moves (2026-11-10 09:12:
  /// off → unknown → off), so a top-up down across its deadline would have counted from the restart and run a day longer.
  /// </summary>
  /// <param name="armedAtAttribute">The attribute's text (ISO 8601); null or unreadable: none.</param>
  /// <param name="lastChanged">The switch's last change (local).</param>
  /// <param name="now">Current time (neither known).</param>
  public static DateTime ArmedAt(string? armedAtAttribute, DateTime? lastChanged, DateTime now) =>
    DateTimeOffset.TryParse(armedAtAttribute, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var a)
      ? a.LocalDateTime : lastChanged ?? now;

  /// <summary>Whether the one-shot switch turns itself off now: armed and its deadline reached.</summary>
  /// <param name="deadline">The top-up's deadline; null when not armed.</param>
  /// <param name="now">Current time.</param>
  public static bool Expired(DateTime? deadline, DateTime now) => deadline is { } d && now >= d;
}
