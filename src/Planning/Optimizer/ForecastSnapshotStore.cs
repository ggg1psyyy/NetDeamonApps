using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace NetDeamon.apps.PVControl.Optimizer;

/// <summary>
/// The stored planning situations (table <c>forecast</c> of PVControl's dp_shadow.db: one <see cref="ForecastSnapshot"/>
/// per quarter). PVControl's DP shadow writes them; a replay, a test or another plugin (the tariff advisor) reads them —
/// the one place that knows the table.
/// </summary>
public static class ForecastSnapshotStore
{
  /// <summary>How long a reader waits while the writer holds the DB, s (it writes once per quarter, briefly).</summary>
  private const int BusyTimeoutSeconds = 10;

  /// <summary>Stores a quarter's snapshot (replaces one stored for the same quarter).</summary>
  /// <param name="con">An open connection to the DB.</param>
  /// <param name="quarter">The quarter.</param>
  /// <param name="snapshot">The planning situation.</param>
  public static async Task WriteAsync(SqliteConnection con, DateTime quarter, ForecastSnapshot snapshot)
  {
    await SqliteSchema.ExecuteAsync(con, "CREATE TABLE IF NOT EXISTS forecast (quarter TEXT PRIMARY KEY, snapshot BLOB)");
    await using var cmd = con.CreateCommand();
    cmd.CommandText = "INSERT OR REPLACE INTO forecast VALUES ($q, $s)";
    cmd.Parameters.AddWithValue("$q", quarter.ToISO8601());
    cmd.Parameters.AddWithValue("$s", snapshot.ToBytes());
    await cmd.ExecuteNonQueryAsync();
  }

  /// <summary>All stored snapshots, by quarter (for a replay).</summary>
  /// <param name="dbPath">The DB.</param>
  public static Task<List<(DateTime Quarter, ForecastSnapshot Snapshot)>> ReadAllAsync(string dbPath) =>
    ReadAsync(dbPath, "SELECT quarter, snapshot FROM forecast ORDER BY quarter");

  /// <summary>The latest stored snapshot.</summary>
  /// <param name="dbPath">The DB (opened read-only).</param>
  /// <returns>The quarter and its snapshot; null if the DB or its table has none.</returns>
  public static async Task<(DateTime Quarter, ForecastSnapshot Snapshot)?> ReadLatestAsync(string dbPath)
  {
    var rows = await ReadAsync(dbPath, "SELECT quarter, snapshot FROM forecast ORDER BY quarter DESC LIMIT 1");
    return rows.Count == 0 ? null : rows[0];
  }

  /// <summary>Reads snapshots read-only, waiting while the writer holds the DB.</summary>
  private static async Task<List<(DateTime, ForecastSnapshot)>> ReadAsync(string dbPath, string sql)
  {
    var result = new List<(DateTime, ForecastSnapshot)>();
    if (!System.IO.File.Exists(dbPath)) return result;
    await using var con = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly;Pooling=False;Default Timeout={BusyTimeoutSeconds}");
    await con.OpenAsync();
    await using (var check = con.CreateCommand())
    {
      check.CommandText = "SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name = 'forecast'";
      if (Convert.ToInt64(await check.ExecuteScalarAsync()) == 0) return result;
    }
    await using var cmd = con.CreateCommand();
    cmd.CommandText = sql;
    await using var rd = await cmd.ExecuteReaderAsync();
    while (await rd.ReadAsync())
      result.Add((Extensions.FromISO8601(rd.GetString(0)), ForecastSnapshot.FromBytes((byte[])rd[1])));
    return result;
  }
}
