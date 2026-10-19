using System.Collections.Generic;
using NetDaemon.HassModel.Entities;

namespace NetDeamon.apps.PVControl
{
  public partial class PVConfig
  {
    /// <summary>Warm-water tank planner (heat pump one-time heating). Optional — omit to disable.</summary>
    public global::NetDeamon.apps.PVControl.WarmWater.WarmWaterConfig? WarmWater { get; set; }
  }
}

namespace NetDeamon.apps.PVControl.WarmWater
{
  /// <summary>YAML configuration of the warm-water planner.</summary>
  public class WarmWaterConfig
  {
    /// <summary>Display name; its slug prefixes the created HA entities (e.g. "Warm Water" → warm_water).</summary>
    public string Name { get; set; } = "Warm Water";

    /// <summary>Tank temperature sensor (°C).</summary>
    public Entity TemperatureEntity { get; set; } = null!;
    /// <summary>On while the heat pump heats warm water (automatic or one-time).</summary>
    public Entity HeatingActiveEntity { get; set; } = null!;
    /// <summary>On while a one-time heating runs.</summary>
    public Entity OneTimeActiveEntity { get; set; } = null!;
    /// <summary>Target temperature of the one-time heating (number).</summary>
    public Entity OneTimeSetpointEntity { get; set; } = null!;
    /// <summary>Starts the one-time heating (switch).</summary>
    public Entity OneTimeSwitchEntity { get; set; } = null!;
    /// <summary>The heat pump's own warm-water setpoint (number) — read only, for the automatic forecast.</summary>
    public Entity? AutomaticSetpointEntity { get; set; }
    /// <summary>Heat pump power (W) — includes space heating, so only used while warm water heats.</summary>
    public Entity? PowerEntity { get; set; }

    /// <summary>Hourly DB column with the warm-water energy, stripped from the house load prediction (the plan adds it back).</summary>
    public string? HistoryDbColumn { get; set; }

    /// <summary>Highest temperature the heat pump reaches with a one-time heating.</summary>
    public double MaxTemp { get; set; } = 57;
    /// <summary>Default of the comfort temperature number entity.</summary>
    public double ComfortTempDefault { get; set; } = 42;
    /// <summary>The plan aims this much above the comfort temperature, for model error.</summary>
    public double ComfortMarginK { get; set; } = 1;
    /// <summary>Default of the boost temperature number entity.</summary>
    public double BoostTempDefault { get; set; } = 45;
    /// <summary>A boost ends after this long even if the temperature isn't reached.</summary>
    public int BoostTimeoutMinutes { get; set; } = 180;
    /// <summary>The heat pump's automatic starts this far below its setpoint (measured: 45 → 39.9 °C).</summary>
    public double AutomaticHysteresisK { get; set; } = 5;
    /// <summary>Cost added per run start in the plan, so it prefers one longer run over several short ones.</summary>
    public float RunStartCostEur { get; set; } = 0.02f;
    /// <summary>
    /// Max minutes between two plans (also replans on every quarter-hour, setting change, price
    /// update, heating start/stop). A plan takes ~1 s (96 house simulations for the prices).
    /// </summary>
    public int ReplanIntervalMinutes { get; set; } = 15;
    /// <summary>Days of HA history the tank model is learned from (daily).</summary>
    public int HistoryDays { get; set; } = 10;

    /// <summary>Times the warm water should be ready, each with its own HA time and enable switch.</summary>
    public List<ComfortPeriodConfig> ComfortPeriods { get; set; } = [];
  }

  public class ComfortPeriodConfig
  {
    public string Name { get; set; } = "";
    /// <summary>Initial value of the time entity ("HH:mm").</summary>
    public string DefaultTime { get; set; } = "07:00";
    /// <summary>How long the tank must stay at the comfort temperature from that time.</summary>
    public int DurationMinutes { get; set; } = 60;
  }
}
