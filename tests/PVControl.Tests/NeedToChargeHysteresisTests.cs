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
/// From 03:15 to 08:00 the battery loses 19 × 100 Wh ≈ 16.5 %. The floor is a hard one (EnforcePreferredSoc,
/// preferred = 12 %): the forecast margin on top of it is what these start/stop bands are about (without enforcing,
/// the preferred minimum is the band instead — <see cref="PreferredMinimumTests"/>).
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
      AbsoluteMinSocPercent = 12, PreferredMinSocPercent = 12, EnforcePreferredSoc = true,
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
  public void TroughBelowFloor_OneChargeLiftsItToFloorPlusMarginOnly()
  {
    // Low ~07:45, 4.4 h ahead → margin 2 %: target = 12 + 2 + 1 = 15 %. Until 2026-10-22 the 5 %
    // band was in the target too (here 20 %; live with a 20 % floor the low landed at 29 %).
    var sim = EnergySimulator.Simulate(Input(26, Normal));
    var beforePv = sim.Slots.Where(s => s.Time <= Night.Date.AddHours(8)).ToList(); // low at 08:00
    var charging = beforePv.Select(s => s.State.Mode == InverterModes.force_charge).ToList();
    int blocks = charging.Where((c, i) => c && (i == 0 || !charging[i - 1])).Count();

    Assert.Equal(InverterModes.force_charge, sim.Slots[0].State.Mode);
    Assert.Equal(1, blocks);
    int nightMin = beforePv.Min(s => s.SoC);
    Assert.InRange(nightMin, 12 + EnergySimulator.ChargeTargetBufferPct, 12 + 2 + EnergySimulator.ChargeTargetBufferPct + 1);
  }

  [Fact]
  public void NotCharging_TroughAtFloorPlusMargin_DoesNotStart()
  {
    // 31 % → trough ≈ 14 % = floor + margin: a charge that just stopped at 15 % and lost a 1 %
    // tick — no restart (that was the 2026-10-19 flapping).
    var sim = EnergySimulator.Simulate(Input(31, Normal));
    Assert.Equal(InverterModes.normal, sim.Slots[0].State.Mode);
  }

  [Fact]
  public void NotCharging_TroughOneBelowFloorPlusMargin_Starts()
  {
    // 30 % → trough ≈ 13 % = floor + margin − 1: a real 2 % drop — starts, in whatever quarter
    // the price logic picks (here: now, flat price).
    var sim = EnergySimulator.Simulate(Input(30, Normal));
    Assert.Equal(InverterModes.force_charge, sim.Slots[0].State.Mode);
  }

  [Fact]
  public void LowImminent_NoMargin_StartsAtTheFloorNotBelow()
  {
    // 07:15, low at 08:00 (45 min → margin 0): 15 % → trough 12 % = the floor itself. Without
    // the clamp the start level would be floor − 1 = 11 % and the battery would touch the floor.
    var night = Input(15, Normal);
    var at0715 = new SimulationInput
    {
      StartTime = Night.Date.AddHours(7).AddMinutes(15), StartSocPercent = 15, BatteryCapacityWh = night.BatteryCapacityWh,
      AbsoluteMinSocPercent = 12, PreferredMinSocPercent = 12, EnforcePreferredSoc = true,
      MaxChargePowerAmps = 30, InverterEfficiency = 0.95f, ImportPrices = night.ImportPrices, ExportPrices = night.ExportPrices,
      LoadPredictionWh = night.LoadPredictionWh, PVPredictionWh = night.PVPredictionWh, ExtraLoads = [],
      EnableCheapForceCharge = false, OpportunisticDischarge = false, ForceChargeMaxPrice = 0.34f,
      ForceChargeTargetSocPercent = 95, CurrentMode = Normal,
    };
    Assert.Equal(InverterModes.force_charge, EnergySimulator.Simulate(at0715).Slots[0].State.Mode);
  }
}
