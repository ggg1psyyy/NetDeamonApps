using System;
using System.Collections.Generic;
using System.Linq;
using LinqToDB;
using NetDeamon.apps.PVControl.Predictions;
using PVControl;
using static NetDeamon.apps.PVControl.PVControlCommon;

namespace NetDeamon.apps.PVControl.LoadModel;

/// <summary>
/// The weather-aware load forecast as a <see cref="Prediction"/>: <see cref="WeatherLoadModel"/>
/// fitted on DataLogger's history (400 days), the hours ahead with the forecast temperature, the
/// heat pump part switched by the heat pump mode. Without a usable weather forecast or history it
/// delivers the hour-of-day average it wraps (<see cref="Source"/> says which), so PVControl can
/// use it in place of the average.
/// </summary>
public sealed class WeatherLoadPrediction : Prediction
{
  const int HistoryDays = 400;
  readonly Prediction _average;
  readonly WeatherForecast _weather;
  readonly LoadForecastConfig _config;
  readonly string _dbLocation;
  readonly IReadOnlyList<string> _excludeColumns;

  public WeatherLoadPrediction(Prediction average, WeatherForecast weather, LoadForecastConfig config, string dbLocation, IReadOnlyList<string> excludeColumns)
  {
    _average = average;
    _weather = weather;
    _config = config;
    _dbLocation = dbLocation;
    _excludeColumns = excludeColumns;
    Initialize("Weather Load Forecast");
  }

  /// <summary>"model" or why the average is used.</summary>
  public string Source { get; private set; } = "not computed yet";
  public WeatherLoadModel? Model { get; private set; }
  public bool HeatingOn { get; private set; }
  /// <summary>Per hour (local start) of the window: base load, heat pump part (Wh), 24-h mean temperature.</summary>
  public IReadOnlyList<(DateTime Hour, double Base, double HeatPump, double Temp24)> Hours { get; private set; } = [];

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

  Dictionary<DateTime, int> Fallback(string why)
  {
    Source = why;
    Model = null;
    Hours = [];
    return new Dictionary<DateTime, int>(_average.TodayAndTomorrow);
  }

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
    string? mode = _config.HeatPumpModeEntity?.State;
    HeatingOn = _config.HeatPumpModeEntity is null || (mode is not null && _config.HeatingModes.Contains(mode));
    DateTime? heatingOnSince = HeatingOn && _config.HeatPumpModeEntity?.EntityState?.LastChanged is { } changed
      ? changed.ToLocalTime() : null;
    var model = WeatherLoadModel.Fit(history, historyTemp24, now, heatingOnSince);

    var hours = new List<(DateTime, double, double, double)>();
    var data = new Dictionary<DateTime, int>();
    for (var hour = now.Date; hour < end; hour = hour.AddHours(1))
    {
      double t24 = Temp24At(hour);
      double baseWh = model.BaseAt(hour.Hour), hpWh = model.HeatDemand.Expected(hour.Hour, t24, HeatingOn);
      hours.Add((hour, baseWh, hpWh, t24));
      for (int q = 0; q < 4; q++)
        data[hour.AddMinutes(15 * q)] = (int)Math.Round((baseWh + hpWh) / 4);
    }
    Model = model;
    Hours = hours;
    Source = "model";
    return data;
  }

  /// <summary>The load really used in the night 22:00–08:00 starting on <paramref name="night"/>
  /// (Wh, same definition as the forecast); null until DataLogger has all 10 hours.</summary>
  public double? ActualNightSum(DateTime night, DateTime now)
  {
    var from = night.AddHours(22);
    var hours = LoadHistory(now).Where(h => h.Local >= from && h.Local < from.AddHours(10))
      .GroupBy(h => h.Local).Select(g => g.First()).ToList();
    return hours.Count == 10 ? hours.Sum(h => h.Total) : null;
  }

  /// <summary>DataLogger's hourly rows of the last <see cref="HistoryDays"/> days as load hours
  /// (corrupt rows skipped), in time order. The row stamped hh:59:59 covers hour hh.</summary>
  List<LoadHour> LoadHistory(DateTime now)
  {
    var from = now.Date.AddDays(-HistoryDays);
    using var db = new EnergyHistoryDb(new DataOptions().UseSQLite($"Data Source={_dbLocation}"));
    var rows = db.Hourlies
      .Where(h => h.Houseenergy != null)
      .Select(h => new { h.Timestamp, h.Houseenergy, h.Carcharge, h.Warmwaterenergy, h.Heatpumpenergy, h.Temperatureoutside })
      .AsEnumerable();
    var result = new List<LoadHour>();
    foreach (var r in rows)
    {
      var local = r.Timestamp.Kind == DateTimeKind.Utc ? r.Timestamp.ToLocalTime() : r.Timestamp;
      var hour = new DateTime(local.Year, local.Month, local.Day, local.Hour, 0, 0);
      if (hour < from || hour >= now) continue;
      double excluded = _excludeColumns.Sum(c => c switch
      {
        "carcharge" => r.Carcharge ?? 0,
        "warmwaterenergy" => r.Warmwaterenergy ?? 0,
        "heatpumpenergy" => r.Heatpumpenergy ?? 0,
        _ => 0,
      });
      if (LoadHour.FromRow(hour, r.Houseenergy, excluded, r.Heatpumpenergy, r.Warmwaterenergy, r.Temperatureoutside) is { } h)
        result.Add(h);
    }
    return result.OrderBy(h => h.Local).ToList();
  }
}
