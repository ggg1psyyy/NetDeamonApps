using System;
using System.Collections.Generic;
using System.Linq;

namespace NetDeamon.apps.PVControl.PvForecast
{
  /// <summary>
  /// The PV forecast the planners use under <see cref="PvForecastMode.Steer"/>: Wh per 15-min slot, today 00:00 → the end
  /// of tomorrow (and the day after, as far as a source covers it: the window after midnight), each source's own Wh times
  /// its learned factor for the slot's day.
  /// </summary>
  /// <param name="Source">The source that steers (the first usable one in the configured order).</param>
  /// <param name="Run">Its forecast run, if it names one.</param>
  /// <param name="FetchedAt">When it was last read.</param>
  /// <param name="Wh">Corrected Wh by quarter start; a quarter no usable source covers is missing (the planners keep
  /// the raw forecast there).</param>
  /// <param name="Fallback">The steering source isn't the first configured one (the first is stale, unlearned or short).</param>
  /// <param name="Reason">Why the first source doesn't steer; null when it does.</param>
  public sealed record SteeringForecast(string Source, string? Run, DateTime FetchedAt, IReadOnlyDictionary<DateTime, int> Wh,
    bool Fallback, string? Reason);

  /// <summary>
  /// Builds the steering forecast from the sources' held forecasts (pure). A source steers when its last fetch is at most
  /// <see cref="PvForecastConfig.SteerMaxAgeHours"/> old, it learned a correction for tomorrow and covers tomorrow's
  /// afternoon; the first such source in the configured order steers. Each quarter takes the first usable source that has
  /// it, the steering one first — a source whose run starts in the middle of the day (GeoSphere's runs start at their
  /// time, the hours before are gone) is completed by the next one, corrected too, never raw.
  /// </summary>
  public static class PvSteering
  {
    /// <summary>The source must have a slot from this hour of tomorrow on to cover tomorrow's PV.</summary>
    public const int CoversTomorrowFromHour = 15;

    /// <summary>A held forecast with a new run merged in: the new run's slots win, the held ones it lacks stay (the hours
    /// of today a later run no longer has), slots before <paramref name="keepFrom"/> dropped.</summary>
    /// <param name="held">The forecast held so far; null if none.</param>
    /// <param name="fresh">The new one.</param>
    /// <param name="keepFrom">Start of today.</param>
    /// <returns>The merged forecast (the new run's time, fetch and name; the band of the new run only).</returns>
    public static SourceForecast Merge(SourceForecast? held, SourceForecast fresh, DateTime keepFrom)
    {
      if (held is null) return fresh;
      var wh = new SortedDictionary<DateTime, double>();
      foreach (var (t, v) in held.Wh) if (t >= keepFrom) wh[t] = v;
      foreach (var (t, v) in fresh.Wh) wh[t] = v;
      return fresh with { Wh = wh };
    }

    /// <summary>A forecast per 15-min slot: hourly values split evenly into their quarters, quarter values as they are.</summary>
    /// <param name="slots">Wh by slot start (15 min or 1 h).</param>
    /// <returns>Wh by quarter start.</returns>
    public static Dictionary<DateTime, double> Quarters(IReadOnlyDictionary<DateTime, double> slots)
    {
      var keys = slots.Keys.OrderBy(t => t).ToList();
      var step = TimeSpan.FromHours(1);
      for (int i = 1; i < keys.Count; i++)
        if (keys[i] - keys[i - 1] is var d && d > TimeSpan.Zero && d < step) step = d;
      int parts = Math.Max(1, (int)Math.Round(step.TotalMinutes / TimeRange.SlotMinutes));
      var result = new Dictionary<DateTime, double>();
      foreach (var (t, v) in slots)
        for (int k = 0; k < parts; k++)
          result[t.AddMinutes(k * TimeRange.SlotMinutes)] = v / parts;
      return result;
    }

    /// <summary>One source as the steering sees it.</summary>
    /// <param name="Name">Its name.</param>
    /// <param name="Forecast">Its held forecast; null if none yet.</param>
    /// <param name="FactorOf">Its learned factor for a day; null where it learned none (too few scored days).</param>
    public sealed record Candidate(string Name, SourceForecast? Forecast, Func<DateTime, double?> FactorOf);

    /// <summary>Why a source can't steer now; null if it can.</summary>
    /// <param name="c">The source.</param>
    /// <param name="now">Current time.</param>
    /// <param name="config">Configuration (the maximum age).</param>
    public static string? Unusable(Candidate c, DateTime now, PvForecastConfig config)
    {
      var tomorrow = now.Date.AddDays(1);
      if (c.Forecast is not { } f) return "no forecast yet";
      if (now - f.FetchedAt > TimeSpan.FromHours(config.SteerMaxAgeHours)) return $"last fetch {f.FetchedAt:dd.MM HH:mm}";
      if (c.FactorOf(tomorrow) is null) return "no learned correction";
      if (!f.Wh.Keys.Any(t => t.Date == tomorrow && t.Hour >= CoversTomorrowFromHour)) return "tomorrow not covered";
      return null;
    }

    /// <summary>A day's total of the steering forecast, kWh (2 decimals); null if it has no quarter of the day.</summary>
    /// <param name="s">The steering forecast.</param>
    /// <param name="day">The day.</param>
    public static double? DayKwh(SteeringForecast s, DateTime day)
    {
      var of = s.Wh.Where(kv => kv.Key.Date == day.Date).ToList();
      return of.Count == 0 ? null : Math.Round(of.Sum(kv => kv.Value) / 1000.0, 2);
    }

    /// <summary>The steering forecast; null if no source can steer.</summary>
    /// <param name="candidates">The sources in the configured order.</param>
    /// <param name="now">Current time.</param>
    /// <param name="config">Configuration.</param>
    public static SteeringForecast? Build(IReadOnlyList<Candidate> candidates, DateTime now, PvForecastConfig config)
    {
      var usable = candidates.Where(c => Unusable(c, now, config) is null).ToList();
      if (usable.Count == 0) return null;
      var lead = usable[0];
      var quarters = usable.Select(c => (c, Q: Quarters(c.Forecast!.Wh))).ToList();
      var wh = new Dictionary<DateTime, int>();
      // Through the day after tomorrow: from 00:00 the planners' window is tomorrow and the day after, and the next run
      // can be up to 5 minutes away — without it the new tomorrow would be raw Open-Meteo (~1.5×) until then.
      for (var t = now.Date; t < now.Date.AddDays(3); t = t.AddMinutes(TimeRange.SlotMinutes))
        foreach (var (c, q) in quarters)
          if (q.TryGetValue(t, out double v) && c.FactorOf(t.Date) is { } factor)
          {
            wh[t] = (int)Math.Round(Math.Max(0, v) * factor);
            break;
          }
      bool fallback = lead != candidates[0];
      return new SteeringForecast(lead.Name, lead.Forecast!.Run, lead.Forecast.FetchedAt, wh, fallback,
        fallback ? $"{candidates[0].Name}: {Unusable(candidates[0], now, config)}" : null);
    }
  }
}
