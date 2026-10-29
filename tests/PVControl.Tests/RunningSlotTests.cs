using NetDeamon.apps;
using NetDeamon.apps.PVControl;
using NetDeamon.apps.PVControl.Simulator;
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace NetDeamonApps.Tests;

/// <summary>
/// Inputs that used to jump at quarter boundaries and with coarse sensor steps (live 2026-10-22:
/// a grid charge stopped at 14:12 and started again at 14:15:15). The simulation's running slot
/// only counts its rest (SimulationInput.SlotFraction), the PV energy counter is made continuous
/// (CounterInterpolator).
/// </summary>
public class RunningSlotTests : TestBase
{
  static SimulationInput Afternoon(DateTime start, int soc)
  {
    var day = start.Date;
    var prices = new List<PriceTableEntry>();
    var load = new Dictionary<DateTime, int>();
    var pv = new Dictionary<DateTime, int>();
    for (var t = day; t < day.AddDays(3); t = t.AddMinutes(15))
    {
      if (t < day.AddDays(2)) prices.Add(new PriceTableEntry(t, t.AddMinutes(15), 0.33f));
      load[t] = 120;                                         // ~480 W
      pv[t] = t.Hour >= 9 && t.Hour < 16 ? 620 : 0;          // ~2.5 kW in the afternoon
    }
    return new SimulationInput
    {
      StartTime = start, StartSocPercent = soc, BatteryCapacityWh = 11_520,
      AbsoluteMinSocPercent = 12, PreferredMinSocPercent = 20, EnforcePreferredSoc = true,
      MaxChargePowerAmps = 30, InverterEfficiency = 0.95f, ImportPrices = prices, ExportPrices = prices,
      LoadPredictionWh = load, PVPredictionWh = pv, ExtraLoads = [],
      EnableCheapForceCharge = false, OpportunisticDischarge = false, ForceChargeMaxPrice = 0.35f,
      ForceChargeTargetSocPercent = 95, CurrentMode = new InverterState(InverterModes.normal),
    };
  }

  static int SoCAt(SimulationResult sim, DateTime t) => sim.Slots.First(s => s.Time == t).SoC;

  [Fact]
  public void QuarterBoundary_SameSoC_SameFuture()
  {
    // 14:14:45 and 14:15:15 with the same live SoC: 30 s apart, the future must be the same
    // within a percent. Before, the 14:00 slot counted in full at 14:14:45 — 15 min of 2 kW PV
    // surplus (+4 % SoC) that had already flowed.
    var before = EnergySimulator.Simulate(Afternoon(new DateTime(2026, 10, 22, 14, 14, 45), 52));
    var after = EnergySimulator.Simulate(Afternoon(new DateTime(2026, 10, 22, 14, 15, 15), 52));
    var evening = new DateTime(2026, 10, 22, 16, 0, 0);
    Assert.InRange(SoCAt(before, evening) - SoCAt(after, evening), -1, 1);
  }

  [Fact]
  public void RunningSlot_OnlyItsRestCounts()
  {
    // 14:10 → 5 of 15 minutes left: a third of the slot's PV and load.
    var sim = EnergySimulator.Simulate(Afternoon(new DateTime(2026, 10, 22, 14, 10, 0), 52));
    Assert.Equal(620 / 3, sim.Slots[0].PVWh);
    Assert.Equal(620, sim.Slots[1].PVWh);
  }

  [Fact]
  public void CounterInterpolator_FillsTheStepsWithPower()
  {
    var t = new DateTime(2026, 10, 22, 14, 0, 0);
    var counter = new CounterInterpolator(100);
    counter.AddPower(t, 2400);
    Assert.Equal(7700, counter.Value(t, 7700));
    Assert.Equal(7760, counter.Value(t.AddSeconds(90), 7700));   // 2.4 kW × 90 s = 60 Wh
    Assert.Equal(7800, counter.Value(t.AddMinutes(5), 7700));    // capped at one step
    Assert.Equal(7800, counter.Value(t.AddMinutes(5).AddSeconds(15), 7800)); // new step: back in line
    Assert.Equal(7810, counter.Value(t.AddMinutes(5).AddSeconds(30), 7800));
  }

  [Fact]
  public void CounterInterpolator_PowerChangesAndMidnight()
  {
    var t = new DateTime(2026, 10, 22, 14, 0, 0);
    var counter = new CounterInterpolator(100);
    counter.AddPower(t, 2400);
    counter.Value(t, 7700);
    counter.AddPower(t.AddSeconds(60), 0);                         // cloud: 40 Wh so far, then nothing
    Assert.Equal(7740, counter.Value(t.AddSeconds(120), 7700));
    Assert.Equal(0, counter.Value(t.Date.AddDays(1), 0));          // counter reset at midnight
  }
}
