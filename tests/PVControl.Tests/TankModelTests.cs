using NetDeamon.apps;
using System.Globalization;
using NetDeamon.apps.PVControl.WarmWater;
using Xunit;

namespace NetDeamonApps.Tests;

/// <summary>
/// TankModel.Fit and the history helpers of WarmWaterController. The real-data test uses 10 days
/// of HA history (Data/warmwater_history_2026-10-19.csv): heat pump automatic at 45 °C (39.9 → 45.1
/// every ~7 h) plus two one-time runs to 57 / 54.5 °C.
/// </summary>
public class TankModelTests : TestBase
{
  static readonly TankModel Wrong = new(5, 0.2, 0, 500, 0, new double[24]);

  internal static (List<(DateTime Time, double Temp)> Temps, List<HeatingRun> Runs) LoadHistory()
  {
    var temps = new List<(DateTime, double)>();
    var runs = new List<HeatingRun>();
    foreach (var line in File.ReadLines(Path.Combine(AppContext.BaseDirectory, "Data", "warmwater_history_2026-10-19.csv")))
    {
      var f = line.Split(';');
      if (f[0] == "T")
        temps.Add((DateTime.Parse(f[1], CultureInfo.InvariantCulture), double.Parse(f[2], CultureInfo.InvariantCulture)));
      else if (f[0] == "R")
        runs.Add(new HeatingRun(DateTime.Parse(f[1], CultureInfo.InvariantCulture), DateTime.Parse(f[2], CultureInfo.InvariantCulture),
          double.Parse(f[3], CultureInfo.InvariantCulture), double.Parse(f[4], CultureInfo.InvariantCulture), double.Parse(f[5], CultureInfo.InvariantCulture)));
    }
    return (temps, runs);
  }

  [Fact]
  public void Fit_RealHistory_MatchesMeasuredBehaviour()
  {
    var (temps, runs) = LoadHistory();
    var m = TankModel.Fit(temps, runs, Wrong);

    // 39.9 → 45.1 °C in ~18 min, 40 → 57 °C in 60 min.
    Assert.InRange(m.HeatingRateKPerHour, 15.5, 19);
    // Standby drop measured 0.73 K/h around 41 °C and ~1.5 K/h around 55 °C (usage at noon ≈ 0).
    Assert.InRange(m.LossKPerHour(41, 12), 0.6, 0.95);
    Assert.InRange(m.LossKPerHour(55, 12), 1.2, 1.9);
    // ~1.67 kW for the 40 → 45 runs, ~1.9 kW for the runs to 57 °C.
    Assert.InRange(m.PowerW(42.5), 1550, 1800);
    Assert.True(m.PowerW(50) > m.PowerW(42.5));
  }

  [Fact]
  public void Fit_RealHistory_ReproducesTheAutomaticCycle()
  {
    // The fitted model, run as the heat pump's automatic (45 °C, 5 K hysteresis), must give the
    // measured cycle: a heating every ~7 h (31 runs 40 → 45 °C in 10 days).
    var (temps, runs) = LoadHistory();
    var m = TankModel.Fit(temps, runs, Wrong);
    var start = new DateTime(2026, 10, 20, 0, 0, 0);
    var slots = Enumerable.Range(0, 192).Select(i => start.AddMinutes(15 * i)).ToList();
    var r = new PlanRequest(start, 45, slots, slots.Select(_ => 0.3f).ToList(), m, [], 57);

    var auto = WarmWaterPlanner.PredictAutomatic(r, 45, 5, heatingNow: false);

    double hoursPerRun = 48.0 / auto.Runs.Count;
    Assert.InRange(hoursPerRun, 5.5, 8.5);
    Assert.All(auto.Runs, run => Assert.InRange(run.TargetTemp, 44.9, 45.0));
  }

  [Fact]
  public void Fit_RunWithoutTankRise_IgnoredForPower()
  {
    // A space-heating run matched by the warm-water template (supply ≥ 35 °C): 4 kW, tank falling.
    var (temps, runs) = LoadHistory();
    var spaceHeating = new HeatingRun(new DateTime(2026, 10, 18, 2, 0, 0), new DateTime(2026, 10, 18, 3, 0, 0), 42, 41.5, 4000);
    var clean = TankModel.Fit(temps, runs, Wrong);
    var withSpaceHeating = TankModel.Fit(temps, [.. runs, spaceHeating], Wrong);

    Assert.Equal(clean.PowerW(45), withSpaceHeating.PowerW(45), 6);
    Assert.Equal(clean.HeatingRateKPerHour, withSpaceHeating.HeatingRateKPerHour, 6);
  }

  [Fact]
  public void Fit_TooLittleData_KeepsFallback()
  {
    var t0 = new DateTime(2026, 10, 1, 0, 0, 0);
    var m = TankModel.Fit([(t0, 45), (t0.AddHours(1), 44)], [], TankModel.Default);
    Assert.Equal(TankModel.Default, m);
  }

