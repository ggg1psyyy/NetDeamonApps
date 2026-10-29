using Microsoft.Data.Sqlite;
using NetDeamon.apps.PVControl.LoadModel;
using NetDeamon.apps.PVControl.Predictions;
using Xunit;

namespace NetDeamonApps.Tests;

/// <summary>
/// Weather-aware load forecast (src/PVControl/LoadModel): data cleaning, the heat demand model
/// (energy signature, 48-h scaling, heating off/just switched on), the base load level, the
/// shadow comparison of nights, and WeatherLoadPrediction on a temporary DataLogger database
/// (model vs fallback). That the model equals the backtested M2 is checked by tools/LoadBacktest
/// (variant "App WeatherLoadModel", identical numbers). Fetching the weather and reading the heat
/// pump mode need a live HA.
/// </summary>
public class LoadModelTests : TestBase
{
  static readonly DateTime Now = new(2026, 1, 15, 21, 0, 0);

  // ── Data cleaning ──────────────────────────────────────────────────────────────────────────

  [Fact]
  public void FromRow_CorruptHours_Skipped()
  {
    var t = Now;
    // Live 2025-12-28: warm water −13.5 kWh in one night (counter glitch).
    Assert.Null(LoadHour.FromRow(t, 800, 0, 300, -13518, 2));
    Assert.Null(LoadHour.FromRow(t, 800, 0, 300, 600, 2));      // warm water above the heat pump
    Assert.Null(LoadHour.FromRow(t, 300, 0, 900, 0, 2));        // heat pump above the house
    Assert.Null(LoadHour.FromRow(t, 300, 2000, 100, 0, 2));     // EV above the house
    var ok = LoadHour.FromRow(t, 1500, 400, 900, 300, 2)!.Value; // EV 400 + warm water 300 excluded
    Assert.Equal(1100, ok.Total);
    Assert.Equal(600, ok.HpHeat);
    Assert.Equal(500, ok.Base);
  }

  // ── Heat demand ────────────────────────────────────────────────────────────────────────────

  /// <summary>60 days, heat pump = 40 + 30·HDH Wh per hour (every hour), base 300 Wh.</summary>
  static (List<LoadHour> Hours, List<double> Temp24) Synthetic(Func<DateTime, double>? heatFactor = null, int days = 60)
  {
    var hours = new List<LoadHour>();
    var temps = new List<double>();
    for (var t = Now.AddDays(-days); t < Now; t = t.AddHours(1))
    {
      double temp24 = 5 + 4 * Math.Sin(t.DayOfYear / 3.0); // 1…9 °C
      double hp = 40 + (heatFactor?.Invoke(t) ?? 1) * 30 * HeatDemandModel.Hdh(temp24);
      hours.Add(new LoadHour(t, 300 + hp, hp, temp24));
      temps.Add(temp24);
    }
    return (hours, temps);
  }

  [Fact]
  public void HeatDemand_RecoversTheEnergySignature()
  {
    var (h, t) = Synthetic();
    var m = HeatDemandModel.Fit(h, t, Now);
    Assert.Equal(30, m.Slope(3), 1);
    Assert.Equal(40, m.Intercept(3), 0);
    Assert.Equal(1, m.Scale, 2);
    Assert.Equal(40 + 30 * 8, m.Expected(3, 5, heatingOn: true), 0); // 5 °C → HDH 8
  }

  [Fact]
  public void HeatDemand_HeatingLowerInTheLast48h_Scaled()
  {
    // Heating curve lowered two days ago: the heat pump heats 60 % of the signature.
    var (h, t) = Synthetic(time => Now - time <= TimeSpan.FromHours(48) ? 0.6 : 1);
    var m = HeatDemandModel.Fit(h, t, Now);
    Assert.InRange(m.Scale, 0.55, 0.7);
  }

  [Fact]
  public void HeatDemand_HeatingOffBefore_JustSwitchedOn_Unscaled()
  {
    var (h, t) = Synthetic(time => Now - time <= TimeSpan.FromHours(48) ? 0 : 1);
    Assert.InRange(HeatDemandModel.Fit(h, t, Now).Scale, 0, 0.1);                                   // winding down
    Assert.Equal(1, HeatDemandModel.Fit(h, t, Now, heatingOnSince: Now.AddHours(-5)).Scale);         // switched on 5 h ago
  }

