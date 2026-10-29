using NetDeamon.apps;
using PVControl;
using Xunit;

namespace NetDeamonApps.Tests;

/// <summary>
/// DataLogger schema v6 (2026-10-22): heat pump operation and the house's state per hour —
/// time-weighted from HA state history. The history queries themselves need a live HA; the
/// computations and the migration are tested here.
/// </summary>
public class DataLoggerOperationTests
{
  static readonly DateTime From = new(2026, 10, 22, 13, 0, 0);
  static readonly DateTime To = From.AddHours(1);

  [Fact]
  public void MinutesInState_FirstEntryBeforeThePeriod_CountsFromThePeriodStart()
  {
    (DateTime, string)[] changes = [(From.AddMinutes(-30), "on"), (From.AddMinutes(10), "off"), (From.AddMinutes(40), "on")];
    Assert.Equal(10 + 20, DataLogger.MinutesInState(changes, From, To, s => s == "on"), 6);
  }

  [Fact]
  public void MinutesInState_ChangeAfterThePeriod_Ignored()
  {
    (DateTime, string)[] changes = [(From, "home"), (To.AddMinutes(5), "not_home")];
    Assert.Equal(60, DataLogger.MinutesInState(changes, From, To, s => s == "home"), 6);
  }

  [Fact]
  public void TimeWeightedAverage_WeighsByDuration_SkipsUnknown()
  {
    // 15 min at 25 °C, 15 min unknown, 30 min at 31 °C → (15·25 + 30·31) / 45.
    (DateTime, double?)[] changes = [(From.AddMinutes(-5), 25), (From.AddMinutes(15), null), (From.AddMinutes(30), 31)];
    Assert.Equal((15 * 25 + 30 * 31) / 45.0, StepFunction.TimeWeightedAverage(changes, From, To)!.Value, 6);
    Assert.Null(StepFunction.TimeWeightedAverage([(From, (double?)null)], From, To));
  }

  [Fact]
  public void PrevailingState_LongestHeldState()
  {
    (DateTime, string)[] changes = [(From.AddMinutes(-60), "Nur Warmwasser"), (From.AddMinutes(20), "Warmwasser & Heizung"), (From.AddMinutes(25), "unavailable")];
    // "Nur Warmwasser" 20 min, heating 5 min, unavailable 35 min (not a state).
    Assert.Equal("Nur Warmwasser", DataLogger.PrevailingState(changes, From, To));
  }

  [Fact]
  public void StateChanges_OnlyRealChangesInsideThePeriod()
  {
    // State at the start (before), Manual→Self Use, a dropout and back (no change), Self Use→Manual, one after the period.
    (DateTime, string)[] changes = [(From.AddMinutes(-20), "Manual Mode"), (From.AddMinutes(5), "Self Use Mode"),
      (From.AddMinutes(10), "unavailable"), (From.AddMinutes(11), "Self Use Mode"), (From.AddMinutes(50), "Manual Mode"),
      (To.AddMinutes(1), "Self Use Mode")];
    Assert.Equal(2, DataLogger.StateChanges(changes, From, To));
  }

  [Fact]
  public void StateChanges_StateAtThePeriodStart_NoChange()
  {
    // HA's history starts with the state held at the period start, stamped with the start time.
    Assert.Equal(0, DataLogger.StateChanges([(From, "30.0"), (From.AddMinutes(30), "30.0")], From, To));
    Assert.Equal(1, DataLogger.StateChanges([(From, "30.0"), (From.AddMinutes(30), "0.0")], From, To));
  }

  [Fact]
  public void MigrationV9_AddsTheControlColumns()
  {
    using var con = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
    con.Open();
    var c = con.CreateCommand();
    c.CommandText = "CREATE TABLE hourly (timestamp TEXT, maxquarterimport REAL)";
    c.ExecuteNonQuery();
    c.CommandText = DataLogger.AddColumnsSql("hourly", DataLogger.ControlColumns, Columns(c, "hourly").ToHashSet());
    c.ExecuteNonQuery();
    var all = Columns(c, "hourly");
    Assert.Contains("pvcontrolmode", all);
    Assert.Contains("invertereepromwrites", all);
    Assert.Equal(8, all.Length);
  }

  static string[] Columns(Microsoft.Data.Sqlite.SqliteCommand c, string table)
  {
    c.CommandText = $"SELECT group_concat(name) FROM pragma_table_info('{table}')";
    return ((string)c.ExecuteScalar()!).Split(',');
  }

