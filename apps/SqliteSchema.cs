using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace NetDeamon.apps
{
  /// <summary>Schema helpers shared by the apps' own SQLite DBs (DataLogger, HeatingTest).</summary>
  public static class SqliteSchema
  {
    /// <summary>Runs SQL without a result.</summary>
    /// <param name="con">Open connection.</param>
    /// <param name="sql">One or more statements.</param>
    public static async Task ExecuteAsync(SqliteConnection con, string sql)
    {
      await using var cmd = con.CreateCommand();
      cmd.CommandText = sql;
      await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>Reads the schema version (<c>PRAGMA user_version</c>).</summary>
    /// <param name="con">Open connection.</param>
    /// <returns>The version; 0 for a new DB.</returns>
    public static async Task<int> GetVersionAsync(SqliteConnection con)
    {
      await using var cmd = con.CreateCommand();
      cmd.CommandText = "PRAGMA user_version";
      return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    /// <summary>Stores the schema version (<c>PRAGMA user_version</c>).</summary>
    /// <param name="con">Open connection.</param>
    /// <param name="version">Schema version.</param>
    public static Task SetVersionAsync(SqliteConnection con, int version) =>
      ExecuteAsync(con, $"PRAGMA user_version = {version}");

    /// <summary>Column names of a table.</summary>
    /// <param name="con">Open connection.</param>
    /// <param name="table">Table name.</param>
    /// <returns>The names, case-insensitive; empty if the table doesn't exist.</returns>
    public static async Task<IReadOnlySet<string>> ColumnsAsync(SqliteConnection con, string table)
    {
      var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
      await using var cmd = con.CreateCommand();
      cmd.CommandText = $"SELECT name FROM pragma_table_info('{table}')";
      await using var reader = await cmd.ExecuteReaderAsync();
      while (await reader.ReadAsync()) columns.Add(reader.GetString(0));
      return columns;
    }
  }
}
