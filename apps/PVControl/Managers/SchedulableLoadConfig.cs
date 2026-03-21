using System.Collections.Generic;
using NetDaemon.HassModel.Entities;

namespace NetDeamon.apps.PVControl
{
  public partial class PVConfig
  {
    /// <summary>
    /// Schedulable extra loads (EV charger, warm-water heat pump, …).
    /// Each entry is deserialized from the SchedulableLoads list in PVControl.yaml.
    /// </summary>
    public List<Managers.SchedulableLoadConfig> SchedulableLoads { get; set; } = [];
  }
}

namespace NetDeamon.apps.PVControl.Managers
{
  /// <summary>
  /// YAML-deserialized configuration for a single schedulable extra load.
  /// </summary>
  public class SchedulableLoadConfig
  {
    /// <summary>Display name shown in HA entities and diagnostics (e.g. "EV Charger").</summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// Scheduling priority integer (higher = wins when loads compete for the same solar slot).
    /// Emergency mode always overrides priority.
    /// </summary>
    public int Priority { get; set; } = 10;

    /// <summary>
    /// Static scheduling mode from YAML. If null, a HA select entity is auto-created so the
    /// user can change the mode from HA (entity ID: select.pv_control_&lt;slug&gt;_mode).
    /// </summary>
    public LoadSchedulingMode? Mode { get; set; }

    /// <summary>
    /// Static target level (SoC %, temperature °C, …) from YAML. If null, a HA number entity
    /// is auto-created (number.pv_control_&lt;slug&gt;_target_level).
    /// </summary>
    public float? TargetLevel { get; set; }

    /// <summary>HA sensor entity reporting the current level (SoC %, temperature °C, …).</summary>
    public Entity CurrentLevelEntity { get; set; } = null!;

    /// <summary>HA sensor entity reporting actual real-time power draw in W. Optional.</summary>
    public Entity? ActualPowerEntity { get; set; }

    /// <summary>HA sensor entity reporting daily energy consumed by this load in kWh (resets at midnight). Optional — enables total energy/cost accumulation.</summary>
    public Entity? ActualEnergyEntity { get; set; }

    /// <summary>
    /// Column name in the hourly DB table whose historical values should be subtracted from the
    /// base load prediction, so this load's past energy is not double-counted when it is added
    /// back as an ExtraLoad in the simulation.
    /// Known column names: "carcharge", "warmwaterenergy", "heatpumpenergy".
    /// Leave null if no historical DB column corresponds to this load.
    /// </summary>
    public string? HistoryDbColumn { get; set; }

    /// <summary>
    /// Energy consumed per unit of level change, in kWh.
    /// Examples: 0.6 kWh/% for a 60 kWh EV battery (60 kWh ÷ 100 %),
    ///           0.11 kWh/°C for a warm-water tank.
    /// Energy needed = (TargetLevel − CurrentLevel) × EnergyPerLevelUnitKwh.
    /// </summary>
    public float EnergyPerLevelUnitKwh { get; set; }

    /// <summary>
    /// Nominal power draw in W used when actual power is not yet measurable
    /// (e.g. car not yet plugged in, before MinActivePowerW is exceeded).
    /// </summary>
    public int AvgPowerW { get; set; }

    /// <summary>
    /// Minimum measured power in W to treat the load as actively running.
    /// Readings below this threshold are ignored (standby / sensor noise).
    /// </summary>
    public int MinActivePowerW { get; set; } = 100;

    // ── HA entity display configuration for auto-created entities ─────────────────────────
    // ── HA entity display configuration ───────────────────────────────────────────────────
    /// <summary>MDI icon used for all auto-created HA entities of this load (e.g. "mdi:ev-station").</summary>
    public string Icon { get; set; } = "mdi:ev-station";

    /// <summary>HA device class for the auto-created ChargeNow binary sensor (e.g. "battery_charging", "heat", "running").</summary>
    public string ChargeNowDeviceClass { get; set; } = "battery_charging";

    /// <summary>Unit label for the level value shown in HA (e.g. "%" or "°C").</summary>
    public string LevelUnit { get; set; } = "%";

    /// <summary>Minimum value for the auto-created TargetLevel number entity.</summary>
    public float TargetLevelMin { get; set; } = 0;

    /// <summary>Maximum value for the auto-created TargetLevel number entity.</summary>
    public float TargetLevelMax { get; set; } = 100;

    /// <summary>Step size for the auto-created TargetLevel number entity.</summary>
    public float TargetLevelStep { get; set; } = 5;

    /// <summary>Default value for the auto-created TargetLevel number entity.</summary>
    public float TargetLevelDefault { get; set; } = 80;
  }
}
