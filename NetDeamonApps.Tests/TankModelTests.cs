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
    Assert.Equal(40, WarmWaterController.ValueAt(values, t0.AddMinutes(-1)));
    Assert.Equal(40, WarmWaterController.ValueAt(values, t0.AddMinutes(9)));
    Assert.Equal(41, WarmWaterController.ValueAt(values, t0.AddMinutes(10)));
  }
}
