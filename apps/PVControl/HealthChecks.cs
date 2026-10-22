using System;
using System.Collections.Generic;
using System.Linq;

namespace NetDeamon.apps.PVControl;

/// <summary>
/// The checks behind sensor.pv_control_health, from values PVControl reads each cycle
/// (<see cref="PVControl"/>.CollectHealthIssues). Pure, so they're tested without HA; the
/// <see cref="HealthMonitor"/> debounces them.
/// </summary>
public static class HealthChecks
{
  /// <summary>How long missing inverter data is degraded before it's an error.</summary>
  public static readonly TimeSpan InverterDataErrorAfter = TimeSpan.FromMinutes(2);

  /// <summary>Tomorrow's prices are expected by then (EPEX day-ahead results ~13:00, in HA ~14:00).</summary>
  public static readonly TimeSpan TomorrowPricesBy = TimeSpan.FromHours(16);

  /// <summary>Battery SoC: held through a dropout (degraded), unavailable longer than the hold
  /// (error — the cycle has put the inverter in normal mode).</summary>
  public static IEnumerable<HealthIssue> BatterySoC(bool readingNow, bool unavailable, DateTime? lastGood)
  {
    if (unavailable)
      yield return new HealthIssue("soc", HealthLevel.error,
        $"Battery SoC unavailable since {lastGood?.ToString("HH:mm:ss") ?? "start"} → inverter in normal mode");
    else if (!readingNow)
      yield return new HealthIssue("soc", HealthLevel.degraded, $"Battery SoC unknown, using last value from {lastGood:HH:mm:ss}");
  }

  /// <summary>Inverter sensors (run mode, battery/grid/PV power, house load) unknown or unavailable.</summary>
  public static IEnumerable<HealthIssue> InverterData(IReadOnlyCollection<string> unavailableEntities)
  {
    if (unavailableEntities.Count > 0)
      yield return new HealthIssue("inverter_data", HealthLevel.degraded,
        $"Inverter data unavailable: {string.Join(", ", unavailableEntities)}", InverterDataErrorAfter);
  }

  /// <param name="withoutData">Configured import price entities without any price data.</param>
  /// <param name="used">The one PVControl uses (data reaching furthest), null if none has data.</param>
  /// <param name="pricesEnd">End of the last price slot PVControl has.</param>
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

  /// <param name="unavailableSources">Configured PV forecast entities without a value.</param>
  public static IEnumerable<HealthIssue> Forecasts(bool loadOk, bool pvOk, IReadOnlyCollection<string> unavailableSources)
  {
    if (!loadOk)
      yield return new HealthIssue("load_prediction", HealthLevel.error, "Load prediction invalid (energy history DB)");
    if (!pvOk)
      yield return new HealthIssue("pv_forecast", HealthLevel.error, "PV forecast invalid");
    else if (unavailableSources.Count > 0)
      yield return new HealthIssue("pv_forecast_source", HealthLevel.degraded, $"PV forecast source unavailable: {string.Join(", ", unavailableSources)}");
  }

  /// <summary>Failed 15-s cycles in a row: one is degraded, three or more an error.</summary>
  public static IEnumerable<HealthIssue> Cycle(int consecutiveFailures, string? lastError)
  {
    if (consecutiveFailures > 0)
      yield return new HealthIssue("cycle", consecutiveFailures >= 3 ? HealthLevel.error : HealthLevel.degraded,
        $"{consecutiveFailures} cycle(s) failed: {lastError}");
  }

  public static IEnumerable<HealthIssue> WarmWater(bool configured, bool heatPumpAvailable)
  {
    if (configured && !heatPumpAvailable)
      yield return new HealthIssue("warm_water", HealthLevel.degraded, "Heat pump entities unavailable — warm-water control paused");
  }

  /// <summary>Weather forecast for the load model: none yet or older than its maximum age.</summary>
  public static IEnumerable<HealthIssue> WeatherForecast(bool configured, DateTime? lastUpdate, DateTime now, TimeSpan maxAge)
  {
    if (!configured) yield break;
    if (lastUpdate is not { } t)
      yield return new HealthIssue("weather", HealthLevel.degraded, "No weather forecast yet");
    else if (now - t > maxAge)
      yield return new HealthIssue("weather", HealthLevel.degraded, $"Weather forecast from {t:dd.MM. HH:mm} is too old");
  }

  /// <summary>Weather load forecast in use (Mode: Model) but falling back to the average.</summary>
  public static IEnumerable<HealthIssue> LoadForecast(bool inUse, string? source)
  {
    if (inUse && source is not null && source != "model")
      yield return new HealthIssue("load_forecast", HealthLevel.degraded, $"Load forecast model not used: {source}");
  }

  /// <summary>DataLogger writes an hourly row at the end of each hour (timestamp hh:59:59); older
  /// than 2 h means at least one row is missing.</summary>
  public static IEnumerable<HealthIssue> DataLogger(DateTime now, DateTime? lastHourlyRow)
  {
    if (lastHourlyRow is not { } last)
      yield return new HealthIssue("datalogger", HealthLevel.degraded, "Energy history DB not readable");
    else if (now - last > TimeSpan.FromHours(2))
      yield return new HealthIssue("datalogger", HealthLevel.degraded, $"DataLogger: last hourly row {last:dd.MM. HH:mm}");
  }
}