  static void Migrate(Microsoft.Data.Sqlite.SqliteCommand c, string table)
  {
    var sql = DataLogger.AddColumnsSql(table, DataLogger.OperationColumns, Columns(c, table).ToHashSet(StringComparer.OrdinalIgnoreCase));
    if (sql.Length == 0) return;
    c.CommandText = sql;
    c.ExecuteNonQuery();
  }

  [Fact]
  public void MigrationV6_AddsTheColumns()
  {
    using var con = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
    con.Open();
    var c = con.CreateCommand();
    c.CommandText = "CREATE TABLE hourly (timestamp TEXT, houseenergy INTEGER)";
    c.ExecuteNonQuery();
    Migrate(c, "hourly");
    var columns = Columns(c, "hourly");
    Assert.Contains("heatingmode", columns);
    Assert.Contains("hptargettemperature", columns);
    Assert.Equal(13, columns.Length);
  }

  [Fact]
  public void MigrationV6_Interrupted_RunsAgainWithoutDuplicateColumn()
  {
    // Columns partly added, version not saved: the next start only adds what's missing.
    using var con = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
    con.Open();
    var c = con.CreateCommand();
    c.CommandText = "CREATE TABLE hourly (timestamp TEXT, houseenergy INTEGER, heatingmode TEXT, heatingminutes REAL)";
    c.ExecuteNonQuery();
    Migrate(c, "hourly");
    Migrate(c, "hourly"); // and once more: nothing left to add
    Assert.Equal(13, Columns(c, "hourly").Length);
    Assert.Equal("", DataLogger.AddColumnsSql("hourly", DataLogger.OperationColumns, Columns(c, "hourly").ToHashSet()));
  }

  [Fact]
  public void MigrationV7_AddsTheHeatColumnsAfterV6()
  {
    using var con = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
    con.Open();
    var c = con.CreateCommand();
    c.CommandText = "CREATE TABLE daily (timestamp TEXT, houseenergy INTEGER)";
    c.ExecuteNonQuery();
    foreach (var columns in new[] { DataLogger.OperationColumns, DataLogger.HeatColumns })
    {
      c.CommandText = DataLogger.AddColumnsSql("daily", columns, Columns(c, "daily").ToHashSet());
      if (c.CommandText.Length > 0) c.ExecuteNonQuery();
    }
    var all = Columns(c, "daily");
    Assert.Contains("heatenergyheating", all);
    Assert.Contains("heatpumpelecwarmwater", all);
    Assert.Equal(17, all.Length);
  }

  // ── v1 columns: the period's value (temperatures, SoC, prices time-weighted since 2026-10-29) ──

  static readonly DateTime Hour = new(2026, 10, 29, 1, 0, 0, DateTimeKind.Utc);

  [Fact]
  public void PeriodValue_Average_IsTimeWeighted_NotTheMeanOfTheChanges()
  {
    // SoC 20 % the whole hour except a charge in the last 6 minutes, reported every minute (the busy phase):
    // the plain mean of the 7 states was 43 %, the hour really sat at ~22 %.
    var readings = new List<(DateTime, double?)> { (Hour, 20) };
    for (int m = 54; m < 60; m++) readings.Add((Hour.AddMinutes(m), 20 + (m - 53) * 8));
    float avg = DataLogger.PeriodValue(readings, DataLogger.FloatTask.average, Hour, Hour.AddHours(1))!.Value;
    Assert.InRange(avg, 21.5, 23.5);
  }

  [Fact]
  public void PeriodValue_Diff_IsTheCounterIncrease_UnknownStatesSkipped() =>
    Assert.Equal(1.5f, DataLogger.PeriodValue([(Hour, 10.0), (Hour.AddMinutes(20), null), (Hour.AddMinutes(40), 11.5)],
      DataLogger.FloatTask.diff, Hour, Hour.AddHours(1))!.Value, 3);

  [Fact]
  public void PeriodValue_NoNumber_IsUnknownNotZero() =>
    Assert.Null(DataLogger.PeriodValue([(Hour, null)], DataLogger.FloatTask.average, Hour, Hour.AddHours(1)));

  [Theory]
  [InlineData("21.5", 21.5)]
  [InlineData("-3", -3.0)]
  [InlineData("1e-05", 0.00001)]   // the old regex rejected this
  [InlineData("unavailable", null)]
  [InlineData("NaN", null)]         // a dropout value: never into a mean or a counter
  [InlineData("Infinity", null)]
  [InlineData(null, null)]
  public void Number_OneParserForEveryColumn(string? state, double? expected) =>
    Assert.Equal(expected, DataLogger.Number(state));
}
