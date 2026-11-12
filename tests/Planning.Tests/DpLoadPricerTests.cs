using NetDeamon.apps;
using NetDeamon.apps.PVControl;
using NetDeamon.apps.PVControl.Optimizer;
using NetDeamon.apps.PVControl.Simulator;
using System;
using System.Linq;
using Xunit;

namespace NetDeamonApps.Tests;

/// <summary>
/// The DP's load pricing (<see cref="DpLoadPricer"/>): the loads' quarters replayed on a DP plan of the house without them
/// (its modes held), the battery's difference after them at the DP's cost of the rest. Live 2026-11-09 22:15 (snapshot in
/// Data/): EV 39 → 45 % (4.97 kWh at 1839 W, from the grid, ready by 06:30); the greedy simulation's re-planned night
/// charges priced 23:15 at 1.98 € and 23:00 at 2.20 € — 23:00 is 0.46 kWh at 0.4154 instead of 0.4611 cheaper.
/// Wiring the pricer into the cycle (HouseEnergy: the DP's while it steers) needs live HA; its choice is
/// <see cref="DpLoadPricer.For"/>.
/// </summary>
public class DpLoadPricerTests
{
  const int EvW = 1839, EvWh = 4968;   // 6 % × 0.828 kWh

  static SimulationInput Tonight() => ForecastSnapshot.FromBytes(System.IO.File.ReadAllBytes(
    System.IO.Path.Combine(AppContext.BaseDirectory, "Data", "forecast_snapshot_2026-11-09_2215.json.gz"))).ToInput().WithExtraLoads([]);

  static readonly DateTime Night = new(2026, 11, 9, 23, 0, 0);

  static ExtraLoad Ev(DateTime start, SimulationInput input) => new()
  {
    Name = "EV Charger", StartTime = start, EndTime = SessionPlanner.EndForEnergy(start, EvWh, EvW, input.StartTime), PowerW = EvW, FromGrid = true,
  };

  [Fact]
  public void Tonight_TheEarlierStartIsCheaperByItsOwnEnergy()
  {
    var input = Tonight();
    var pricer = new DpLoadPricer(input, new DpOptions());
    float at2300 = pricer.Replay([Ev(Night, input)]).CostEur;
    float at2315 = pricer.Replay([Ev(Night.AddMinutes(15), input)]).CostEur;
    float ownGap = EvW / 4000f * (input.ImportPrices.GetPriceOrMean(Night.AddHours(2).AddMinutes(45)) - input.ImportPrices.GetPriceOrMean(Night));
    Assert.True(at2300 < at2315, $"23:00 {at2300:F3} € vs 23:15 {at2315:F3} €");
    Assert.InRange(at2315 - at2300, ownGap - 0.006f, ownGap + 0.006f);
  }

  [Fact]
  public void Tonight_TheScanPicks2300_AndShowsTheGreedyCostNextToIt()
  {
    var input = Tonight();
    var baseline = EnergySimulator.Simulate(input);
    var pricer = new DpLoadPricer(input, new DpOptions());
    var template = Ev(Night, input);
    SimulationResult Run(DateTime s, DateTime e) =>
      SessionPlanner.ChooseSupply(l => input.SimulateWith(l), template.Between(s, e), baseline, pricer: pricer).Sim;
    var p = Assert.NotNull(SessionPlanner.FindBestPriceAwareStart(input.StartTime.RoundToNearestQuarterHour(), Night.AddHours(8), EvWh, EvW,
      Run, baseline, new DateTime(2026, 11, 10, 6, 30, 0), now: input.StartTime, pricer: pricer));
    Assert.Equal(Night, p.Start);
    Assert.StartsWith("dp: ", p.Scan);
    Assert.Contains("(greedy ", p.Scan);
  }

