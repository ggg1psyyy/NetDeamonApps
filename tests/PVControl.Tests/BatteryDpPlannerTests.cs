using NetDeamon.apps;
using NetDeamon.apps.PVControl;
using NetDeamon.apps.PVControl.Optimizer;
using NetDeamon.apps.PVControl.Simulator;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Xunit;

namespace NetDeamonApps.Tests;

/// <summary>
/// The DP shadow planner (<see cref="BatteryDpPlanner"/>) and the one cost model both plans are judged by
/// (<see cref="PlanEvaluation"/>). The greedy simulation decides quarter by quarter, the next low at a time; the DP plans
/// the whole horizon at once (live 2026-10-30: the greedy plan charged at 23:00 for the morning and at 03:00 again for
/// the next evening's dip, found only after 23:00 had passed in its run). Scenario: 11.52 kWh battery, absolute 12 %,
/// preferred 20 %, ~400 W house load, PV 10–15 h, the prices of 2026-10-30.
/// </summary>
public class BatteryDpPlannerTests : TestBase
{
  static readonly DateTime Day = new(2026, 10, 30);

  static float Price(DateTime t) => (t.Date == Day ? t.Hour : t.Hour + 24) switch
  {
    < 6 => 0.39f, 6 => 0.47f, 7 or 8 => 0.50f, 9 => 0.45f, 10 => 0.426f, 11 => 0.414f, 12 => 0.401f, 13 => 0.404f,
    14 => 0.425f, 15 => 0.469f, 16 => 0.510f, 17 => 0.514f, 18 => 0.499f, 19 => 0.488f, 20 => 0.468f, 21 => 0.435f,
    22 => 0.412f, 23 => 0.394f, >= 24 and < 29 => 0.40f, 29 => 0.41f, 30 => 0.47f, 31 or 32 => 0.50f, _ => 0.45f,
  };

  static SimulationInput Input(DateTime start, int soc, int pvWh = 240, int loadWh = 100, int preferred = 20, bool enforce = false)
  {
    var prices = new List<PriceTableEntry>();
    var export = new List<PriceTableEntry>();   // live the export earns about half the import price (no network fee, no tax)
    var load = new Dictionary<DateTime, int>();
    var pv = new Dictionary<DateTime, int>();
    for (var t = Day.AddDays(-1); t < Day.AddDays(3); t = t.AddMinutes(15))
    {
      prices.Add(new PriceTableEntry(t, t.AddMinutes(15), Price(t)));
      export.Add(new PriceTableEntry(t, t.AddMinutes(15), Price(t) / 2));
      load[t] = loadWh;
      pv[t] = t.Hour >= 10 && t.Hour < 15 ? pvWh : 0;
    }
    return new SimulationInput
    {
      StartTime = start, StartSocPercent = soc, BatteryCapacityWh = 11_520,
      AbsoluteMinSocPercent = 12, PreferredMinSocPercent = preferred, EnforcePreferredSoc = enforce,
      MaxChargePowerAmps = 30, InverterEfficiency = 0.95f, ImportPrices = prices, ExportPrices = export,
      LoadPredictionWh = load, PVPredictionWh = pv, ExtraLoads = [],
      EnableCheapForceCharge = false, OpportunisticDischarge = false, ForceChargeMaxPrice = 0.35f,
      ForceChargeTargetSocPercent = 95, CurrentMode = new InverterState(InverterModes.normal),
    };
  }

  static IEnumerable<DateTime> ChargeStarts(IReadOnlyList<DpSlot> slots) =>
    slots.Where((s, i) => s.Mode == InverterModes.force_charge && (i == 0 || slots[i - 1].Mode != InverterModes.force_charge)).Select(s => s.Time);

  static readonly DpOptions O = new();

  public static IEnumerable<object[]> Scenarios() =>
  [
    [Day.AddHours(22), 29, 240],            // tonight: the morning and the next evening's dip
    [Day.AddHours(10).AddMinutes(30), 27, 240],  // the midday case
    [Day.AddHours(1), 20, 240],
    [Day.AddHours(7), 15, 240],             // close to the floor before the morning peak
    [Day.AddHours(18), 60, 600],            // full evening, sunny tomorrow
  ];

  [Theory]
  [MemberData(nameof(Scenarios))]
  public void Dp_NeverDearerThanTheGreedyPlan_UnderTheOneModel(DateTime start, int soc, int pvWh)
  {
    var input = Input(start, soc, pvWh);
    var greedy = PlanEvaluation.Evaluate(EnergySimulator.Simulate(input), O);
    var dp = BatteryDpPlanner.Plan(input, O).Cost;
    // 0.25-% grid of the DP: a few cents of rounding at most
    Assert.True(dp.Total <= greedy.Total + 0.03f, $"dp {dp} vs greedy {greedy}");
  }

