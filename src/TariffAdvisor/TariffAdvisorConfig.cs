using System;

namespace NetDeamon.apps.TariffAdvisor;

/// <summary>YAML configuration of the tariff advisor (TariffAdvisor.yaml).</summary>
public class TariffAdvisorConfig
{
  /// <summary>false: the app does nothing at all — no entities, no timer.</summary>
  public bool Enabled { get; set; }
  /// <summary>PVControl's DP shadow DB (its snapshots, read only); relative = from this plugin's folder.</summary>
  public string SnapshotDb { get; set; } = "../PVControl/dp_shadow.db";
  /// <summary>The other tariff is the result only if it saves at least this much, €.</summary>
  public float MinSavingEur { get; set; } = 0.30f;
  /// <summary>An older snapshot isn't judged (PVControl not running), minutes.</summary>
  public int MaxSnapshotAgeMinutes { get; set; } = 30;

  /// <summary>The settings of the decision.</summary>
  public AdviceSettings Settings => new(MinSavingEur, MaxSnapshotAgeMinutes);
}