  [Fact]
  public void HeatDemand_HeatingOff_OnlyTheRecentStandby()
  {
    var (h, t) = Synthetic(time => Now - time <= TimeSpan.FromHours(48) ? 0 : 1);
    var m = HeatDemandModel.Fit(h, t, Now);
    Assert.Equal(40, m.Expected(3, -5, heatingOn: false), 0);
  }

  // ── Base load ──────────────────────────────────────────────────────────────────────────────

  [Fact]
  public void BaseLoad_StepInTheLast48h_HalfOfItTakenOver()
  {
    // Base load 300 Wh, the last 48 h 340 Wh (e.g. a new standby consumer): +20 Wh.
    var (h, t) = Synthetic();
    h = h.Select(x => Now - x.Local <= TimeSpan.FromHours(48) ? x with { Total = x.Total + 40 } : x).ToList();
    var m = WeatherLoadModel.Fit(h, t, Now);
    Assert.InRange(m.BaseLevel, 12, 20); // half of the deviation from the (partly shifted) profile
  }

  [Fact]
  public void Rolling24_MeanOfTheLast24Hours_GapsFilled()
  {
    var temps = Enumerable.Range(0, 48).Select(i => (double?)(i < 24 ? 0 : 24)).ToList();
    temps[30] = null; // gap → previous value (24)
    var r = WeatherLoadModel.Rolling24(temps);
    Assert.Equal(0, r[23]);
    Assert.Equal(1, r[24], 6);   // one hour at 24 °C in the window
    Assert.Equal(24, r[47], 6);
  }

  // ── Shadow comparison of nights ────────────────────────────────────────────────────────────

  [Fact]
  public void NightTracker_SnapshotAt21_ScoredNextMorning()
  {
    var tracker = new NightForecastTracker();
    var day = new DateTime(2026, 10, 22);
    Assert.False(tracker.TakeSnapshot(day.AddHours(20.9), 4000, 3500));
    Assert.True(tracker.TakeSnapshot(day.AddHours(21), 4000, 3500));
    Assert.False(tracker.TakeSnapshot(day.AddHours(21.5), 4100, 3500)); // once per night
    Assert.False(tracker.Evaluate(day.AddDays(1).AddHours(7.9), _ => 4200));
    Assert.True(tracker.Evaluate(day.AddDays(1).AddHours(8.2), _ => 4200));
    var (model, average, n) = tracker.MeanAbsoluteError();
    Assert.Equal((200, 700, 1), (model, average, n));
  }

  [Fact]
  public void NightTracker_NightOfAndNightSum()
  {
    var day = new DateTime(2026, 10, 22);
    Assert.Equal(day, NightForecastTracker.NightOf(day.AddHours(14)));
    Assert.Equal(day.AddDays(-1), NightForecastTracker.NightOf(day.AddHours(3)));
    var slots = new Dictionary<DateTime, int>();
    for (var t = day; t < day.AddDays(2); t = t.AddMinutes(15)) slots[t] = 100;
    Assert.Equal(40 * 100, NightForecastTracker.NightSum(slots, day)); // 22:00–08:00 = 40 quarters
  }

  // ── WeatherLoadPrediction on a DataLogger database ─────────────────────────────────────────

  static string TempDb(int days, DateTime now)
  {
    string path = Path.Combine(Path.GetTempPath(), $"loadmodel_{Guid.NewGuid():N}.db");
    using var con = new SqliteConnection($"Data Source={path};Pooling=False");
    con.Open();
    var c = con.CreateCommand();
    c.CommandText = "CREATE TABLE hourly (timestamp TEXT, houseenergy INTEGER, carcharge INTEGER, warmwaterenergy INTEGER, heatpumpenergy INTEGER, temperatureoutside REAL)";
    c.ExecuteNonQuery();
    using var tx = con.BeginTransaction();
    c.Transaction = tx;
    c.CommandText = "INSERT INTO hourly VALUES ($t, $h, 0, 0, $hp, $temp)";
    var pt = c.Parameters.Add("$t", SqliteType.Text); var ph = c.Parameters.Add("$h", SqliteType.Integer);
    var php = c.Parameters.Add("$hp", SqliteType.Integer); var ptemp = c.Parameters.Add("$temp", SqliteType.Real);
    for (var t = now.Date.AddDays(-days); t < now.AddHours(-1); t = t.AddHours(1))
    {
      // 5-day spells alternating between −1 and 7 °C: the slope can only be learned from varying
      // temperatures, and the heat pump follows the 24-h mean (spells keep it equal to the day's).
      double temp = t.DayOfYear / 5 % 2 == 0 ? -1 : 7;
      double hp = 40 + 30 * HeatDemandModel.Hdh(temp);
      pt.Value = new DateTimeOffset(t.AddMinutes(59).AddSeconds(59)).ToString("yyyy-MM-ddTHH:mm:sszzz");
      ph.Value = (int)(300 + hp); php.Value = (int)hp; ptemp.Value = temp;
      c.ExecuteNonQuery();
    }
    tx.Commit();
    return path;
  }

