using System;
using System.Collections.Generic;
using System.Linq;

namespace NetDeamon.apps.PVControl.WarmWater;

/// <summary>One warm-water heating run seen in the history: start/end and the tank temperature at both.</summary>
public readonly record struct HeatingRun(DateTime Start, DateTime End, double StartTemp, double EndTemp, double? AvgPowerW = null)
{
  public double Hours => (End - Start).TotalHours;
}

/// <summary>
/// Thermal model of the warm-water tank, learned from the temperature history (see <see cref="Fit"/>).
/// Heating raises the temperature at a constant rate (measured 40 → 57 °C: ~17 K/h at any
/// temperature). Without heating it falls by the standby loss, proportional to the difference to
/// an apparent ambient temperature (measured 0.73 K/h at 41 °C, 1.56 K/h at 55 °C), plus a
/// per-hour-of-day usage term. The power draw grows slightly with the temperature (~1.67 kW at
/// 42 °C, ~1.9 kW at 50+ °C).
/// </summary>
public sealed record TankModel(
  double HeatingRateKPerHour,
  double LossCoefficientPerHour,
  double AmbientTempC,
  double PowerAt45W,
  double PowerSlopeWPerK,
  IReadOnlyList<double> UsageKPerHour)
{
  /// <summary>Standby loss never drops below this, even below the fitted ambient temperature.</summary>
  public const double MinLossKPerHour = 0.2;

  /// <summary>Values measured 2026-10-09 – 10-19 (HP automatic at 45 °C plus two one-time runs to 57 °C).</summary>
  public static TankModel Default { get; } = new(17.3, 0.059, 28.6, 1720, 26, new double[24]);

  public double LossKPerHour(double temp, int hour) =>
    Math.Max(MinLossKPerHour, LossCoefficientPerHour * (temp - AmbientTempC)) + UsageKPerHour[hour];

  public double PowerW(double temp) => Math.Max(100, PowerAt45W + PowerSlopeWPerK * (temp - 45));

  /// <summary>Temperature after <paramref name="hours"/> without heating, starting at <paramref name="time"/>.</summary>
  public double Cool(double temp, DateTime time, double hours) =>
    temp - LossKPerHour(temp, time.Hour) * hours;

  // ── Fitting ──────────────────────────────────────────────────────────────────────────────

  /// <summary>Heating runs shorter than this are not used to fit the heating rate (start-up dominates).</summary>
  public static readonly TimeSpan MinFitRun = TimeSpan.FromMinutes(8);
  /// <summary>After a run the sensor still rises for a while — standby windows start this much later.</summary>
  public static readonly TimeSpan AfterRunSettle = TimeSpan.FromMinutes(40);
  /// <summary>Length of one standby window for the loss fit.</summary>
  public static readonly TimeSpan LossWindow = TimeSpan.FromHours(1);
  /// <summary>The loss slope needs this temperature spread; below it only the offset is fitted.</summary>
  public const double MinSpreadForSlopeK = 4;

  /// <summary>
  /// Learns the model from history. <paramref name="temps"/> are (time, °C) readings in time
  /// order, <paramref name="runs"/> the heating runs (any kind: automatic or one-time). Every part
  /// that has too little data keeps the value of <paramref name="fallback"/>.
  /// </summary>
  public static TankModel Fit(IReadOnlyList<(DateTime Time, double Temp)> temps, IReadOnlyList<HeatingRun> runs, TankModel fallback)
  {
    var model = fallback;

    // Only runs that clearly warmed the tank: the "warm water heating" sensor can be a template
    // (supply temperature ≥ 35 °C and compressor > 25 %), which a space-heating run with a high
    // supply temperature would also match.
    var fitRuns = runs.Where(r => r.End - r.Start >= MinFitRun && r.EndTemp - r.StartTemp > 1).ToList();

    // Heating rate: total rise over total heating time of the longer runs.
    if (fitRuns.Count >= 2)
      model = model with { HeatingRateKPerHour = Math.Clamp(fitRuns.Sum(r => r.EndTemp - r.StartTemp) / fitRuns.Sum(r => r.Hours), 3, 60) };

    // Power: linear over the run's mean temperature when the runs cover enough range.
    var powerRuns = fitRuns.Where(r => r.AvgPowerW is > 100).ToList();
    if (powerRuns.Count >= 2)
    {
      var xs = powerRuns.Select(r => (r.StartTemp + r.EndTemp) / 2).ToList();
      var ys = powerRuns.Select(r => r.AvgPowerW!.Value).ToList();
      if (xs.Max() - xs.Min() >= MinSpreadForSlopeK && LinearFit(xs, ys) is { } fit)
      {
        double slope = Math.Clamp(fit.Slope, 0, 200);
        model = model with { PowerSlopeWPerK = slope, PowerAt45W = Math.Clamp(ys.Average() + slope * (45 - xs.Average()), 300, 5000) };
      }
      else
        model = model with { PowerAt45W = Math.Clamp(ys.Average() - model.PowerSlopeWPerK * (xs.Average() - 45), 300, 5000) };
    }

    // Standby loss: drop over one-hour windows (stepped every 15 min) away from any heating.
    var windows = StandbyWindows(temps, runs);
    if (windows.Count >= 20)
    {
      var xs = windows.Select(w => w.AvgTemp).ToList();
      var ys = windows.Select(w => w.DropKPerHour).ToList();
      double k = model.LossCoefficientPerHour;
      if (xs.Max() - xs.Min() >= MinSpreadForSlopeK && LinearFit(xs, ys) is { Slope: > 0 } fit)
        k = Math.Clamp(fit.Slope, 0.01, 0.3);
      // drop = k·(T − ambient)  →  ambient = mean(T) − mean(drop)/k
      double ambient = Math.Clamp(xs.Average() - ys.Average() / k, -10, 45);
      model = model with { LossCoefficientPerHour = k, AmbientTempC = ambient };

      // Usage: what an hour of the day loses beyond the standby loss.
      var usage = new double[24];
      foreach (var g in windows.GroupBy(w => w.Hour))
        if (g.Count() >= 4)
          usage[g.Key] = Math.Clamp(g.Average(w => w.DropKPerHour - Math.Max(MinLossKPerHour, k * (w.AvgTemp - ambient))), 0, 5);
      model = model with { UsageKPerHour = usage };
    }
    return model;
  }

  /// <summary>One standby hour: its hour of day, mean temperature and temperature drop.</summary>
  public readonly record struct StandbyWindow(int Hour, double AvgTemp, double DropKPerHour);

  /// <summary>One-hour windows (every 15 min) that don't touch a heating run or its settling.</summary>
  public static List<StandbyWindow> StandbyWindows(IReadOnlyList<(DateTime Time, double Temp)> temps, IReadOnlyList<HeatingRun> runs)
  {
    var result = new List<StandbyWindow>();
    if (temps.Count < 2) return result;
    bool NearHeating(DateTime a, DateTime b) =>
      runs.Any(r => r.Start - TimeSpan.FromMinutes(5) < b && r.End + AfterRunSettle > a);

    var times = temps.Select(t => t.Time).ToList();
    double At(DateTime t)
    {
      int i = times.BinarySearch(t);
      if (i < 0) i = ~i - 1;
      return temps[Math.Max(i, 0)].Temp;
    }
    var first = temps[0].Time.RoundToNearestQuarterHour().AddMinutes(15);
    for (var a = first; a + LossWindow <= temps[^1].Time; a = a.AddMinutes(15))
    {
      var b = a + LossWindow;
      if (NearHeating(a, b)) continue;
      double ta = At(a), tb = At(b);
      result.Add(new StandbyWindow(a.Hour, (ta + tb) / 2, (ta - tb) / LossWindow.TotalHours));
    }
    return result;
  }

  /// <summary>Least squares y = slope·x + intercept; null when x has no spread.</summary>
  public static (double Slope, double Intercept)? LinearFit(IReadOnlyList<double> xs, IReadOnlyList<double> ys)
  {
    double mx = xs.Average(), my = ys.Average();
    double sxx = 0, sxy = 0;
    for (int i = 0; i < xs.Count; i++)
    {
      sxx += (xs[i] - mx) * (xs[i] - mx);
      sxy += (xs[i] - mx) * (ys[i] - my);
    }
    if (sxx < 1e-9) return null;
    double slope = sxy / sxx;
    return (slope, my - slope * mx);
  }
}
