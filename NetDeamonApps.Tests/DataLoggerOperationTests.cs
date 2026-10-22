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
    Assert.Equal((15 * 25 + 30 * 31) / 45.0, DataLogger.TimeWeightedAverage(changes, From, To)!.Value, 6);
    Assert.Null(DataLogger.TimeWeightedAverage([(From, (double?)null)], From, To));
  }

  [Fact]
  public void PrevailingState_LongestHeldState()
  {
    (DateTime, string)[] changes = [(From.AddMinutes(-60), "Nur Warmwasser"), (From.AddMinutes(20), "Warmwasser & Heizung"), (From.AddMinutes(25), "unavailable")];
    // "Nur Warmwasser" 20 min, heating 5 min, unavailable 35 min (not a state).
    Assert.Equal("Nur Warmwasser", DataLogger.PrevailingState(changes, From, To));
  }

  static string[] Columns(Microsoft.Data.Sqlite.SqliteCommand c, string table)
  {
    c.CommandText = $"SELECT group_concat(name) FROM pragma_table_info('{table}')";
    return ((string)c.ExecuteScalar()!).Split(',');
  }

  static void Migrate(Microsoft.Data.Sqlite.SqliteCommand c, string table)
  {
    var sql = DataLogger.AddOperationColumnsSql(table, Columns(c, table).ToHashSet(StringComparer.OrdinalIgnoreCase));
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
    Assert.Equal("", DataLogger.AddOperationColumnsSql("hourly", Columns(c, "hourly").ToHashSet()));
  }
}
