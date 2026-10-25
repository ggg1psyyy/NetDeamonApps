using System;
using System.Collections.Generic;
using System.Linq;

namespace NetDeamon.apps.PVControl;

/// <summary>The checks behind sensor.pv_control_health, from values PVControl reads each cycle; <see cref="HealthMonitor"/> debounces them.</summary>
public static class HealthChecks
{
  /// <summary>How long missing inverter data is degraded before it's an error.</summary>
  public static readonly TimeSpan InverterDataErrorAfter = TimeSpan.FromMinutes(2);

  /// <summary>Tomorrow's prices are expected by then (EPEX day-ahead results ~13:00, in HA ~14:00).</summary>
  public static readonly TimeSpan TomorrowPricesBy = TimeSpan.FromHours(16);

  /// <summary>Battery SoC: held through a dropout = degraded; unavailable longer than the hold = error.</summary>
  /// <param name="readingNow">The sensor has a value now.</param>
  /// <param name="unavailable">No reading for longer than the hold (<see cref="BatteryState.IsSoCUnavailable"/>).</param>
  /// <param name="lastGood">Time of the last good reading.</param>
  /// <returns>The issues.</returns>
  public static IEnumerable<HealthIssue> BatterySoC(bool readingNow, bool unavailable, DateTime? lastGood)
  {
    if (unavailable)
      yield return new HealthIssue("soc", HealthLevel.error,
        $"Battery SoC unavailable since {lastGood?.ToString("HH:mm:ss") ?? "start"} → inverter in normal mode");
    else if (!readingNow)
      yield return new HealthIssue("soc", HealthLevel.degraded, $"Battery SoC unknown, using last value from {lastGood:HH:mm:ss}");
  }

  /// <summary>Inverter sensors (run mode, battery/grid/PV power, house load) unknown or unavailable; error after <see cref="InverterDataErrorAfter"/>.</summary>
  /// <param name="unavailableEntities">Entity ids without a value.</param>
  /// <returns>The issues.</returns>
  public static IEnumerable<HealthIssue> InverterData(IReadOnlyCollection<string> unavailableEntities)
  {
    if (unavailableEntities.Count > 0)
      yield return new HealthIssue("inverter_data", HealthLevel.degraded,
        $"Inverter data unavailable: {string.Join(", ", unavailableEntities)}", InverterDataErrorAfter);
  }

  /// <summary>Import prices: none for now = error; a source without data or tomorrow missing after <see cref="TomorrowPricesBy"/> = degraded.</summary>
  /// <param name="now">Current time.</param>
  /// <param name="withoutData">Configured price entities without any data.</param>
  /// <param name="used">The one in use (data reaching furthest); null if none has data.</param>
  /// <param name="pricesEnd">End of the last price slot in use.</param>
  /// <returns>The issues.</returns>
  public static IEnumerable<HealthIssue> ImportPrices(DateTime now, IReadOnlyList<string> withoutData, string? used, DateTime? pricesEnd)
  {
    if (used is null || pricesEnd is not { } end || end <= now)
    {
      yield return new HealthIssue("prices", HealthLevel.error, "No import price for now");
      yield break;
    }
    // All sources are equal (the furthest wins), so which one is used isn't an issue — one
    // without any data is (failed integration, disabled entry), there's one fallback less.
    if (withoutData.Count > 0)
      yield return new HealthIssue("price_source", HealthLevel.degraded, $"Price source {string.Join(", ", withoutData)} has no data, using {used}");
    if (now.TimeOfDay >= TomorrowPricesBy && end < now.Date.AddDays(2))
      yield return new HealthIssue("prices_tomorrow", HealthLevel.degraded, $"Tomorrow's prices not published yet (prices until {end:dd.MM. HH:mm})");
  }

  /// <summary>Load and PV forecasts: invalid = error; a PV source unavailable = degraded.</summary>
  /// <param name="loadOk">Load prediction valid.</param>
  /// <param name="pvOk">PV forecast valid.</param>
  /// <param name="unavailableSources">Configured PV forecast entities without a value.</param>
  /// <returns>The issues.</returns>
  public static IEnumerable<HealthIssue> Forecasts(bool loadOk, bool pvOk, IReadOnlyCollection<string> unavailableSources)
  {
    if (!loadOk)
      yield return new HealthIssue("load_prediction", HealthLevel.error, "Load prediction invalid (energy history DB)");
    if (!pvOk)
      yield return new HealthIssue("pv_forecast", HealthLevel.error, "PV forecast invalid");
    else if (unavailableSources.Count > 0)
      yield return new HealthIssue("pv_forecast_source", HealthLevel.degraded, $"PV forecast source unavailable: {string.Join(", ", unavailableSources)}");
  }

