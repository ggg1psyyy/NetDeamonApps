using NetDeamon.apps;
using NetDeamon.apps.PVControl;
using NetDeamon.apps.PVControl.Optimizer;
using NetDeamon.apps.PVControl.Simulator;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace NetDeamonApps.Tests;

/// <summary>
/// The DP under a fixed import tariff (<see cref="SimulationInput.FixedImportPeriods"/>): no holds, no preferred reserve
/// unless enforced, and so — at a flat feed-in too (Hofer Sonne) — no grid charge either; mostly normal. On live
/// snapshots with the user's Fair price (26.54 ct gross) and the Sonne feed-in (16.21 ct).
/// </summary>
public class FixedTariffRulesTests
{
  const float Fair = 0.2654f, Sonne = 0.1621f;

  static ForecastSnapshot Snap(string name) => ForecastSnapshot.FromBytes(File.ReadAllBytes(
    Path.Combine(AppContext.BaseDirectory, "Data", $"forecast_snapshot_{name}.json.gz")));

  static SimulationInput Live(string name) => Snap(name).ToInput();

  static PriceList Flat(PriceList like, float price) =>
    new(like.Select(p => new PriceTableEntry(p.StartTime, p.EndTime, price)).ToList());

  /// <summary>The live input at Fair/Sonne, the fixed tariff from <paramref name="fixedFrom"/> (default: everywhere).</summary>
  static SimulationInput AtFair(SimulationInput live, DateTime? fixedFrom = null)
  {
    var end = BatteryDpPlanner.HorizonEnd(live);
    var flat = live.WithPrices(Flat(live.ImportPrices, Fair), Flat(live.ExportPrices, Sonne), live.PublishedPricesUntil);
    return flat.WithImportTariff(flat.ImportPrices, [new(fixedFrom ?? live.StartTime, end)]);
  }

  [Theory]
  [InlineData("2026-11-02_0600")]   // sunny day ahead, battery 90 %
  [InlineData("2026-11-04_1200")]   // midday, 62 %
  [InlineData("2026-11-03_1230")]   // dull day
  [InlineData("2026-11-07_2300")]   // night at 23 %, an EV from the grid 01:15–04:00
  public void Fixed_NoHoldNoCharge_TheReserveNotKept_MostlyNormal(string name)
  {
    var live = Live(name);
    var plan = BatteryDpPlanner.Plan(AtFair(live));
    // A hold only while a grid-fed load draws (the load's supply: the battery isn't drained by the EV — costless here).
    Assert.All(plan.Slots.Where(s => s.Mode == InverterModes.grid_only),
      s => Assert.Contains(live.ExtraLoads, e => e.FromGrid && s.Time >= e.StartTime.AddMinutes(-15) && s.Time < e.EndTime));
    Assert.DoesNotContain(plan.Slots, s => s.Mode == InverterModes.force_charge);
    Assert.True(plan.Slots.Count(s => s.Mode is InverterModes.normal or InverterModes.grid_only) >= plan.Slots.Count * 0.95);
    Assert.All(plan.Slots, s => Assert.True(s.SocPct >= live.AbsoluteMinSocPercent));
  }

  [Fact]
  public void Fixed_TheBatteryRunsDownToTheAbsoluteMinimum_NotHeldAtThePreferred()
  {
    var live = Live("2026-11-04_1200");
    Assert.True(BatteryDpPlanner.Plan(AtFair(live)).Slots.Min(s => s.SocPct) < live.GetPreferredMinSoC());
  }

  [Fact]
  public void Fixed_Enforced_ThePreferredMinimumStaysTheFloor()
  {
    var enforced = (Snap("2026-11-04_1200") with { EnforcePreferredSoc = true }).ToInput();
    Assert.Equal(enforced.PreferredMinSocPercent, enforced.PreferredMinSocAt(enforced.StartTime.AddHours(1)));
    var plan = BatteryDpPlanner.Plan(AtFair(enforced));
    int floorWh = enforced.SocToWh(enforced.PreferredMinSocPercent);
    Assert.All(plan.Slots, s => Assert.True(s.EnergyWh >= floorWh - 1, $"{s.Time:HH:mm} {s.EnergyWh} Wh"));
  }

