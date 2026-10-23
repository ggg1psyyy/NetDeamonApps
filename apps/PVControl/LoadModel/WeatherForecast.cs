using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using NetDaemon.HassModel.Entities;
using static NetDeamon.apps.PVControl.PVControlCommon;

namespace NetDeamon.apps.PVControl.LoadModel;

/// <summary>One forecast hour (local start time).</summary>
public readonly record struct HourlyWeather(DateTime Local, double Temperature, double? CloudCoverage, double? WindSpeed);

/// <summary>
/// Hourly weather forecast from a weather entity (weather.get_forecasts, type hourly — Pirate
/// Weather gives 168 h), fetched every <see cref="LoadForecastConfig.WeatherRefreshMinutes"/> and
/// kept when a fetch fails. The load model needs the temperature of the coming hours, not the
/// measured one.
/// </summary>
public sealed class WeatherForecast(LoadForecastConfig config)
{
  /// <summary>Older than this, the forecast is reported (health) and not used any more.</summary>
  public static readonly TimeSpan MaxAge = TimeSpan.FromHours(3);

  public IReadOnlyList<HourlyWeather> Hours { get; private set; } = [];
  public DateTime? LastUpdate { get; private set; }
  string? _lastError;

  public bool IsStale(DateTime now) => LastUpdate is not { } t || now - t > MaxAge;

  /// <summary>Forecast temperature of the hour containing <paramref name="local"/>; null if the
  /// forecast doesn't cover it.</summary>
  public double? Temperature(DateTime local) => TemperatureAt(Hours, local);

  public static double? TemperatureAt(IReadOnlyList<HourlyWeather> hours, DateTime local)
  {
    var hourStart = local.Floor(60);
    foreach (var h in hours)
      if (h.Local == hourStart) return h.Temperature;
    return null;
  }

  /// <summary>Sets the forecast directly (as fetched at <paramref name="at"/>), e.g. in tests.</summary>
  public void Use(IReadOnlyList<HourlyWeather> hours, DateTime at)
  {
    Hours = hours;
    LastUpdate = at;
  }

  /// <summary>Wait after a failed fetch before the next try (otherwise every 15-s cycle tries again).</summary>
  public static readonly TimeSpan RetryAfterFailure = TimeSpan.FromMinutes(5);
  DateTime? _lastAttempt;

  /// <summary>A fetch is due: refresh interval passed since the last success, and after a failure
  /// <see cref="RetryAfterFailure"/> since the last try.</summary>
  public static bool IsDue(DateTime now, DateTime? lastSuccess, DateTime? lastAttempt, bool lastFailed, TimeSpan refresh) =>
    (lastSuccess is not { } s || now - s >= refresh)
    && (!lastFailed || lastAttempt is not { } a || now - a >= RetryAfterFailure);

  public async Task RefreshIfDueAsync(DateTime now)
  {
    if (config.WeatherEntity is not { } entity
        || !IsDue(now, LastUpdate, _lastAttempt, _lastError is not null, TimeSpan.FromMinutes(config.WeatherRefreshMinutes)))
      return;
    _lastAttempt = now;
    try
    {
      var response = await entity.CallServiceWithResponseAsync("get_forecasts", new { type = "hourly" });
      var hours = response is { } r ? Parse(r, entity.EntityId) : [];
      if (hours.Count == 0)
        throw new InvalidOperationException("no hourly forecast in the response");
      Hours = hours;
      LastUpdate = now;
      if (_lastError is not null)
        PVCC_Logger.LogInformation("Weather forecast from {Entity} available again", entity.EntityId);
      _lastError = null;
    }
    catch (Exception ex)
    {
      if (_lastError != ex.Message)
        PVCC_Logger.LogWarning("Weather forecast from {Entity} failed: {Error} — keeping the last one ({Age})",
          entity.EntityId, ex.Message, LastUpdate is { } t ? $"{(now - t).TotalMinutes:F0} min old" : "none");
      _lastError = ex.Message;
    }
  }

  /// <summary>
  /// Hours of a weather.get_forecasts response: <c>{ "weather.x": { "forecast": [ { "datetime",
  /// "temperature", "cloud_coverage", "wind_speed", … } ] } }</c>, also when wrapped in
  /// <c>"response"</c>. Times are converted to local time; entries without a temperature are skipped.
  /// </summary>
  public static IReadOnlyList<HourlyWeather> Parse(JsonElement response, string entityId)
  {
    if (response.ValueKind == JsonValueKind.Object && response.TryGetProperty("response", out var inner))
      response = inner;
    if (response.ValueKind != JsonValueKind.Object
        || !response.TryGetProperty(entityId, out var entity)
        || !entity.TryGetProperty("forecast", out var forecast)
        || forecast.ValueKind != JsonValueKind.Array)
      return [];

    static double? Number(JsonElement e, string name) =>
      e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    var result = new List<HourlyWeather>();
    foreach (var h in forecast.EnumerateArray())
    {
      if (!h.TryGetProperty("datetime", out var dt) || dt.GetString() is not { } text
          || Number(h, "temperature") is not { } temp)
        continue;
      var local = DateTimeOffset.Parse(text, CultureInfo.InvariantCulture).LocalDateTime;
      result.Add(new HourlyWeather(local, temp, Number(h, "cloud_coverage"), Number(h, "wind_speed")));
    }
    return result.OrderBy(h => h.Local).ToList();
  }
}
