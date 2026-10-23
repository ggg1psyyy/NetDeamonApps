using System;
using System.Collections.Generic;
using System.Linq;
using NetDeamon.apps;
using NetDeamon.apps.PVControl.Predictions;
using static NetDeamon.apps.PVControl.PVControlCommon;
using Math = System.Math;

namespace NetDeamon.apps.PVControl;

public class BatteryState
{
  private readonly RunningIntAverage _battChargeAverage;
  private readonly float _defaultInverterEfficiency = 0.9f;

  /// <summary>User setting: preferred minimum SoC in %.</summary>
  public bool EnforcePreferredSoC { get; set; }
  public int PreferredMinBatterySoC { get; set; }

  /// <summary>Set after construction once Prediction_BatterySoC is available.</summary>
  public Prediction? SoCPrediction { get; set; }

  /// <summary>Starts the 1-min battery power average from the current reading.</summary>
  /// <exception cref="NullReferenceException">The battery power entity is not configured.</exception>
  public BatteryState()
  {
    _battChargeAverage = new RunningIntAverage(TimeSpan.FromMinutes(1));
    if (PVCC_Config.CurrentBatteryPowerEntity is null)
      throw new NullReferenceException("BatteryPowerEntity not available");
    if (PVCC_Config.CurrentBatteryPowerEntity.TryGetStateValue(out int bat))
      _battChargeAverage.AddValue(bat);

    PreferredMinBatterySoC = 30;
    EnforcePreferredSoC = false;
  }

  /// <summary>Adds a battery power reading to the 1-min average.</summary>
  /// <param name="value">Battery power, W (positive = charging).</param>
  public void AddBatteryPowerValue(int value) => _battChargeAverage.AddValue(value);

  /// <summary>How long the last good SoC is used through a sensor dropout; after that the SoC is
  /// unavailable (<see cref="IsSoCUnavailable"/>) and PVControl hands the inverter back to normal mode.</summary>
  public static readonly TimeSpan SoCMaxHold = TimeSpan.FromMinutes(2);
  private readonly HeldReading _soc = new(SoCMaxHold);

  /// <summary>Reads the SoC sensor into the held reading (null when unknown).</summary>
  private void ReadSoC() =>
    _soc.Update(DateTime.Now, PVCC_Config.BatterySoCEntity is not null && PVCC_Config.BatterySoCEntity.TryGetStateValue(out int soc) ? soc : null);

  /// <summary>Battery SoC (%); the last good reading while the sensor is unknown (not 0 — that's an
  /// empty battery to the simulation and triggered a grid charge on 2026-10-21 13:28).</summary>
  public int BatterySoc
  {
    get
    {
      ReadSoC();
      return _soc.Value ?? 0;
    }
  }

  /// <summary>Whether there was no SoC reading for longer than <see cref="SoCMaxHold"/> (or none yet).</summary>
  /// <param name="now">Current time.</param>
  /// <returns>True if the SoC is unavailable.</returns>
  public bool IsSoCUnavailable(DateTime now)
  {
    ReadSoC();
    return _soc.IsStale(now);
  }

  public DateTime? LastGoodSoC => _soc.LastGood;

  private int? _lastKnownMinBatterySoC;

  /// <summary>Absolute minimum battery SoC floor (%), plus a 2% safety margin. Falls back to the
  /// last known-good sensor reading (or the static config fallback) when the entity is transiently
  /// unavailable or reports an implausible value, instead of collapsing toward 0 — this is a
  /// safety-relevant floor that must not silently drop just because a sensor blipped (same failure
  /// class as the BatteryCapacity divide-by-zero bug).</summary>
  public int AbsoluteMinimalSoC
  {
    get
    {
      if (PVCC_Config.MinBatterySoCEntity is not null
          && PVCC_Config.MinBatterySoCEntity.TryGetStateValue(out int minSoc)
          && minSoc is >= 0 and <= 100)
        _lastKnownMinBatterySoC = minSoc;
      else if (_lastKnownMinBatterySoC is null)
        _lastKnownMinBatterySoC = PVCC_Config.MinBatterySoCValue != default ? PVCC_Config.MinBatterySoCValue : 10;

      // add 2% to prevent inverter from shutting off early and needing to import probably expensive energy
      return _lastKnownMinBatterySoC.Value + 2;
    }
  }

