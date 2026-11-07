using LinqToDB;
using LinqToDB.Data;
using PVControl;
using Xunit;

namespace NetDeamonApps.Tests;

/// <summary>
/// <see cref="EnergyCostDb"/>: the cost log's timestamps stay text without trailing zero fractions — linq2db 6 wrote
/// "2026-11-07 15:00:00.000" where every row before had "2026-11-07 14:45:00" (live 2026-11-07 15:00).
/// </summary>
public class EnergyCostDbTests : IDisposable
{
  private readonly string _db = Path.Combine(Path.GetTempPath(), "energycosts-" + Guid.NewGuid().ToString("N") + ".db");

  public void Dispose()
  {
    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();   // the pool keeps the file open
    if (File.Exists(_db)) File.Delete(_db);
  }

  List<string> RawTimestamps(EnergyCostDb db)
  {
    var c = (Microsoft.Data.Sqlite.SqliteConnection)db.OpenDbConnection();
    using var cmd = c.CreateCommand();
    cmd.CommandText = "select timestamp from costs order by rowid";
    using var r = cmd.ExecuteReader();
    var list = new List<string>();
    while (r.Read()) list.Add(r.GetString(0));
    return list;
  }

  [Fact]
  public async Task Timestamps_KeepTheFormatOfTheOlderRows_AndBothFormatsReadBack()
  {
    using var db = new EnergyCostDb(SqliteOptions.ForFile(_db));
    // the live table's schema
    db.Execute("CREATE TABLE [costs] ([timestamp] DateTime2 NOT NULL, [gridimporttotal] Real NULL, [gridexporttotal] Real NULL, " +
               "[gridimport] Real NULL, [gridexport] Real NULL, [baseprice] Real NULL, [importpriceprovider] Real NULL, " +
               "[importpricetotal] Real NULL, [exportpriceprovider] Real NULL, [exportpricetotal] Real NULL)");
    db.Execute("insert into costs (timestamp) values ('2026-11-07 15:00:00.000')");   // a row linq2db 6 wrote before the fix
    var t = new DateTime(2026, 11, 7, 15, 15, 0);
    db.Insert(new Costs { Timestamp = t });
    await db.InsertAsync(new Costs { Timestamp = t.AddMinutes(15) });   // PVControl's path
    db.Insert(new Costs { Timestamp = t.AddMinutes(30).AddMilliseconds(250) });

    Assert.Equal(["2026-11-07 15:00:00.000", "2026-11-07 15:15:00", "2026-11-07 15:30:00", "2026-11-07 15:45:00.25"], RawTimestamps(db));
    Assert.Equal([new DateTime(2026, 11, 7, 15, 0, 0), t, t.AddMinutes(15), t.AddMinutes(30).AddMilliseconds(250)],
      db.CostEntries.OrderBy(c => c.Timestamp).Select(c => c.Timestamp).ToList());
    Assert.Equal(t.AddMinutes(30).AddMilliseconds(250), db.CostEntries.OrderByDescending(c => c.Timestamp).First().Timestamp);   // the restore's read
  }
}
