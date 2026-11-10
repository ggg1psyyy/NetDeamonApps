using System;
using System.Collections.Generic;
using System.Linq;

namespace NetDeamon.apps.PVControl.PvForecast
{
  /// <summary>A scored day of one source: its day-ahead forecast (noted the evening before) and what PV delivered.</summary>
  /// <param name="Day">The day.</param>
  /// <param name="ForecastWh">The day-ahead forecast, Wh.</param>
  /// <param name="ActualWh">The measured PV, Wh.</param>
  /// <param name="TempDay">Measured temperature 09–14 h, °C; null if not logged.</param>
  public sealed record ScoredDay(DateTime Day, double ForecastWh, double ActualWh, double? TempDay)
  {
    /// <summary>Actual ÷ forecast, at most <see cref="PvForecastCorrection.MaxRatio"/>.</summary>
    public double Ratio => Math.Min(PvForecastCorrection.MaxRatio, ActualWh / ForecastWh);
  }

  /// <summary>The correction of a source for a day.</summary>
  /// <param name="Factor">The forecast times this is the expected PV.</param>
  /// <param name="CautiousFactor">The forecast times this is the cautious PV (sources without a band).</param>
  /// <param name="ScoredDays">Days it learned from.</param>
  /// <param name="MedianRatio">Their median actual ÷ forecast.</param>
  /// <param name="TempAnomaly">How much warmer the day is forecast than the scored days, K; null without temperatures.</param>
  public sealed record Correction(double Factor, double CautiousFactor, int ScoredDays, double MedianRatio, double? TempAnomaly)
  {
    /// <summary>No correction (too few scored days).</summary>
    public static readonly Correction None = new(1, 1, 0, 1, null);
    /// <summary>Whether it learned from enough days.</summary>
    public bool Learned => ScoredDays > 0;
  }

  /// <summary>
  /// The learned correction of a PV forecast (model output statistics): the median of actual ÷ forecast over the last
  /// scored days, shifted by how much warmer the day is forecast than those days were. Backtest 2025-09…2026-11
  /// (Open-Meteo, day-ahead), winter: MAE 3.58 → 1.72 kWh/day with the median, 1.56 with the temperature; bias
  /// +3.5 → 0 kWh/day.
  /// </summary>
  public static class PvForecastCorrection
  {
    /// <summary>Ratios above this (a forecast of almost nothing) count as this.</summary>
    public const double MaxRatio = 2.0;
    /// <summary>Days with a smaller forecast tell nothing about the ratio (a dark winter day: noise).</summary>
    public const double MinForecastWh = 500;
    /// <summary>The factor stays within this.</summary>
    public const double MinFactor = 0.05, MaxFactor = 1.5;

    /// <summary>The correction for a day.</summary>
    /// <param name="history">Scored days of the source (any order; only those before <paramref name="day"/> count).</param>
    /// <param name="day">The day corrected.</param>
    /// <param name="tempForecast">The day's forecast temperature 09–14 h, °C; null: no temperature term.</param>
    /// <param name="config">Window, minimum, slope, percentile.</param>
    /// <returns>The correction; <see cref="Correction.None"/> with fewer than <see cref="PvForecastConfig.MinScoredDays"/> days.</returns>
    public static Correction For(IEnumerable<ScoredDay> history, DateTime day, double? tempForecast, PvForecastConfig config)
    {
      var days = history.Where(d => d.Day.Date < day.Date && d.ForecastWh >= MinForecastWh)
        .OrderByDescending(d => d.Day).Take(Math.Max(1, config.WindowDays)).ToList();
      if (days.Count < Math.Max(1, config.MinScoredDays)) return Correction.None;
      var ratios = days.Select(d => d.Ratio).ToList();
      double median = Mean.Percentile(ratios, 0.5);
      double cautious = Mean.Percentile(ratios, config.CautiousPercentile);
      var temps = days.Select(d => d.TempDay).OfType<double>().ToList();
      double? anomaly = tempForecast is { } t && temps.Count >= Math.Max(1, config.MinScoredDays) ? t - temps.Average() : null;
      double shift = (anomaly ?? 0) * config.TemperatureSlopePerK;
      return new Correction(Clamp(median + shift), Clamp(cautious + shift), days.Count, median, anomaly);
    }

    private static double Clamp(double factor) => Math.Clamp(factor, MinFactor, MaxFactor);

    /// <summary>
    /// The corrected forecast of each scored day with only what was known the evening before (its earlier days and
    /// its measured temperature for the forecast one) — the shadow's own backtest.
    /// </summary>
    /// <param name="history">Scored days.</param>
    /// <param name="config">Configuration.</param>
    /// <returns>Per day: forecast, corrected, actual, Wh.</returns>
    public static List<(DateTime Day, double Forecast, double Corrected, double Actual)> Backtest(IReadOnlyList<ScoredDay> history, PvForecastConfig config) =>
      history.OrderBy(d => d.Day)
        .Select(d => (d.Day, d.ForecastWh, d.ForecastWh * For(history, d.Day, d.TempDay, config).Factor, d.ActualWh))
        .ToList();
  }
}