  /// <summary>Preferred can never be lower than AbsoluteMinimalSoC.</summary>
  public int PreferredMinimalSoC => Math.Max(PreferredMinBatterySoC, AbsoluteMinimalSoC);

  public float InverterEfficiency =>
    PVCC_Config.InverterEfficiency != default ? PVCC_Config.InverterEfficiency : _defaultInverterEfficiency;

  private float _lastKnownBatteryCapacity;

  /// <summary>Physical battery capacity (Wh). Falls back to the last known-good reading when the
  /// sensor is transiently unavailable (e.g. a BMS dropout) instead of collapsing to 0 — capacity
  /// is a near-constant, and a 0 here causes a divide-by-zero in <c>EnergySimulator</c>.</summary>
  public int BatteryCapacity
  {
    get
    {
      if (PVCC_Config.BatteryCapacityEntity is not null && PVCC_Config.BatteryCapacityEntity.TryGetStateValue(out float battCapacity) && battCapacity > 0)
        _lastKnownBatteryCapacity = battCapacity;
      else if (_lastKnownBatteryCapacity == default && PVCC_Config.BatteryCapacityValue != default)
        _lastKnownBatteryCapacity = PVCC_Config.BatteryCapacityValue;

      return (int)_lastKnownBatteryCapacity;
    }
  }

  public BatteryStatuses BatteryStatus
  {
    get
    {
      if (CurrentAverageBatteryChargeDischargePower is > -10 and < 10)
        return BatteryStatuses.idle;
      else if (CurrentAverageBatteryChargeDischargePower > 0)
        return BatteryStatuses.charging;
      else if (CurrentAverageBatteryChargeDischargePower < 0)
        return BatteryStatuses.discharging;
      else
        return BatteryStatuses.unknown;
    }
  }

  public int UsableBatteryEnergy =>
    CalculateBatteryEnergyAtSoC(BatterySoc, EnforcePreferredSoC ? PreferredMinimalSoC : AbsoluteMinimalSoC);

  public int ReserveBatteryEnergy =>
    CalculateBatteryEnergyAtSoC(EnforcePreferredSoC ? PreferredMinimalSoC : AbsoluteMinimalSoC, 0);

  public int EstimatedTimeToBatteryFullOrEmpty
  {
    get
    {
      if (CurrentAverageBatteryChargeDischargePower is > -10 and < 10)
        return 0;
      else if (CurrentAverageBatteryChargeDischargePower > 0)
        return CalculateChargingDurationWh(BatterySoc, 100, CurrentAverageBatteryChargeDischargePower);
      else if (CurrentAverageBatteryChargeDischargePower < 0)
        return CalculateChargingDurationWh(BatterySoc, PreferredMinimalSoC, CurrentAverageBatteryChargeDischargePower);
      else
        return 0;
    }
  }

  public int CurrentAverageBatteryChargeDischargePower => _battChargeAverage.GetAverage();

  public int AvailablePower => 0;
  public int AvailableEnergy => 0;

  /// <summary>Energy above a floor at a SoC.</summary>
  /// <param name="soc">SoC, %.</param>
  /// <param name="minSoC">Floor, %; −1 = the preferred minimum.</param>
  /// <returns>Wh (negative below the floor).</returns>
  public int CalculateBatteryEnergyAtSoC(int soc, int minSoC = -1) =>
    Soc.ToWh(soc - (minSoC < 0 ? PreferredMinimalSoC : minSoC), BatteryCapacity);

  /// <summary>Time to charge or discharge between two SoCs at a power (<see cref="Soc.ChargeMinutes"/>).</summary>
  /// <param name="startSoC">Start SoC, %.</param>
  /// <param name="endSoC">End SoC, %.</param>
  /// <param name="pow">Power, W (negative = discharging).</param>
  /// <returns>Minutes.</returns>
  public int CalculateChargingDurationWh(int startSoC, int endSoC, int pow) =>
    Soc.ChargeMinutes(startSoC, endSoC, BatteryCapacity, InverterEfficiency, pow);
}