  [Fact]
  public void StandbyWindows_SkipHeatingAndItsSettling()
  {
    var t0 = new DateTime(2026, 10, 1, 0, 0, 0);
    var temps = Enumerable.Range(0, 12 * 12).Select(i => (t0.AddMinutes(5 * i), 50 - i * 0.05)).ToList();
    var run = new HeatingRun(t0.AddHours(5), t0.AddHours(5.5), 45, 50);

    var windows = TankModel.StandbyWindows(temps, [run]);

    Assert.NotEmpty(windows);
    // No window may overlap 04:55 – 06:10 (run plus 40 min settling): none starts in 04:00 – 06:00.
    Assert.DoesNotContain(windows, w => w.Hour is 4 or 5);
    Assert.Contains(windows, w => w.Hour == 3);
    Assert.Contains(windows, w => w.Hour == 6);
    Assert.All(windows, w => Assert.Equal(0.6, w.DropKPerHour, 3));
  }

  [Fact]
  public void Fit_ExtraDropAtOneHour_BecomesUsage()
  {
    // Constant standby loss, plus 2 K extra between 07:00 and 08:00 every day (a shower).
    var t0 = new DateTime(2026, 10, 1, 0, 0, 0);
    var temps = new List<(DateTime, double)>();
    var runs = new List<HeatingRun>();
    double temp = 50;
    for (var t = t0; t < t0.AddDays(8); t = t.AddMinutes(5))
    {
      temps.Add((t, temp));
      temp -= 0.8 / 12 + (t.Hour == 7 ? 2.0 / 12 : 0);
      if (temp < 42 && t.Hour is 12)
      {
        runs.Add(new HeatingRun(t, t.AddMinutes(30), temp, 50));
        t = t.AddMinutes(30);
        temp = 50;
      }
    }
    var m = TankModel.Fit(temps, runs, TankModel.Default);

    // One-hour windows starting 06:15 – 07:45 each see part of the shower, so it's smeared over
    // hours 6 and 7 — but clearly there, and nothing in the afternoon.
    Assert.True(m.UsageKPerHour[7] > 1.0, $"usage 07:00 = {m.UsageKPerHour[7]:F2}");
    Assert.True(m.UsageKPerHour[6] + m.UsageKPerHour[7] > 1.5);
    Assert.InRange(m.UsageKPerHour[15], 0, 0.3);
  }

  [Fact]
  public void OnIntervals_PairsOnAndOff_DropsOpenInterval()
  {
    var t0 = new DateTime(2026, 10, 1, 0, 0, 0);
    var iv = WarmWaterController.OnIntervals([
      (t0, "off"), (t0.AddMinutes(10), "on"), (t0.AddMinutes(30), "off"),
      (t0.AddMinutes(40), "on"), (t0.AddMinutes(45), "unavailable"), (t0.AddMinutes(50), "on")]);

    Assert.Equal([(t0.AddMinutes(10), t0.AddMinutes(30)), (t0.AddMinutes(40), t0.AddMinutes(45))], iv);
  }

  [Fact]
  public void TimeWeightedAverage_WeighsByDuration()
  {
    var t0 = new DateTime(2026, 10, 1, 0, 0, 0);
    // 1000 W from before the start until 10 min, then 2000 W until the end at 30 min.
    double? avg = StepFunction.TimeWeightedAverage([(t0.AddMinutes(-5), 1000), (t0.AddMinutes(10), 2000)], t0, t0.AddMinutes(30));
    Assert.Equal(1000 * 10.0 / 30 + 2000 * 20.0 / 30, avg!.Value, 6);
  }

  [Fact]
  public void ValueAt_LastValueAtOrBefore()
  {
    var t0 = new DateTime(2026, 10, 1, 0, 0, 0);
    var values = new List<(DateTime, double)> { (t0, 40), (t0.AddMinutes(10), 41) };
    Assert.Equal(40, StepFunction.ValueAt(values, t0.AddMinutes(-1)));
    Assert.Equal(40, StepFunction.ValueAt(values, t0.AddMinutes(9)));
    Assert.Equal(41, StepFunction.ValueAt(values, t0.AddMinutes(10)));
  }

  // ── Heating rate per degree (the slower last few K) ─────────────────────────────────────────

  static TankModel WithBands(params (int Band, double Rate)[] bands) =>
    TankModel.Default with { HeatingRateKPerHour = 17.6, RateByDegree = bands.ToDictionary(b => b.Band, b => b.Rate) };

  [Fact]
  public void RateAt_SlowerBandsCount_FasterOnesAndGapsKeepTheConstant_AboveTheTopTheTopBand()
  {
    Assert.Equal(17.3, TankModel.Default.RateAt(49.5));                // nothing learned
    var m = WithBands((42, 19.5), (44, 16.8), (49, 8.0));
    Assert.Equal(17.6, m.RateAt(42.3));   // measured faster: the constant keeps the start-ups in it
    Assert.Equal(16.8, m.RateAt(44.9));
    Assert.Equal(17.6, m.RateAt(46.0));   // no data
    Assert.Equal(8.0, m.RateAt(49.0));
    Assert.Equal(8.0, m.RateAt(53.0));    // above the data: no faster than its top band
  }