  [Fact]
  public void WeatherLoadPrediction_WithWeather_ModelColderNightMoreLoad()
  {
    // A fixed time of day: tomorrow 02–04 h uses the 24-h mean before it, and the forecast below
    // starts at now − 2 h — run late in the evening, most of that day came from the history and
    // the cold/warm gap shrank under the threshold (failed at 21:00 on 2026-10-26).
    var now = DateTime.Today.AddHours(8);
    string db = TempDb(60, now);
    try
    {
      var average = new HourlyWeightedAverageLoadPrediction(db, 10);
      int TomorrowNight(double forecastTemp)
      {
        var weather = new WeatherForecast(new LoadForecastConfig());
        weather.Use(Enumerable.Range(-2, 60).Select(k => new HourlyWeather(now.Date.AddHours(now.Hour + k), forecastTemp, null, null)).ToList(), now);
        var p = new WeatherLoadPrediction(average, weather, new LoadForecastConfig(), db, []);
        p.UpdateData(now);
        Assert.Equal("model", p.Source);
        Assert.True(p.DataOK);
        return Enumerable.Range(0, 8).Sum(q => p.TodayAndTomorrow[now.Date.AddDays(1).AddHours(2).AddMinutes(15 * q)]);
      }
      // Same history (5-day spells of −1 / 7 °C), the forecast −7 vs +12 °C: tomorrow 02–04 h
      // the 24-h mean differs by ~11 K → ~0.25 kWh more heat pump energy (slope ~20 Wh/K·h).
      int cold = TomorrowNight(-7), warm = TomorrowNight(12);
      Assert.True(cold - warm > 300, $"cold {cold} Wh vs warm {warm} Wh");
      Assert.True(warm >= 2 * 300, $"warm {warm} Wh: at least the base load");
    }
    finally { SqliteConnection.ClearAllPools(); File.Delete(db); }
  }

  [Fact]
  public void WeatherLoadPrediction_NoWeather_FallsBackToTheAverage()
  {
    var now = DateTime.Now;
    string db = TempDb(60, now);
    try
    {
      var average = new HourlyWeightedAverageLoadPrediction(db, 10);
      var p = new WeatherLoadPrediction(average, new WeatherForecast(new LoadForecastConfig()), new LoadForecastConfig(), db, []);
      p.UpdateData(now);
      Assert.StartsWith("average", p.Source);
      Assert.Equal(average.TodayAndTomorrow, p.TodayAndTomorrow);
    }
    finally { SqliteConnection.ClearAllPools(); File.Delete(db); }
  }

  // ── Heat pump mode (unavailable keeps the last known one) ──────────────────────────────────

  static readonly string[] HeatingModes = ["Warmwasser & Heizung"];

  [Fact]
  public void ModeTracker_KnxDropout_NotSwitchedOn()
  {
    // Heating on, KNX drops out for a minute, back to the same mode: no "just switched on".
    var m = new HeatPumpModeTracker(HeatingModes);
    m.Seed([(Now.AddDays(-3), "Warmwasser & Heizung")]);
    m.Observe(Now.AddMinutes(-2), "unavailable");
    Assert.True(m.HeatingOn);
    m.Observe(Now, "Warmwasser & Heizung");
    Assert.True(m.HeatingOn);
    Assert.Null(m.HeatingOnSince);
  }

  [Fact]
  public void ModeTracker_RealSwitch_SinceThen()
  {
    var m = new HeatPumpModeTracker(HeatingModes);
    m.Observe(Now.AddHours(-5), "Nur Warmwasser");
    Assert.False(m.HeatingOn);
    m.Observe(Now.AddHours(-1), "unavailable");                 // dropout while off: still off
    Assert.False(m.HeatingOn);
    m.Observe(Now, "Warmwasser & Heizung");
    Assert.Equal(Now, m.HeatingOnSince);
    m.Observe(Now.AddHours(1), "Nur Warmwasser");
    Assert.Null(m.HeatingOnSince);
  }

