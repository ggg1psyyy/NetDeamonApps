using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace NetDeamon.apps.HeatingTest;

/// <summary>One sample of the heat pump (any value null when unavailable).</summary>
/// <param name="Time">Sample time.</param>
/// <param name="RunId">Run going on; null if none.</param>
/// <param name="Phase">Run kind ("Test", "Reference") or "Normal".</param>
/// <param name="Outdoor">Heat pump's outside temperature, °C.</param>
/// <param name="OutdoorHouse">House's outside temperature, °C.</param>
/// <param name="Flow">Flow temperature, °C.</param>
/// <param name="FlowSetpoint">Flow setpoint, °C.</param>
/// <param name="Return">Return temperature, °C.</param>
/// <param name="Compressor">Compressor output.</param>
/// <param name="Power">Electrical power, W.</param>
/// <param name="Cop">COP.</param>
/// <param name="Level">Heating-curve level.</param>
/// <param name="Slope">Heating-curve slope.</param>
/// <param name="Heating">Space heating flag.</param>
/// <param name="WarmWater">Warm-water flag.</param>
/// <param name="Mode">Operating mode.</param>
public sealed record HeatPumpSample(
  DateTime Time, long? RunId, string Phase,
  double? Outdoor, double? OutdoorHouse, double? Flow, double? FlowSetpoint, double? Return,
  double? Compressor, double? Power, double? Cop, double? Level, double? Slope,
  bool? Heating, bool? WarmWater, string? Mode = null);

/// <summary>One sample of a room controller.</summary>
/// <param name="Room">Room name.</param>
/// <param name="Current">Current temperature, °C.</param>
/// <param name="Setpoint">Setpoint, °C.</param>
/// <param name="Action">hvac_action.</param>
/// <param name="Preset">Preset mode.</param>
public sealed record RoomSample(string Room, double? Current, double? Setpoint, string? Action, string? Preset);

/// <summary>The test's own SQLite log (runs, samples, room_samples); times as local ISO 8601 with offset, like the DataLogger.</summary>
/// <param name="path">DB file path.</param>
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

  /// <summary>A time as stored (<see cref="Extensions.ToISO8601"/>).</summary>
  /// <param name="t">Local time.</param>
  public static string Ts(DateTime t) => t.ToISO8601();
  /// <summary>A stored time as local time (<see cref="Extensions.FromISO8601"/>).</summary>
  /// <param name="s">Stored text.</param>
  static DateTime ParseTs(string s) => Extensions.FromISO8601(s);

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
    await SqliteSchema.ExecuteAsync(con, Schema);
    int version = await SqliteSchema.GetVersionAsync(con);

    // v1: samples.hp_mode — the first deployed version (2026-10-20, user_version 0) had no such column.
    if (version < 1 && !(await SqliteSchema.ColumnsAsync(con, "samples")).Contains("hp_mode"))
      await SqliteSchema.ExecuteAsync(con, "ALTER TABLE samples ADD COLUMN hp_mode TEXT");

    if (version < SchemaVersion)
      await SqliteSchema.SetVersionAsync(con, SchemaVersion);
  }




  /// <summary>Records a run start.</summary>
  /// <param name="kind">Test or reference.</param>
  /// <param name="start">Start time.</param>
  /// <param name="plannedEnd">Planned end.</param>
  /// <param name="offsetK">Level offset, K.</param>
  /// <param name="originalLevel">Level to restore; null for a reference night.</param>
  /// <param name="outdoor">Outside temperature at the start, °C.</param>
  /// <returns>The run's id.</returns>
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

  /// <summary>Records a run end.</summary>
  /// <param name="id">Run id.</param>
  /// <param name="end">End time.</param>
  /// <param name="reason">How it ended.</param>
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

  /// <summary>Marks a test's original level as restored.</summary>
  /// <param name="id">Run id.</param>
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

  /// <summary>The latest run.</summary>
  /// <returns>The run; null if none.</returns>
  public async Task<HeatingRun?> LastRunAsync() =>
    await ReadRunAsync("SELECT id, kind, start, planned_end, offset_k, original_level FROM runs ORDER BY id DESC LIMIT 1");

  /// <summary>Reads the first run a query returns.</summary>
  /// <param name="sql">Query selecting id, kind, start, planned_end, offset_k, original_level.</param>
  /// <returns>The run; null if none.</returns>
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

  /// <summary>The last runs with how they ended, newest first (status entity).</summary>
  /// <param name="count">How many.</param>
  /// <returns>(run, actual end, end reason).</returns>
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

  /// <summary>Stores one heat pump sample and the room samples of the same time.</summary>
  /// <param name="s">Heat pump sample.</param>
  /// <param name="rooms">Room samples.</param>
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

  /// <summary>Minutes the heat pump heated the house (compressor running, heating flag on) since a time.</summary>
  /// <param name="since">Start of the count.</param>
  /// <param name="sampleMinutes">Minutes one sample stands for.</param>
  /// <returns>Minutes.</returns>
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
