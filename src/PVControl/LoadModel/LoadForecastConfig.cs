using System.Collections.Generic;
using NetDaemon.HassModel.Entities;

namespace NetDeamon.apps.PVControl
{
  public partial class PVConfig
  {
    /// <summary>Weather-aware load forecast (base load + heat pump heating). Optional — omit to
    /// keep the plain hour-of-day average.</summary>
    public global::NetDeamon.apps.PVControl.LoadModel.LoadForecastConfig? LoadForecast { get; set; }
  }
}

namespace NetDeamon.apps.PVControl.LoadModel
{
  public enum LoadForecastMode
  {
    /// <summary>The hour-of-day average only (the model isn't computed).</summary>
    Average,
    /// <summary>The model runs alongside and is published (sensor.pv_control_load_forecast), PVControl uses the average.</summary>
    Shadow,
    /// <summary>PVControl uses the model (falling back to the average when weather or data are missing).</summary>
    Model,
  }

  /// <summary>YAML configuration of the weather-aware load forecast.</summary>
  public sealed class LoadForecastConfig
  {
    public LoadForecastMode Mode { get; set; } = LoadForecastMode.Shadow;
    /// <summary>Heat pump operating mode (select) and the states in which it heats the house;
    /// otherwise the heat pump part is only its recent standby level.</summary>
    public Entity? HeatPumpModeEntity { get; set; }
    public List<string> HeatingModes { get; set; } = [];
    /// <summary>Weather entity with an hourly forecast (weather.get_forecasts), e.g. Pirate Weather.</summary>
    public Entity? WeatherEntity { get; set; }
    /// <summary>How often the hourly forecast is fetched (minutes).</summary>
    public int WeatherRefreshMinutes { get; set; } = 60;
  }
}
