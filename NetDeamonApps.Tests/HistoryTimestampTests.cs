using NetDeamon.apps.PVControl.Predictions;
using DataLogger = PVControl.DataLogger;
using System;
using System.Collections.Generic;
using Xunit;

namespace NetDeamonApps.Tests;

/// <summary>
/// Local-time handling of the energy history DB. Two bugs: since Microsoft.Data.Sqlite 10,
/// SqliteDataReader.GetDateTime returns stored "+02:00" timestamps as UTC (DataLogger then logged
/// future, empty hours and shifted days from 2026-06-27 on); and LinqToDB's Timestamp.Hour became
/// SQLite strftime('%H'), evaluated in UTC (the load forecast was shifted by 1–2 h).
/// Expected values assume the test machine runs in the HA's time zone (Europe/Vienna-like:
/// UTC+1 winter, UTC+2 summer), as the app itself does.
/// </summary>
public class HistoryTimestampTests
{
  [Fact]
  public void DbTimestamp_WithLocalOffset_IsReadAsThatLocalTime()
  {
    var t = DataLogger.ParseDbTimestamp("2026-06-27T12:59:59+02:00");

    Assert.Equal(new DateTime(2026, 6, 27, 12, 59, 59), t);
    Assert.NotEqual(DateTimeKind.Utc, t.Kind);
  }

  [Fact]
  public void DbTimestamp_WrittenAsUtc_IsConvertedToLocal()
  {
    // The rows the broken logger wrote: same instant as 12:59:59+02:00.
    Assert.Equal(new DateTime(2026, 6, 27, 12, 59, 59), DataLogger.ParseDbTimestamp("2026-06-27T10:59:59+00:00"));
  }

  // ── DataLogger counter differences ───────────────────────────────────────────────────

  [Fact]
  public void CounterIncrease_DailyResetAtMidnight_CountsAfterTheReset()
  {
    // Day window starting with yesterday's total, reset to a small value (no exact 0 seen):
    // everything since the reset counts, i.e. the day's final reading.
    Assert.Equal(2.5f, DataLogger.CounterIncrease([12.5f, 0.1f, 1.0f, 2.5f]), 3);
  }

  [Fact]
  public void CounterIncrease_RunningTotal_IsLastMinusFirst()
  {
    // Heat pump meter 2026-10-16: 74.527 → 77.220 kWh. The old "skip to the first 0" logic logged 0.
    Assert.Equal(2.693f, DataLogger.CounterIncrease([74.527f, 75.0f, 76.1f, 77.22f]), 3);
  }

  [Fact]
  public void CounterIncrease_SmallDrop_IsNoiseNotReset() =>
    Assert.Equal(1.0f, DataLogger.CounterIncrease([10f, 9.5f, 11f]), 3); // the dip isn't counted twice

  [Fact]
  public void CounterIncrease_GlitchToZeroAndBack_IsNotAReset()
  {
    // Heat pump total, live 2026-10-12 09:09–09:17: drops to 0 and back within a second.
    float[] readings = [60.713f, 0f, 60.714f, 63.106f, 63.107f, 60.716f, 0f, 60.717f, 63.109f, 63.110f, 63.111f, 63.112f];
    Assert.Equal(63.112f - 60.713f, DataLogger.CounterIncrease(readings), 3);
  }

  [Fact]
  public void CounterIncrease_GlitchToPartialValueAndBack_IsNotAReset()
  {
    // Live 2026-10-14 20:25: 70.962 → 2.678 → 0 → 68.284 → 70.962.
    float[] readings = [70.962f, 2.678f, 0f, 68.284f, 70.962f, 70.963f, 70.964f];
    Assert.Equal(0.002f, DataLogger.CounterIncrease(readings), 3);
  }

  [Theory]
  [InlineData(new float[] { })]
  [InlineData(new float[] { 5f })]
  public void CounterIncrease_NoChange_IsZero(float[] readings) =>
    Assert.Equal(0f, DataLogger.CounterIncrease(readings));

  [Fact]
  public void Migration_RemovesDuplicateTimestamps_KeepingTheFirstRow()
  {
    using var con = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
    con.Open();
    var c = con.CreateCommand();
    c.CommandText = @"
      CREATE TABLE hourly (timestamp TEXT, houseenergy INTEGER);
      CREATE TABLE daily  (timestamp TEXT, houseenergy INTEGER);
      INSERT INTO hourly VALUES ('2025-01-16T08:59:59+01:00', 1085), ('2025-01-16T08:59:59+01:00', 9999), ('2025-01-16T09:59:59+01:00', 900);
      INSERT INTO daily  VALUES ('2025-01-16T23:59:59+01:00', 20000), ('2025-01-16T23:59:59+01:00', 20000);";
    c.ExecuteNonQuery();

    c.CommandText = DataLogger.DedupeAndIndexSql;
    c.ExecuteNonQuery();

    c.CommandText = "SELECT group_concat(houseenergy) FROM (SELECT houseenergy FROM hourly ORDER BY timestamp)";
    Assert.Equal("1085,900", c.ExecuteScalar());
    c.CommandText = "SELECT count(*) FROM daily";
    Assert.Equal(1L, c.ExecuteScalar());
    c.CommandText = "INSERT OR IGNORE INTO hourly VALUES ('2025-01-16T08:59:59+01:00', 1)";
    Assert.Equal(0, c.ExecuteNonQuery());
  }

  [Fact]
  public void HourlyAverages_UseTheLocalHourOfTheRow()
  {
    var today = new DateTime(2026, 10, 18);
    var rows = new List<(DateTime, int)>
    {
      (new DateTime(2026, 10, 17, 12, 59, 59), 1000), // covers 12:00–13:00 local
      (new DateTime(2026, 10, 17, 14, 59, 59), 3000),
    };

    var avg = HourlyWeightedAverageLoadPrediction.HourlyWeightedAverages(rows, today, 20f);

    Assert.Equal(1000, avg[12]);
    Assert.Equal(3000, avg[14]);
    Assert.Equal(0, avg[13]);
  }

  [Fact]
  public void HourlyAverages_UtcRows_CountForTheirLocalHour()
  {
    var rows = new List<(DateTime, int)> { (new DateTime(2026, 10, 17, 10, 59, 59, DateTimeKind.Utc), 500) };

    var avg = HourlyWeightedAverageLoadPrediction.HourlyWeightedAverages(rows, new DateTime(2026, 10, 18), 20f);

    Assert.Equal(500, avg[new DateTime(2026, 10, 17, 10, 59, 59, DateTimeKind.Utc).ToLocalTime().Hour]);
  }

  [Fact]
  public void HourlyAverages_RecentDaysWeighMore()
  {
    var today = new DateTime(2026, 10, 18);
    var rows = new List<(DateTime, int)>
    {
      (today.AddDays(-1).AddHours(18).AddMinutes(59), 1000),
      (today.AddDays(-200).AddHours(18).AddMinutes(59), 3000),
    };

    var avg = HourlyWeightedAverageLoadPrediction.HourlyWeightedAverages(rows, today, 20f);

    Assert.InRange(avg[18], 1000, 1010);
  }
}
