using System.Text.Json;
using NetDeamon.apps;
using NetDeamon.apps.PVControl.LoadModel;
using NetDeamon.apps.PVControl.PvForecast;
using Xunit;

namespace NetDeamonApps.Tests;

/// <summary>
/// The PV forecast shadow: the learned correction, GeoSphere's responses (recorded 2026-11-10, the grid point
/// replaced), the measured day from DataLogger's hours, the log and a run with fake sources. The HTTP calls
/// themselves need the live data hub and aren't unit tested.
/// </summary>
public class PvForecastTests : TestBase
{
  static JsonElement Fixture(string name) =>
    JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Data", name))).RootElement;

  static readonly PvForecastConfig Config = new();

  static List<ScoredDay> Days(DateTime last, params double[] ratios) =>
    ratios.Select((r, i) => new ScoredDay(last.AddDays(-ratios.Length + 1 + i), 10000, 10000 * r, null)).ToList();

  // ── Correction ──────────────────────────────────────────────────────────────────────────

  [Fact]
  public void Correction_TheMedianOfTheLast14Days_Nov9()
  {
    // 2026-11-09: Open-Meteo 10.3 kWh the evening before; the 14 days before ran at a median of 0.68 → 7.0 kWh (actual 3.0).
    var history = Days(new DateTime(2026, 11, 8), 0.98, 0.9, 0.85, 0.7, 0.66, 0.92, 0.87, 0.47, 0.35, 0.49, 0.8, 0.53, 0.51, 0.36);
    var c = PvForecastCorrection.For(history, new DateTime(2026, 11, 9), null, Config);
    Assert.Equal(14, c.ScoredDays);
    Assert.Equal(0.68, c.Factor, 3);
    Assert.Equal(7.0, 10.3 * c.Factor, 1);
    Assert.True(c.CautiousFactor < c.Factor);
    Assert.Equal(Mean.Percentile(history.Select(d => d.Ratio), 0.25), c.CautiousFactor, 6);
  }

  [Fact]
  public void Correction_FewerThan7Days_None()
  {
    var c = PvForecastCorrection.For(Days(new DateTime(2026, 11, 8), 0.5, 0.5, 0.5, 0.5, 0.5, 0.5), new DateTime(2026, 11, 9), null, Config);
    Assert.Same(Correction.None, c);
    Assert.False(c.Learned);
  }

  [Fact]
  public void Correction_OnlyDaysBefore_OnlyTheWindow_NoDarkDays()
  {
    var history = Days(new DateTime(2026, 11, 8), Enumerable.Repeat(0.5, 14).ToArray()).ToList();
    history.AddRange(Days(new DateTime(2026, 10, 25), Enumerable.Repeat(1.0, 20).ToArray()));   // older than the window
    history.Add(new ScoredDay(new DateTime(2026, 11, 9), 10000, 1000, null));                      // the day itself: not known
    history.Add(new ScoredDay(new DateTime(2026, 11, 7), 400, 4000, null));                        // a forecast of almost nothing
    var c = PvForecastCorrection.For(history, new DateTime(2026, 11, 9), null, Config);
    Assert.Equal(0.5, c.Factor, 6);
  }

  [Fact]
  public void Correction_AColderDay_LessPv()
  {
    // A day forecast 4 K colder than the scored days were: 0.035 per K lower (inversion, fog).
    var history = Days(new DateTime(2026, 11, 8), Enumerable.Repeat(0.6, 14).ToArray())
      .Select(d => d with { TempDay = 8 }).ToList();
    var c = PvForecastCorrection.For(history, new DateTime(2026, 11, 9), 4, Config);
    Assert.Equal(-4, c.TempAnomaly!.Value, 6);
    Assert.Equal(0.6 - 4 * 0.035, c.Factor, 6);
    Assert.Equal(0.6, PvForecastCorrection.For(history, new DateTime(2026, 11, 9), null, Config).Factor, 6);
  }

