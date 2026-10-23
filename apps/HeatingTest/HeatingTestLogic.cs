using System;
using System.Collections.Generic;
using System.Linq;

namespace NetDeamon.apps.HeatingTest;

/// <summary>Kind of a night run.</summary>
public enum RunKind
{
  /// <summary>Heating-curve level lowered by the offset for the run.</summary>
  Test,
  /// <summary>Nothing changed — a normal night to compare the test nights with.</summary>
  Reference,
}

/// <summary>A test or reference run.</summary>
/// <param name="Id">DB id.</param>
/// <param name="Kind">Test or reference.</param>
/// <param name="Start">Start time.</param>
/// <param name="PlannedEnd">Planned end.</param>
/// <param name="OffsetK">Level offset of a test, K.</param>
/// <param name="OriginalLevel">Level to restore after a test; null for a reference night.</param>
public sealed record HeatingRun(long Id, RunKind Kind, DateTime Start, DateTime PlannedEnd, double OffsetK, double? OriginalLevel);

/// <summary>The user settings of the test (HA entities).</summary>
/// <param name="Armed">Test armed.</param>
/// <param name="StartTime">Start time of day.</param>
/// <param name="DurationHours">Run length, hours.</param>
/// <param name="OffsetK">Level offset of a test, K.</param>
public readonly record struct TestSettings(bool Armed, TimeOnly StartTime, double DurationHours, double OffsetK);

/// <summary>A room controller's reading.</summary>
/// <param name="Name">Room name.</param>
/// <param name="Current">Current temperature, °C.</param>
/// <param name="Setpoint">Setpoint, °C.</param>
/// <param name="AbortBelowK">A test aborts this far below the setpoint, K; null = never.</param>
public readonly record struct RoomReading(string Name, double? Current, double? Setpoint, double? AbortBelowK);

/// <summary>Live inputs of one decision.</summary>
/// <param name="Now">Current time.</param>
/// <param name="Settings">User settings.</param>
/// <param name="Active">The run going on; null if none.</param>
/// <param name="LastKind">Kind of the last run (test and reference alternate).</param>
/// <param name="LastAttemptDate">Day the start was last decided (started or skipped) — once per night.</param>
/// <param name="Outdoor">Outside temperature, °C.</param>
/// <param name="HeatingMinutes24h">Minutes the heat pump heated in the last 24 h.</param>
/// <param name="Rooms">Room readings.</param>
/// <param name="MaxOutdoorAtStart">Warmest outside temperature for a start, °C.</param>
/// <param name="MinHeatingMinutes24h">Least heating in 24 h for a start, minutes.</param>
/// <param name="HeatingMode">Heat pump operating mode; null when not configured.</param>
/// <param name="HeatingModeOn">The mode includes space heating (false also when unavailable).</param>
public readonly record struct TestInput(
  DateTime Now, TestSettings Settings, HeatingRun? Active, RunKind? LastKind, DateOnly? LastAttemptDate,
  double? Outdoor, int HeatingMinutes24h, IReadOnlyList<RoomReading> Rooms,
  double MaxOutdoorAtStart, int MinHeatingMinutes24h,
  string? HeatingMode = null, bool HeatingModeOn = true);

/// <summary>What to do this cycle.</summary>
public enum TestAction { None, Start, End, Skip }

/// <summary>Result of <see cref="HeatingTestLogic.Decide"/>.</summary>
/// <param name="Action">What to do.</param>
/// <param name="Kind">For Start: which kind of run.</param>
/// <param name="Reason">Explanation for the status entity.</param>
public readonly record struct TestDecision(TestAction Action, RunKind? Kind, string Reason);

/// <summary>
/// When the heating step test starts, ends or aborts. Test and reference nights alternate (a skipped
/// night doesn't count), starting at the start time if the heat pump heats, heated these days and
/// it's cold enough; a run ends at its planned end, when disarmed or the heating mode is left, a test
/// also when a room gets too cold.
/// </summary>
public static class HeatingTestLogic
{
  /// <summary>The start is only taken within this many minutes after the start time (e.g. not after a restart at 03:00).</summary>
  public const int StartWindowMinutes = 10;

