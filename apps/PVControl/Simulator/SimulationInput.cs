using System.Collections.Generic;

namespace NetDeamon.apps.PVControl.Simulator;

/// <summary>
/// All inputs required by <see cref="PVSimulator.Simulate"/>.
/// This is a pure-data object with no dependencies on Home Assistant or any live state —
/// the caller (HouseEnergy.RunSimulation) is responsible for reading current sensor values
/// and populating this before each simulation run.
/// Keeping the simulator input/output free of HA types makes the logic independently testable.
/// </summary>
public class SimulationInput
{
  /// <summary>
  /// The real wall-clock time at which the simulation starts.
  /// It is rounded down to the nearest 15-minute boundary internally,
  /// so the first slot aligns with the current quarter-hour.
  /// </summary>
  public required DateTime StartTime { get; init; }

  /// <summary>Current battery state of charge in %, read from the BatterySoC sensor.</summary>
  public required int StartSocPercent { get; init; }

  /// <summary>Usable battery capacity in Wh (read from inverter or set in config).</summary>
  public required int BatteryCapacityWh { get; init; }

  /// <summary>
  /// Hard floor SoC the inverter should never discharge below, in %.
  /// Typically the inverter's own minimum reserve plus a small safety buffer.
  /// </summary>
  public required int AbsoluteMinSocPercent { get; init; }

  /// <summary>
  /// User-configured preferred minimum SoC, in %.
  /// Only enforced when <see cref="EnforcePreferredSoc"/> is true; otherwise the system
  /// is allowed to go as low as <see cref="AbsoluteMinSocPercent"/> to wait for cheaper prices.
  /// </summary>
  public required int PreferredMinSocPercent { get; init; }

  /// <summary>
  /// When true the preferred SoC acts as a hard floor (same as AbsoluteMinSocPercent).
  /// When false the system may dip below preferred SoC if the next cheap window arrives soon.
  /// </summary>
  public required bool EnforcePreferredSoc { get; init; }

  /// <summary>
  /// Maximum grid-to-battery charge current in Amps (matches PVCC_Config.MaxBatteryChargePower).
  /// Converted to watts internally as Amps × 240 V.
  /// </summary>
  public required int MaxChargePowerAmps { get; init; }

  /// <summary>
  /// Round-trip inverter efficiency (0–1). Used when estimating how long charging will take.
  /// A value of 0.9 means 10 % of grid energy is lost as heat during charge/discharge.
  /// </summary>
  public required float InverterEfficiency { get; init; }

  /// <summary>
  /// Hourly import price table (EPEX Spot + taxes + network fees), in ct/kWh.
  /// Covers today and — once EPEX publishes them around 13:00 — tomorrow's prices.
  /// </summary>
  public required List<PriceTableEntry> ImportPrices { get; init; }

  /// <summary>
  /// Hourly export/feed-in price table, in ct/kWh.
  /// Either the same variable spot prices as import (scaled) or a fixed feed-in tariff,
  /// depending on ExportPriceIsVariable in config.
  /// </summary>
  public required List<PriceTableEntry> ExportPrices { get; init; }

  /// <summary>
  /// Historical-average house load per 15-minute slot, in Wh.
  /// Produced by HourlyWeightedAverageLoadPrediction from the SQLite energy history DB.
  /// Keys are quarter-hour-aligned DateTime values starting from today's midnight.
  /// </summary>
  public required Dictionary<DateTime, int> LoadPredictionWh { get; init; }

  /// <summary>
  /// Forecast PV generation per 15-minute slot, in Wh.
  /// Produced by OpenMeteoSolarForecastPrediction from the HA forecast entities.
  /// Keys are quarter-hour-aligned DateTime values starting from today's midnight.
  /// </summary>
  public required Dictionary<DateTime, int> PVPredictionWh { get; init; }

  /// <summary>
  /// Optional list of additional loads not captured in the historical load prediction
  /// (e.g. car charging, scheduled appliances). The simulator adds their energy draw
  /// on top of <see cref="LoadPredictionWh"/> for each affected slot.
  /// </summary>
  public List<ExtraLoad> ExtraLoads { get; init; } = [];

  /// <summary>
  /// When true the user has activated the "force charge at cheapest price" switch,
  /// instructing the system to fill the battery to <see cref="ForceChargeTargetSocPercent"/>
  /// during the cheapest import window of the day regardless of the SoC forecast.
  /// </summary>
  public required bool ForceCharge { get; init; }

  /// <summary>
  /// When true the system may force-discharge the battery to the grid during the two
  /// daily price maxima, as long as the SoC forecast shows we can still reach 100 % later
  /// and stay above the minimum floor.
  /// </summary>
  public required bool OpportunisticDischarge { get; init; }

  /// <summary>
  /// The import price ceiling (ct/kWh) above which opportunistic discharge is triggered.
  /// Also used as the upper limit for user-initiated force charge: we never force-charge
  /// at a price above this value.
  /// </summary>
  public required int ForceChargeMaxPriceCt { get; init; }

  /// <summary>
  /// Target SoC % for user-initiated force charging (ForceCharge switch).
  /// Charging stops once this level is reached. Independently, NeedToCharge logic
  /// always charges toward 96–100 %.
  /// </summary>
  public required int ForceChargeTargetSocPercent { get; init; }

  /// <summary>
  /// User override for the inverter mode (e.g. force the inverter to grid_only for maintenance).
  /// When set to anything other than <see cref="InverterModes.automatic"/> all simulation logic
  /// is bypassed and every slot returns this fixed mode.
  /// </summary>
  public required InverterModes OverrideMode { get; init; }

  /// <summary>
  /// The inverter mode that was active at the end of the previous simulation run.
  /// Passed into the simulator so the first slot inherits hysteresis state
  /// (e.g. knowing we were already in force_charge prevents unnecessary mode flips).
  /// </summary>
  public required InverterState CurrentMode { get; init; }

  /// <summary>
  /// How many more times the simulator should emit <see cref="InverterModes.reset"/> before
  /// switching to normal operation. Set to 2 by HouseEnergy when the inverter returns from
  /// remote/manual mode — the reset pulse is needed to unlock normal battery usage again.
  /// </summary>
  public required int CurrentResetCounter { get; init; }

  /// <summary>
  /// Current running-average grid power in W (positive = importing from grid).
  /// Only used for the first simulation slot to detect the known inverter quirk where it
  /// imports 50–300 W in normal mode instead of using the battery.
  /// Future slots have no live grid reading so this check is skipped for them.
  /// </summary>
  public required int CurrentAverageGridPowerW { get; init; }
}
