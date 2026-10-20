using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace NetDeamon.apps.HeatingTest;

/// <summary>One sample of the heat pump values (any value null when unavailable).</summary>
public sealed record HeatPumpSample(
  DateTime Time, long? RunId, string Phase,
  double? Outdoor, double? OutdoorHouse, double? Flow, double? FlowSetpoint, double? Return,
  double? Compressor, double? Power, double? Cop, double? Level, double? Slope,
  bool? Heating, bool? WarmWater, string? Mode = null);

public sealed record RoomSample(string Room, double? Current, double? Setpoint, string? Action, string? Preset);

/// <summary>
/// The test's own SQLite log, for analysis with any tool:
/// <list type="bullet">
/// <item><c>runs</c> — one row per test or reference night (start, planned and actual end, offset, original level, how it ended)</item>
/// <item><c>samples</c> — the heat pump every sample interval; <c>run_id</c>/<c>phase</c> say whether a run was going</item>
/// <item><c>room_samples</c> — every room controller at the same times</item>
/// </list>
/// Times are local ISO-8601 text with offset (e.g. <c>2026-11-20T22:00:00+01:00</c>), like the DataLogger.
/// </summary>
public sealed class HeatingTestDb(string path)
{
  // No pooling: one write a minute doesn't need it, and the file isn't held open between writes.
  readonly string _connectionString = $"Data Source={path};Pooling=False";

  const string Schema = """
    CREATE TABLE IF NOT EXISTS runs (
      id INTEGER PRIMARY KEY AUTOINCREMENT,
      kind TEXT NOT NULL,
      start TEXT NOT NULL,
      planned_end TEXT NOT NULL,
      actual_end TEXT,
      offset_k REAL NOT NULL,
      original_level REAL,
      restored INTEGER NOT NULL DEFAULT 0,
      end_reason TEXT,
      outdoor_at_start REAL);
    CREATE TABLE IF NOT EXISTS samples (
      ts TEXT NOT NULL,
      run_id INTEGER,
      phase TEXT NOT NULL,
      outdoor REAL, outdoor_house REAL,
      flow REAL, flow_setpoint REAL, return_temp REAL,
      compressor REAL, power REAL, cop REAL,
      level REAL, slope REAL,
      heating INTEGER, warmwater INTEGER, hp_mode TEXT);
    CREATE INDEX IF NOT EXISTS ix_samples_ts ON samples(ts);
    CREATE TABLE IF NOT EXISTS room_samples (
      ts TEXT NOT NULL,
      room TEXT NOT NULL,
      current REAL, setpoint REAL, action TEXT, preset TEXT);
    CREATE INDEX IF NOT EXISTS ix_room_samples_ts ON room_samples(ts);
    """;