  [Fact]
  public void BatteryLoad_InANormalQuarter_CostsTheBatteryNotTheGrid_MoreTheMoreItTakes()
  {
    // DpDeviationTests' evening: filled to 99 % at night (0.415/0.461), the day at 0.65 runs on the battery, a charge at
    // 21:00 (0.45) after it.
    var input = DpDeviationTests.Input();
    var pricer = new DpLoadPricer(input, new DpOptions());
    var at = new DateTime(2026, 11, 10, 17, 0, 0);
    var slot = pricer.Plan.Slots.Single(s => s.Time == at);
    Assert.Equal(InverterModes.normal, slot.Mode);
    float grid = input.ImportPrices.GetPriceOrMean(at);
    float PerKwh(int wh)
    {
      var load = new ExtraLoad { Name = "Warm water", StartTime = at, EndTime = at.AddHours(wh / 2000.0), PowerW = 2000 };
      var replay = pricer.Replay([load]);
      Assert.All(replay.With, q => Assert.Equal(InverterModes.normal, q.Mode));
      Assert.Equal(0f, replay.GridEur, 3);   // the battery covers it
      return replay.CostEur / (wh / 1000f);
    }
    // A little: the DP's marginal value there (a bit more charged at 21:00) — its own number, not the quarter's 0.65.
    Assert.InRange(PerKwh(115), slot.BatteryValue - 0.03f, slot.BatteryValue + 0.03f);
    // More: under the preferred minimum and into the dear hours — dearer per kWh, still under the grid then. A single
    // marginal value (BatteryValue) would price it as the small one.
    float big = PerKwh(2000);
    Assert.True(big > slot.BatteryValue + 0.05f && big < grid - 0.01f, $"{big:F3} vs value {slot.BatteryValue:F3}, grid {grid:F3}");
  }

  [Fact]
  public void FromGridLoad_InANormalQuarter_HoldsTheBatteryForTheHouseToo()
  {
    var input = DpDeviationTests.Input();
    var pricer = new DpLoadPricer(input, new DpOptions());
    var at = new DateTime(2026, 11, 10, 17, 0, 0);
    var ev = new ExtraLoad { Name = "EV", StartTime = at, EndTime = at.AddMinutes(15), PowerW = 2000, FromGrid = true };
    var replay = pricer.Replay([ev]);
    var without = Assert.Single(replay.Without);
    var with = Assert.Single(replay.With);
    Assert.Equal(InverterModes.normal, without.Mode);
    Assert.True(without.DischargeWh > 0);
    Assert.Equal(InverterModes.grid_only, with.Mode);       // as live: the battery held while the EV draws
    Assert.Equal(0, with.DischargeWh);
    Assert.Equal(150 + 500, with.ImportWh);                  // the house's 150 Wh with the EV's 500
    Assert.True(replay.RestEur < 0);                         // the battery kept for later is worth something
  }

  [Fact]
  public void Pricer_TheDpsWhileItSteers_ElseTheGreedySimulations()
  {
    var input = DpDeviationTests.Input();
    Assert.Null(DpLoadPricer.For(null, input, null));
    var dp = Assert.IsType<DpLoadPricer>(DpLoadPricer.For(new DpOptions(), input, new InverterState(InverterModes.grid_only)));
    Assert.Equal("dp", dp.Name);
  }

  [Fact]
  public void GreedyPricing_IsTheSimulationsDifference_OfTheAddedLoadsOnly()
  {
    var input = DpDeviationTests.Input();
    var baseline = EnergySimulator.Simulate(input);
    var load = new ExtraLoad { Name = "EV", StartTime = Night, EndTime = Night.AddHours(1), PowerW = 2000, FromGrid = true };
    var sim = input.SimulateWith(load);
    Assert.Same(load, Assert.Single(LoadPricing.AddedLoads(sim, baseline)));
    Assert.Equal(sim.NetGridCostVs(baseline), LoadPricing.CostOf(null, sim, baseline));
    Assert.Equal(sim.NetGridCostVs(baseline), new GreedyLoadPricer(baseline).Cost([load], () => sim));
  }
}