  [Fact]
  public void Fixed_Enforced_HoldsOnlyAtTheFloor_NormalDownToItThenHeld()
  {
    // Live 2026-11-17 07:30 (Fair, enforced 20 %, 63 %): grid_only at 61 %, 58 %, 34 %, 30 %, normal in between — ties at
    // the flat price. Now normal down to the floor, then held to the PV; no charge (the morning's weak PV kept the hold
    // out and a charge kept the floor, which the price-block rule started at once).
    var live = Live("2026-11-17_0730");
    Assert.True(live.EnforcePreferredSoc);
    var plan = BatteryDpPlanner.Plan(live);
    int floorWh = live.SocToWh(live.PreferredMinSocPercent);
    Assert.DoesNotContain(plan.Slots, s => s.Mode == InverterModes.force_charge);
    Assert.All(plan.Slots.Where(s => s.Mode == InverterModes.grid_only),
      s => Assert.True(s.EnergyWh < floorWh + live.SocToWh(2), $"{s.Time:dd HH:mm} held at {s.EnergyWh} Wh"));
    Assert.All(plan.Slots, s => Assert.True(s.EnergyWh >= floorWh - 1, $"{s.Time:dd HH:mm} {s.EnergyWh} Wh"));
    // one hold block: at most one normal quarter inside it (the last part of a percent above the floor)
    int holdStarts = plan.Slots.Zip(plan.Slots.Skip(1)).Count(p => p.First.Mode != InverterModes.grid_only && p.Second.Mode == InverterModes.grid_only);
    Assert.InRange(holdStarts, 1, 2);
    Assert.True(BatteryDpPlanner.HoldOnlyAtFloor(live, live.StartTime));
    Assert.False(BatteryDpPlanner.HoldOnlyAtFloor((Snap("2026-11-17_0730") with { EnforcePreferredSoc = false }).ToInput(), live.StartTime));   // banned there (HoldAllowed)
  }

  [Theory]
  [InlineData(45, InverterModes.normal)]         // the snapshot
  [InlineData(46, InverterModes.normal)]         // a minute later, after +1 %
  [InlineData(46, InverterModes.force_charge)]   // and while the pulled charge ran
  public void Fixed_ATieChargeTomorrowNight_IsNotPulledToNow(int socPct, InverterModes running)
  {
    // Live 2026-11-17 18:00 (Fair, enforced 20 %, 45 %): the plan's charge at tomorrow 23:30 lay in the same "price block"
    // (the whole fixed horizon) and the charge-now rule started it at once — +1 % in a minute, normal, again (40 starts by
    // 19:25). The rule looks 4 h ahead only.
    var snap = Snap("2026-11-17_1800");
    Assert.True(snap.EnforcePreferredSoc);
    var input = (snap with { StartSocPercent = socPct, CurrentMode = running }).ToInput();
    var plan = BatteryDpPlanner.Plan(input);
    Assert.All(plan.Slots.Where(s => s.Time < input.StartTime.AddHours(4)),
      s => Assert.NotEqual(InverterModes.force_charge, s.Mode));
  }

  [Theory]
  [InlineData(0)]
  [InlineData(6)]
  [InlineData(9)]
  [InlineData(14)]
  public void Fixed_Enforced_AHoldAtTheFloor_StaysForTheWholeQuarter(int minutesIn)
  {
    // Live 2026-11-19 19:30 (Fair, enforced 20 %, ~1 % above the floor after a top-up): grid_only at each quarter's start,
    // normal 6–7 minutes in — the floor check judged the quarter's rest, which no longer reached the floor (9 switches by
    // 20:24). The whole quarter is judged: the running hold stays.
    var snap = Snap("2026-11-19_1930");
    var input = (snap with { StartTime = snap.StartTime.Date.AddHours(19).AddMinutes(30 + minutesIn).AddSeconds(5), CurrentMode = InverterModes.grid_only }).ToInput();
    Assert.True(BatteryDpPlanner.HoldOnlyAtFloor(input, input.StartTime));
    Assert.Equal(InverterModes.grid_only, BatteryDpPlanner.Plan(input).Slots[0].Mode);
  }