  /// <summary>Failed 15-s cycles in a row: one = degraded, three or more = error.</summary>
  /// <param name="consecutiveFailures">Failures in a row.</param>
  /// <param name="lastError">Message of the last failure.</param>
  /// <returns>The issues.</returns>
  public static IEnumerable<HealthIssue> Cycle(int consecutiveFailures, string? lastError)
  {
    if (consecutiveFailures > 0)
      yield return new HealthIssue("cycle", consecutiveFailures >= 3 ? HealthLevel.error : HealthLevel.degraded,
        $"{consecutiveFailures} cycle(s) failed: {lastError}");
  }

  /// <summary>How long a quarter over the import cap stays reported.</summary>
  public static readonly TimeSpan OverCapReportedFor = TimeSpan.FromHours(1);

  /// <summary>Import cap: no import counter = error (the guard is blind), only a fallback counter = degraded, a recent quarter over the cap = degraded.</summary>
  /// <param name="capW">The cap, W; 0 = off (no issues).</param>
  /// <param name="activeSource">Index of the counter in use; -1 = none works.</param>
  /// <param name="sources">Counter entity ids in priority order.</param>
  /// <param name="lastOverCap">Last finished quarter over the cap; null = none.</param>
  /// <param name="now">Current time.</param>
  /// <returns>The issues.</returns>
  public static IEnumerable<HealthIssue> ImportCap(int capW, int activeSource, IReadOnlyList<string> sources, ImportCap.QuarterResult? lastOverCap, DateTime now)
  {
    if (capW <= 0 || sources.Count == 0) yield break;
    if (activeSource < 0)
      yield return new HealthIssue("import_cap", HealthLevel.error, "No grid import counter works — import cap can't be kept");
    else if (activeSource > 0)
      yield return new HealthIssue("import_counter", HealthLevel.degraded, $"Import counter {sources[0]} failed — using {sources[activeSource]}");
    if (lastOverCap is { } q && now - q.Start < OverCapReportedFor + TimeSpan.FromMinutes(TimeRange.SlotMinutes))
      yield return new HealthIssue("import_over_cap", HealthLevel.degraded, $"Quarter {q.Start:HH:mm} imported {q.AvgKw:F2} kW, over the {capW / 1000.0:F1} kW cap");
  }

  /// <summary>Warm-water control configured but the heat pump unavailable = degraded.</summary>
  /// <param name="configured">Warm-water control configured.</param>
  /// <param name="heatPumpAvailable">Heat pump entities available.</param>
  /// <returns>The issues.</returns>
  public static IEnumerable<HealthIssue> WarmWater(bool configured, bool heatPumpAvailable)
  {
    if (configured && !heatPumpAvailable)
      yield return new HealthIssue("warm_water", HealthLevel.degraded, "Heat pump entities unavailable — warm-water control paused");
  }

  /// <summary>Weather forecast for the load model: none yet or older than its maximum age = degraded.</summary>
  /// <param name="configured">Weather forecast configured.</param>
  /// <param name="lastUpdate">Last successful update; null = none.</param>
  /// <param name="now">Current time.</param>
  /// <param name="maxAge">Maximum age.</param>
  /// <returns>The issues.</returns>
  public static IEnumerable<HealthIssue> WeatherForecast(bool configured, DateTime? lastUpdate, DateTime now, TimeSpan maxAge)
  {
    if (!configured) yield break;
    if (lastUpdate is not { } t)
      yield return new HealthIssue("weather", HealthLevel.degraded, "No weather forecast yet");
    else if (now - t > maxAge)
      yield return new HealthIssue("weather", HealthLevel.degraded, $"Weather forecast from {t:dd.MM. HH:mm} is too old");
  }

  /// <summary>Weather load forecast in use (Mode: Model) but falling back to the average = degraded.</summary>
  /// <param name="inUse">Mode is Model.</param>
  /// <param name="source">Source of the current forecast.</param>
  /// <returns>The issues.</returns>
  public static IEnumerable<HealthIssue> LoadForecast(bool inUse, string? source)
  {
    if (inUse && source is not null && source != "model")
      yield return new HealthIssue("load_forecast", HealthLevel.degraded, $"Load forecast model not used: {source}");
  }

  /// <summary>DataLogger's last hourly row (stamped hh:59:59) older than 2 h = degraded.</summary>
  /// <param name="now">Current time.</param>
  /// <param name="lastHourlyRow">Timestamp of the last hourly row; null = DB not readable (also degraded).</param>
  /// <returns>The issues.</returns>
  public static IEnumerable<HealthIssue> DataLogger(DateTime now, DateTime? lastHourlyRow)
  {
    if (lastHourlyRow is not { } last)
      yield return new HealthIssue("datalogger", HealthLevel.degraded, "Energy history DB not readable");
    else if (now - last > TimeSpan.FromHours(2))
      yield return new HealthIssue("datalogger", HealthLevel.degraded, $"DataLogger: last hourly row {last:dd.MM. HH:mm}");
  }
}
