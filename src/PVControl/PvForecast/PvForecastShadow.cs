using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NetDeamon.apps.PVControl.PvForecast
{
  /// <summary>One source's forecast for today and tomorrow, raw and corrected, kWh.</summary>
  /// <param name="Source">The source.</param>
  /// <param name="Run">Its forecast run.</param>
  /// <param name="FetchedAt">When it was read.</param>
  /// <param name="TodayRaw">Today, raw.</param>
  /// <param name="TodayCorrected">Today, corrected.</param>
  /// <param name="TomorrowRaw">Tomorrow, raw.</param>
  /// <param name="TomorrowCorrected">Tomorrow, corrected (the expected value).</param>
  /// <param name="TomorrowCautious">Tomorrow, cautious: the band's low end corrected, else the low percentile of the ratios.</param>
  /// <param name="TomorrowHigh">Tomorrow, the band's high end corrected; null without a band.</param>
  /// <param name="Correction">Tomorrow's correction.</param>
  /// <param name="MaeRaw">Mean absolute error of the raw day-ahead forecast over the backtest window, kWh.</param>
  /// <param name="MaeCorrected">The same, corrected.</param>
  /// <param name="BacktestDays">Days of the backtest.</param>
  public sealed record SourceReport(string Source, string? Run, DateTime? FetchedAt, double? TodayRaw, double? TodayCorrected,
    double? TomorrowRaw, double? TomorrowCorrected, double? TomorrowCautious, double? TomorrowHigh, Correction Correction,
    double? MaeRaw, double? MaeCorrected, int BacktestDays);

  /// <summary>
  /// The PV forecast shadow: reads every configured source, notes each one's forecast for tomorrow once in the evening
  /// (<see cref="PvForecastConfig.SnapshotHour"/>), scores the ended days against the measured PV, logs weather services'
  /// measured days, and corrects each source by what it learned (<see cref="PvForecastCorrection"/>). Published only —
  /// the planners keep the raw forecast.
  /// </summary>
  /// <param name="config">Configuration.</param>
  /// <param name="sources">The sources (the first is the main one).</param>
  /// <param name="log">The log.</param>
  /// <param name="actualOfDay">The measured PV (Wh) and temperature 09–14 h of an ended day; null while incomplete.</param>
  /// <param name="tempForecast">Forecast temperature 09–14 h of a day; null if not covered.</param>
  public sealed class PvForecastShadow(PvForecastConfig config, IReadOnlyList<IPvForecastSource> sources, PvForecastLog log,
    Func<DateTime, (double PvWh, double? TempDay)?> actualOfDay, Func<DateTime, double?> tempForecast)
  {
    /// <summary>Days back the scoring and the measured-day reading look for gaps.</summary>
    public const int CatchUpDays = 10;
    /// <summary>Days of scored history read for the correction and the backtest.</summary>
    public const int HistoryDays = 90;
    private readonly Dictionary<string, SourceForecast> _latest = [];
    private DateTime _lastScoring = DateTime.MinValue, _lastObserving = DateTime.MinValue;

    /// <summary>The sources.</summary>
    public IReadOnlyList<IPvForecastSource> Sources => sources;

    /// <summary>The last report, one entry per source in the configured order.</summary>
    public IReadOnlyList<SourceReport> Last { get; private set; } = [];

    /// <summary>The main source's backtest (day, forecast, corrected, actual — kWh), the newest last.</summary>
    public IReadOnlyList<(DateTime Day, double Forecast, double Corrected, double Actual)> Recent { get; private set; } = [];

    /// <summary>The newest measured values of a weather service (day and values).</summary>
    public (DateTime Day, IReadOnlyDictionary<string, double> Values)? LastObservation { get; private set; }

    /// <summary>The day whose forecast the evening snapshot notes: tomorrow.</summary>
    /// <param name="now">Current time.</param>
    public static DateTime SnapshotDay(DateTime now) => now.Date.AddDays(1);

    /// <summary>Whether the snapshot hour is now.</summary>
    /// <param name="now">Current time.</param>
    /// <param name="hour">The configured hour.</param>
    public static bool InSnapshotHour(DateTime now, int hour) => now.Hour == hour;

    /// <summary>One run: fetch, snapshot, scoring, measured days, report.</summary>
    /// <param name="now">Current time.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task RunAsync(DateTime now, CancellationToken ct)
    {
      foreach (var s in sources)
        if (await s.GetAsync(now, ct) is { } f) _latest[s.Name] = f;

      if (InSnapshotHour(now, config.SnapshotHour))
        await SnapshotAsync(now);

      if (now - _lastScoring >= TimeSpan.FromMinutes(15))
      {
        _lastScoring = now;
        foreach (var day in await log.UnscoredDaysAsync(now.Date.AddDays(-CatchUpDays), now.Date))
          if (actualOfDay(day) is { } a)
            await log.SetActualAsync(day, a.PvWh, a.TempDay);
      }

      if (now - _lastObserving >= TimeSpan.FromHours(1))
      {
        _lastObserving = now;
        foreach (var o in sources.OfType<IPvObservationSource>())
          foreach (var day in await log.UnobservedDaysAsync(now.Date.AddDays(-CatchUpDays), "inca_kwh_m2"))
          {
            var values = await o.ObserveDayAsync(day, ct);
            await log.SetObservationsAsync(day, values);
            if (values.Count > 0 && (LastObservation is not { } l || day >= l.Day)) LastObservation = (day, values);
          }
      }

      await ReportAsync(now);
    }

    /// <summary>Notes tomorrow's forecast of every source once.</summary>
    private async Task SnapshotAsync(DateTime now)
    {
      var day = SnapshotDay(now);
      foreach (var s in sources)
      {
        if (!_latest.TryGetValue(s.Name, out var f) || SourceForecast.DayTotal(f.Wh, day) is not { } total
            || await log.HasDayAheadAsync(day, s.Name))
          continue;
        await log.NoteAsync(new DayAhead(day, s.Name, now, f.Run, total, SourceForecast.DayTotal(f.LowWh, day),
          SourceForecast.DayTotal(f.HighWh, day), tempForecast(day), f.HoursOf(day)));
      }
    }

    private async Task ReportAsync(DateTime now)
    {
      var reports = new List<SourceReport>();
      foreach (var s in sources)
      {
        var history = await log.ScoredAsync(s.Name, now.Date.AddDays(-HistoryDays));
        _latest.TryGetValue(s.Name, out var f);
        var report = Report(s.Name, f, history, now, tempForecast, config);
        reports.Add(report);
        if (s == sources[0])
          Recent = PvForecastCorrection.Backtest(history, config).TakeLast(Math.Max(1, config.WindowDays))
            .Select(b => (b.Day, Kwh(b.Forecast), Kwh(b.Corrected), Kwh(b.Actual))).ToList();
      }
      Last = reports;
    }

    /// <summary>A source's report: today and tomorrow raw and corrected, the cautious value, the backtest's errors.</summary>
    /// <param name="name">The source.</param>
    /// <param name="f">Its current forecast; null if none yet.</param>
    /// <param name="history">Its scored days.</param>
    /// <param name="now">Current time.</param>
    /// <param name="tempForecast">Forecast temperature of a day.</param>
    /// <param name="config">Configuration.</param>
    public static SourceReport Report(string name, SourceForecast? f, IReadOnlyList<ScoredDay> history, DateTime now,
      Func<DateTime, double?> tempForecast, PvForecastConfig config)
    {
      var today = now.Date;
      var tomorrow = today.AddDays(1);
      var cToday = PvForecastCorrection.For(history, today, tempForecast(today), config);
      var cTomorrow = PvForecastCorrection.For(history, tomorrow, tempForecast(tomorrow), config);
      double? todayRaw = SourceForecast.DayTotal(f?.Wh, today), tomorrowRaw = SourceForecast.DayTotal(f?.Wh, tomorrow);
      double? low = SourceForecast.DayTotal(f?.LowWh, tomorrow), high = SourceForecast.DayTotal(f?.HighWh, tomorrow);
      double? cautious = low is { } l ? l * cTomorrow.Factor : tomorrowRaw * cTomorrow.CautiousFactor;
      var backtest = PvForecastCorrection.Backtest(history, config).TakeLast(Math.Max(1, config.WindowDays)).ToList();
      return new SourceReport(name, f?.Run, f?.FetchedAt,
        Kwh(todayRaw), Kwh(todayRaw * cToday.Factor), Kwh(tomorrowRaw), Kwh(tomorrowRaw * cTomorrow.Factor), Kwh(cautious), Kwh(high * cTomorrow.Factor),
        cTomorrow,
        backtest.Count == 0 ? null : Kwh(backtest.Average(b => Math.Abs(b.Forecast - b.Actual))),
        backtest.Count == 0 ? null : Kwh(backtest.Average(b => Math.Abs(b.Corrected - b.Actual))),
        backtest.Count);
    }

    private static double Kwh(double wh) => Math.Round(wh / 1000, 2);
    private static double? Kwh(double? wh) => wh is { } w ? Kwh(w) : null;
  }
}
