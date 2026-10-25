using NetDeamon.apps;
using NetDeamon.apps.PVControl;
using NetDeamon.apps.PVControl.Simulator;
using Xunit;
using Xunit.Abstractions;

namespace NetDeamonApps.Tests;

/// <summary>
/// Live 2026-10-25 14:00: the EV (PriorityPlus, 22 → 25 %, 68 min) was planned 06:00–07:08 from the
/// house battery — Priority's late start before sunrise, kept as the preferred start — and the house
/// planned a night grid charge 00:00–00:30 (34.9 ct) only to feed it. The battery path cost the same
/// as the grid (the simulation moves energy through it without loss), so the planner had no reason to
/// prefer the grid. PriorityPlus's grid sessions now take the energy directly from the grid (battery
/// held while they run without enough PV), and the cost counts the round-trip loss of battery use.
/// Foggy tomorrow: PV never covers house + EV, so the night quarters at 34.9 ct are the only option.
/// </summary>
public class EvDirectFromGridTests : TestBase
{
  static readonly DateTime Day = new(2026, 10, 25);
  // Gross import €/kWh per hour: today (2026-10-25) and tomorrow (published 14:00).
  static readonly float[] Today = [0.349f, 0.344f, 0.348f, 0.348f, 0.368f, 0.377f, 0.365f, 0.355f, 0.328f, 0.322f, 0.350f, 0.353f,
    0.354f, 0.361f, 0.395f, 0.423f, 0.433f, 0.420f, 0.419f, 0.420f, 0.412f, 0.403f, 0.361f, 0.356f];
  static readonly float[] Tomorrow = [0.349f, 0.370f, 0.363f, 0.355f, 0.366f, 0.372f, 0.416f, 0.394f, 0.361f, 0.361f, 0.361f, 0.366f,
    0.370f, 0.365f, 0.400f, 0.431f, 0.461f, 0.487f, 0.464f, 0.453f, 0.431f, 0.429f, 0.417f, 0.396f];
  static readonly DateTime Midnight = Day.AddDays(1);

  static SimulationInput Input(bool sunny = false)
  {
    var load = new Dictionary<DateTime, int>();
    var pv = new Dictionary<DateTime, int>();
    for (var t = Day.AddDays(-1); t < Day.AddDays(3); t = t.AddMinutes(15))
    {
      load[t] = 110;                                                       // 440 W: 86 % → ~48 % at midnight
      pv[t] = t.Date > Day && t.Hour is >= 9 and < 15 ? (sunny ? 1500 : 300) : 0;   // fog: 1.2 kW < house + EV; sunny: 6 kW
    }
    var prices = new List<PriceTableEntry>();
    for (var t = Day; t < Day.AddDays(2); t = t.AddHours(1))
      prices.Add(new PriceTableEntry(t, t.AddHours(1), t.Date == Day ? Today[t.Hour] : Tomorrow[t.Hour]));
    return new SimulationInput
    {
      StartTime = Day.AddHours(14), StartSocPercent = 86, BatteryCapacityWh = 11_520,
      AbsoluteMinSocPercent = 12, PreferredMinSocPercent = 30, EnforcePreferredSoc = false,
      MaxChargePowerAmps = 30, ChargeVoltageV = 240, InverterEfficiency = 0.95f,
      ImportPrices = new PriceList(prices), ExportPrices = new PriceList(prices.Select(p => new PriceTableEntry(p.StartTime, p.EndTime, 0.08f))),
      PublishedPricesUntil = Day.AddDays(2), LoadPredictionWh = load, PVPredictionWh = pv,
      EnableCheapForceCharge = false, OpportunisticDischarge = false,
      ForceChargeMaxPrice = 0.35f, ForceChargeTargetSocPercent = 100,
      CurrentMode = new InverterState(InverterModes.normal), MaxGridImportW = 9800,
    };
  }

  static ExtraLoad Ev(DateTime start, DateTime end, bool fromGrid = true) =>
    new() { Name = "EV", Priority = 10, StartTime = start, EndTime = end, PowerW = 1930, FromGrid = fromGrid };