  [Fact]
  public void Tonight_BothLowsBoughtInOneCharge()
  {
    // 22:00 at 29 %: the greedy plan charges at 23:00 for the morning and at 03:00 again for tomorrow evening.
    var input = Input(Day.AddHours(22), 29);
    var greedy = EnergySimulator.Simulate(input).Slots;
    var dp = BatteryDpPlanner.Plan(input, O).Slots;
    var dpStarts = ChargeStarts(dp).Where(t => t < Day.AddDays(1).AddHours(9)).ToList();
    Assert.True(dpStarts.Count == 1, string.Join(", ", dp.Where(s => s.Time.Minute % 30 == 0).Select(s => $"{s.Time:dd HH:mm} {s.Mode} {s.SocPct}%")));
    Assert.Equal(23, dpStarts[0].Hour);
    Assert.True(dp.Where(s => s.Time >= Day.AddDays(1).AddHours(15)).All(s => s.SocPct >= 19),
      string.Join(", ", dp.Where(s => s.Time.Minute == 0).Select(s => $"{s.Time:dd HH} {s.Mode} {s.SocPct}%")));
    Assert.NotEmpty(greedy);
  }

  [Fact]
  public void HardFloor_NeverPlannedBelow()
  {
    var plan = BatteryDpPlanner.Plan(Input(Day.AddHours(7), 13), O);
    Assert.True(plan.Slots.All(s => s.SocPct >= 11), string.Join(", ", plan.Slots.Take(12).Select(s => $"{s.Time:HH:mm} {s.Mode} {s.SocPct}%")));
    Assert.Equal(0f, plan.Cost.FloorPenalty, 2);
  }

  [Fact]
  public void Enforced_NoBandPenalty_ThePreferredIsTheFloor()
  {
    var plan = BatteryDpPlanner.Plan(Input(Day.AddHours(18), 30, enforce: true, preferred: 25), O);
    Assert.Equal(0f, plan.Cost.BandPenalty);
    Assert.True(plan.Slots.All(s => s.SocPct >= 24));
  }

  [Fact]
  public void Flows_AddUp_TheSocFollowsThem()
  {
    var input = Input(Day.AddHours(10).AddMinutes(30), 27);
    var slots = BatteryDpPlanner.Plan(input, O).Slots;
    for (int i = 0; i < slots.Count - 1; i++)
    {
      int expected = input.WhToSoc(Math.Clamp(input.SocToWh(slots[i].SocPct) + slots[i].BatteryChargeWh - slots[i].BatteryDischargeWh, 0, input.BatteryCapacityWh));
      Assert.InRange(slots[i + 1].SocPct, expected - 1, expected + 1);
    }
  }

  [Fact]
  public void Evaluate_GreedyPlan_SameCostAsTheSimulationsOwn()
  {
    // The model's grid cost of the greedy run equals the simulation's own (one cost function, GridCostOf).
    var input = Input(Day.AddHours(22), 29);
    var sim = EnergySimulator.Simulate(input);
    float own = sim.Slots.Sum(s => input.GridCostOf(s.Time, s.GridImportWh, s.GridExportWh));
    Assert.Equal(own, PlanEvaluation.Evaluate(sim, O).GridCost, 4);
  }

  [Fact]
  public void Runtime_WellWithinTheShadowInterval()
  {
    var input = Input(Day.AddHours(1), 20);
    BatteryDpPlanner.Plan(input, O);   // warm-up
    var sw = Stopwatch.StartNew();
    BatteryDpPlanner.Plan(input, O);
    Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"{sw.ElapsedMilliseconds} ms");
  }

  [Fact]
  public void EndValue_AtTheCheapestPriceOfTheLastDay()
  {
    // Horizon from 30.10. 22:00 to 01.11. 00:00: its last 24 h are 31.10.; cheapest there 0.40 (00–05 h).
    var input = Input(Day.AddHours(22), 29);
    Assert.Equal(0.40f, PlanEvaluation.RefillPrice(input), 4);
    int floorWh = input.SocToWh(input.GetEffectiveMinSoC());
    Assert.Equal(0.40f * input.RoundTripEfficiency, PlanEvaluation.EndValue(input, floorWh + 1000), 4);
    Assert.Equal(0f, PlanEvaluation.EndValue(input, floorWh - 500));
  }

  [Fact]
  public void HorizonEnd_NoChargeAboveTheRefillPrice()
  {
    // Live 2026-10-30 12:17: valued at the mean price, the plan bought 5.5 kWh at 0.41 at the horizon's very end for
    // after it. At the refill price (the last day's cheapest) a charge there doesn't pay.
    var input = Input(Day.AddHours(22), 29);
    var end = BatteryDpPlanner.HorizonEnd(input);
    var lastHours = BatteryDpPlanner.Plan(input, O).Slots.Where(s => s.Time >= end.AddHours(-4)).ToList();
    Assert.True(!lastHours.Any(s => s.Mode == InverterModes.force_charge),
      string.Join(", ", BatteryDpPlanner.Plan(input, O).Slots.Where(s => s.Time.Minute % 30 == 0).Select(s => $"{s.Time:dd HH:mm} {s.Mode} {s.SocPct}%")));
  }
}

