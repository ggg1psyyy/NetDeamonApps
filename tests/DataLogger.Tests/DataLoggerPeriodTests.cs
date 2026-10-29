using NetDeamon.apps;
using PVControl;
using Xunit;

namespace NetDeamonApps.Tests;

/// <summary>
/// DataLogger periods across the clock changes: on 2026-10-25 the summer-time 02:00 hour was never
/// logged and the winter-time one got an empty row (local clock arithmetic in the repeated hour).
/// Row stamps assume the test machine runs in the HA's time zone (Europe/Vienna-like).
/// </summary>
public class DataLoggerPeriodTests
{
  static readonly TimeZoneInfo Vienna = TimeZoneInfo.FindSystemTimeZoneById("Europe/Vienna");

  static DateTime Utc(string iso) => DateTimeOffset.Parse(iso).UtcDateTime;

  [Fact]
  public void Hourly_SummerTimeEnds_BothRepeatedHoursLogged()
  {
    var periods = DataLogger.DuePeriods(Utc("2026-10-25T01:59:59+02:00"), Utc("2026-10-25T04:00:30+01:00"), false, Vienna).ToList();

    Assert.Equal(["2026-10-25T02:59:59+02:00", "2026-10-25T02:59:59+01:00", "2026-10-25T03:59:59+01:00"], periods.Select(p => p.End.ToISO8601()));
    Assert.All(periods, p => Assert.Equal(TimeSpan.FromHours(1), p.End.AddSeconds(1) - p.Start));
    Assert.Equal("2026-10-25T02:00:00+02:00", periods[0].Start.ToISO8601());
  }

  [Fact]
  public void Hourly_SummerTimeStarts_SkippedHourHasNoRow()
  {
    var periods = DataLogger.DuePeriods(Utc("2026-03-29T01:59:59+01:00"), Utc("2026-03-29T04:00:30+02:00"), false, Vienna).ToList();

    Assert.Equal(["2026-03-29T03:59:59+02:00"], periods.Select(p => p.End.ToISO8601()));
    Assert.Equal("2026-03-29T03:00:00+02:00", periods[0].Start.ToISO8601());
  }

  [Theory]
  [InlineData("2026-10-24T23:59:59+02:00", "2026-10-25T00:00:00+02:00", 25)]
  [InlineData("2026-03-28T23:59:59+01:00", "2026-03-29T00:00:00+01:00", 23)]
  [InlineData("2026-10-23T23:59:59+02:00", "2026-10-24T00:00:00+02:00", 24)]
  public void Daily_ClockChangeDay_LocalMidnightToMidnight(string lastEnd, string start, int hours)
  {
    var day = DataLogger.DuePeriods(Utc(lastEnd), Utc(lastEnd).AddDays(3), true, Vienna).First();

    Assert.Equal(start, day.Start.ToISO8601());
    Assert.Equal(TimeSpan.FromHours(hours), day.End.AddSeconds(1) - day.Start);
    Assert.EndsWith("T23:59:59", day.End.ToISO8601()[..19]);
  }

  [Fact]
  public void EmptyTable_BackfillsFromLocalMidnight()
  {
    var now = Utc("2026-10-25T09:20:00+01:00");

    var hourly = DataLogger.DuePeriods(null, now, false, Vienna).ToList();
    var daily = DataLogger.DuePeriods(null, now, true, Vienna).ToList();

    Assert.Equal("2026-10-18T00:00:00+02:00", hourly[0].Start.ToISO8601());
    Assert.Equal("2026-10-25T08:59:59+01:00", hourly[^1].End.ToISO8601());
    Assert.Equal(7 * 24 + 1 + 9, hourly.Count); // the repeated hour twice
    Assert.Equal(["2026-10-19", "2026-10-20", "2026-10-21", "2026-10-22", "2026-10-23", "2026-10-24"],
      daily.Select(d => d.End.ToISO8601()[..10]));
  }

  [Fact]
  public void LastRowOfTheOldLogger_ContinuesWithTheNextHour()
  {
    // Rows were always hh:59:59 local: the next period follows without a gap or overlap.
    var next = DataLogger.DuePeriods(Utc("2026-10-24T13:59:59+02:00"), Utc("2026-10-24T15:00:10+02:00"), false, Vienna).Single();

    Assert.Equal("2026-10-24T14:00:00+02:00", next.Start.ToISO8601());
  }
}
