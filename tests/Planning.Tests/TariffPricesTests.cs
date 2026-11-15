using Microsoft.Data.Sqlite;
using NetDeamon.apps;
using NetDeamon.apps.PVControl;
using NetDeamon.apps.PVControl.Optimizer;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace NetDeamonApps.Tests;

/// <summary>
/// The import tariffs in a snapshot (<see cref="TariffPrices"/>, written by PVControl's DP shadow for the tariff advisor)
/// and the snapshot store (<see cref="ForecastSnapshotStore"/>).
/// </summary>
public class TariffPricesTests : IDisposable
{
  static readonly DateTime Day = new(2026, 11, 15);
  readonly string _db = Path.Combine(Path.GetTempPath(), "snapshots-" + Guid.NewGuid().ToString("N") + ".db");

  public void Dispose()
  {
    SqliteConnection.ClearAllPools();
    if (File.Exists(_db)) File.Delete(_db);
  }

  static PriceList Flat(float price) =>
    new(Enumerable.Range(0, 192).Select(q => new PriceTableEntry(Day.AddMinutes(15 * q), Day.AddMinutes(15 * q + 15), price)).ToList());

  static TariffPrices Tariffs(string active = "Market") =>
    TariffPrices.Of(active, Day.AddDays(1), [("Market", Flat(0.30f)), ("Fixed", Flat(0.20f))]);

  static ForecastSnapshot Snap(string name) => ForecastSnapshot.FromBytes(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Data", name)));

  [Fact]
  public void From_TodayWithTheActiveTariff_FromTheSwitchWithTheOther()
  {
    var prices = Tariffs().From("Fixed")!;
    Assert.Equal(192, prices.Count);
    Assert.All(prices.Where(p => p.StartTime < Day.AddDays(1)), p => Assert.Equal(0.30f, p.Price));
    Assert.All(prices.Where(p => p.StartTime >= Day.AddDays(1)), p => Assert.Equal(0.20f, p.Price));
    Assert.Equal(prices.OrderBy(p => p.StartTime).Select(p => p.StartTime), prices.Select(p => p.StartTime));
  }

  [Fact]
  public void From_TheActiveTariff_ItsOwnListEverywhere() =>
    Assert.All(Tariffs("Fixed").From("Fixed")!, p => Assert.Equal(0.20f, p.Price));

  [Fact]
  public void From_ATariffWithoutItsList_IsNull()
  {
    var onlyMarket = TariffPrices.Of("Market", Day.AddDays(1), [("Market", Flat(0.30f))]);
    Assert.Null(onlyMarket.From("Fixed"));
  }

  [Fact]
  public void Snapshot_OldRowsWithoutTariffs_StillLoad_NewOnesRoundTrip()
  {
    var old = Snap("forecast_snapshot_2026-11-02_0600.json.gz");
    Assert.Null(old.ImportTariffs);
    Assert.Null(old.Options);
    var stored = old with { ImportTariffs = Tariffs(), Options = new DpOptions(ChargeStartEur: 0.02f) };
    var again = ForecastSnapshot.FromBytes(stored.ToBytes());
    Assert.Equal("Market", again.ImportTariffs!.Active);
    Assert.Equal(Day.AddDays(1), again.ImportTariffs.NextSwitch);
    Assert.Equal(stored.ImportTariffs!.Import["Fixed"], again.ImportTariffs.Import["Fixed"]);
    Assert.Equal(0.02f, again.Options!.ChargeStartEur);
  }

  [Fact]
  public async Task Store_TheLatestQuarter_NoneWithoutDbOrTable()
  {
    Assert.Null(await ForecastSnapshotStore.ReadLatestAsync(_db));   // no DB yet
    var snap = Snap("forecast_snapshot_2026-11-02_0600.json.gz");
    await using (var con = new SqliteConnection($"Data Source={_db}"))
    {
      await con.OpenAsync();
      await SqliteSchema.ExecuteAsync(con, "CREATE TABLE other (x INTEGER)");
      SqliteConnection.ClearPool(con);
    }
    Assert.Null(await ForecastSnapshotStore.ReadLatestAsync(_db));   // the table isn't there yet
    await using (var con = new SqliteConnection($"Data Source={_db}"))
    {
      await con.OpenAsync();
      await ForecastSnapshotStore.WriteAsync(con, Day.AddHours(13), snap with { StartSocPercent = 40 });
      await ForecastSnapshotStore.WriteAsync(con, Day.AddHours(13.25), snap with { StartSocPercent = 41 });
      await ForecastSnapshotStore.WriteAsync(con, Day.AddHours(12.75), snap with { StartSocPercent = 39 });
    }
    var (quarter, latest) = (await ForecastSnapshotStore.ReadLatestAsync(_db))!.Value;
    Assert.Equal(Day.AddHours(13.25), quarter);
    Assert.Equal(41, latest.StartSocPercent);
    Assert.Equal(3, (await ForecastSnapshotStore.ReadAllAsync(_db)).Count);
  }
}
