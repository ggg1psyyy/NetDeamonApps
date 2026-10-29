using System;
using System.Collections.Generic;
using System.Linq;
using LinqToDB;
using NetDeamon.apps.PVControl.Predictions;
using PVControl;
using static NetDeamon.apps.PVControl.PVControlCommon;

namespace NetDeamon.apps.PVControl.LoadModel;

/// <summary>
/// The weather-aware load forecast as a <see cref="Prediction"/>: <see cref="WeatherLoadModel"/> fitted on
/// 400 days of DataLogger history with the forecast temperature ahead; the wrapped hour-of-day
/// average without a usable forecast or history (<see cref="Source"/> says which).
/// </summary>
public sealed class WeatherLoadPrediction : Prediction
{
  const int HistoryDays = 400;
  readonly Prediction _average;
  readonly WeatherForecast _weather;
  readonly LoadForecastConfig _config;
  readonly string _dbLocation;
  readonly IReadOnlyList<string> _excludeColumns;

  /// <summary>Creates the forecast and seeds the heat pump mode from the history.</summary>
  /// <param name="average">Hour-of-day average (the fallback).</param>
  /// <param name="weather">Weather forecast.</param>
  /// <param name="config">Load forecast configuration.</param>
  /// <param name="dbLocation">Path of the energy history DB.</param>
  /// <param name="excludeColumns">DB columns subtracted from the house load.</param>
  public WeatherLoadPrediction(Prediction average, WeatherForecast weather, LoadForecastConfig config, string dbLocation, IReadOnlyList<string> excludeColumns)
  {
    _average = average;
    _weather = weather;
    _config = config;
    _dbLocation = dbLocation;
    _excludeColumns = excludeColumns;
    _mode = new HeatPumpModeTracker(config.HeatingModes);
    SeedModeFromHistory();
    Initialize("Weather Load Forecast");
  }

  readonly HeatPumpModeTracker _mode;

  /// <summary>Follows the heat pump mode (every cycle, so a switch counts within 15 s).</summary>
  /// <param name="now">Current time.</param>
  public void ObserveMode(DateTime now) => _mode.Observe(now, _config.HeatPumpModeEntity?.State);

  /// <summary>The heat pump mode changed since the last calculation (recompute now, not in up to an hour).</summary>
  public bool ModeChanged => _config.HeatPumpModeEntity is not null && _mode.HeatingOn is { } on && on != HeatingOn && Model is not null;