  [Fact]
  public void Fixed_ATieChargeTomorrowNight_WithoutTheLimit_StartsNow()
  {
    // The snapshot's own flip: looking over the whole horizon, the charge-now rule started tomorrow's 23:30 charge at once.
    var input = Live("2026-11-17_1800");
    var plan = BatteryDpPlanner.Plan(input, new DpOptions(ChargeNowWithinQuarters: 1000));
    Assert.Equal(InverterModes.force_charge, plan.Slots[0].Mode);
    Assert.Contains(BatteryDpPlanner.Plan(input).Slots, s => s.Mode == InverterModes.force_charge && s.Time >= input.StartTime.AddHours(24));
  }

  [Fact]
  public void MarketToday_FixedFromMidnight_HoldsOnlyBeforeTheSwitch()
  {
    var live = Live("2026-11-04_1200");
    var midnight = live.StartTime.Date.AddDays(1);
    var mixed = AtFair(live, midnight);
    mixed = mixed.WithImportTariff(new PriceList(live.ImportPrices.Where(p => p.StartTime < midnight)
      .Concat(mixed.ImportPrices.Where(p => p.StartTime >= midnight)).ToList()), mixed.FixedImportPeriods);
    var plan = BatteryDpPlanner.Plan(mixed);
    Assert.DoesNotContain(plan.Slots, s => s.Time >= midnight && s.Mode == InverterModes.grid_only);
    Assert.True(BatteryDpPlanner.HoldAllowed(mixed, midnight.AddMinutes(-15)) || !BatteryDpPlanner.HoldAllowed(live, midnight.AddMinutes(-15)));
    Assert.False(BatteryDpPlanner.HoldAllowed(mixed, midnight));
  }

  [Fact]
  public void PreferredMinSocAt_TheAbsoluteUnderTheFixedTariff_ElseThePreferred()
  {
    var live = Live("2026-11-04_1200");
    var from = live.StartTime.Date.AddDays(1);
    var input = live.WithImportTariff(live.ImportPrices, [new(from, from.AddHours(6))]);
    Assert.Equal(live.GetPreferredMinSoC(), input.PreferredMinSocAt(from.AddMinutes(-15)));
    Assert.Equal(live.AbsoluteMinSocPercent, input.PreferredMinSocAt(from));
    Assert.Equal(live.GetPreferredMinSoC(), input.PreferredMinSocAt(from.AddHours(6)));
    Assert.False(input.FixedImportAt(from.AddHours(6)));
  }

  [Fact]
  public void TariffPricesInput_TheFixedQuartersOfEachCandidate_AndTheSnapshotKeepsThem()
  {
    var live = Live("2026-11-04_1200");
    var next = live.StartTime.Date.AddDays(1);
    var end = BatteryDpPlanner.HorizonEnd(live);
    var lists = new Dictionary<string, List<PricePoint>>
    {
      ["Market"] = live.ImportPrices.Select(p => new PricePoint(p.StartTime, p.EndTime, p.Price)).ToList(),
      ["Fixed"] = live.ImportPrices.Select(p => new PricePoint(p.StartTime, p.EndTime, Fair)).ToList(),
    };
    var marketToday = new TariffPrices("Market", next, lists, ["Fixed"]);
    Assert.Equal([new FixedImportPeriod(next, end)], marketToday.Input(live, "Fixed")!.FixedImportPeriods);
    Assert.Empty(marketToday.Input(live, "Market")!.FixedImportPeriods);
    var fixedToday = marketToday with { Active = "Fixed" };
    var quarter = TimeRange.Floor(live.StartTime, TimeRange.SlotMinutes);   // the running quarter too (the snapshot starts seconds after it)
    Assert.NotEqual(quarter, live.StartTime);
    Assert.Equal([new FixedImportPeriod(quarter, next)], fixedToday.Input(live, "Market")!.FixedImportPeriods);
    Assert.Equal([new FixedImportPeriod(quarter, next), new FixedImportPeriod(next, end)], fixedToday.Input(live, "Fixed")!.FixedImportPeriods);
    Assert.False(BatteryDpPlanner.HoldAllowed(fixedToday.Input(live, "Market")!, quarter));
    Assert.Empty((marketToday with { Fixed = null }).Input(live, "Fixed")!.FixedImportPeriods);   // rows before the list: none

    var input = fixedToday.Input(live, "Market")!;
    var again = ForecastSnapshot.FromBytes(ForecastSnapshot.From(input).ToBytes()).ToInput();
    Assert.Equal(input.FixedImportPeriods, again.FixedImportPeriods);
    Assert.Empty(ForecastSnapshot.FromBytes(ForecastSnapshot.From(live).ToBytes()).ToInput().FixedImportPeriods);
  }

