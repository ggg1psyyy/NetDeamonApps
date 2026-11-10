using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using static NetDeamon.apps.PVControl.PVControlCommon;

namespace NetDeamon.apps.PVControl.PvForecast
{
  /// <summary>
  /// A source's PV forecast, Wh per slot (local slot start; 15 min or 1 h), with an optional band (a low and a high
  /// estimate, e.g. an ensemble's 10th and 90th percentile).
  /// </summary>
  /// <param name="FetchedAt">When it was read.</param>
  /// <param name="Run">The forecast run (model time), if the source names one.</param>
  /// <param name="Wh">The expected PV, Wh by slot start.</param>
  /// <param name="LowWh">Low estimate, Wh by slot start; null without a band.</param>
  /// <param name="HighWh">High estimate, Wh by slot start; null without a band.</param>
  public sealed record SourceForecast(DateTime FetchedAt, string? Run, IReadOnlyDictionary<DateTime, double> Wh,
    IReadOnlyDictionary<DateTime, double>? LowWh = null, IReadOnlyDictionary<DateTime, double>? HighWh = null)
  {
    /// <summary>The sum over a local day.</summary>
    /// <param name="slots">Wh by slot start.</param>
    /// <param name="day">The day (its date).</param>
    /// <returns>Wh; null if no slot of the day is there.</returns>
    public static double? DayTotal(IReadOnlyDictionary<DateTime, double>? slots, DateTime day)
    {
      if (slots is null) return null;
      var of = slots.Where(s => s.Key.Date == day.Date).ToList();
      return of.Count == 0 ? null : of.Sum(s => s.Value);
    }

    /// <summary>The hourly sums of a local day (for the log).</summary>
    /// <param name="day">The day.</param>
    /// <returns>Wh by hour of day.</returns>
    public SortedDictionary<int, double> HoursOf(DateTime day)
    {
      var hours = new SortedDictionary<int, double>();
      foreach (var s in Wh.Where(s => s.Key.Date == day.Date))
        hours[s.Key.Hour] = hours.GetValueOrDefault(s.Key.Hour) + s.Value;
      return hours;
    }
  }

  /// <summary>
  /// A PV forecast source, exchangeable: the shadow compares every configured one, and a new source is a class with this
  /// interface and a case in <see cref="PvForecastSources.Create"/>.
  /// </summary>
  public interface IPvForecastSource
  {
    /// <summary>Name in the log and the sensor.</summary>
    string Name { get; }

    /// <summary>Its forecast now: fetched when due (a source keeps its last one between fetches and after a failure).</summary>
    /// <param name="now">Current time.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The forecast; null if there is none yet.</returns>
    Task<SourceForecast?> GetAsync(DateTime now, CancellationToken ct);
  }

  /// <summary>A source that also measures (a weather service's stations): values of a past day for the log.</summary>
  public interface IPvObservationSource
  {
    /// <summary>Measured values of a day, by key (e.g. <c>valley_sun_h</c>).</summary>
    /// <param name="day">The day (local date).</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>Values; empty if none could be read.</returns>
    Task<IReadOnlyDictionary<string, double>> ObserveDayAsync(DateTime day, CancellationToken ct);
  }

  /// <summary>The configured sources.</summary>
  public static class PvForecastSources
  {
    /// <summary>Creates a source from its configuration.</summary>
    /// <param name="config">The source's configuration.</param>
    /// <param name="openMeteo">The raw Open-Meteo forecast (<c>Prediction_PV</c>), Wh per 15-min slot.</param>
    /// <param name="home">Latitude and longitude of the plant (HA's home zone); null if unknown.</param>
    /// <returns>The source; null for an unknown type or a source that can't work (logged).</returns>
    public static IPvForecastSource? Create(PvForecastSourceConfig config, Func<IReadOnlyDictionary<DateTime, int>> openMeteo,
      (double Lat, double Lon)? home)
    {
      string name = string.IsNullOrWhiteSpace(config.Name) ? config.Type : config.Name;
      switch (config.Type)
      {
        case "OpenMeteoEntities":
          return new OpenMeteoEntitiesSource(name, openMeteo);
        case "GeoSphere":
          if (home is not { } h)
          {
            PVCC_Logger?.LogWarning("PV forecast source {Name}: no location (zone.home), not used", name);
            return null;
          }
          return new GeoSphereSource(name, config, h.Lat, h.Lon);
        default:
          PVCC_Logger?.LogWarning("PV forecast source type {Type} unknown, not used", config.Type);
          return null;
      }
    }
  }

  /// <summary>The Open-Meteo Solar Forecast entities PVControl plans with (<c>ForecastPVEnergy…Entities</c>), raw.</summary>
  /// <param name="name">Name.</param>
  /// <param name="forecast">The raw forecast, Wh per slot.</param>
  public sealed class OpenMeteoEntitiesSource(string name, Func<IReadOnlyDictionary<DateTime, int>> forecast) : IPvForecastSource
  {
    /// <inheritdoc/>
    public string Name => name;

    /// <inheritdoc/>
    public Task<SourceForecast?> GetAsync(DateTime now, CancellationToken ct)
    {
      var slots = forecast();
      return Task.FromResult<SourceForecast?>(slots.Count == 0 ? null
        : new SourceForecast(now, null, slots.ToDictionary(s => s.Key, s => (double)s.Value)));
    }
  }
}
