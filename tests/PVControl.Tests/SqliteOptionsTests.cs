using LinqToDB;
using LinqToDB.Data;
using PVControl;
using Xunit;

namespace NetDeamonApps.Tests;

/// <summary>
/// <see cref="SqliteOptions"/>: PVControl's DBs use Microsoft.Data.Sqlite, named explicitly. LinqToDB's plain UseSQLite
/// detects the driver by its assembly next to linq2db.dll and otherwise takes System.Data.SQLite — live 2026-10-29
/// 17:39 PVControl failed to start ("Cannot load assembly System.Data.SQLite") once Microsoft.Data.Sqlite lay only in
/// the host's folder. In the test output both lie together, so detection can't fail here: the tests pin the explicit
/// choice instead.
/// </summary>
public class SqliteOptionsTests : IDisposable
{
  private readonly string _db = Path.Combine(Path.GetTempPath(), "sqliteoptions-" + Guid.NewGuid().ToString("N") + ".db");

  public void Dispose()
  {
    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();   // the pool keeps the file open
    if (File.Exists(_db)) File.Delete(_db);
  }

  [Fact]
  public void For_NamesTheMicrosoftDriver_NoDetection() =>
    Assert.Equal(ProviderName.SQLiteMS, SqliteOptions.ForFile(_db).ConnectionOptions.ProviderName);

  [Fact]
  public void PlainUseSQLite_LeavesTheDriverToDetection() =>
    Assert.Equal(ProviderName.SQLite, new DataOptions().UseSQLite("Data Source=" + _db).ConnectionOptions.ProviderName);

  [Fact]
  public void ForFile_OpensAndQueriesTheDb()
  {
    using var db = new DataConnection(SqliteOptions.ForFile(_db));
    Assert.Equal(ProviderName.SQLiteMS, db.DataProvider.Name);
    Assert.Equal(1, db.Execute<int>("select 1"));
  }
}
