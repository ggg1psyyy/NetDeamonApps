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

  // ── Extra loads in the running quarter ───────────────────────────────────────────────────────

  [Theory]
  [InlineData(-40, 120)]   // started before the quarter
  [InlineData(0, 120)]     // at the quarter's start
  [InlineData(4, 120)]     // in the quarter, before now (the load search ran a few minutes ago)
  [InlineData(9, 120)]     // now (the load search ran this cycle)
  [InlineData(12, 120)]    // later in the running quarter
  [InlineData(-40, 7)]     // ended before now
  [InlineData(-40, 11)]    // ends in the running quarter, after now
  public void ExtraLoad_SummedOverTheSlots_IsItsEnergyFromNow(int startMin, int endMin)
  {
    // The running quarter counted a load that starts "now" twice short (its rest × the rest's fraction).
    var quarter = new DateTime(2026, 11, 21, 10, 30, 0);
    var now = quarter.AddMinutes(9);
    var ev = new ExtraLoad { Name = "EV", StartTime = quarter.AddMinutes(startMin), EndTime = quarter.AddMinutes(endMin), PowerW = 1823 };
    var input = Afternoon(now, 21).WithExtraLoads([ev]);
    int sum = 0;
    for (var t = quarter; t < quarter.AddHours(3); t = t.AddMinutes(15)) sum += input.SlotEnergyWh(t).ExtraWh;
    Assert.InRange(sum, ev.EnergyFromWh(now) - 12, ev.EnergyFromWh(now) + 1);   // whole Wh per slot
  }

  [Fact]
  public void ExtraLoad_TheWholeRunningQuarter_TheSameAtEveryMinuteWhileItsStartMoves()
  {
    // Live 2026-11-21 10:30: the EV's start moved with the load search; the whole-quarter floor check saw only its last
    // minutes and dropped the hold 9–11 minutes in.
    var quarter = new DateTime(2026, 11, 21, 10, 30, 0);
    foreach (int min in new[] { 0, 3, 6, 9, 11, 14 })
    {
      var now = quarter.AddMinutes(min).AddSeconds(5);
      var ev = new ExtraLoad { Name = "EV", StartTime = now, EndTime = quarter.AddHours(3), PowerW = 1823 };
      var input = Afternoon(now, 21).WithExtraLoads([ev]);
      Assert.Equal(455, input.QuarterEnergyWh(quarter).ExtraWh);                  // 1823 W × 15 min
      Assert.Equal((int)Energy.Wh(1823, quarter.AddMinutes(15) - now), input.SlotEnergyWh(quarter).ExtraWh);   // the rest: from now, once
    }
    // a load that ended before now isn't in the quarter any more, a later one only from its start
    var later = quarter.AddMinutes(9);
    var input2 = Afternoon(later, 21).WithExtraLoads([new ExtraLoad { Name = "x", StartTime = quarter, EndTime = quarter.AddMinutes(5), PowerW = 1200 },
      new ExtraLoad { Name = "y", StartTime = quarter.AddMinutes(12), EndTime = quarter.AddHours(1), PowerW = 1200 }]);
    Assert.Equal(60, input2.QuarterEnergyWh(quarter).ExtraWh);                    // y: 3 min × 1.2 kW
  }

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
