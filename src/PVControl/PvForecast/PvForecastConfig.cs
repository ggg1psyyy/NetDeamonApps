using System.Collections.Generic;

namespace NetDeamon.apps.PVControl
{
  public partial class PVConfig
  {
    /// <summary>PV forecast correction and comparison (<see cref="PvForecast.PvForecastShadow"/>); null = off.</summary>
    public PvForecast.PvForecastConfig? PvForecast { get; set; }
  }
}

namespace NetDeamon.apps.PVControl.PvForecast
{
  /// <summary>What the PV forecast correction does.</summary>
  public enum PvForecastMode
  {
    /// <summary>Not computed.</summary>
    Off,
    /// <summary>Computed, logged and published (sensor.pv_control_pv_forecast); the planners keep the raw forecast.</summary>
    Shadow,
  }

  /// <summary>YAML (<c>PvForecast:</c>): the sources compared, the evening snapshot, the learned correction.</summary>
  public sealed class PvForecastConfig
  {
    /// <summary>Off or Shadow.</summary>
    public PvForecastMode Mode { get; set; } = PvForecastMode.Shadow;
    /// <summary>The log (relative: in this plugin's folder).</summary>
    public string DBLocation { get; set; } = "pv_forecast.db";
    /// <summary>Hour (local) in which tomorrow's forecast of every source is noted once.</summary>
    public int SnapshotHour { get; set; } = 21;
    /// <summary>Scored days the correction learns from (the most recent).</summary>
    public int WindowDays { get; set; } = 14;
    /// <summary>Fewer scored days: no correction (factor 1).</summary>
    public int MinScoredDays { get; set; } = 7;
    /// <summary>Change of the ratio per K the day is warmer than the scored days (fog/inversion days are cold); 0 = off.</summary>
    public double TemperatureSlopePerK { get; set; } = 0.035;
    /// <summary>Percentile of the scored ratios for the cautious value (sources without their own band).</summary>
    public double CautiousPercentile { get; set; } = 0.25;
    /// <summary>The forecast sources, compared side by side; the first is the one the sensor's state shows.</summary>
    public List<PvForecastSourceConfig> Sources { get; set; } = [];
  }

  /// <summary>One forecast source (<see cref="PvForecastSources.Create"/> by <see cref="Type"/>).</summary>
  public sealed class PvForecastSourceConfig
  {
    /// <summary>Kind: <c>OpenMeteoEntities</c> (the configured forecast entities) or <c>GeoSphere</c>.</summary>
    public string Type { get; set; } = "";
    /// <summary>Name in the log and the sensor; default: the type.</summary>
    public string? Name { get; set; }
    /// <summary>False: not created.</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>Minutes between two fetches (GeoSphere: a new run every 3 h).</summary>
    public int RefreshMinutes { get; set; } = 60;
    /// <summary>GeoSphere: PV energy per global radiation, kWh per kWh/m² (the plant's size × its efficiency; learned on top).</summary>
    public double KwhPerKwhM2 { get; set; } = 4.9;
    /// <summary>GeoSphere: the measuring station nearest in the valley (TAWES/klima id) — its measured day is logged.</summary>
    public int? ValleyStation { get; set; }
    /// <summary>GeoSphere: stations on the hills around — sunshine above a fogged valley is the inversion signal.</summary>
    public List<int> HillStations { get; set; } = [];
  }
}