  /// <summary>The decision of this cycle.</summary>
  /// <param name="c">Live inputs.</param>
  /// <returns>Action, kind and reason.</returns>
  public static TestDecision Decide(TestInput c)
  {
    if (c.Active is { } run)
    {
      if (c.Now >= run.PlannedEnd)
        return new(TestAction.End, null, "completed");
      if (!c.Settings.Armed)
        return new(TestAction.End, null, "disarmed");
      if (!c.HeatingModeOn)
        return new(TestAction.End, null, $"heat pump left heating mode ({c.HeatingMode ?? "unavailable"})");
      if (run.Kind == RunKind.Test && TooCold(c.Rooms) is { } room)
        return new(TestAction.End, null, $"aborted: {room}");
      return new(TestAction.None, null, $"{run.Kind} running until {run.PlannedEnd:HH:mm}");
    }

    if (!c.Settings.Armed)
      return new(TestAction.None, null, "not armed");

    var start = c.Now.Date + c.Settings.StartTime.ToTimeSpan();
    var today = DateOnly.FromDateTime(c.Now);
    if (c.Now < start || c.Now >= start.AddMinutes(StartWindowMinutes) || c.LastAttemptDate == today)
      return new(TestAction.None, null, $"next start {NextStart(c.Now, c.Settings.StartTime, c.LastAttemptDate == today):dd.MM. HH:mm}");

    if (!c.HeatingModeOn)
      return new(TestAction.Skip, null, $"skipped: heat pump not in heating mode ({c.HeatingMode ?? "unavailable"})");
    if (c.HeatingMinutes24h < c.MinHeatingMinutes24h)
      return new(TestAction.Skip, null, $"skipped: heat pump heated only {c.HeatingMinutes24h} min in 24 h (< {c.MinHeatingMinutes24h})");
    if (c.Outdoor is not { } outdoor)
      return new(TestAction.Skip, null, "skipped: no outdoor temperature");
    if (outdoor > c.MaxOutdoorAtStart)
      return new(TestAction.Skip, null, $"skipped: {outdoor:F1} °C outside (> {c.MaxOutdoorAtStart:F1})");

    var kind = c.LastKind == RunKind.Test ? RunKind.Reference : RunKind.Test;
    return new(TestAction.Start, kind, $"{kind} night started");
  }

  /// <summary>The first room more than its abort margin below its setpoint.</summary>
  /// <param name="rooms">Room readings.</param>
  /// <returns>A description; null if none.</returns>
  public static string? TooCold(IEnumerable<RoomReading> rooms)
  {
    foreach (var r in rooms)
      if (r is { AbortBelowK: { } margin, Current: { } cur, Setpoint: { } sp } && cur < sp - margin)
        return $"{r.Name} {cur:F1} °C, {sp - cur:F1} K below its setpoint {sp:F1} °C";
    return null;
  }

  /// <summary>Next start of a run.</summary>
  /// <param name="now">Current time.</param>
  /// <param name="startTime">Start time of day.</param>
  /// <param name="doneToday">Today's start was already decided — then tomorrow.</param>
  /// <returns>The next start.</returns>
  public static DateTime NextStart(DateTime now, TimeOnly startTime, bool doneToday) =>
    doneToday ? now.Date.AddDays(1) + startTime.ToTimeSpan() : TimeRange.NextOccurrence(startTime, now);

  /// <summary>The test level: original + offset, within the heat pump's range, rounded to 0.1.</summary>
  /// <param name="original">Current level.</param>
  /// <param name="offsetK">Offset, K.</param>
  /// <param name="min">Lowest level.</param>
  /// <param name="max">Highest level.</param>
  /// <returns>The level.</returns>
  public static double TestLevel(double original, double offsetK, double min = -15, double max = 40) =>
    Math.Round(Math.Clamp(original + offsetK, min, max), 1);
}
