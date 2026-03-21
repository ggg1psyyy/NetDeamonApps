using NetDeamon.apps;

namespace NetDeamon.apps.PVControl.Simulator;

/// <summary>
/// All inputs required by <see cref="LoadSchedulingDecision.Decide"/>.
/// Pure-data object with no HA dependencies — the caller (HouseEnergy) populates this from
/// live sensor readings and the latest simulation results before each evaluation cycle.
/// </summary>
public record LoadSchedulingInput
{
  /// <summary>Selected scheduling mode (Off / Optimal / Priority / PriorityPlus / Emergency).</summary>
  public required LoadSchedulingMode Mode { get; init; }

  /// <summary>User-configured target level (SoC %, temperature °C, …).</summary>
  public required float TargetLevel { get; init; }

  /// <summary>Current level (SoC %, temperature °C, …).</summary>
  public required float CurrentLevel { get; init; }

  /// <summary>Load power draw rate in W (from config, e.g. 1800 W for EV charging).</summary>
  public required int ChargeRateW { get; init; }

  /// <summary>
  /// Net PV available for this load, in W: PV power minus base house load (load power already stripped).
  /// Positive = PV surplus; negative = PV deficit.
  /// </summary>
  public required int NetPvW { get; init; }

  /// <summary>Current house battery SoC in %.</summary>
  public required int BatterySoC { get; init; }

  /// <summary>Effective minimum house battery SoC that must be maintained in %.</summary>
  public required int PreferredMinSoC { get; init; }

  /// <summary>
  /// True when the simulation predicts the house battery will reach ≥ 99 % today.
  /// Used as the start condition for Optimal mode.
  /// </summary>
  public required bool WillReachMaxSocToday { get; init; }

  /// <summary>Current value of the active output (used for hysteresis keep-condition).</summary>
  public required bool CurrentlyActive { get; init; }

  /// <summary>
  /// Hysteresis margin in %. Applied to relax start thresholds once the load is active.
  /// E.g. 8 means: require battery ≥ preferred+8 % to start, vs. preferred % to keep.
  /// </summary>
  public required int HysteresisMarginPct { get; init; }

  /// <summary>
  /// How much grid import (W, negative net PV) is tolerated when already active.
  /// 0 = no import allowed even when active; 200 = up to 200 W import accepted.
  /// </summary>
  public required int ImportToleranceW { get; init; }

  /// <summary>Current total import price in ct/kWh.</summary>
  public required float CurrentImportPriceCt { get; init; }

  /// <summary>Max price threshold for PriorityPlus grid-import allow, in ct/kWh.</summary>
  public required float MaxPriceCt { get; init; }
}
