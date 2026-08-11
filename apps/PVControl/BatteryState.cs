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

  public int AbsoluteMinimalSoC
  {
    get
    {
      int minAllowedSoC = PVCC_Config.MinBatterySoCValue != default ? PVCC_Config.MinBatterySoCValue : 0;
      if (PVCC_Config.MinBatterySoCEntity is not null && PVCC_Config.MinBatterySoCEntity.TryGetStateValue(out int minSoc))
        minAllowedSoC = minSoc;
      // add 2% to prevent inverter from shutting off early and needing to import probably expensive energy
      return minAllowedSoC + 2;
    }
  }

  /// <summary>Preferred can never be lower than AbsoluteMinimalSoC.</summary>
  public int PreferredMinimalSoC => Math.Max(PreferredMinBatterySoC, AbsoluteMinimalSoC);

  public float InverterEfficiency =>
    PVCC_Config.InverterEfficiency != default ? PVCC_Config.InverterEfficiency : _defaultInverterEfficiency;

  public int BatteryCapacity
  {
    get
    {
      float batteryCapacity = PVCC_Config.BatteryCapacityValue != default ? PVCC_Config.BatteryCapacityValue : 0;
      if (PVCC_Config.BatteryCapacityEntity is not null && PVCC_Config.BatteryCapacityEntity.TryGetStateValue(out float battCapacity))
        batteryCapacity = battCapacity;
      return (int)batteryCapacity;
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
