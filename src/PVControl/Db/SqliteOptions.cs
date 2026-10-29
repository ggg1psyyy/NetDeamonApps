using LinqToDB;

namespace PVControl
{
  /// <summary>The one LinqToDB configuration for PVControl's SQLite DBs.</summary>
  public static class SqliteOptions
  {
    /// <summary>
    /// Options for a SQLite DB through Microsoft.Data.Sqlite, named explicitly: LinqToDB's plain <c>UseSQLite</c>
    /// looks for the driver's assembly next to linq2db.dll and otherwise takes System.Data.SQLite — as a plugin,
    /// Microsoft.Data.Sqlite lies in the host's folder, not next to linq2db in the plugin's.
    /// </summary>
    /// <param name="connectionString">SQLite connection string (<c>Data Source=…</c>).</param>
    public static DataOptions For(string connectionString) => new DataOptions().UseSQLiteMicrosoft(connectionString);

    /// <summary>Options for the SQLite DB file at <paramref name="path"/>.</summary>
    /// <param name="path">The DB file.</param>
    public static DataOptions ForFile(string path) => For($"Data Source={path}");
  }
}
