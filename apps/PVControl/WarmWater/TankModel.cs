using System;
using System.Collections.Generic;
using System.Linq;

namespace NetDeamon.apps.PVControl.WarmWater;

/// <summary>One warm-water heating run seen in the history.</summary>
/// <param name="Start">Heating started.</param>
/// <param name="End">Heating stopped.</param>
/// <param name="StartTemp">Tank temperature at the start, °C.</param>
/// <param name="EndTemp">Tank temperature at the end, °C.</param>
/// <param name="AvgPowerW">Mean heat pump power, W; null if unknown.</param>
public readonly record struct HeatingRun(DateTime Start, DateTime End, double StartTemp, double EndTemp, double? AvgPowerW = null)
{
  /// <summary>Length of the run, hours.</summary>
  public double Hours => (End - Start).TotalHours;
}

/// <summary>
/// Thermal model of the warm-water tank, learned from the history (<see cref="Fit"/>): heating at a
/// constant rate; standby loss proportional to the difference to an apparent ambient temperature,
/// plus a usage term per hour of day; power rising slightly with the temperature.
/// </summary>
/// <param name="HeatingRateKPerHour">Temperature rise while heating, K/h.</param>
/// <param name="LossCoefficientPerHour">Standby loss per K above ambient, 1/h.</param>
/// <param name="AmbientTempC">Apparent ambient temperature, °C.</param>
/// <param name="PowerAt45W">Heating power at 45 °C, W.</param>
/// <param name="PowerSlopeWPerK">Power change per K, W/K.</param>
/// <param name="UsageKPerHour">Extra drop by usage per hour of day, K/h (24 values).</param>
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

  /// <summary>Values measured 2026-10-09 – 10-19, used until a fit has enough data.</summary>
  public static TankModel Default { get; } = new(17.3, 0.059, 28.6, 1720, 26, new double[24]);

  /// <summary>Temperature drop without heating (standby + usage, at least <see cref="MinLossKPerHour"/> standby).</summary>
  /// <param name="temp">Tank temperature, °C.</param>
  /// <param name="hour">Hour of day 0–23.</param>
  /// <returns>K/h.</returns>
  public double LossKPerHour(double temp, int hour) =>
    Math.Max(MinLossKPerHour, LossCoefficientPerHour * (temp - AmbientTempC)) + UsageKPerHour[hour];

  /// <summary>Heating power at a tank temperature (at least 100 W).</summary>
  /// <param name="temp">Tank temperature, °C.</param>
  /// <returns>W.</returns>
  public double PowerW(double temp) => Math.Max(100, PowerAt45W + PowerSlopeWPerK * (temp - 45));

  /// <summary>Temperature after a time without heating.</summary>
  /// <param name="temp">Start temperature, °C.</param>
  /// <param name="time">Start time (hour of day for the usage term).</param>
  /// <param name="hours">Duration, hours.</param>
  /// <returns>°C.</returns>
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

  /// <summary>Learns the model from history; every part with too little data keeps the fallback's value.</summary>
  /// <param name="temps">(time, °C) readings in time order.</param>
  /// <param name="runs">Heating runs (automatic or one-time).</param>
  /// <param name="fallback">Values for parts without enough data.</param>
  /// <returns>The model.</returns>
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
      // The line through the runs' mean at a slope, read at 45 °C (the fitted slope, or the model's when the runs don't spread enough).
      double PowerAt45(double slope) => Math.Clamp(ys.Average() + slope * (45 - xs.Average()), 300, 5000);
      if (xs.Max() - xs.Min() >= MinSpreadForSlopeK && Regression.LinearFit(xs, ys) is { } fit)
      {
        double slope = Math.Clamp(fit.Slope, 0, 200);
        model = model with { PowerSlopeWPerK = slope, PowerAt45W = PowerAt45(slope) };
      }
      else
        model = model with { PowerAt45W = PowerAt45(model.PowerSlopeWPerK) };
    }

    // Standby loss: drop over one-hour windows (stepped every 15 min) away from any heating.
    var windows = StandbyWindows(temps, runs);
    if (windows.Count >= 20)
    {
      var xs = windows.Select(w => w.AvgTemp).ToList();
      var ys = windows.Select(w => w.DropKPerHour).ToList();
      double k = model.LossCoefficientPerHour;
      if (xs.Max() - xs.Min() >= MinSpreadForSlopeK && Regression.LinearFit(xs, ys) is { Slope: > 0 } fit)
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

  /// <summary>One standby hour for the loss fit.</summary>
  /// <param name="Hour">Hour of day 0–23.</param>
  /// <param name="AvgTemp">Mean tank temperature, °C.</param>
  /// <param name="DropKPerHour">Temperature drop, K/h.</param>
  public readonly record struct StandbyWindow(int Hour, double AvgTemp, double DropKPerHour);

  /// <summary>One-hour windows (every 15 min) that don't touch a heating run or its settling time.</summary>
  /// <param name="temps">(time, °C) readings in time order.</param>
  /// <param name="runs">Heating runs.</param>
  /// <returns>The windows.</returns>
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
}