  [Fact]
  public void TheFloor_NoEuroStepAtIt_TheDpsStepsRoundedUpLikeTheFloor()
  {
    // 12 % of 11520 Wh = 1382.4 → 1383 Wh; the DP's step there was 1382 and blended the step under the floor (1 €/% and
    // quarter): its rest jumped by ~1 € between 1383 and 1412 Wh, and the plan held or charged to dodge it.
    var input = AtFair(Live("2026-11-04_1200"));
    var plan = BatteryDpPlanner.Plan(input);
    int floor = input.SocToWh(input.AbsoluteMinSocPercent);
    int k = 64;   // the night (05 04:00)
    float step = plan.Rest!.At(k, false, floor) - plan.Rest.At(k, false, floor + 29);
    Assert.InRange(step, 0f, 0.02f);   // 29 Wh are worth ~0.8 ct, not a euro
    Assert.Equal(0f, PlanEvaluation.Penalty(input, Soc.FromWhExact(floor - 1, input.BatteryCapacityWh), new DpOptions(), InverterModes.normal, input.StartTime).Floor);
    Assert.True(PlanEvaluation.Penalty(input, Soc.FromWhExact(floor - 29, input.BatteryCapacityWh), new DpOptions(), InverterModes.normal, input.StartTime).Floor > 0.2f);
  }

  [Fact]
  public void NextEnergy_ADischargeToTheFloorEndsAtIt_ARealDeficitStillGoesUnder()
  {
    var input = Live("2026-11-04_1200");
    int floor = input.SocToWh(input.AbsoluteMinSocPercent);
    int toFloor = input.DischargeAcForWh(floor + 28 - floor);   // what the flows deliver from 28 Wh above the floor
    Assert.Equal(floor, input.NextEnergyWh(floor + 28, 0, toFloor));
    Assert.True(input.NextEnergyWh(floor + 28, 0, 200) < floor - 100);   // more than it has (a replay): under, as the battery
  }

  [Fact]
  public void ForLoadPlanning_TheMarketsPrices_TheLimitAtThePricePaid()
  {
    // Under Fair the EV is placed on the market's prices, but the 40-ct limit is checked against the 26.54 ct paid: a
    // 45-ct market evening doesn't hold the EV back (it did before: the gate read the market's prices).
    var live = (Snap("2026-11-04_1200") with { ForceChargeMaxPrice = 0.40f }).ToInput();
    var fair = AtFair(live);
    var market = new PriceList(live.ImportPrices.Select(p => new PriceTableEntry(p.StartTime, p.EndTime, 0.45f)).ToList());
    var forLoads = fair.ForLoadPlanning(market);
    Assert.Empty(forLoads.FixedImportPeriods);
    Assert.All(forLoads.ImportPrices, p => Assert.Equal(0.45f, p.Price));
    var at = live.StartTime.Date.AddHours(19);
    var ev = new ExtraLoad { Name = "EV", StartTime = at, EndTime = at.AddHours(2), PowerW = 7000, FromGrid = true };
    Assert.True(EnergySimulator.Simulate(forLoads.WithExtraLoads([ev])).IsGridCheapVs(EnergySimulator.Simulate(forLoads)));
    var unpaid = fair.WithImportTariff(market, []);   // the market's prices as if paid: over the limit
    Assert.False(EnergySimulator.Simulate(unpaid.WithExtraLoads([ev])).IsGridCheapVs(EnergySimulator.Simulate(unpaid)));
  }