  [Fact]
  public void Heat_AndHoursToHeat_IntegrateAcrossBands_AndAreInverse()
  {
    var m = WithBands((49, 8.0));
    // 47 → 49 at 17.6 K/h (0.1136 h), then 49 → 50 at 8 K/h (0.125 h)
    Assert.Equal(2 / 17.6 + 1 / 8.0, m.HoursToHeat(47, 50), 6);
    Assert.Equal(50, m.Heat(47, 2 / 17.6 + 1 / 8.0, 57), 6);
    foreach (var h in new[] { 0.05, 0.2, 0.3 })
      Assert.Equal(h, m.HoursToHeat(46.3, m.Heat(46.3, h, 57)), 6);
    Assert.Equal(49.5, m.Heat(47, 1, 49.5), 6);   // stops at the maximum
    Assert.Equal(0, m.HoursToHeat(50, 49));
  }

  /// <summary>Synthetic runs 40 → 50 °C: 18 K/h, 8 K/h from 48 °C; readings every 20 s in 0.1-K steps.</summary>
  static (List<(DateTime Time, double Temp)> Temps, List<HeatingRun> Runs) KneeHistory(int runs, bool defrostPause = false)
  {
    var temps = new List<(DateTime, double)>();
    var result = new List<HeatingRun>();
    var t = new DateTime(2026, 11, 1, 2, 0, 0);
    for (int n = 0; n < runs; n++, t = t.AddHours(8))
    {
      double temp = 40;
      var now = t;
      temps.Add((now, temp));
      var pauseAt = t.AddMinutes(30);
      while (temp < 50)
      {
        if (defrostPause && now >= pauseAt && now < pauseAt.AddMinutes(2)) { now = now.AddSeconds(20); continue; }
        double rate = temp >= 48 ? 8 : 18;
        now = now.AddSeconds(20);
        temp = Math.Round(temp + rate * 20 / 3600.0, 3);
        temps.Add((now, Math.Floor(temp * 10) / 10));
      }
      if (defrostPause)
      {
        result.Add(new HeatingRun(t, pauseAt, 40, 0));
        result.Add(new HeatingRun(pauseAt.AddMinutes(2), now, 0, 50));
      }
      else result.Add(new HeatingRun(t, now, 40, 50));
      temps.Add((now.AddMinutes(30), 50.5));   // the sensor's next step after the run: not the run's
    }
    return (temps, result);
  }

  [Fact]
  public void RatesByDegree_LearnsTheSlowTop_AndLeavesTheRestAtTheMeasuredRate()
  {
    var (temps, runs) = KneeHistory(3);
    var bands = TankModel.RatesByDegree(temps, runs);
    Assert.InRange(bands[42], 16.5, 19.5);
    Assert.InRange(bands[48], 7, 9);
    Assert.InRange(bands[49], 7, 9);
    Assert.False(bands.ContainsKey(50));     // after the run
  }

  [Fact]
  public void RatesByDegree_TooFewRuns_LearnsNothing()
  {
    var (temps, runs) = KneeHistory(2);
    Assert.Empty(TankModel.RatesByDegree(temps, runs));
  }

  [Fact]
  public void RatesByDegree_ADefrostPause_StaysInTheRun()
  {
    // Live 2026-11-09 15:00: a 2-min defrost split the run in two; the second part (49.48 → 49.6 °C, 5 min) alone
    // is shorter than MinFitRun. Merged, the pause counts as the degree's time.
    var (temps, runs) = KneeHistory(3, defrostPause: true);
    var bands = TankModel.RatesByDegree(temps, runs);
    double band = Math.Floor(temps.First(x => x.Time >= runs[0].End).Temp);   // where the pause was
    Assert.True(bands[(int)band] < (band >= 48 ? 8 : 18) - 1, $"{band}: {bands[(int)band]:F1}");
  }

  [Fact]
  public void Fit_RealHistory_NoSlowBandFromTheSensorStepAfterTheRun()
  {
    // October 2026: the sensor stepped 44.28 → 45.08 only after the heat pump had stopped; counted to the run's end,
    // band 44 read 3 K/h. Reading to reading, every learned band is at the measured ~19 K/h.
    var (temps, runs) = LoadHistory();
    var m = TankModel.Fit(temps, runs, Wrong);
    Assert.NotNull(m.RateByDegree);
    Assert.All(m.RateByDegree!, b => Assert.InRange(b.Value, 16, 22));
    Assert.Equal(m.HeatingRateKPerHour, m.RateAt(44.5));
  }
}
