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
  public void EndShortfall_OffUnderTheFixedTariff_OnUnderTheMarket()
  {
    var live = Live("2026-11-04_1200");
    int under = live.SocToWh(live.AbsoluteMinSocPercent) - 200;   // under the floor at the end
    Assert.True(PlanEvaluation.EndShortfall(live, under, new DpOptions()) > 1);   // the market: the end rule (preferred 20 %)
    Assert.Equal(0f, PlanEvaluation.EndShortfall(AtFair(live), live.SocToWh(live.AbsoluteMinSocPercent), new DpOptions()));
    var enforced = AtFair((Snap("2026-11-04_1200") with { EnforcePreferredSoc = true }).ToInput());
    Assert.True(PlanEvaluation.EndShortfall(enforced, enforced.SocToWh(enforced.AbsoluteMinSocPercent), new DpOptions()) > 1);
  }
}
