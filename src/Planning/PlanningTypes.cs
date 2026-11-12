using System;
using System.Text.Json.Serialization;

// The types the planner plans with (moved from PVControl with the planner; namespaces unchanged, so YAML values and
// stored names keep working).
namespace NetDeamon.apps
{
  // PVControl's own types and helpers (the generic HA helpers are in Common's Extensions).
  /// <summary>Price of a time range.</summary>
  /// <param name="startTime">Start (inclusive).</param>
  /// <param name="endTime">End (exclusive).</param>
  /// <param name="price">Price in €/kWh.</param>
  public struct PriceTableEntry(DateTime startTime, DateTime endTime, float price)
  {
    [JsonPropertyName("start_time")]
    public DateTime StartTime { get; set; } = startTime;
    [JsonPropertyName("end_time")]
    public DateTime EndTime { get; set; } = endTime;
    [JsonPropertyName("price_per_kwh")]
    public float Price { get; set; } = price;

    /// <summary>Whether a time lies in this entry.</summary>
    /// <param name="t">Time to test.</param>
    /// <returns>True if StartTime ≤ <paramref name="t"/> &lt; EndTime.</returns>
    public readonly bool Covers(DateTime t) => TimeRange.Contains(StartTime, EndTime, t);
  }
  public enum InverterModes
  {
    automatic,
    normal,
    force_charge,
    force_charge_grid_only,
    grid_only,
    force_discharge,
    feedin_priority,
  }
  public enum BatteryStatuses
  {
    idle,
    charging,
    discharging,
    unknown,
  }
  public enum ForceChargeReasons
  {
    None,
    GoingUnderPreferredMinima,
    GoingUnderAbsoluteMinima,
    ForcedChargeAtMinimumPrice,
    ImportPriceNegative,
    ExportPriceNegative,
    OpportunisticDischarge,
    NextHourCheaper,
    BugFixMode,
    UserMode,
    /// <summary>PV surplus fed in now (battery not charged) to keep room for PV in coming negative-export hours.</summary>
    RoomForNegativeExport,
    /// <summary>Battery held while a load that never draws it runs (PriorityPlus EV from the grid).</summary>
    LoadFromGrid,
    /// <summary>The DP plan steers (select.pv_control_planner = dp): its grid charge or hold (no reason derived).</summary>
    DpPlan,
    // The DP's own reasons (Optimizer.DpSteering.ReasonOf), apart from the greedy ones: the greedy simulation's
    // hysteresis reads the running mode's reason (a NeedToCharge charge, a floor hold) and must not take a DP charge
    // for its own. Appended: stored snapshots and the published command keep the values as numbers.
    /// <summary>DP: without this charge or hold the battery would go under the absolute minimum before it is filled again.</summary>
    AbsoluteMinimum,
    /// <summary>DP: … under the preferred minimum (or end the horizon under it).</summary>
    PreferredMinimum,
    /// <summary>DP: grid charge now, cheaper than the grid at the times it replaces later.</summary>
    CheaperThanLater,
    /// <summary>DP: battery held for a dearer time later.</summary>
    SavedForLater,
    /// <summary>DP: PV surplus fed in now instead of stored (feedin_priority) — the battery fills anyway or the export is
    /// worth more now; it still covers the house when PV falls short.</summary>
    FeedInFirst,
    /// <summary>DP: grid charge for the battery top-up — without it the target would be missed at the deadline.</summary>
    TopUp,
  }

  public enum PriceResolution
  {
    QuarterHourly,
    Hourly,
  }

  public enum PVPeriods
  {
    BeforePV,
    InPVPeriod,
    AfterPV,
  }
  // Optimal/Priority/PriorityPlus form a numbered ladder, climbed from the bottom in
  // HouseEnergy.FindLoadWindow: each mode's ruleset runs in full, and if it doesn't allow
  // charging, the next-higher-numbered ruleset gets a turn. The gaps between 10/20/30 are
  // deliberate room for a future mode to slot in between without renumbering anything else.
  // Off/Emergency are unrelated, independent gates (handled before the ladder even starts),
  // so their own values don't need to fit the numbering scheme.
  public enum LoadSchedulingMode
  {
    Off = 0,
    Emergency = 1,
    Optimal = 10,
    Priority = 20,
    PriorityPlus = 30,
  }
}

namespace NetDeamon.apps.PVControl
{
  /// <summary>Whether the battery needs a grid charge (<see cref="Simulator.EnergySimulator"/>).</summary>
  public struct NeedToChargeResult
  {
    /// <summary>A grid charge is needed.</summary>
    public bool NeedToCharge;
    /// <summary>Latest time to start charging.</summary>
    public DateTime LatestChargeTime;
    /// <summary>SoC at the critical point (or the lowest point without a need), %.</summary>
    public int EstimatedSoc;

    /// <summary>Creates the result.</summary>
    /// <param name="estimatedSoc">SoC at the critical point, %.</param>
    /// <param name="latestChargeTime">Latest time to start charging.</param>
    /// <param name="needToCharge">A grid charge is needed.</param>
    public NeedToChargeResult(int estimatedSoc, DateTime latestChargeTime, bool needToCharge)
    {
      EstimatedSoc = estimatedSoc;
      LatestChargeTime = latestChargeTime;
      NeedToCharge = needToCharge;
    }
  }

  /// <summary>A snapshot of the house.</summary>
  /// <summary>An inverter command: mode, reason and the two modifiers.</summary>
  /// <param name="mode">Inverter mode.</param>
  /// <param name="modeReason">Why this mode.</param>
  /// <param name="batteryChargeEnable">PV surplus may charge the battery.</param>
  /// <param name="exportAllowed">PV surplus may be exported.</param>
  public struct InverterState(InverterModes mode = InverterModes.normal, ForceChargeReasons modeReason = ForceChargeReasons.None, bool batteryChargeEnable = true, bool exportAllowed = true)
  {
    public InverterModes Mode = mode;
    public ForceChargeReasons ModeReason = modeReason;
    public bool BatteryChargeEnable = batteryChargeEnable;
    /// <summary>PV surplus may be exported; false at a negative export price, independent of <see cref="Mode"/>.</summary>
    public bool ExportAllowed = exportAllowed;
    /// <summary>A grid charge at full power (SolaX: Manual Force Charge), not the slower remote-controlled
    /// (VPP) path; decided when the charge starts (<see cref="Simulator.EnergySimulator.ChargesBulk"/>).</summary>
    public bool Bulk;
  }
}