  [Theory]
  [InlineData(false)]
  [InlineData(true)]   // 06:00 planned last cycle (Priority's late start before sunrise)
  public void EvRunsDirectlyFromTheGridInTheCheapQuarters(bool preferMorning)
  {
    var input = Input();
    var baseline = EnergySimulator.Simulate(input);
    SimulationResult Run(DateTime s, DateTime e) => EnergySimulator.Simulate(input.WithExtraLoads([Ev(s, e)]));

    var p = Assert.NotNull(SessionPlanner.FindBestPriceAwareStart(input.StartTime, Day.AddDays(2), 68, Run, baseline,
      preferredStart: preferMorning ? Midnight.AddHours(6) : null, minWindowMinutes: 15));

    // The only quarters at or under 35 ct before PV: 00:00–01:00 (01:00 is 37 ct) — the session is cut there.
    Assert.Equal(Midnight, p.Start);
    Assert.Equal(Midnight.AddHours(1), p.End);
    var plan = Run(p.Start, p.End!.Value);
    Assert.All(plan.Where(s => s.Time >= p.Start && s.Time < p.End), s => Assert.Equal(InverterModes.grid_only, s.State.Mode));
    Assert.True(plan.Sum(s => s.BatteryDischargeWh) <= baseline.Sum(s => s.BatteryDischargeWh), "the EV drew the house battery");
  }

  static SimulationInput WithSoc(SimulationInput i, int soc) => new()
  {
    StartTime = i.StartTime, StartSocPercent = soc, BatteryCapacityWh = i.BatteryCapacityWh,
    AbsoluteMinSocPercent = i.AbsoluteMinSocPercent, PreferredMinSocPercent = i.PreferredMinSocPercent, EnforcePreferredSoc = i.EnforcePreferredSoc,
    MaxChargePowerAmps = i.MaxChargePowerAmps, ChargeVoltageV = i.ChargeVoltageV, InverterEfficiency = i.InverterEfficiency,
    ImportPrices = i.ImportPrices, ExportPrices = i.ExportPrices, PublishedPricesUntil = i.PublishedPricesUntil,
    LoadPredictionWh = i.LoadPredictionWh, PVPredictionWh = i.PVPredictionWh,
    EnableCheapForceCharge = false, OpportunisticDischarge = false, ForceChargeMaxPrice = i.ForceChargeMaxPrice,
    ForceChargeTargetSocPercent = 100, CurrentMode = i.CurrentMode, MaxGridImportW = i.MaxGridImportW,
  };

  [Fact]
  public void ChooseSupply_BatteryEnoughWithoutGrid_UsesTheBattery()
  {
    // Full battery, sunny tomorrow: the EV in the morning (e.g. a ready-by target) needs no extra grid — like Priority.
    var input = WithSoc(Input(sunny: true), 100);
    var baseline = EnergySimulator.Simulate(input);
    // 30 min (~8 %): the battery (~27 % at the low without it) still stays above floor + margin.
    var (load, sim) = SessionPlanner.ChooseSupply(l => input.SimulateWith(l), Ev(Midnight.AddHours(5), Midnight.AddHours(5).AddMinutes(30)), baseline);
    Assert.False(load.FromGrid);
    Assert.False(sim.HasNewGridVs(baseline));
  }

  [Fact]
  public void ChooseSupply_GridNeeded_RunsDirectlyFromTheGrid()
  {
    var input = Input();
    var baseline = EnergySimulator.Simulate(input);
    var (load, _) = SessionPlanner.ChooseSupply(l => input.SimulateWith(l), Ev(Midnight.AddHours(5), Midnight.AddHours(6).AddMinutes(8)), baseline);
    Assert.True(load.FromGrid);
  }

  [Fact]
  public void HouseNeedsAChargeAnyway_EvRunsAlongTheForceCharge()
  {
    // Low battery: the house charges in the cheap 00:00 quarters itself; an EV from the grid there
    // runs alongside — force_charge stays (battery charging), not grid_only.
    var input = WithSoc(Input(), 25);
    var plan = EnergySimulator.Simulate(input.WithExtraLoads([Ev(Midnight, Midnight.AddMinutes(30))]));
    var evSlots = plan.Where(s => s.Time >= Midnight && s.Time < Midnight.AddMinutes(30)).ToList();
    Assert.All(evSlots, s => Assert.Equal(InverterModes.force_charge, s.State.Mode));
    Assert.All(evSlots, s => Assert.True(s.BatteryChargeWh > 0 && s.GridImportWh >= s.BatteryChargeWh + s.ExtraLoadWh));
  }

  [Fact]
  public void BatteryPathCostsItsRoundTripLoss()
  {
    // The same energy via the battery (Priority-type session) costs more than directly from the grid.
    var input = Input();
    var baseline = EnergySimulator.Simulate(input);
    var direct = EnergySimulator.Simulate(input.WithExtraLoads([Ev(Midnight, Midnight.AddHours(1))]));
    var viaBattery = EnergySimulator.Simulate(input.WithExtraLoads([Ev(Midnight, Midnight.AddHours(1), fromGrid: false)]));
    Assert.True(direct.NetGridCostVs(baseline) < viaBattery.NetGridCostVs(baseline),
      $"direct {direct.NetGridCostVs(baseline):F3} € vs battery {viaBattery.NetGridCostVs(baseline):F3} €");
  }
}