  [Fact]
  public void Correction_StaysWithinItsLimits()
  {
    var high = PvForecastCorrection.For(Days(new DateTime(2026, 7, 8), Enumerable.Repeat(3.0, 14).ToArray()), new DateTime(2026, 7, 9), null, Config);
    Assert.Equal(PvForecastCorrection.MaxRatio, high.MedianRatio, 6);   // ratios capped at 2
    Assert.Equal(PvForecastCorrection.MaxFactor, high.Factor, 6);
    var snow = PvForecastCorrection.For(Days(new DateTime(2026, 1, 8), Enumerable.Repeat(0.0, 14).ToArray()), new DateTime(2026, 1, 9), null, Config);
    Assert.Equal(PvForecastCorrection.MinFactor, snow.Factor, 6);
  }

  [Fact]
  public void Backtest_EachDayOnlyWithItsPast()
  {
    var history = Days(new DateTime(2026, 11, 8), Enumerable.Repeat(0.5, 10).ToArray());
    var b = PvForecastCorrection.Backtest(history, Config);
    Assert.Equal(10, b.Count);
    Assert.Equal(b[0].Forecast, b[0].Corrected);        // the first 7 have too few days before them
    Assert.Equal(b[6].Forecast, b[6].Corrected);
    Assert.Equal(b[7].Forecast * 0.5, b[7].Corrected, 6);
  }

  // ── GeoSphere ───────────────────────────────────────────────────────────────────────────

  [Fact]
  public void GeoSphere_Forecast_WattsPerM2ToPvWh_LocalHourStart()
  {
    var nwp = GeoSphereSource.Hourly(Fixture("geosphere_nwp2_2026-11-10.json"), "ssrd", 4.9);
    // The first value is stamped 10:00 UTC: the hour 09–10 UTC = 10–11 local, 12.5 W/m² × 4.9.
    Assert.Equal(12.5 * 4.9, nwp[new DateTime(2026, 11, 10, 10, 0, 0)], 6);
    // 2026-11-11: 1462.4 Wh/m² over the day → 7.17 kWh PV (actual to compare on the evening of 11-11).
    Assert.Equal(1462.4 * 4.9, SourceForecast.DayTotal(nwp, new DateTime(2026, 11, 11))!.Value, 0);
    var ens = Fixture("geosphere_ens2_2026-11-10.json");
    Assert.Equal(797.6 * 4.9, SourceForecast.DayTotal(GeoSphereSource.Hourly(ens, "ssrd_p10", 4.9), new DateTime(2026, 11, 11))!.Value, 0);
    Assert.Equal(1836.2 * 4.9, SourceForecast.DayTotal(GeoSphereSource.Hourly(ens, "ssrd_p90", 4.9), new DateTime(2026, 11, 11))!.Value, 0);
    Assert.Empty(GeoSphereSource.Hourly(ens, "ssrd", 4.9));   // not in the ensemble's response
  }

  [Fact]
  public void GeoSphere_MeasuredDays_StationAndInca()
  {
    var (id, kwh, sun) = Assert.Single(GeoSphereSource.Stations(Fixture("geosphere_station23_2026-11-01_09.json")));
    Assert.Equal(23, id);
    Assert.Equal(8.7247, kwh!.Value, 3);   // Enns, 2026-11-01…09
    Assert.Equal(13.242, sun!.Value, 2);
    Assert.Equal(9.467, GeoSphereSource.DaySum(Fixture("geosphere_inca2_2026-11-01_09.json"), "ssrd", 1.0)!.Value, 3);
  }

  [Fact]
  public void GeoSphere_InversionSignal_HillsMinusValley()
  {
    // 2026-11-07: Enns 0.6 h, Bad Zell 4.2 h, Reichenau 6.4 h.
    var v = new Dictionary<string, double> { ["station_23_sun_h"] = 0.6, ["station_3425_sun_h"] = 4.2, ["station_161_sun_h"] = 6.4 };
    Assert.Equal(4.7, GeoSphereSource.InversionSunGap(v, 23, [3425, 161])!.Value, 6);
    Assert.Null(GeoSphereSource.InversionSunGap(v, null, [3425]));
    Assert.Null(GeoSphereSource.InversionSunGap(v, 23, [99]));
  }

  [Fact]
  public void GeoSphere_DayAsUtcRange()
  {
    Assert.Equal(("2026-11-09T23:10", "2026-11-10T23:00"), GeoSphereSource.UtcRange(new DateTime(2026, 11, 10)));   // 00:10 … 24:00 local
    Assert.Equal(("2026-06-30T22:10", "2026-07-01T22:00"), GeoSphereSource.UtcRange(new DateTime(2026, 7, 1)));
  }

