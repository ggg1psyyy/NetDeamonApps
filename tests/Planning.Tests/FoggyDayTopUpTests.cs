using NetDeamon.apps;
using NetDeamon.apps.PVControl;
using NetDeamon.apps.PVControl.Simulator;
using Xunit;

namespace NetDeamonApps.Tests;

/// <summary>
/// Live 2026-10-24 11:30 (heating just switched on, 44 %): a small PV bump in the afternoon, fog
/// tomorrow. The plan showed force_charge 13:00–15:00 that only took the PV surplus (+3 %/30 min),
/// then the floor from 04:00 held with grid_only until tomorrow 13:00 at ~0.42 €/kWh — although
/// today 13:00 costs 0.353. The charge target ended its trough search at the afternoon bump
/// ("next PV peak"), so the deficit was 0.
/// </summary>
public class FoggyDayTopUpTests
{
  static readonly DateTime Day = new(2026, 10, 24);

  // Gross import €/kWh 2026-10-24 per hour (EPEX + 1.2 ct + network, WiNAP 22–04, × 1.2).
  static readonly float[] Gross = [0.387f, 0.392f, 0.386f, 0.392f, 0.404f, 0.395f, 0.410f, 0.405f, 0.414f, 0.420f, 0.394f, 0.359f,
    0.354f, 0.353f, 0.361f, 0.402f, 0.399f, 0.428f, 0.430f, 0.416f, 0.419f, 0.416f, 0.397f, 0.365f];

  static SimulationInput Input(DateTime start, int soc)
  {
    var load = new Dictionary<DateTime, int>();
    var pv = new Dictionary<DateTime, int>();
    for (var t = Day.AddDays(-1); t < Day.AddDays(3); t = t.AddMinutes(15))
    {
      load[t] = t.Hour is >= 22 or < 8 ? 100 : 130;                       // ~400 W night, ~520 W day (heating on)
      pv[t] = t.Date == Day && t.Hour is >= 11 and < 16 ? 300              // small bump today
            : t.Date > Day && t.Hour is >= 10 and < 15 ? 90 : 0;           // fog tomorrow: below the load
    }
    var prices = new List<PriceTableEntry>();
    for (var t = Day; t < Day.AddDays(1); t = t.AddHours(1))
      prices.Add(new PriceTableEntry(t, t.AddHours(1), Gross[t.Hour]));
    var import = new PriceList(prices).WithEstimatedPrices(Day.AddDays(2), 0.05f); // tomorrow: estimated
    return new SimulationInput
    {
      StartTime = start, StartSocPercent = soc, BatteryCapacityWh = 11_520,
      AbsoluteMinSocPercent = 12, PreferredMinSocPercent = 20, EnforcePreferredSoc = true,
      MaxChargePowerAmps = 16, InverterEfficiency = 0.95f,
      ImportPrices = import, ExportPrices = new PriceList(prices.Select(p => new PriceTableEntry(p.StartTime, p.EndTime, 0.08f))),
      PublishedPricesUntil = Day.AddDays(1),
      LoadPredictionWh = load, PVPredictionWh = pv,
      EnableCheapForceCharge = false, OpportunisticDischarge = false,
      ForceChargeMaxPrice = 0.25f, ForceChargeTargetSocPercent = 100,
      CurrentMode = new InverterState(InverterModes.normal),
    };
  }

  [Fact]
  public void SmallBumpBeforeAFoggyDay_ChargesFromTheGridInTheCheapHours()
  {
    var result = EnergySimulator.Simulate(Input(Day.AddHours(11).AddMinutes(30), 44));

    // Grid energy into the battery in today's cheap 12–15 h (PV covers only part of it).
    int charged = result.Where(s => s.Time >= Day.AddHours(12) && s.Time < Day.AddHours(15)).Sum(s => s.BatteryChargeWh);
    int pvSurplus = result.Where(s => s.Time >= Day.AddHours(12) && s.Time < Day.AddHours(15)).Sum(s => Math.Max(0, s.PVWh - s.LoadWh));
    Assert.True(charged > pvSurplus + 1000, $"charged {charged} Wh, PV surplus only {pvSurplus} Wh");

    // Tomorrow morning isn't bridged by holding the floor with the grid for hours.
    int heldSlots = result.Count(s => s.Time >= Day.AddDays(1) && s.Time < Day.AddDays(1).AddHours(13)
                                      && s.State.Mode == InverterModes.grid_only);
    Assert.True(heldSlots < 8, $"{heldSlots} slots of grid_only tomorrow morning");
  }
}
