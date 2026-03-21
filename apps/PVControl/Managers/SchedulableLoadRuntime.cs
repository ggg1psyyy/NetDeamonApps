using System;
using System.Collections.Generic;
using NetDeamon.apps;
using NetDeamon.apps.PVControl.Simulator;
using NetDaemon.HassModel.Entities;

namespace NetDeamon.apps.PVControl.Managers
{
  /// <summary>
  /// Runtime state of a schedulable extra load (EV charger, warm-water heat pump, etc.).
  /// Wraps <see cref="SchedulableLoadConfig"/> with live HA entity state, a power
  /// running-average, and the result of the last simulation window search.
  /// </summary>
  public class SchedulableLoadRuntime
  {
    public SchedulableLoadConfig Config { get; }

    // ── HA entity references — set by PVControl during RegisterControlSensors ──────────────
    /// <summary>Mode select entity. Null when Mode is defined statically in YAML config.</summary>
    public Entity? ModeEntity { get; set; }

    /// <summary>Target-level number entity. Null when TargetLevel is defined statically in YAML config.</summary>
    public Entity? TargetLevelEntity { get; set; }

    /// <summary>Charge-now binary sensor — always auto-created in HA.</summary>
    public Entity ChargeNowEntity { get; set; } = null!;

    /// <summary>Accumulating total energy consumed by this load (kWh). Null when ActualPowerEntity is not configured.</summary>
    public Entity? TotalEnergyKwhEntity { get; set; }

    /// <summary>Accumulating total import cost for this load (€ brutto). Null when ActualPowerEntity is not configured.</summary>
    public Entity? TotalCostEurEntity { get; set; }

    /// <summary>Last value of ActualEnergyEntity (kWh), used to compute the delta on each state change.</summary>
    public float LastEnergySum { get; set; }

    /// <summary>Running power average. Set by HouseEnergy when ActualPowerEntity is configured.</summary>
    public RunningIntAverage? PowerAverage { get; internal set; }

    // ── Live derived properties ────────────────────────────────────────────────────────────
    /// <summary>Current scheduling mode: from YAML config or the auto-created HA select entity.</summary>
    public LoadSchedulingMode Mode
    {
      get
      {
        if (Config.Mode.HasValue) return Config.Mode.Value;
        if (ModeEntity is not null
            && ModeEntity.TryGetStateValue(out string s)
            && Enum.TryParse(s, ignoreCase: true, out LoadSchedulingMode m))
          return m;
        return LoadSchedulingMode.Off;
      }
    }

    /// <summary>Target level: from YAML config or the auto-created HA number entity.</summary>
    public float TargetLevel
    {
      get
      {
        if (Config.TargetLevel.HasValue) return Config.TargetLevel.Value;
        if (TargetLevelEntity is not null && TargetLevelEntity.TryGetStateValue(out float v)) return v;
        return Config.TargetLevelDefault;
      }
    }

    /// <summary>Current level read from the HA sensor entity (SoC %, temperature °C, …).</summary>
    public float CurrentLevel
    {
      get
      {
        Config.CurrentLevelEntity.TryGetStateValue(out float v);
        return v;
      }
    }

    /// <summary>
    /// Effective power draw in W: running average of actual power if above MinActivePowerW,
    /// otherwise falls back to AvgPowerW from config.
    /// </summary>
    public int EffectivePowerW
    {
      get
      {
        if (PowerAverage is not null)
        {
          int avg = PowerAverage.GetAverage();
          if (avg > Config.MinActivePowerW) return avg;
        }
        return Config.AvgPowerW;
      }
    }

    /// <summary>Energy still needed to reach TargetLevel, in Wh.</summary>
    public int EnergyNeededWh =>
      (int)Math.Max(0, (TargetLevel - CurrentLevel) * Config.EnergyPerLevelUnitKwh * 1000);

    /// <summary>Estimated duration to reach TargetLevel at EffectivePowerW, in minutes.</summary>
    public int DurationMinutes
    {
      get
      {
        int pw = EffectivePowerW;
        return pw > 0 ? EnergyNeededWh * 60 / pw : 0;
      }
    }

    // ── Simulation output — written by HouseEnergy.FindLoadWindow ──────────────────────────
    /// <summary>True when this load should be running now according to the simulation oracle.</summary>
    public bool ChargeNow { get; internal set; }

    /// <summary>Human-readable explanation of the current ChargeNow decision.</summary>
    public string ChargeReason { get; internal set; } = "Not initialized";

    /// <summary>Predicted end of the current active session. Null when not running.</summary>
    public DateTime? PredictedEnd { get; internal set; }

    /// <summary>ExtraLoad windows found by the last FindLoadWindow call.</summary>
    public List<ExtraLoad> ExtraLoads { get; internal set; } = [];

    public SchedulableLoadRuntime(SchedulableLoadConfig config) => Config = config;

    /// <summary>HA entity-ID slug: lowercase Name with spaces replaced by underscores.</summary>
    public string Slug => Config.Name.ToLowerInvariant().Replace(' ', '_');
  }
}
