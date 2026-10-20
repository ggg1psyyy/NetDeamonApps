using System;
using System.Collections.Generic;
using System.Linq;

namespace NetDeamon.apps.HeatingTest;

public enum RunKind
{
  /// <summary>Heating-curve level lowered by the offset for the run.</summary>
  Test,
  /// <summary>Nothing changed — a normal night to compare the test nights with.</summary>
  Reference,
}

/// <summary>A test or reference run. <see cref="OriginalLevel"/> is the level to restore after a test.</summary>
public sealed record HeatingRun(long Id, RunKind Kind, DateTime Start, DateTime PlannedEnd, double OffsetK, double? OriginalLevel);

/// <summary>The user settings of the test (HA entities).</summary>
public readonly record struct TestSettings(bool Armed, TimeOnly StartTime, double DurationHours, double OffsetK);

/// <summary>A room's controller: current temperature, setpoint, and how far below it a test aborts (null = never).</summary>
public readonly record struct RoomReading(string Name, double? Current, double? Setpoint, double? AbortBelowK);

/// <summary>Live inputs of one decision.</summary>
/// <param name="HeatingMinutes24h">Minutes the heat pump heated the house in the last 24 h (from the log).</param>
/// <param name="LastKind">Kind of the last run, to alternate test and reference nights.</param>
/// <param name="LastAttemptDate">Day the start was last decided (started or skipped) — once per night.</param>
/// <param name="HeatingMode">The heat pump's operating mode, e.g. "Nur Warmwasser"; null when not configured.</param>
/// <param name="HeatingModeOn">Whether that mode includes space heating (false also when unavailable).</param>
public readonly record struct TestInput(
  DateTime Now, TestSettings Settings, HeatingRun? Active, RunKind? LastKind, DateOnly? LastAttemptDate,
  double? Outdoor, int HeatingMinutes24h, IReadOnlyList<RoomReading> Rooms,
  double MaxOutdoorAtStart, int MinHeatingMinutes24h,
  string? HeatingMode = null, bool HeatingModeOn = true);

public enum TestAction { None, Start, End, Skip }

/// <param name="Kind">For Start: which kind of run.</param>
public readonly record struct TestDecision(TestAction Action, RunKind? Kind, string Reason);

/// <summary>
/// When the heating step test starts, ends or aborts. Pure, so it's testable without Home Assistant.
/// Test and reference nights alternate (a skipped night doesn't count), each starting at the start
/// time if the heat pump is in a heating mode, actually heated these days and it's cold enough. A
/// run ends at its planned end, when disarmed or when the heat pump leaves its heating mode; a test
/// also when a room falls too far below its setpoint.
/// </summary>
public static class HeatingTestLogic
{
  /// <summary>The start is only taken within this many minutes after the start time (e.g. not after a restart at 03:00).</summary>
  public const int StartWindowMinutes = 10;

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

  /// <summary>The first room more than its abort margin below its setpoint, described; null if none.</summary>
  public static string? TooCold(IEnumerable<RoomReading> rooms)
  {
    foreach (var r in rooms)
      if (r is { AbortBelowK: { } margin, Current: { } cur, Setpoint: { } sp } && cur < sp - margin)
        return $"{r.Name} {cur:F1} °C, {sp - cur:F1} K below its setpoint {sp:F1} °C";
    return null;
  }

  public static DateTime NextStart(DateTime now, TimeOnly startTime, bool doneToday)
  {
    var today = now.Date + startTime.ToTimeSpan();
    return now < today && !doneToday ? today : today.AddDays(1);
  }

  /// <summary>The test level: original + offset, kept within the heat pump's range.</summary>
  public static double TestLevel(double original, double offsetK, double min = -15, double max = 40) =>
    Math.Round(Math.Clamp(original + offsetK, min, max), 1);
}