  [Fact]
  public void Sources_ByType()
  {
    Func<IReadOnlyDictionary<DateTime, int>> om = () => new Dictionary<DateTime, int>();
    Assert.IsType<OpenMeteoEntitiesSource>(PvForecastSources.Create(new PvForecastSourceConfig { Type = "OpenMeteoEntities" }, om, null));
    Assert.IsType<GeoSphereSource>(PvForecastSources.Create(new PvForecastSourceConfig { Type = "GeoSphere", Name = "GS" }, om, (48, 14)));
    Assert.Null(PvForecastSources.Create(new PvForecastSourceConfig { Type = "GeoSphere" }, om, null));   // no location
    Assert.Null(PvForecastSources.Create(new PvForecastSourceConfig { Type = "Solcast" }, om, null));
    Assert.Equal("GS", PvForecastSources.Create(new PvForecastSourceConfig { Type = "GeoSphere", Name = "GS" }, om, (48, 14))!.Name);
  }

  // ── The measured day ────────────────────────────────────────────────────────────────────

  static IEnumerable<PvHour> Hours(DateTime day, int count, Func<int, int?> pv, double temp = 5) =>
    Enumerable.Range(0, count).Select(h => new PvHour(day.AddHours(h), pv(h), temp + h % 3));

  [Fact]
  public void Actual_TheDaysHours_TheResetHourAs0()
  {
    var day = new DateTime(2025, 7, 10);
    // Older rows: the last hour held the counter's midnight reset (−21700 Wh).
    var a = PvActual.OfDay(Hours(day, 24, h => h == 23 ? -21700 : h is >= 6 and <= 18 ? 1000 : 0), day, TimeZoneInfo.Local)!.Value;
    Assert.Equal(13000, a.PvWh);
    Assert.Equal(Math.Round(Enumerable.Range(9, 6).Average(h => 5.0 + h % 3), 1), a.TempDay);
  }

  [Fact]
  public void Actual_AMissingHour_NotYet()
  {
    var day = new DateTime(2026, 11, 9);
    Assert.Null(PvActual.OfDay(Hours(day, 23, _ => 100), day, TimeZoneInfo.Local));
    Assert.Null(PvActual.OfDay(Hours(day, 24, h => h == 12 ? null : 100), day, TimeZoneInfo.Local));
  }

  [Fact]
  public void Actual_ADstDay_23HoursAreComplete()
  {
    var zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Vienna");
    var day = new DateTime(2026, 3, 29);   // 02:00 → 03:00
    var hours = Enumerable.Range(0, 24).Where(h => h != 2).Select(h => new PvHour(day.AddHours(h), 100, 5));
    Assert.Equal(2300, PvActual.OfDay(hours, day, zone)!.Value.PvWh);
  }

  [Fact]
  public void WeatherForecast_MeanTemperature_OnlyFullyCovered()
  {
    var day = new DateTime(2026, 11, 11);
    var hours = Enumerable.Range(0, 24).Select(h => new HourlyWeather(day.AddHours(h), h, null, null)).ToList();
    Assert.Equal(11.5, WeatherForecast.MeanTemperature(hours, day.AddHours(9), day.AddHours(15)));
    Assert.Null(WeatherForecast.MeanTemperature(hours.Take(12).ToList(), day.AddHours(9), day.AddHours(15)));
  }

  // ── Log and run ─────────────────────────────────────────────────────────────────────────

  static string TempDb() => Path.Combine(Path.GetTempPath(), $"pv_forecast_{Guid.NewGuid():N}.db");