  [Fact]
  public void ModeTracker_SeededFromDataLogger_KnowsTheSwitchAfterARestart()
  {
    var m = new HeatPumpModeTracker(HeatingModes);
    m.Seed([(Now.AddHours(-30), "Nur Warmwasser"), (Now.AddHours(-29), null), (Now.AddHours(-28), "Warmwasser & Heizung"), (Now.AddHours(-1), "Warmwasser & Heizung")]);
    Assert.True(m.HeatingOn);
    Assert.Equal(Now.AddHours(-28), m.HeatingOnSince);
  }

  [Fact]
  public void HeatDemand_DropoutDoesNotDisableTheScaling()
  {
    // With HeatingOnSince left null by the dropout, the 48-h scaling stays on (lowered curve: ~0.6).
    var (h, t) = Synthetic(time => Now - time <= TimeSpan.FromHours(48) ? 0.6 : 1);
    var m = new HeatPumpModeTracker(HeatingModes);
    m.Seed([(Now.AddDays(-3), "Warmwasser & Heizung")]);
    m.Observe(Now.AddMinutes(-1), "unavailable");
    m.Observe(Now, "Warmwasser & Heizung");
    Assert.InRange(HeatDemandModel.Fit(h, t, Now, m.HeatingOnSince).Scale, 0.55, 0.7);
    // Before: HA's LastChanged (= the end of the dropout) was taken as the switch-on time.
    Assert.Equal(1, HeatDemandModel.Fit(h, t, Now, heatingOnSince: Now).Scale);
  }

  // ── Weather forecast retry ─────────────────────────────────────────────────────────────────

  [Fact]
  public void WeatherForecast_AfterAFailure_RetriesEvery5MinNotEveryCycle()
  {
    var refresh = TimeSpan.FromMinutes(60);
    var lastSuccess = Now.AddHours(-2);
    Assert.True(WeatherForecast.IsDue(Now, lastSuccess, null, false, refresh));
    Assert.False(WeatherForecast.IsDue(Now, lastSuccess, Now.AddSeconds(-15), true, refresh)); // failed 15 s ago
    Assert.True(WeatherForecast.IsDue(Now, lastSuccess, Now.AddMinutes(-5), true, refresh));
    Assert.False(WeatherForecast.IsDue(Now, Now.AddMinutes(-30), null, false, refresh));        // fresh enough
  }

  // ── Night total across DST ─────────────────────────────────────────────────────────────────

  static readonly TimeZoneInfo Vienna = TimeZoneInfo.FindSystemTimeZoneById("Europe/Vienna");

  [Fact]
  public void NightTotal_OctoberDst_BothRepeatedHoursCount()
  {
    // 2026-10-25 03:00 → 02:00: the night 24.→25.10. has 11 real hours, 02:00 twice.
    var night = new DateTime(2026, 10, 24);
    var hours = Enumerable.Range(0, 10).Select(k => new LoadHour(night.AddHours(22 + k), 300, 40, 5)).ToList();
    hours.Add(new LoadHour(night.AddHours(26), 300, 40, 5)); // the second 02:00
    Assert.Equal(11 * 300, WeatherLoadPrediction.NightTotal(hours, night, Vienna));
  }

  [Fact]
  public void NightTotal_MarchDst_SkippedHourNotMissing()
  {
    // 2027-03-28 02:00 → 03:00: the hour 02:00 doesn't exist.
    var night = new DateTime(2027, 3, 27);
    var hours = Enumerable.Range(0, 10).Where(k => k != 4).Select(k => new LoadHour(night.AddHours(22 + k), 300, 40, 5)).ToList();
    Assert.Equal(9 * 300, WeatherLoadPrediction.NightTotal(hours, night, Vienna));
  }

  [Fact]
  public void NightTotal_HourMissing_NotScored()
  {
    var night = new DateTime(2026, 10, 22);
    var hours = Enumerable.Range(0, 10).Where(k => k != 3).Select(k => new LoadHour(night.AddHours(22 + k), 300, 40, 5)).ToList();
    Assert.Null(WeatherLoadPrediction.NightTotal(hours, night, Vienna));
  }
}