  [Fact]
  public void EndShortfall_OffWithNothingAboveTheFloor_OnForAPreferredOrEnforcedReserve()
  {
    var live = Live("2026-11-04_1200");
    int under = live.SocToWh(live.AbsoluteMinSocPercent) - 200;   // under the floor at the end
    Assert.True(PlanEvaluation.EndShortfall(live, under, new DpOptions()) > 1);   // the market: the end rule (preferred 20 %)
    Assert.Equal(0f, PlanEvaluation.EndShortfall(AtFair(live), live.SocToWh(live.AbsoluteMinSocPercent), new DpOptions()));
    var atFloor = (Snap("2026-11-04_1200") with { PreferredMinSocPercent = live.AbsoluteMinSocPercent }).ToInput();
    Assert.Equal(0f, PlanEvaluation.EndShortfall(atFloor, live.SocToWh(live.AbsoluteMinSocPercent) - 15, new DpOptions()));   // the market, preferred = floor
    var enforced = AtFair((Snap("2026-11-04_1200") with { EnforcePreferredSoc = true }).ToInput());
    Assert.True(PlanEvaluation.EndShortfall(enforced, enforced.SocToWh(enforced.AbsoluteMinSocPercent), new DpOptions()) > 1);
  }

  [Fact]
  public void FixedToday_TheMarketFromMidnight_HoldsAllowedBeforeTheSwitch_ReasonsNotThePreferredMinimum()
  {
    // Fair today, the market from midnight, its morning dearer (the advisor's Market run from Fixed, or a switch chosen):
    // a hold at the fixed price before the switch may pay, so it's allowed there; after it the market's rules.
    var live = Live("2026-11-03_1230");
    var midnight = live.StartTime.Date.AddDays(1);
    var end = BatteryDpPlanner.HorizonEnd(live);
    var prices = new PriceList(live.ImportPrices.Select(p => new PriceTableEntry(p.StartTime, p.EndTime, p.StartTime < midnight ? Fair : p.Price)).ToList());
    var input = live.WithPrices(prices, Flat(live.ExportPrices, Sonne), live.PublishedPricesUntil)
      .WithImportTariff(prices, [new(TimeRange.Floor(live.StartTime, TimeRange.SlotMinutes), midnight)]);
    Assert.False(input.FixedImportUntilEnd(midnight.AddMinutes(-15), end));
    Assert.True(BatteryDpPlanner.HoldAllowed(input, midnight.AddHours(-2)) == BatteryDpPlanner.HoldAllowed(live, midnight.AddHours(-2)));
    Assert.False(BatteryDpPlanner.HoldAllowed(AtFair(live), midnight.AddHours(-2)));   // fixed to the end: no hold
    Assert.True(AtFair(live).FixedImportUntilEnd(midnight.AddHours(-2), end));

    // It buys at the fixed price before the switch and holds the battery for the dearer market morning.
    var plan = BatteryDpPlanner.Plan(input);
    Assert.Contains(plan.Slots, x => x.Mode == InverterModes.force_charge && x.Time < midnight);
    Assert.Contains(plan.Slots, x => x.Mode == InverterModes.grid_only && x.Time < midnight);
  }

  [Fact]
  public void ReasonOf_ALowInTheFixedQuarters_NotThePreferredMinimum()
  {
    // A charge (~5 %), then the battery runs down to 18 % — 13 % without it, between the absolute 12 % and the preferred 20 %:
    // under the market that charge keeps the preferred minimum; under the fixed tariff none is kept there.
    var live = Live("2026-11-04_1200");
    var t0 = TimeRange.Floor(live.StartTime, TimeRange.SlotMinutes);
    int Wh(int pct) => live.SocToWh(pct);
    var slots = new List<DpSlot> { new(t0, 20, InverterModes.force_charge, false, 30, 600, 0, 600, 0, EnergyWh: Wh(20)) };
    for (int k = 1; k <= 12; k++)
      slots.Add(new(t0.AddMinutes(15 * k), 30 - k, InverterModes.normal, false, 0, 0, 0, 0, 120, EnergyWh: Wh(30 - k)));
    var plan = new DpPlan(slots, new PlanCost(0, 0, 0, 0), 19, EndEnergyWh: Wh(19));   // the low (18 %) in the slots, not at the end
    var greedy = new Dictionary<DateTime, SimulationSlot>();
    Assert.Equal(ForceChargeReasons.PreferredMinimum, DpSteering.ReasonOf(plan, 0, live, greedy));
    Assert.NotEqual(ForceChargeReasons.PreferredMinimum, DpSteering.ReasonOf(plan, 0, AtFair(live), greedy));
  }
}
