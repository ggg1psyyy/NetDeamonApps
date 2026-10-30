using NetDeamon.apps;
using NetDeamon.apps.PVControl;
using NetDeamon.apps.PVControl.Simulator;
using Xunit;

namespace NetDeamonApps.Tests;

/// <summary>
/// Live 2026-10-26 13:30 (44 %, PV surplus until ~15:00, the floor reached ~01:30): the plan charged
/// tomorrow 00:30 at 0.3727 although today 13:00–14:00 costs 0.3649 — the battery carries the night
/// and the morning either way. Charging vs holding the floor compared the cheapest price with the
/// hours to bridge; at midday "until PV takes over" was today's PV (now), so it fell back to one
/// hour after the low (~0.377, charging lost), at 00:30 the span ran to tomorrow's PV with the
/// morning peak (~0.415, charging won). The span is now always the low until PV after it.
/// </summary>
public class MiddayChargeVsNightTests
{
  static readonly DateTime Day = new(2026, 10, 26);

  // Gross import €/kWh per hour, 2026-10-26 and 2026-10-27 (both published at 13:15).
  static readonly float[] Today = [0.349f, 0.370f, 0.363f, 0.355f, 0.366f, 0.372f, 0.416f, 0.394f, 0.361f, 0.361f, 0.361f, 0.366f,
    0.370f, 0.3649f, 0.3996f, 0.4308f, 0.4610f, 0.4865f, 0.4635f, 0.4526f, 0.4313f, 0.4290f, 0.4173f, 0.3959f];
  static readonly float[] Tomorrow = [0.3727f, 0.3752f, 0.3808f, 0.3776f, 0.3860f, 0.4220f, 0.4379f, 0.4487f, 0.4379f, 0.4224f, 0.3759f, 0.3623f,
    0.3504f, 0.3612f, 0.3844f, 0.4351f, 0.4474f, 0.4511f, 0.4486f, 0.4474f, 0.4264f, 0.4030f, 0.3905f, 0.3550f];

  static SimulationInput Input(DateTime start, int soc, Func<DateTime, float>? price = null)
  {
    var load = new Dictionary<DateTime, int>();
    var pv = new Dictionary<DateTime, int>();
    for (var t = Day.AddDays(-1); t < Day.AddDays(3); t = t.AddMinutes(15))
    {
      load[t] = 110;                                                       // ~440 W
      pv[t] = t.Date == Day && t.Hour is >= 12 and < 15 ? 450              // midday surplus today
            : t.Date > Day && t.Hour is >= 10 and < 15 ? 600 : 0;          // tomorrow's PV from 10:00
    }
    var prices = new List<PriceTableEntry>();
    for (var t = Day; t < Day.AddDays(2); t = t.AddHours(1))
      prices.Add(new PriceTableEntry(t, t.AddHours(1), price?.Invoke(t) ?? (t.Date == Day ? Today[t.Hour] : Tomorrow[t.Hour])));
    return new SimulationInput
    {
      StartTime = start, StartSocPercent = soc, BatteryCapacityWh = 11_520,
      AbsoluteMinSocPercent = 12, PreferredMinSocPercent = 12, EnforcePreferredSoc = true, // a hard floor with the forecast margin: the scenario as pinned (without enforcing, the preferred minimum is a band the plan keeps — PreferredMinimumTests)
      MaxChargePowerAmps = 30, InverterEfficiency = 0.95f,
      ImportPrices = new PriceList(prices), ExportPrices = new PriceList(prices.Select(p => new PriceTableEntry(p.StartTime, p.EndTime, 0.17f))),
      PublishedPricesUntil = Day.AddDays(2),
      LoadPredictionWh = load, PVPredictionWh = pv,
      EnableCheapForceCharge = false, OpportunisticDischarge = false,
      ForceChargeMaxPrice = 0.35f, ForceChargeTargetSocPercent = 95,
      CurrentMode = new InverterState(InverterModes.normal),
    };
  }

  [Fact]
  public void NightAndMorningOnTheBattery_ChargesInTheCheaperMiddayQuarters()
  {
    var result = EnergySimulator.Simulate(Input(Day.AddHours(13).AddMinutes(30), 44));

    Assert.Equal(InverterModes.force_charge, result[0].State.Mode);
    // The night needs no real charge any more — at most a top-up of the floor itself (~70 Wh here).
    int nightGridCharge = result.Where(s => s.Time >= Day.AddDays(1) && s.Time < Day.AddDays(1).AddHours(10)
                                            && s.State.Mode == InverterModes.force_charge).Sum(s => s.BatteryChargeWh);
    Assert.True(nightGridCharge < 300, $"night charge {nightGridCharge} Wh");
  }

  [Fact]
  public void HoldingStillWins_WhenTheHoursToBridgeAreCheap()
  {
    // Same day, but tomorrow's night and morning cost what today 13:00 does: buying them directly
    // later beats charging now with the round-trip loss.
    var result = EnergySimulator.Simulate(Input(Day.AddHours(13).AddMinutes(30), 44,
      t => t < Day.AddHours(14) ? Today[t.Hour] : 0.37f));

    Assert.NotEqual(InverterModes.force_charge, result[0].State.Mode);
  }
}
