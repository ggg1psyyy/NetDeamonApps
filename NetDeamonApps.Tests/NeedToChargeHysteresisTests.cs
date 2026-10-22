using NetDeamon.apps;
using NetDeamon.apps.PVControl;
using NetDeamon.apps.PVControl.Simulator;
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace NetDeamonApps.Tests;

/// <summary>
/// Hysteresis of the NeedToCharge grid charge. Live 2026-10-19: after charging 12 → 32 % at
/// 03:00, every 1 % SoC tick moved the forecast trough below the floor again, giving 28 grid
/// charges of 30–60 s between 03:20 and 04:37. Night scenario: 11.52 kWh battery, floor 12 %,
/// ~400 W house load, PV from 08:00, flat price (the current quarter is always the cheapest).
/// From 03:15 to 08:00 the battery loses 19 × 100 Wh ≈ 16.5 %.
/// </summary>
public class NeedToChargeHysteresisTests : TestBase
{
  static readonly DateTime Night = new(2026, 10, 19, 3, 20, 0);

  static SimulationInput Input(int soc, InverterState mode)
  {
    var date = Night.Date;
    var prices = new List<PriceTableEntry>();
    var load = new Dictionary<DateTime, int>();
    var pv = new Dictionary<DateTime, int>();
    for (var t = date; t < date.AddDays(3); t = t.AddMinutes(15))
    {
      if (t < date.AddDays(2)) prices.Add(new PriceTableEntry(t, t.AddMinutes(15), 0.30f));
      load[t] = 100;
      pv[t] = t.Hour >= 8 && t.Hour < 17 ? 600 : 0;
    }
    return new SimulationInput
    {
      StartTime = Night, StartSocPercent = soc, BatteryCapacityWh = 11_520,
      AbsoluteMinSocPercent = 12, PreferredMinSocPercent = 15, EnforcePreferredSoc = false,
      MaxChargePowerAmps = 30, InverterEfficiency = 0.95f, ImportPrices = prices, ExportPrices = prices,
      LoadPredictionWh = load, PVPredictionWh = pv, ExtraLoads = [],
      EnableCheapForceCharge = false, OpportunisticDischarge = false, ForceChargeMaxPrice = 0.34f,
      ForceChargeTargetSocPercent = 95, CurrentMode = mode,
    };
  }

  static readonly InverterState Charging = new(InverterModes.force_charge, ForceChargeReasons.GoingUnderAbsoluteMinima);
  static readonly InverterState Normal = new(InverterModes.normal);

  [Fact]
  public void WhileCharging_TroughJustAboveFloor_KeepsCharging()
  {
    // 31 % → trough ≈ 14 %, only 2 % above the floor. With the old 1 % band the charge
    // stopped here, and the next SoC tick started it again.
    var sim = EnergySimulator.Simulate(Input(31, Charging));
    Assert.Equal(InverterModes.force_charge, sim.Slots[0].State.Mode);
  }

  [Fact]
  public void NotCharging_TroughJustAboveFloor_DoesNotStart()
  {
    // Starts once the trough reaches floor + forecast margin (trough ~07:45, 4.4 h ahead → +2 %,
    // since 2026-10-22): 35 % → trough ≈ 18 %, above 14 %.
    var sim = EnergySimulator.Simulate(Input(35, Normal));
    Assert.Equal(InverterModes.normal, sim.Slots[0].State.Mode);
  }

  [Fact]
  public void TroughBelowFloor_OneChargeLiftsItByTheBand()
  {
    var sim = EnergySimulator.Simulate(Input(26, Normal));
    var beforePv = sim.Slots.Where(s => s.Time < Night.Date.AddHours(8)).ToList();
    var charging = beforePv.Select(s => s.State.Mode == InverterModes.force_charge).ToList();
    int blocks = charging.Where((c, i) => c && (i == 0 || !charging[i - 1])).Count();

    Assert.Equal(InverterModes.force_charge, sim.Slots[0].State.Mode);
    Assert.Equal(1, blocks);
    // After the charge the battery stays at least the band above the floor (−1 for rounding).
    Assert.True(beforePv.Min(s => s.SoC) >= 12 + EnergySimulator.NeedToChargeHysteresisPct - 1,
      $"Night minimum {beforePv.Min(s => s.SoC)} %");
  }
}
