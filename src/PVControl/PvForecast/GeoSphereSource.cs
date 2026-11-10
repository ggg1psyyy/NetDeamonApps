using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NetDeamon.apps.PVControl.LoadModel;
using static NetDeamon.apps.PVControl.PVControlCommon;

namespace NetDeamon.apps.PVControl.PvForecast
{
  /// <summary>
  /// GeoSphere Austria's data hub (https://dataset.api.hub.geosphere.at, CC BY 4.0 — "Datenquelle: GeoSphere Austria"):
  /// the C-LAEF 1-km control run's global radiation (<c>nwp-v2-1h-1km</c>, <c>ssrd</c>) as the expected value and the
  /// ensemble's 10th/90th percentile (<c>ensemble-v2-1h-1km</c>) as the band, converted to PV with
  /// <see cref="PvForecastSourceConfig.KwhPerKwhM2"/>; and the measured day (INCA's gridded radiation, the configured
  /// stations' radiation and sunshine) for the log.
  /// </summary>
  public sealed class GeoSphereSource : IPvForecastSource, IPvObservationSource
  {
    /// <summary>The data hub.</summary>
    public const string BaseUrl = "https://dataset.api.hub.geosphere.at/v1";
    private static readonly HttpClient Http = CreateClient();
    private readonly PvForecastSourceConfig _config;
    private readonly string _latLon;
    private SourceForecast? _last;
    private DateTime? _lastSuccess, _lastAttempt;
    private bool _lastFailed;
    private string? _lastError;

    /// <summary>Creates the source.</summary>
    /// <param name="name">Name.</param>
    /// <param name="config">Its configuration.</param>
    /// <param name="lat">Latitude of the plant.</param>
    /// <param name="lon">Longitude of the plant.</param>
    public GeoSphereSource(string name, PvForecastSourceConfig config, double lat, double lon)
    {
      Name = name;
      _config = config;
      _latLon = string.Create(CultureInfo.InvariantCulture, $"{lat:0.###},{lon:0.###}");
    }

    /// <inheritdoc/>
    public string Name { get; }

    private static HttpClient CreateClient()
    {
      var c = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
      c.DefaultRequestHeaders.UserAgent.ParseAdd("PVControl-NetDaemon");
      return c;
    }

    /// <inheritdoc/>
    public async Task<SourceForecast?> GetAsync(DateTime now, CancellationToken ct)
    {
      if (!WeatherForecast.IsDue(now, _lastSuccess, _lastAttempt, _lastFailed, TimeSpan.FromMinutes(Math.Max(15, _config.RefreshMinutes))))
        return _last;
      _lastAttempt = now;
      try
      {
        var nwp = await GetJsonAsync($"/timeseries/forecast/nwp-v2-1h-1km?parameters=ssrd&lat_lon={_latLon}", ct);
        var ens = await GetJsonAsync($"/timeseries/forecast/ensemble-v2-1h-1km?parameters=ssrd_p10&parameters=ssrd_p90&lat_lon={_latLon}", ct);
        _last = new SourceForecast(now, nwp.TryGetProperty("reference_time", out var rt) ? rt.GetString() : null,
          Hourly(nwp, "ssrd", _config.KwhPerKwhM2), Hourly(ens, "ssrd_p10", _config.KwhPerKwhM2), Hourly(ens, "ssrd_p90", _config.KwhPerKwhM2));
        (_lastSuccess, _lastFailed, _lastError) = (now, false, null);
      }
      catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
      {
        _lastFailed = true;
        if (ex.Message != _lastError)
          PVCC_Logger?.LogWarning("PV forecast {Name}: fetch failed ({Message}), keeps the last one", Name, ex.Message);
        _lastError = ex.Message;
      }
      return _last;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyDictionary<string, double>> ObserveDayAsync(DateTime day, CancellationToken ct)
    {
      var values = new Dictionary<string, double>();
      var (from, to) = UtcRange(day);
      try
      {
        var inca = await GetJsonAsync($"/timeseries/historical/inca-v2-1h-1km?parameters=ssrd&lat_lon={_latLon}&start={from}&end={to}", ct);
        if (DaySum(inca, "ssrd", 1.0) is { } kwh) values["inca_kwh_m2"] = Math.Round(kwh, 3);
        var ids = new List<int>(_config.HillStations);
        if (_config.ValleyStation is { } v) ids.Insert(0, v);
        if (ids.Count > 0)
        {
          var st = await GetJsonAsync($"/station/historical/klima-v2-10min?parameters=cglo&parameters=so&{string.Join("&", ids.Select(i => $"station_ids={i}"))}&start={from}&end={to}", ct);
          foreach (var (id, kwhM2, sunH) in Stations(st))
          {
            if (kwhM2 is { } k) values[$"station_{id}_kwh_m2"] = Math.Round(k, 3);
            if (sunH is { } s) values[$"station_{id}_sun_h"] = Math.Round(s, 2);
          }
          if (InversionSunGap(values, _config.ValleyStation, _config.HillStations) is { } gap) values["hill_minus_valley_sun_h"] = Math.Round(gap, 2);
        }
      }
      catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
      {
        PVCC_Logger?.LogWarning("PV forecast {Name}: measured day {Day:yyyy-MM-dd} not read ({Message})", Name, day, ex.Message);
      }
      return values;
    }

    private static async Task<JsonElement> GetJsonAsync(string path, CancellationToken ct)
    {
      using var response = await Http.GetAsync(BaseUrl + path, ct);
      response.EnsureSuccessStatusCode();
      await using var stream = await response.Content.ReadAsStreamAsync(ct);
      using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
      return doc.RootElement.Clone();
    }

    /// <summary>A local day as the API's UTC start and end (<c>yyyy-MM-ddTHH:mm</c>).</summary>
    /// <param name="day">The day.</param>
    public static (string From, string To) UtcRange(DateTime day)
    {
      static string Utc(DateTime local) => DateTime.SpecifyKind(local, DateTimeKind.Local).ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture);
      return (Utc(day.Date), Utc(day.Date.AddDays(1).AddMinutes(-10)));
    }

