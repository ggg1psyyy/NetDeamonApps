using NetDeamon.apps;
using NetDeamon.apps.PVControl;
using NetDeamon.apps.PVControl.Simulator;
using Xunit;

namespace NetDeamonApps.Tests;

/// <summary>
/// Live 2026-10-25 08:00: EV on PriorityPlus, 18 → 25 % (≈ 5 kWh, 2 h 37 min at 1930 W), house in
/// force_charge in the day's cheapest hours (08–10 h, 32 ct, limit 35 ct). The EV didn't charge:
/// every full-length start ran past 10:00 on the house battery, which the house then bought back
/// overnight at 36–38 ct — so no start passed the price check. The cheap part alone does.
/// </summary>
public class CheapWindowShorterThanSessionTests : TestBase
{
  static readonly DateTime Day = new(2026, 10, 25);
  const float MaxPrice = 0.35f;
  const int EvW = 1930, SessionMinutes = 157;

  // Gross import €/kWh 2026-10-25 per hour.
  static readonly float[] Gross = [0.349f, 0.344f, 0.348f, 0.348f, 0.368f, 0.377f, 0.365f, 0.355f, 0.328f, 0.322f, 0.350f, 0.353f,
    0.354f, 0.361f, 0.395f, 0.423f, 0.433f, 0.420f, 0.419f, 0.420f, 0.412f, 0.403f, 0.361f, 0.356f];

  static SimulationInput Input()
  {
    var load = new Dictionary<DateTime, int>();
    var pv = new Dictionary<DateTime, int>();
    for (var t = Day.AddDays(-1); t < Day.AddDays(3); t = t.AddMinutes(15))
    {
      load[t] = t.Hour is >= 22 or < 8 ? 100 : 130; // heating on
      pv[t] = t.Hour is >= 10 and < 15 ? 120 : 0;   // fog
    }
    var prices = new List<PriceTableEntry>();
    for (var t = Day; t < Day.AddDays(1); t = t.AddHours(1))
      prices.Add(new PriceTableEntry(t, t.AddHours(1), Gross[t.Hour]));
    return new SimulationInput
    {
      StartTime = Day.AddHours(8), StartSocPercent = 18, BatteryCapacityWh = 11_520,
      AbsoluteMinSocPercent = 12, PreferredMinSocPercent = 12, EnforcePreferredSoc = true, // a hard 12 % floor with the forecast margin: the scenario as pinned (without enforcing, the preferred minimum is a band the plan keeps — PreferredMinimumTests)
      MaxChargePowerAmps = 16, InverterEfficiency = 0.95f,
      ImportPrices = new PriceList(prices).WithEstimatedPrices(Day.AddDays(2), 0.05f),
      ExportPrices = new PriceList(prices.Select(p => new PriceTableEntry(p.StartTime, p.EndTime, 0.08f))),
      PublishedPricesUntil = Day.AddDays(1), LoadPredictionWh = load, PVPredictionWh = pv,
      EnableCheapForceCharge = false, OpportunisticDischarge = false,
      ForceChargeMaxPrice = MaxPrice, ForceChargeTargetSocPercent = 100,
      CurrentMode = new InverterState(InverterModes.normal),
    };
  }

  static ExtraLoad Ev(DateTime start, DateTime end) => new() { Name = "EV", Priority = 10, StartTime = start, EndTime = end, PowerW = EvW };

  [Fact]
  public void FullSessionNeverPasses_ChargesTheCheapWindowNow()
  {
    var input = Input();
    var baseline = EnergySimulator.Simulate(input);
    SimulationResult Run(DateTime s, DateTime e) => EnergySimulator.Simulate(input.WithExtraLoads([Ev(s, e)]));

    var planned = SessionPlanner.FindBestPriceAwareStart(input.StartTime, Day.AddDays(1), (int)Math.Ceiling(EvW * SessionMinutes / 60.0), EvW, Run, baseline, minWindowMinutes: 30);

    var p = Assert.NotNull(planned);
    Assert.Equal(StartChoice.Partial, p.Choice);
    Assert.Equal(Day.AddHours(8), p.Start);
    Assert.Equal(Day.AddHours(10), p.End);
    Assert.True(Run(p.Start, p.End!.Value).IsGridCheapVs(baseline));
    Assert.False(Run(p.Start, p.End.Value.AddMinutes(15)).IsGridCheapVs(baseline)); // the longest that passes
  }

  [Fact]
  public void CheapWindowShorterThanMinWindow_NothingStarts()
  {
    var input = Input();
    var baseline = EnergySimulator.Simulate(input);
    SimulationResult Run(DateTime s, DateTime e) => EnergySimulator.Simulate(input.WithExtraLoads([Ev(s, e)]));

    Assert.Null(SessionPlanner.FindBestPriceAwareStart(input.StartTime, Day.AddDays(1), (int)Math.Ceiling(EvW * SessionMinutes / 60.0), EvW, Run, baseline, minWindowMinutes: 150));
  }

  [Fact]
  public void FollowUp_PlansTheRestAfterTheShortenedSession()
  {
    var input = Input();
    var first = Ev(Day.AddHours(8), Day.AddHours(10));

    var followUps = SessionPlanner.PlanFollowUpSessions(input, [first], EvW * SessionMinutes / 60, Day.AddDays(1));

    Assert.All(followUps, f => Assert.True(f.StartTime >= first.EndTime));
  }
}