  [Fact]
  public async Task Log_RoundTrip()
  {
    var path = TempDb();
    try
    {
      var log = new PvForecastLog(path);
      var day = new DateTime(2026, 11, 9);
      await log.NoteAsync(new DayAhead(day, "OpenMeteo", day.AddHours(-3), null, 10300, null, null, 4.5, new Dictionary<int, double> { [12] = 1650 }));
      Assert.True(await log.HasDayAheadAsync(day, "OpenMeteo"));
      Assert.False(await log.HasDayAheadAsync(day, "GeoSphere"));
      Assert.Equal([day], await log.UnscoredDaysAsync(day.AddDays(-5), day.AddDays(1)));
      Assert.Empty(await log.UnscoredDaysAsync(day.AddDays(-5), day));   // not ended
      await log.SetActualAsync(day, 3000, 6.2);
      Assert.Empty(await log.UnscoredDaysAsync(day.AddDays(-5), day.AddDays(1)));
      var s = Assert.Single(await log.ScoredAsync("OpenMeteo", day.AddDays(-5)));
      Assert.Equal((10300.0, 3000.0, 6.2), (s.ForecastWh, s.ActualWh, s.TempDay!.Value));
      Assert.Equal([day], await log.UnobservedDaysAsync(day.AddDays(-5), "inca_kwh_m2"));
      await log.SetObservationsAsync(day, new Dictionary<string, double> { ["inca_kwh_m2"] = 0.68 });
      Assert.Empty(await log.UnobservedDaysAsync(day.AddDays(-5), "inca_kwh_m2"));
    }
    finally { File.Delete(path); }
  }

  sealed class FakeSource(string name, Func<DateTime, SourceForecast?> forecast) : IPvForecastSource, IPvObservationSource
  {
    public string Name => name;
    public int Observed;
    public Task<SourceForecast?> GetAsync(DateTime now, CancellationToken ct) => Task.FromResult(forecast(now));
    public Task<IReadOnlyDictionary<string, double>> ObserveDayAsync(DateTime day, CancellationToken ct)
    {
      Observed++;
      return Task.FromResult<IReadOnlyDictionary<string, double>>(new Dictionary<string, double> { ["inca_kwh_m2"] = 1.0 });
    }
  }

  static SourceForecast Flat(DateTime now, double whPerHour, double? low = null) =>
    new(now, "run", Enumerable.Range(0, 48).ToDictionary(h => now.Date.AddHours(h), h => h % 24 is >= 8 and < 16 ? whPerHour : 0.0),
      low is { } l ? Enumerable.Range(0, 48).ToDictionary(h => now.Date.AddHours(h), h => h % 24 is >= 8 and < 16 ? l : 0.0) : null);

  [Fact]
  public async Task Run_NotesTomorrowOnceInTheEvening_ScoresIt_Corrects()
  {
    var path = TempDb();
    try
    {
      var log = new PvForecastLog(path);
      var om = new FakeSource("OpenMeteo", now => Flat(now, 1000));
      var gs = new FakeSource("GeoSphere", now => Flat(now, 800, low: 400));
      var shadow = new PvForecastShadow(Config, [om, gs], log, _ => (4000, 5), _ => 5);
      var start = new DateTime(2026, 10, 20, 21, 5, 0);
      for (int d = 0; d < 10; d++)
      {
        await shadow.RunAsync(start.AddDays(d).AddHours(-3), CancellationToken.None);   // 18:05: no snapshot
        await shadow.RunAsync(start.AddDays(d), CancellationToken.None);                // 21:05: tomorrow noted
        await shadow.RunAsync(start.AddDays(d).AddMinutes(30), CancellationToken.None); // a second time: kept
      }
      var scored = await log.ScoredAsync("OpenMeteo", start.AddDays(-1));
      Assert.Equal(8, scored.Count);                      // 10-21…10-28: on 10-29 that day itself has not ended
      Assert.All(scored, s => Assert.Equal(8000, s.ForecastWh));
      var report = shadow.Last[0];
      Assert.Equal("OpenMeteo", report.Source);
      Assert.Equal(8.0, report.TomorrowRaw);
      Assert.Equal(4.0, report.TomorrowCorrected);        // learned 0.5 from 8 days
      Assert.Equal(4.0, shadow.Last[1].TomorrowCorrected); // GeoSphere 6.4 × 0.625
      Assert.Equal(2.0, shadow.Last[1].TomorrowCautious);  // its band's low end, corrected
      Assert.True(om.Observed > 0);
      Assert.NotNull(shadow.LastObservation);
      Assert.Equal(8, shadow.Recent.Count);
    }
    finally { File.Delete(path); }
  }
}