    /// <summary>
    /// An hourly radiation series of a timeseries response as PV, Wh by local slot start: a value is the mean over the
    /// hour ending at its timestamp (W/m² × 1 h = Wh/m²), times the plant's kWh per kWh/m².
    /// </summary>
    /// <param name="root">The response (a FeatureCollection with <c>timestamps</c>).</param>
    /// <param name="parameter">The parameter (e.g. <c>ssrd</c>).</param>
    /// <param name="kwhPerKwhM2">PV per global radiation.</param>
    /// <returns>Wh by local hour start; empty if the parameter is missing.</returns>
    public static Dictionary<DateTime, double> Hourly(JsonElement root, string parameter, double kwhPerKwhM2)
    {
      var result = new Dictionary<DateTime, double>();
      foreach (var (end, value) in Series(root, parameter))
        if (value is { } v)
          result[end.AddHours(-1).ToLocalTime().DateTime] = Math.Max(0, v) * kwhPerKwhM2;
      return result;
    }

    /// <summary>A series' sum over the response's time range, kWh/m² (W/m² means × their step in hours / 1000).</summary>
    /// <param name="root">The response.</param>
    /// <param name="parameter">The parameter.</param>
    /// <param name="stepHours">Hours per value.</param>
    /// <returns>kWh/m²; null if no value is there.</returns>
    public static double? DaySum(JsonElement root, string parameter, double stepHours)
    {
      var values = Series(root, parameter).Select(s => s.Value).OfType<double>().ToList();
      return values.Count == 0 ? null : values.Sum(v => Math.Max(0, v)) * stepHours / 1000;
    }

    /// <summary>Each station of a 10-min station response: radiation (kWh/m², <c>cglo</c>) and sunshine (h, <c>so</c> in s).</summary>
    /// <param name="root">The response.</param>
    public static IEnumerable<(int Id, double? KwhM2, double? SunH)> Stations(JsonElement root)
    {
      if (!root.TryGetProperty("features", out var features)) yield break;
      foreach (var f in features.EnumerateArray())
      {
        if (!f.TryGetProperty("properties", out var p) || !p.TryGetProperty("station", out var idEl)) continue;
        int id = idEl.ValueKind == JsonValueKind.Number ? idEl.GetInt32() : int.Parse(idEl.GetString()!, CultureInfo.InvariantCulture);
        var radiation = Values(p, "cglo");
        var sun = Values(p, "so");
        yield return (id, radiation.Count == 0 ? null : radiation.Sum(v => Math.Max(0, v)) / 6 / 1000, sun.Count == 0 ? null : sun.Sum() / 3600);
      }
    }

    /// <summary>
    /// The inversion signal of a day: the hills' mean sunshine minus the valley station's, h — a fogged valley under
    /// sunny hills (2026-11-06/07/09: 4–8 h on the hills, 0–3 h in the valley, PV half of Open-Meteo's forecast).
    /// </summary>
    /// <param name="values">The day's measured values (<c>station_{id}_sun_h</c>).</param>
    /// <param name="valley">The valley station.</param>
    /// <param name="hills">The hill stations.</param>
    /// <returns>h; null without the valley or any hill.</returns>
    public static double? InversionSunGap(IReadOnlyDictionary<string, double> values, int? valley, IEnumerable<int> hills)
    {
      if (valley is not { } v || !values.TryGetValue($"station_{v}_sun_h", out double valleySun)) return null;
      var hill = hills.Select(h => values.TryGetValue($"station_{h}_sun_h", out double s) ? s : (double?)null).OfType<double>().ToList();
      return hill.Count == 0 ? null : hill.Average() - valleySun;
    }

    private static List<double> Values(JsonElement properties, string parameter) =>
      properties.TryGetProperty("parameters", out var ps) && ps.TryGetProperty(parameter, out var par) && par.TryGetProperty("data", out var data)
        ? data.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Number).Select(x => x.GetDouble()).ToList()
        : [];

    private static IEnumerable<(DateTimeOffset End, double? Value)> Series(JsonElement root, string parameter)
    {
      if (!root.TryGetProperty("timestamps", out var ts) || !root.TryGetProperty("features", out var features) || features.GetArrayLength() == 0)
        yield break;
      var p = features[0].GetProperty("properties");
      if (!p.TryGetProperty("parameters", out var ps) || !ps.TryGetProperty(parameter, out var par) || !par.TryGetProperty("data", out var data))
        yield break;
      var times = ts.EnumerateArray().Select(t => DateTimeOffset.Parse(t.GetString()!, CultureInfo.InvariantCulture)).ToList();
      int i = 0;
      foreach (var x in data.EnumerateArray())
      {
        if (i >= times.Count) yield break;
        yield return (times[i++], x.ValueKind == JsonValueKind.Number ? x.GetDouble() : null);
      }
    }
  }
}
