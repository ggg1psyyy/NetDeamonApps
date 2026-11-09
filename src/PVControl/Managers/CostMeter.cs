using System;
using System.Collections.Generic;
using NetDaemon.HassModel.Entities;

namespace NetDeamon.apps.PVControl
{
  public partial class PVConfig
  {
    /// <summary>Consumers whose energy is priced by source (PV/battery/grid) into a running € total (CostMeters in PVControl.yaml).</summary>
    public List<Managers.CostMeterConfig> CostMeters { get; set; } = [];
  }
}

namespace NetDeamon.apps.PVControl.Managers
{
  /// <summary>YAML configuration of one cost meter.</summary>
  public class CostMeterConfig
  {
    /// <summary>Display name of the HA sensor (e.g. "Heat pump").</summary>
    public string Name { get; set; } = "";

    /// <summary>Entity id part: the total goes to sensor.pv_control_&lt;Slug&gt;_total_cost.</summary>
    public string Slug { get; set; } = "";

    /// <summary>The consumer's energy counter (kWh; a running total or a daily counter).</summary>
    public Entity? EnergyEntity { get; set; }

    /// <summary>The consumer's highest power in W: a counter step above 2 h of it is a glitch, not energy.</summary>
    public int MaxPowerW { get; set; } = 4000;
  }

  /// <summary>
  /// A consumer's energy cost total: every increase of its counter costs the €/kWh of the moment
  /// (its sources' mix priced as the EV charger's, <see cref="SourceShares.Cost"/>).
  /// </summary>
  public sealed class CostMeter(CostMeterConfig config)
  {
    /// <summary>The YAML configuration.</summary>
    public CostMeterConfig Config { get; } = config;

    /// <summary>The HA sensor holding the total (sensor.pv_control_&lt;Slug&gt;_total_cost).</summary>
    public Entity? TotalCostEurEntity { get; set; }

    /// <summary>The running total in €, kept in memory (seeded from HA on start, never read back).</summary>
    public float TotalCostEur { get; set; }

    private float _lastKwh = float.NaN;   // none yet: the first reading after a start books nothing

    /// <summary>The largest plausible counter step of a consumer: 2 h at its highest power, kWh.</summary>
    /// <param name="maxPowerW">The consumer's highest power in W.</param>
    public static float MaxStepKwh(float maxPowerW) => (float)Energy.Wh(maxPowerW, TimeSpan.FromHours(2)) / 1000f;

    /// <summary>Books a counter reading: the increase since the last one at the price of now.</summary>
    /// <param name="counterWh">The counter in Wh (the base unit of <see cref="Extensions.TryGetStateValue{T}"/>).</param>
    /// <param name="pricePerKwh">The consumer's €/kWh now.</param>
    /// <returns>kWh booked, and the step that was skipped as implausible (0 if none).</returns>
    public (float BookedKwh, float SkippedKwh) Book(float counterWh, float pricePerKwh)
    {
      float diff = CostTracker.CounterDeltaKwh(ref _lastKwh, counterWh);
      // A counter that blips to 0 and back reads as a reset, then the whole total as one step.
      if (diff > MaxStepKwh(Config.MaxPowerW)) return (0, diff);
      TotalCostEur += diff * pricePerKwh;
      return (diff, 0);
    }
  }
}