  public static string Ts(DateTime t) => t.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);
  static DateTime ParseTs(string s) => DateTimeOffset.Parse(s, CultureInfo.InvariantCulture).LocalDateTime;

  async Task<SqliteConnection> OpenAsync()
  {
    var con = new SqliteConnection(_connectionString);
    await con.OpenAsync();
    return con;
  }

  /// <summary>Schema version written to <c>PRAGMA user_version</c>; raise it with every migration step.</summary>
  public const int SchemaVersion = 1;

  /// <summary>Creates the tables, then brings an older DB (CREATE IF NOT EXISTS leaves existing tables alone) up to <see cref="SchemaVersion"/>.</summary>
  public async Task InitializeAsync()
  {
    await using var con = await OpenAsync();
    await Execute(con, Schema);
    int version = Convert.ToInt32(await Scalar(con, "PRAGMA user_version"));

    // v1: samples.hp_mode — the first deployed version (2026-10-20, user_version 0) had no such column.
    if (version < 1 && !await HasColumn(con, "samples", "hp_mode"))
      await Execute(con, "ALTER TABLE samples ADD COLUMN hp_mode TEXT");

    if (version < SchemaVersion)
      await Execute(con, $"PRAGMA user_version = {SchemaVersion}");
  }

  static async Task Execute(SqliteConnection con, string sql)
  {
    await using var cmd = con.CreateCommand();
    cmd.CommandText = sql;
    await cmd.ExecuteNonQueryAsync();
  }

  static async Task<object?> Scalar(SqliteConnection con, string sql)
  {
    await using var cmd = con.CreateCommand();
    cmd.CommandText = sql;
    return await cmd.ExecuteScalarAsync();
  }

  static async Task<bool> HasColumn(SqliteConnection con, string table, string column)
  {
    await using var cmd = con.CreateCommand();
    cmd.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = $col";
    cmd.Parameters.AddWithValue("$col", column);
    return (long)(await cmd.ExecuteScalarAsync())! > 0;
  }

  public async Task<long> StartRunAsync(RunKind kind, DateTime start, DateTime plannedEnd, double offsetK, double? originalLevel, double? outdoor)
  {
    await using var con = await OpenAsync();
    await using var cmd = con.CreateCommand();
    cmd.CommandText = """
      INSERT INTO runs (kind, start, planned_end, offset_k, original_level, outdoor_at_start)
      VALUES ($kind, $start, $end, $offset, $orig, $outdoor);
      SELECT last_insert_rowid();
      """;
    cmd.Parameters.AddWithValue("$kind", kind.ToString());
    cmd.Parameters.AddWithValue("$start", Ts(start));
    cmd.Parameters.AddWithValue("$end", Ts(plannedEnd));
    cmd.Parameters.AddWithValue("$offset", offsetK);
    cmd.Parameters.AddWithValue("$orig", (object?)originalLevel ?? DBNull.Value);
    cmd.Parameters.AddWithValue("$outdoor", (object?)outdoor ?? DBNull.Value);
    return (long)(await cmd.ExecuteScalarAsync())!;
  }

  public async Task EndRunAsync(long id, DateTime end, string reason)
  {
    await using var con = await OpenAsync();
    await using var cmd = con.CreateCommand();
    cmd.CommandText = "UPDATE runs SET actual_end = $end, end_reason = $reason WHERE id = $id";
    cmd.Parameters.AddWithValue("$end", Ts(end));
    cmd.Parameters.AddWithValue("$reason", reason);
    cmd.Parameters.AddWithValue("$id", id);
    await cmd.ExecuteNonQueryAsync();
  }

  public async Task MarkRestoredAsync(long id)
  {
    await using var con = await OpenAsync();
    await using var cmd = con.CreateCommand();
    cmd.CommandText = "UPDATE runs SET restored = 1 WHERE id = $id";
    cmd.Parameters.AddWithValue("$id", id);
    await cmd.ExecuteNonQueryAsync();
  }

  /// <summary>The run still going (no actual end), if any — after a restart the test continues or ends from here.</summary>
  public async Task<HeatingRun?> ActiveRunAsync() =>
    await ReadRunAsync("SELECT id, kind, start, planned_end, offset_k, original_level FROM runs WHERE actual_end IS NULL ORDER BY id DESC LIMIT 1");

  /// <summary>An ended test whose original level isn't confirmed restored yet.</summary>
  public async Task<HeatingRun?> UnrestoredTestAsync() =>
    await ReadRunAsync("SELECT id, kind, start, planned_end, offset_k, original_level FROM runs WHERE actual_end IS NOT NULL AND kind = 'Test' AND restored = 0 AND original_level IS NOT NULL ORDER BY id DESC LIMIT 1");

  public async Task<HeatingRun?> LastRunAsync() =>
    await ReadRunAsync("SELECT id, kind, start, planned_end, offset_k, original_level FROM runs ORDER BY id DESC LIMIT 1");

  async Task<HeatingRun?> ReadRunAsync(string sql)
  {
    await using var con = await OpenAsync();
    await using var cmd = con.CreateCommand();
    cmd.CommandText = sql;
    await using var r = await cmd.ExecuteReaderAsync();
    if (!await r.ReadAsync()) return null;
    return new HeatingRun(r.GetInt64(0), Enum.Parse<RunKind>(r.GetString(1)), ParseTs(r.GetString(2)), ParseTs(r.GetString(3)),
      r.GetDouble(4), r.IsDBNull(5) ? null : r.GetDouble(5));
  }

  /// <summary>The last runs with how they ended, newest first, for the status entity.</summary>
  public async Task<List<(HeatingRun Run, DateTime? End, string? Reason)>> RecentRunsAsync(int count)
  {
    var list = new List<(HeatingRun, DateTime?, string?)>();
    await using var con = await OpenAsync();
    await using var cmd = con.CreateCommand();
    cmd.CommandText = "SELECT id, kind, start, planned_end, offset_k, original_level, actual_end, end_reason FROM runs ORDER BY id DESC LIMIT $n";
    cmd.Parameters.AddWithValue("$n", count);
    await using var r = await cmd.ExecuteReaderAsync();
    while (await r.ReadAsync())
      list.Add((new HeatingRun(r.GetInt64(0), Enum.Parse<RunKind>(r.GetString(1)), ParseTs(r.GetString(2)), ParseTs(r.GetString(3)),
        r.GetDouble(4), r.IsDBNull(5) ? null : r.GetDouble(5)),
        r.IsDBNull(6) ? null : ParseTs(r.GetString(6)), r.IsDBNull(7) ? null : r.GetString(7)));
    return list;
  }

  public async Task InsertSampleAsync(HeatPumpSample s, IReadOnlyList<RoomSample> rooms)
  {
    await using var con = await OpenAsync();
    await using var tx = (SqliteTransaction)await con.BeginTransactionAsync();
    await using (var cmd = con.CreateCommand())
    {
      cmd.Transaction = tx;
      cmd.CommandText = """
        INSERT INTO samples (ts, run_id, phase, outdoor, outdoor_house, flow, flow_setpoint, return_temp,
                             compressor, power, cop, level, slope, heating, warmwater, hp_mode)
        VALUES ($ts, $run, $phase, $out, $outh, $flow, $flowsp, $ret, $comp, $pow, $cop, $level, $slope, $heat, $ww, $mode)
        """;
      cmd.Parameters.AddWithValue("$ts", Ts(s.Time));
      cmd.Parameters.AddWithValue("$run", (object?)s.RunId ?? DBNull.Value);
      cmd.Parameters.AddWithValue("$phase", s.Phase);
      void Add(string name, object? value) => cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
      Add("$out", s.Outdoor); Add("$outh", s.OutdoorHouse); Add("$flow", s.Flow); Add("$flowsp", s.FlowSetpoint);
      Add("$ret", s.Return); Add("$comp", s.Compressor); Add("$pow", s.Power); Add("$cop", s.Cop);
      Add("$level", s.Level); Add("$slope", s.Slope);
      Add("$heat", s.Heating is { } h ? (h ? 1 : 0) : null); Add("$ww", s.WarmWater is { } w ? (w ? 1 : 0) : null);
      Add("$mode", s.Mode);
      await cmd.ExecuteNonQueryAsync();
    }
    foreach (var room in rooms)
    {
      await using var cmd = con.CreateCommand();
      cmd.Transaction = tx;
      cmd.CommandText = "INSERT INTO room_samples (ts, room, current, setpoint, action, preset) VALUES ($ts, $room, $cur, $sp, $act, $preset)";
      cmd.Parameters.AddWithValue("$ts", Ts(s.Time));
      cmd.Parameters.AddWithValue("$room", room.Room);
      cmd.Parameters.AddWithValue("$cur", (object?)room.Current ?? DBNull.Value);
      cmd.Parameters.AddWithValue("$sp", (object?)room.Setpoint ?? DBNull.Value);
      cmd.Parameters.AddWithValue("$act", (object?)room.Action ?? DBNull.Value);
      cmd.Parameters.AddWithValue("$preset", (object?)room.Preset ?? DBNull.Value);
      await cmd.ExecuteNonQueryAsync();
    }
    await tx.CommitAsync();
  }

  /// <summary>
  /// Minutes the heat pump heated the house (compressor running, heating flag on) between
  /// <paramref name="since"/> and now, counting each sample as <paramref name="sampleMinutes"/>.
  /// </summary>
  public async Task<int> HeatingMinutesSinceAsync(DateTime since, double sampleMinutes)
  {
    await using var con = await OpenAsync();
    await using var cmd = con.CreateCommand();
    // ts is ISO text with a fixed format, so text order is time order (within one UTC offset;
    // around a DST switch the window is off by an hour at most — fine for "did it heat today").
    cmd.CommandText = "SELECT COUNT(*) FROM samples WHERE ts >= $since AND heating = 1 AND compressor > 0";
    cmd.Parameters.AddWithValue("$since", Ts(since));
    long n = (long)(await cmd.ExecuteScalarAsync())!;
    return (int)Math.Round(n * sampleMinutes);
  }
}