  /// <summary>Seeds the heat pump mode from DataLogger's last 72 hourly modes (unknown without DB or column).</summary>
  void SeedModeFromHistory()
  {
    if (_config.HeatPumpModeEntity is null) return;
    try
    {
      using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_dbLocation};Mode=ReadOnly;Pooling=False");
      connection.Open();
      using var command = connection.CreateCommand();
      command.CommandText = "SELECT timestamp, heatingmode FROM hourly WHERE heatingmode IS NOT NULL ORDER BY timestamp DESC LIMIT 72";
      using var reader = command.ExecuteReader();
      var rows = new List<(DateTime, string?)>();
      while (reader.Read())
      {
        var end = Extensions.FromISO8601(reader.GetString(0));
        rows.Add((end.Floor(60), reader.GetString(1)));
      }
      rows.Reverse();
      _mode.Seed(rows);
    }
    catch (Exception ex)
    {
      PVCC_Logger.LogInformation("Weather load forecast: heat pump mode history not available ({Error})", ex.Message);
    }
  }

  /// <summary>"model" or why the average is used.</summary>
  public string Source { get; private set; } = "not computed yet";
  /// <summary>The fitted model; null when the average is used.</summary>
  public WeatherLoadModel? Model { get; private set; }
  /// <summary>Heating counted as on in the last calculation.</summary>
  public bool HeatingOn { get; private set; }
  /// <summary>Per hour (local start) of the window: base load, heat pump part (Wh), 24-h mean temperature.</summary>
  public IReadOnlyList<(DateTime Hour, double Base, double HeatPump, double Temp24)> Hours { get; private set; } = [];

  /// <summary>The model's forecast, or the average when the weather forecast is stale or the history shorter than 30 days.</summary>
  /// <param name="now">Day of the window.</param>
  /// <returns>Wh by slot start.</returns>
  protected override Dictionary<DateTime, int> PopulateData(DateTime now)
  {
    if (HouseEnergy.NeedsDayRollover(_average.TodayAndTomorrow, now))
      _average.UpdateData(now);
    try
    {
      if (_weather.IsStale(now))
        return Fallback($"average (weather forecast {(_weather.LastUpdate is { } t ? $"from {t:HH:mm}" : "missing")})");
      var history = LoadHistory(now);
      if (history.Count < 24 * 30)
        return Fallback($"average (only {history.Count} hours of history)");
      return Compute(history, now);
    }
    catch (Exception ex)
    {
      PVCC_Logger.LogWarning(ex, "Weather load forecast failed, using the average");
      return Fallback($"average ({ex.Message})");
    }
  }

  /// <summary>Uses the hour-of-day average.</summary>
  /// <param name="why">Reason for <see cref="Source"/>.</param>
  /// <returns>The average's window.</returns>
  Dictionary<DateTime, int> Fallback(string why)
  {
    Source = why;
    Model = null;
    Hours = [];
    return new Dictionary<DateTime, int>(_average.TodayAndTomorrow);
  }

  /// <summary>Fits the model and forecasts today and tomorrow per hour (measured temperatures, then the forecast).</summary>
  /// <param name="history">History hours, in time order.</param>
  /// <param name="now">Current time.</param>
  /// <returns>Wh by slot start.</returns>
  Dictionary<DateTime, int> Compute(List<LoadHour> history, DateTime now)
  {
    // One hourly temperature series from the first history hour to the end of tomorrow:
    // measured where logged, the forecast after that.
    var start = history[0].Local;
    var end = now.Date.AddDays(2);
    var measured = new Dictionary<DateTime, double?>();
    foreach (var h in history)
      measured[h.Local] = h.OutdoorTemp; // the repeated hour at the end of DST: last one wins
    var temps = new List<double?>();
    for (var t = start; t < end; t = t.AddHours(1))
      temps.Add(measured.TryGetValue(t, out var m) && m is not null ? m : _weather.Temperature(t));
    var temp24 = WeatherLoadModel.Rolling24(temps);
    double Temp24At(DateTime hour) => temp24[(int)(hour - start).TotalHours];

    var historyTemp24 = history.Select(h => Temp24At(h.Local)).ToList();
    // Without a mode entity (or never known yet) heating counts as on — the 48-h scaling then
    // follows what the heat pump really does (backtest model M2).
    ObserveMode(now);
    HeatingOn = _config.HeatPumpModeEntity is null || (_mode.HeatingOn ?? true);
    DateTime? heatingOnSince = HeatingOn ? _mode.HeatingOnSince : null;
    var model = WeatherLoadModel.Fit(history, historyTemp24, now, heatingOnSince);

    var hours = new List<(DateTime, double, double, double)>();
    var data = new Dictionary<DateTime, int>();
    for (var hour = now.Date; hour < end; hour = hour.AddHours(1))
    {
      double t24 = Temp24At(hour);
      double baseWh = model.BaseAt(hour.Hour), hpWh = model.HeatDemand.Expected(hour.Hour, t24, HeatingOn);
      hours.Add((hour, baseWh, hpWh, t24));
      for (int q = 0; q < 4; q++)
        data[hour.AddMinutes(15 * q)] = Energy.SlotWh(baseWh + hpWh); // hourly Wh = mean W
    }
    Model = model;
    Hours = hours;
    Source = "model";
    return data;
  }

  /// <summary>Measured load of a night (<see cref="NightTotal"/>).</summary>
  /// <param name="night">Start date of the night.</param>
  /// <param name="now">Current time (history end).</param>
  /// <returns>Wh; null until DataLogger has all hours.</returns>
  public double? ActualNightSum(DateTime night, DateTime now) => NightTotal(LoadHistory(now), night, TimeZoneInfo.Local);

  /// <summary>Load of a night 22:00–08:00 from logged hours; DST: the repeated hour counts twice, the skipped one isn't missing.</summary>
  /// <param name="hours">Logged hours.</param>
  /// <param name="night">Start date of the night.</param>
  /// <param name="zone">Time zone (DST).</param>
  /// <returns>Wh; null unless every existing hour is there.</returns>
  public static double? NightTotal(IEnumerable<LoadHour> hours, DateTime night, TimeZoneInfo zone)
  {
    var (from, to) = NightForecastTracker.NightWindow(night);
    var inNight = hours.Where(h => TimeRange.Contains(from, to, h.Local)).ToList();
    var expected = Enumerable.Range(0, 10).Select(k => from.AddHours(k)).Where(h => !zone.IsInvalidTime(h));
    return expected.All(h => inNight.Any(x => x.Local == h)) ? inNight.Sum(h => h.Total) : null;
  }

  /// <summary>DataLogger's hourly rows of the last <see cref="HistoryDays"/> days as load hours (corrupt rows skipped), in time order.</summary>
  /// <param name="now">Current time (history end).</param>
  List<LoadHour> LoadHistory(DateTime now)
  {
    var from = now.Date.AddDays(-HistoryDays);
    using var db = new EnergyHistoryDb(SqliteOptions.ForFile(_dbLocation));
    var rows = db.Hourlies
      .Where(h => h.Houseenergy != null)
      .Select(h => new { h.Timestamp, h.Houseenergy, h.Carcharge, h.Warmwaterenergy, h.Heatpumpenergy, h.Temperatureoutside })
      .AsEnumerable();
    var result = new List<LoadHour>();
    foreach (var r in rows)
    {
      var hour = r.Timestamp.AsLocal().Floor(60);
      if (hour < from || hour >= now) continue;
      double excluded = HourlyWeightedAverageLoadPrediction.ExcludedEnergy(_excludeColumns, r.Carcharge, r.Warmwaterenergy, r.Heatpumpenergy);
      if (LoadHour.FromRow(hour, r.Houseenergy, excluded, r.Heatpumpenergy, r.Warmwaterenergy, r.Temperatureoutside) is { } h)
        result.Add(h);
    }
    return result.OrderBy(h => h.Local).ToList();
  }
}
