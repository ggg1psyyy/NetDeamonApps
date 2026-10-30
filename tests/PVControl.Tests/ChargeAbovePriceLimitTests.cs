using NetDeamon.apps;
using NetDeamon.apps.PVControl;
using NetDeamon.apps.PVControl.Simulator;
using Xunit;

namespace NetDeamonApps.Tests;

/// <summary>
/// Above the force-charge price limit a grid charge protects only the floor itself, never the
/// forecast margin alone (live 2026-10-25 12:15: 35.4 ct over a 35-ct limit for a margin 19 h
/// ahead, again once the EV drew the battery). Every price here is over the limit (36–40 ct).
/// </summary>
public class ChargeAbovePriceLimitTests : TestBase
{
  static readonly DateTime Day = new(2026, 10, 25);

  static SimulationResult Plan(int soc)
  {
    var load = new Dictionary<DateTime, int>();
    var pv = new Dictionary<DateTime, int>();
    for (var t = Day.AddDays(-1); t < Day.AddDays(3); t = t.AddMinutes(15))
    {
      load[t] = 100;                                                    // 400 W
      pv[t] = t.Date > Day && t.Hour is >= 9 and < 15 ? 600 : 0;        // PV only from tomorrow 09:00
    }
    var prices = new List<PriceTableEntry>();
    for (var t = Day; t < Day.AddDays(1); t = t.AddHours(1))
      prices.Add(new PriceTableEntry(t, t.AddHours(1), t.Hour == 22 ? 0.36f : 0.40f));
    return EnergySimulator.Simulate(new SimulationInput
    {
      StartTime = Day.AddHours(13).AddMinutes(30), StartSocPercent = soc, BatteryCapacityWh = 11_520,
      AbsoluteMinSocPercent = 12, PreferredMinSocPercent = 12, EnforcePreferredSoc = true, // a hard floor with the forecast margin: the scenario as pinned (without enforcing, the preferred minimum is a band the plan keeps — PreferredMinimumTests)
      MaxChargePowerAmps = 30, ChargeVoltageV = 240, InverterEfficiency = 0.95f,
      ImportPrices = new PriceList(prices).WithEstimatedPrices(Day.AddDays(2), 0.05f),
      ExportPrices = new PriceList(prices.Select(p => new PriceTableEntry(p.StartTime, p.EndTime, 0.08f))),
      PublishedPricesUntil = Day.AddDays(1), LoadPredictionWh = load, PVPredictionWh = pv,
      EnableCheapForceCharge = false, OpportunisticDischarge = false,
      ForceChargeMaxPrice = 0.35f, ForceChargeTargetSocPercent = 100,
      CurrentMode = new InverterState(InverterModes.normal), MaxGridImportW = 9800,
    });
  }

  [Fact]
  public void OnlyTheMarginShort_NoChargeAboveTheLimit()
  {
    // Low ~14 %: above the 12 % floor, inside floor + 4 % margin. Before: 4 quarters at 36 ct.
    var plan = Plan(82);
    Assert.DoesNotContain(plan, s => s.State.Mode == InverterModes.force_charge);
    Assert.True(plan.Min(s => s.SoC) >= 12);
  }

  [Fact]
  public void FloorAtRisk_ChargesOnlyForTheFloor()
  {
    // Without charging the low falls under the floor: one quarter in the cheapest hour lifts it
    // to floor + buffer (before: 4 quarters, up to floor + margin).
    var plan = Plan(78);
    int quarters = plan.Count(s => s.State.Mode == InverterModes.force_charge);
    Assert.InRange(quarters, 1, 2);
    Assert.All(plan.Where(s => s.State.Mode == InverterModes.force_charge), s => Assert.Equal(22, s.Time.Hour));
    Assert.True(plan.Min(s => s.SoC) >= 12);
  }
}
