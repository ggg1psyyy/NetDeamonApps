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

  public void AddBatteryPowerValue(int value) => _battChargeAverage.AddValue(value);

  public int BatterySoc =>
    PVCC_Config.BatterySoCEntity is not null && PVCC_Config.BatterySoCEntity.TryGetStateValue(out int soc) ? soc : 0;

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

  public int CalculateBatteryEnergyAtSoC(int soc, int minSoC = -1)
  {
    float s = (float)soc / 100;
    float ms = minSoC < 0 ? (float)PreferredMinimalSoC / 100 : (float)minSoC / 100;
    float e = BatteryCapacity * s - BatteryCapacity * ms;
    return (int)e;
  }

  public int CalculateChargingDurationWh(int startSoC, int endSoC, int pow)
  {
    float sS = (float)startSoC / 100;
    float eS = (float)endSoC / 100;
    float reqEnergy = (eS - sS) * BatteryCapacity * InverterEfficiency;
    float duration = reqEnergy / pow;
    return (int)(duration * 60);
  }

  public int CalculateChargingDurationA(int startSoC, int endSoC, int amps, int volts = 240)
  {
    int pow = amps * volts;
    return CalculateChargingDurationWh(startSoC, endSoC, pow);
  }
}
