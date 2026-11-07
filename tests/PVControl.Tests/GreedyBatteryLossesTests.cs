using NetDeamon.apps;
using NetDeamon.apps.PVControl;
using NetDeamon.apps.PVControl.Optimizer;
using NetDeamon.apps.PVControl.Simulator;
using Xunit;

namespace NetDeamonApps.Tests;

/// <summary>
/// The greedy simulation with the battery's conversion losses (<see cref="SimulationInput.BatteryLosses"/>, the default):
/// its flows, its look-ahead and its charge sizing on the one energy model — lossless it planned the night 1:1 and ran
/// under the hard floor once the losses were counted (replay of 149 live snapshots 2026-11-04…07: 119 under the floor,
/// down to 0 %; with the losses none visibly). <see cref="SimulationResult.NetGridCostVs"/> on the exact end energy.
/// </summary>
public class GreedyBatteryLossesTests : TestBase
{
  static readonly DateTime Evening = new(2026, 11, 7, 18, 0, 0);
  const float Price = 0.30f;

  static ForecastSnapshot Snapshot(string name) =>
    ForecastSnapshot.FromBytes(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Data", name)));

  /// <summary>Flat 0.30, 200 W house, no PV, 90 % at 18:00: no grid charge until tomorrow's midnight (the battery carries it).</summary>
  static SimulationInput Input(int loadWh = 50)
  {
    var load = new Dictionary<DateTime, int>();
    var pv = new Dictionary<DateTime, int>();
    for (var t = Evening.Date; t < Evening.Date.AddDays(3); t = t.AddMinutes(15))
      (load[t], pv[t]) = (loadWh, 0);
    var prices = new List<PriceTableEntry> { new(Evening.Date, Evening.Date.AddDays(3), Price) };
    return new SimulationInput
    {
      StartTime = Evening, StartSocPercent = 90, BatteryCapacityWh = 10_000,
      AbsoluteMinSocPercent = 12, PreferredMinSocPercent = 12, EnforcePreferredSoc = true,
      MaxChargePowerAmps = 30, ChargeVoltageV = 240, InverterEfficiency = 0.95f,
      ImportPrices = prices, ExportPrices = prices.Select(p => new PriceTableEntry(p.StartTime, p.EndTime, 0.08f)).ToList(),
      LoadPredictionWh = load, PVPredictionWh = pv,
      EnableCheapForceCharge = false, OpportunisticDischarge = false,
      ForceChargeMaxPrice = 0.25f, ForceChargeTargetSocPercent = 100,
      CurrentMode = new InverterState(InverterModes.normal),
    };
  }

  [Fact]
  public void LiveSnapshot_TheFloorHoldsOnlyWithTheLosses()
  {
    // 2026-11-07 11:00: lossless the plan's own flows, replayed against the real battery, went down to 5 %.
    var input = Snapshot("forecast_snapshot_2026-11-07_1100.json.gz").ToInput();
    Assert.True(input.BatteryLosses);
    double LowestPct(SimulationResult r) => r.EnergyPathWh(input).Min(wh => Soc.FromWhExact(wh, input.BatteryCapacityWh));
    int floor = input.GetEffectiveMinSoC();

    Assert.InRange(LowestPct(EnergySimulator.Simulate(input)), floor - 0.5, 100);
    Assert.True(LowestPct(EnergySimulator.Simulate(input.WithBatteryLosses(false))) < floor - 5);
  }

  [Fact]
  public void SimulatedSoc_IsTheEnergyPathOfItsFlows()
  {
    // The look-ahead, the decisions and the flows step the battery alike: the slots' SoC is the exact replay.
    var input = Snapshot("forecast_snapshot_2026-11-07_0945.json.gz").ToInput();
    var sim = EnergySimulator.Simulate(input);
    var path = sim.EnergyPathWh();
    Assert.All(sim.Slots.Select((s, i) => (s.SoC, Pct: input.WhToSoc(path[i]))), x => Assert.Equal(x.Pct, x.SoC));
  }

  [Fact]
  public void NightDrain_IsTheLoadOverTheDischargeEfficiency()
  {
    var lossy = EnergySimulator.Simulate(Input());
    var lossless = EnergySimulator.Simulate(Input().WithBatteryLosses(false));
    Assert.DoesNotContain(lossy.Slots, s => s.State.Mode != InverterModes.normal);
    int drawn = lossy.EnergyPathWh()[0] - lossy.EnergyPathWh()[^1];
    int delivered = lossless.EnergyPathWh()[0] - lossless.EnergyPathWh()[^1];
    Assert.Equal(delivered / 0.95, drawn, 0.01 * drawn);
  }

  [Fact]
  public void NetGridCost_UnderOnePercent_StillCounts()
  {
    // 60 Wh (0.6 %) from the battery: the end SoC in whole % may not move — the exact end energy does. It is refilled at
    // the refill price with the charge loss (StoredPrice); the discharge loss is in the drawn energy.
    var input = Input();
    var baseline = EnergySimulator.Simulate(input);
    var small = EnergySimulator.Simulate(input.WithExtraLoads([new ExtraLoad
      { Name = "probe", Priority = 10, StartTime = Evening.AddHours(2), EndTime = Evening.AddHours(2).AddMinutes(15), PowerW = 240 }]));

    float expected = input.DrawnWh(60) / 1000f * input.StoredPrice(input.RefillPrice);
    Assert.Equal(expected, small.NetGridCostVs(baseline), 3);
    Assert.Equal(60 / 1000f * input.DeliveredPrice(Price), small.NetGridCostVs(baseline), 3);   // the round trip in total
  }

  [Fact]
  public void EvFromTheGrid_HoldMovesTheHouseOffTheBattery_ItsRoundTripSaved()
  {
    // While a grid session runs the battery is held: the house's 400 Wh over these 2 h come from the grid directly instead
    // of through the battery — a flat price, so the only difference is the loss the battery would have taken.
    var input = Input();
    var baseline = EnergySimulator.Simulate(input);
    var withEv = EnergySimulator.Simulate(input.WithExtraLoads([new ExtraLoad
      { Name = "EV", Priority = 10, StartTime = Evening.AddHours(2), EndTime = Evening.AddHours(4), PowerW = 1856, FromGrid = true }]));

    float evKwh = 1856 * 2 / 1000f, houseKwh = 8 * 50 / 1000f;
    float saved = houseKwh * (input.DeliveredPrice(Price) - Price);
    // ± 0.2 ct: the battery steps in whole Wh per quarter (8 × ⌈52.6⌉ drawn).
    Assert.InRange(withEv.NetGridCostVs(baseline), evKwh * Price - saved - 0.002f, evKwh * Price - saved + 0.002f);
  }
}
