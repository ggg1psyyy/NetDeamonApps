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
    // Late tomorrow evening the reserve may bridge to the next cheap night (beyond the horizon): only the hard floor holds.
    Assert.True(dp.Where(s => s.Time >= Day.AddDays(1).AddHours(15)).All(s => s.SocPct >= 12),
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
    Assert.Equal(own, sim.GridCost, 4);   // sensor.pv_control_mode's grid_eur = the shadow's greedy_grid_eur
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
    // what 1 kWh above the floor delivers after the discharge loss (the DP's input; the charge loss is in its flows)
    Assert.Equal(0.40f * 0.95f, PlanEvaluation.EndValue(input.WithBatteryLosses(), floorWh + 1000), 3);
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

  /// <summary>An evening peak (0.51 until 21:00, 0.48 until 23:00) before a cheap night (0.39 from 23:00).</summary>
  internal static SimulationInput EveningPeak(int soc)
  {
    var b = Input(Day.AddHours(18), soc, loadWh: 80);
    float P(DateTime t) => (t.Date == Day ? t.Hour : t.Hour + 24) switch
    {
      >= 18 and < 21 => 0.51f, 21 or 22 => 0.48f, >= 23 and < 29 => 0.39f, >= 29 and < 33 => 0.47f, _ => 0.44f,
    };
    var prices = new List<PriceTableEntry>();
    var export = new List<PriceTableEntry>();
    for (var t = Day.AddDays(-1); t < Day.AddDays(3); t = t.AddMinutes(15))
    {
      prices.Add(new PriceTableEntry(t, t.AddMinutes(15), P(t)));
      export.Add(new PriceTableEntry(t, t.AddMinutes(15), P(t) / 2));
    }
    return new SimulationInput
    {
      StartTime = b.StartTime, StartSocPercent = soc, BatteryCapacityWh = b.BatteryCapacityWh,
      AbsoluteMinSocPercent = 12, PreferredMinSocPercent = 20, EnforcePreferredSoc = false,
      MaxChargePowerAmps = 30, InverterEfficiency = 0.95f, ImportPrices = prices, ExportPrices = export,
      LoadPredictionWh = b.LoadPredictionWh, PVPredictionWh = b.PVPredictionWh, ExtraLoads = [],
      EnableCheapForceCharge = false, OpportunisticDischarge = false, ForceChargeMaxPrice = 0.35f,
      ForceChargeTargetSocPercent = 95, CurrentMode = new InverterState(InverterModes.normal),
    };
  }

  [Fact]
  public void EveningPeak_TheReserveBridgesToTheCheapNight()
  {
    // 18:00 at 30 %: the evening drains it to ~16 % by 23:00. Using ~0.4 kWh of the reserve for ~2 h to buy at 0.39
    // instead of 0.48–0.51 saves ~5 ct; the reserve costs ~0.75 ct at the default — the DP stretches: no charge in the
    // peak, the band used, the hard floor kept (the user, 2026-10-31: stretching an expensive time to a cheaper charge
    // may use up the preferred minimum).
    var plan = BatteryDpPlanner.Plan(EveningPeak(30), new DpOptions());
    var evening = plan.Slots.Where(s => s.Time < Day.AddHours(23)).ToList();
    string trace = string.Join(", ", plan.Slots.Where(s => s.Time < Day.AddDays(1).AddHours(6)).Select(s => $"{s.Time:HH:mm} {s.Mode} {s.SocPct}%"));
    Assert.True(evening.All(s => s.Mode != InverterModes.force_charge), trace);
    Assert.True(evening.Min(s => s.SocPct) < 20, trace);
    Assert.True(plan.Slots.All(s => s.SocPct >= 12), trace);
    // With a reserve worth ten times as much (0.005 € per % and quarter) the reserve is kept — by holding the battery
    // in the peak, not by buying into it (with the conversion losses a peak charge costs more than the grid directly).
    var strict = BatteryDpPlanner.Plan(EveningPeak(30), new DpOptions(BandPenaltyPerPctQuarter: 0.005f));
    var strictEvening = strict.Slots.Where(s => s.Time < Day.AddHours(23)).ToList();
    Assert.True(strictEvening.Min(s => s.SocPct) >= 20, string.Join(", ", strictEvening.Select(s => $"{s.Time:HH:mm} {s.Mode} {s.SocPct}%")));
    Assert.DoesNotContain(strictEvening, s => s.Mode == InverterModes.force_charge);
  }

  [Fact]
  public void HorizonEnd_AtLeastThePreferredMinimum()
  {
    // Tonight's case: without the rule the plan let tomorrow's late evening slide to 17 % at the horizon's end (the
    // next cheap night lies beyond it). The horizon ends at least at the preferred minimum (the user, 2026-10-31).
    var input = Input(Day.AddHours(22), 29);
    var plan = BatteryDpPlanner.Plan(input, O);
    Assert.True(plan.EndSocPct >= 20, $"end {plan.EndSocPct} %");
    Assert.Equal(0f, plan.Cost.EndShortfall);
    // the same rule judges the greedy plan: under the minimum at the end it pays the shortfall
    Assert.Equal(3 * O.FloorPenaltyPerPctQuarter, PlanEvaluation.EndShortfall(input, input.SocToWh(17), O), 2);
  }

  [Fact]
  public void Bulk_NotWhereVppChargesAsCheaply()
  {
    // Live 2026-10-31 19:45 (the stored planning situation): the night charge's 04:45 quarter tips to bulk in the DP for
    // a few Wh above VPP's limit (1213 vs 1196) — VPP in the same hourly price block is enough. What steers is VPP
    // (DpSteering.BlockPaths: bulk only where VPP can't deliver the block's charge).
    var bytes = System.IO.File.ReadAllBytes(System.IO.Path.Combine(AppContext.BaseDirectory, "Data", "forecast_snapshot_2026-10-31_1945.json.gz"));
    var input = ForecastSnapshot.FromBytes(bytes).ToInput();
    var timeline = DpSteering.ToTimeline(BatteryDpPlanner.Plan(input, O), EnergySimulator.Simulate(input));
    Assert.DoesNotContain(timeline, s => s.State.Mode == InverterModes.force_charge && s.State.Bulk);
  }

  [Fact]
  public void Bulk_WhereItSavesMore()
  {
    // One cheap quarter (0.20) at 60 % in an expensive day: VPP's limit at that SoC leaves energy bulk would buy —
    // worth far more than the cent, so the DP still charges bulk.
    var b = Input(Day.AddHours(1), 60, pvWh: 0);
    var cheap = Day.AddHours(2);
    var prices = b.ImportPrices.Select(p => new PriceTableEntry(p.StartTime, p.EndTime, p.StartTime == cheap ? 0.20f : 0.50f)).ToList();
    var input = new SimulationInput
    {
      StartTime = b.StartTime, StartSocPercent = 60, BatteryCapacityWh = b.BatteryCapacityWh,
      AbsoluteMinSocPercent = 12, PreferredMinSocPercent = 20, EnforcePreferredSoc = false,
      MaxChargePowerAmps = 30, InverterEfficiency = 0.95f, ImportPrices = prices, ExportPrices = b.ExportPrices,
      LoadPredictionWh = b.LoadPredictionWh, PVPredictionWh = b.PVPredictionWh, ExtraLoads = [],
      EnableCheapForceCharge = false, OpportunisticDischarge = false, ForceChargeMaxPrice = 0.35f,
      ForceChargeTargetSocPercent = 95, CurrentMode = new InverterState(InverterModes.normal),
      VppChargePowerW = soc => 5950 - 41 * soc,   // the YAML's default line: ~3.5 kW at 60 % against bulk's 6.9 kW
    };
    var slot = BatteryDpPlanner.Plan(input, O).Slots.Single(s => s.Time == cheap);
    Assert.Equal(InverterModes.force_charge, slot.Mode);
    Assert.True(slot.Bulk, $"{slot}");
  }

  [Fact]
  public void EndShortfall_FromTheExactSoc_NoStepAtWholePercent()
  {
    // 19.9 % at the end is 0.1 % short, not 1 % (whole % rounded it down).
    var input = Input(Day.AddHours(22), 29);
    int wh = (int)(input.BatteryCapacityWh * 0.199);
    Assert.Equal(0.1f * O.FloorPenaltyPerPctQuarter, PlanEvaluation.EndShortfall(input, wh, O), 2);
    Assert.Equal(0f, PlanEvaluation.EndShortfall(input, input.SocToWh(20) + 1, O));
  }

  [Fact]
  public void EndShortfall_NoEuroJumpBetweenNeighbouringRuns()
  {
    // Live 2026-11-02 06:00 (the stored planning situation) replanned a few minutes later at 88–90 %: with the
    // shortfall in whole % some runs paid 1 € for ending at ~19.9 %, their neighbours nothing. Now none is near 1 €.
    var bytes = System.IO.File.ReadAllBytes(System.IO.Path.Combine(AppContext.BaseDirectory, "Data", "forecast_snapshot_2026-11-02_0600.json.gz"));
    var snap = ForecastSnapshot.FromBytes(bytes);
    foreach (var soc in new[] { 88, 89, 90 })
      foreach (var min in new[] { 1, 6, 10, 14 })
      {
        var plan = BatteryDpPlanner.Plan((snap with { StartTime = new DateTime(2026, 11, 2, 6, min, 0), StartSocPercent = soc }).ToInput(), O);
        Assert.True(plan.Cost.EndShortfall < 0.25f, $"06:{min:00} at {soc} %: shortfall {plan.Cost.EndShortfall} €");   // a fraction of a %, never the 1-€ step
      }
  }

  [Fact]
  public void SurplusQuarters_FeedinPriority_NotGridOnly()
  {
    // Live 2026-11-02 07:00 (the stored planning situation, battery 86 %, full by the afternoon anyway): the DP held the
    // battery with grid_only at 08:45 and 14:00 (0.51/0.50) to export 36 Wh each at the morning's export price — in a
    // quarter where PV covers the house the flows are the same, but grid_only buys every dip from the grid. Now
    // feedin_priority (the battery covers dips, never charges), and no grid_only where PV covers the house.
    var bytes = System.IO.File.ReadAllBytes(System.IO.Path.Combine(AppContext.BaseDirectory, "Data", "forecast_snapshot_2026-11-02_0700.json.gz"));
    var input = ForecastSnapshot.FromBytes(bytes).ToInput();
    var plan = BatteryDpPlanner.Plan(input, O);
    var timeline = DpSteering.ToTimeline(plan, EnergySimulator.Simulate(input));
    // (with the conversion losses storing the surplus beats exporting it here: normal, not feedin_priority)
    Assert.DoesNotContain(timeline, s => s.State.Mode == InverterModes.grid_only && s.PVWh >= s.LoadWh + s.ExtraLoadWh);
    // nor where PV nearly covers it (08:30/14:15: 2 Wh short): a hold needs a deficit of at least 1 % SoC
    var lossy = input.WithBatteryLosses();
    Assert.DoesNotContain(timeline, s => s.State is { Mode: InverterModes.grid_only, ModeReason: not ForceChargeReasons.LoadFromGrid }
      && !BatteryDpPlanner.HoldAllowed(lossy, s.PVWh, s.LoadWh + s.ExtraLoadWh));
    // feedin_priority only where PV covers the house (elsewhere it would be normal) and it never charges
    Assert.All(timeline.Where(s => s.State.Mode == InverterModes.feedin_priority),
      s => Assert.Equal(0, s.BatteryChargeWh));
  }

  [Fact]
  public void LossHelpers_OnlyWithBatteryLosses()
  {
    // InverterEfficiency per direction (measured 2026-11-02: ~88 % round trip; 0.95 configured): 1000 Wh AC in stores
    // 950, 950 Wh AC out draws 1000; the inverses give the AC flows that fit a room or an available energy exactly.
    var lossless = Input(Day.AddHours(22), 50);
    var lossy = lossless.WithBatteryLosses();
    Assert.Equal((1000, 1000), (lossless.StoredWh(1000), lossless.DrawnWh(1000)));
    Assert.Equal((950, 1000), (lossy.StoredWh(1000), lossy.DrawnWh(950)));
    Assert.Equal((1000, 950), (lossy.ChargeAcForWh(950), lossy.DischargeAcForWh(1000)));
    Assert.True(lossy.StoredWh(lossy.ChargeAcForWh(777)) <= 777 && lossy.DrawnWh(lossy.DischargeAcForWh(777)) <= 777);
    Assert.Equal(5000 + 950 - 1000, lossy.NextEnergyWh(5000, 1000, 950));
    Assert.Same(lossy, lossy.WithBatteryLosses());
  }

  [Fact]
  public void FlatPrice_NoGridCharge_WhileTheBatteryHolds()
  {
    // A fixed-price contract (2026-11: 14.125 ct netto): every quarter costs the same, so a grid charge only loses the
    // round trip. Live 2026-11-02 07:00 at 86 %, the import flat: the lossless model charged in 8 quarters (ties).
    var bytes = System.IO.File.ReadAllBytes(System.IO.Path.Combine(AppContext.BaseDirectory, "Data", "forecast_snapshot_2026-11-02_0700.json.gz"));
    var snap = ForecastSnapshot.FromBytes(bytes);
    var flat = snap with { ImportPrices = snap.ImportPrices.Select(p => p with { Price = 0.30f }).ToList() };
    var plan = BatteryDpPlanner.Plan(flat.ToInput(), O);
    Assert.DoesNotContain(plan.Slots, s => s.Mode == InverterModes.force_charge);
  }

  [Fact]
  public void SmallSpread_NoCharge_TheRoundTripCostsMore()
  {
    // Night 0.40, day 0.42 (5 % dearer): charging at night for the day loses ~10 % on the way — no grid charge while the
    // day can be bought directly (the battery runs down to the minimum and holds); with 0.50 by day (25 %) it pays.
    SimulationInput With(float day)
    {
      var b = Input(Day.AddHours(22), 60, pvWh: 0);
      var prices = b.ImportPrices.Select(p => new PriceTableEntry(p.StartTime, p.EndTime, p.StartTime.Hour is >= 22 or < 6 ? 0.40f : day)).ToList();
      return b.WithPrices(new PriceList(prices), b.ExportPrices);
    }
    Assert.DoesNotContain(BatteryDpPlanner.Plan(With(0.42f), O).Slots, s => s.Mode == InverterModes.force_charge);
    Assert.Contains(BatteryDpPlanner.Plan(With(0.50f), O).Slots, s => s.Mode == InverterModes.force_charge);
  }

  [Fact]
  public void HoldAllowed_OnlyForADeficitOfAtLeastOnePercent()
  {
    // With any PV: 1 % of 11.52 kWh = 115 Wh from the battery = 110 Wh AC at 0.95 — below that a hold only buys PV's dips.
    // Without PV every hold is allowed.
    var input = Input(Day.AddHours(22), 50).WithBatteryLosses();
    Assert.False(BatteryDpPlanner.HoldAllowed(input, pvWh: 129, totalLoadWh: 131));   // PV 2 Wh short (live 2026-11-02 08:30)
    Assert.False(BatteryDpPlanner.HoldAllowed(input, pvWh: 300, totalLoadWh: 130));   // PV covers the house
    Assert.False(BatteryDpPlanner.HoldAllowed(input, pvWh: 20, totalLoadWh: 120));    // 106 Wh drawn: under 1 %
    Assert.True(BatteryDpPlanner.HoldAllowed(input, pvWh: 20, totalLoadWh: 140));     // 127 Wh drawn
    Assert.False(BatteryDpPlanner.HoldAllowed(input, pvWh: 86, totalLoadWh: 173));    // dull day, no PV period (live 2026-11-03 13:00)
    Assert.True(BatteryDpPlanner.HoldAllowed(input, pvWh: 0, totalLoadWh: 50));       // no PV: always
  }

  [Fact]
  public void Bulk_WhenTheCheapHourIsFullOnVpp()
  {
    // Live 2026-11-02 12:15 (the stored planning situation): the 23:00 hour (0.394) is full on VPP (1196 → 918 Wh per
    // quarter). Running the house on the battery until 22:00 (0.444) and refilling at 23:00 saves ~2 ct but needs bulk —
    // a 1-ct cost per bulk quarter (the earlier tie-breaker) held the battery from 21:00 instead. Bulk where it saves.
    var bytes = System.IO.File.ReadAllBytes(System.IO.Path.Combine(AppContext.BaseDirectory, "Data", "forecast_snapshot_2026-11-02_1215.json.gz"));
    var input = ForecastSnapshot.FromBytes(bytes).ToInput();
    var plan = BatteryDpPlanner.Plan(input, O);
    var at21 = plan.Slots.Single(s => s.Time == new DateTime(2026, 11, 2, 21, 0, 0));
    Assert.Equal(InverterModes.normal, at21.Mode);
    // what steers: the whole 23:00 block in bulk (VPP can't deliver it), no switching within it
    var timeline = DpSteering.ToTimeline(plan, EnergySimulator.Simulate(input));
    var night = timeline.Where(s => s.State.Mode == InverterModes.force_charge && s.Time.Hour == 23).ToList();
    Assert.NotEmpty(night);
    Assert.All(night, s => Assert.True(s.State.Bulk));
  }

  [Fact]
  public void LoadMargin_80thPercentileOfTheNightError()
  {
    // The 11 scored nights of 2026-10-22…11-01 (relative error of the weather model): 80th percentile +15 %.
    var errors = new[] { 0.01, 0.06, 0.09, 0.09, 0.28, -0.38, -0.02, 0.15, -0.02, 0.21, -0.21 };
    NetDeamon.apps.PVControl.LoadModel.NightResult N(int i, double e) => new(Day.AddDays(-i), 4000, 4000, 4000 * (1 + e));
    Assert.Equal(0.15, NetDeamon.apps.PVControl.LoadModel.NightForecastTracker.LoadMargin(errors.Select((e, i) => N(i, e)))!.Value, 3);
    // too few nights: none (the YAML default applies); never below 0 or above 30 %
    Assert.Null(NetDeamon.apps.PVControl.LoadModel.NightForecastTracker.LoadMargin(errors.Take(6).Select((e, i) => N(i, e))));
    Assert.Equal(0, NetDeamon.apps.PVControl.LoadModel.NightForecastTracker.LoadMargin(Enumerable.Range(0, 8).Select(i => N(i, -0.1)))!.Value, 6);
    Assert.Equal(0.3, NetDeamon.apps.PVControl.LoadModel.NightForecastTracker.LoadMargin(Enumerable.Range(0, 8).Select(i => N(i, 0.5)))!.Value, 6);
    // unscored nights don't count
    Assert.Null(NetDeamon.apps.PVControl.LoadModel.NightForecastTracker.LoadMargin(
      Enumerable.Range(0, 10).Select(i => new NetDeamon.apps.PVControl.LoadModel.NightResult(Day.AddDays(-i), 4000, 4000, null))));
  }

  [Fact]
  public void WithLoadMargin_RaisesTheHouseLoadOnly()
  {
    var input = Input(Day.AddHours(22), 50, loadWh: 100);
    var raised = input.WithLoadMargin(0.15);
    Assert.All(raised.LoadPredictionWh, kv => Assert.Equal(115, kv.Value));
    Assert.Same(input.ExtraLoads, raised.ExtraLoads);
    Assert.Same(input, input.WithLoadMargin(0));
  }

  [Fact]
  public void LoadMargin_MoreReserveIntoTheExpensiveDay()
  {
    // Live 2026-11-02 12:15 (the stored planning situation; no cheap midday tomorrow): planning for 15 % more load the
    // night charge buys more and the battery goes into the expensive day fuller — the reserve for a load above the
    // forecast (one night in five) instead of buying at 0.44–0.53.
    var bytes = System.IO.File.ReadAllBytes(System.IO.Path.Combine(AppContext.BaseDirectory, "Data", "forecast_snapshot_2026-11-02_1215.json.gz"));
    var input = ForecastSnapshot.FromBytes(bytes).ToInput();
    var plain = BatteryDpPlanner.Plan(input, O);
    var reserve = BatteryDpPlanner.Plan(input.WithLoadMargin(0.15), O);
    var midnight = new DateTime(2026, 11, 3);
    Assert.True(reserve.Slots.Single(s => s.Time == midnight).SocPct >= plain.Slots.Single(s => s.Time == midnight).SocPct + 5,
      $"{plain.Slots.Single(s => s.Time == midnight).SocPct} → {reserve.Slots.Single(s => s.Time == midnight).SocPct} %");
    Assert.True(reserve.Slots.Where(s => s.Mode == InverterModes.force_charge).Sum(s => s.BatteryChargeWh)
      > plain.Slots.Where(s => s.Mode == InverterModes.force_charge).Sum(s => s.BatteryChargeWh));
  }

  [Fact]
  public void DullDay_NoHoldWhilePvProduces_TheRunningModeStaysOnATie()
  {
    // Live 2026-11-03 12:30: flat 0.50 prices until 21:00, the battery (60 %) too small for the evening — the DP buys
    // ~3 kWh in the 0.50 hours either way and held in the "cheapest" of them, worth tenths of a cent: 9 switches in
    // 1.5 h, holds in daylight where PV (no PV period: forecast below the load) covered the house in reality.
    var bytes = System.IO.File.ReadAllBytes(System.IO.Path.Combine(AppContext.BaseDirectory, "Data", "forecast_snapshot_2026-11-03_1230.json.gz"));
    var snap = ForecastSnapshot.FromBytes(bytes);
    var input = snap.ToInput();
    var plan = BatteryDpPlanner.Plan(input, O);
    // no hold in a quarter with PV and a deficit under 1 %
    var lossy = input.WithBatteryLosses();
    var timeline = DpSteering.ToTimeline(plan, EnergySimulator.Simulate(input));
    Assert.DoesNotContain(timeline, s => s.State is { Mode: InverterModes.grid_only, ModeReason: not ForceChargeReasons.LoadFromGrid }
      && s.PVWh > 0 && !BatteryDpPlanner.HoldAllowed(lossy, s.PVWh, s.LoadWh + s.ExtraLoadWh));
    // nor a "charge" under 1 % in its place (a hold under another name; 2026-10-31's next day had 1–34 Wh ones)
    foreach (var f in new[] { "forecast_snapshot_2026-11-03_1230.json.gz", "forecast_snapshot_2026-10-31_1945.json.gz" })
    {
      var inp = ForecastSnapshot.FromBytes(System.IO.File.ReadAllBytes(System.IO.Path.Combine(AppContext.BaseDirectory, "Data", f))).ToInput();
      var l = inp.WithBatteryLosses();
      var tl = DpSteering.ToTimeline(BatteryDpPlanner.Plan(inp, O), EnergySimulator.Simulate(inp));
      Assert.DoesNotContain(tl, s => s.State.Mode == InverterModes.force_charge
        && l.StoredWh(s.BatteryChargeWh) < l.SocToWh(1) && !BatteryDpPlanner.HoldAllowed(l, s.PVWh, s.LoadWh + s.ExtraLoadWh));
    }
    // the running mode stays in the current quarter where switching is worth under 0.1 ct
    foreach (var running in new[] { InverterModes.normal, InverterModes.grid_only })
    {
      var night = (snap with { StartTime = new DateTime(2026, 11, 3, 16, 5, 0), CurrentMode = running }).ToInput();
      var first = BatteryDpPlanner.Plan(night, O).Slots[0];
      var free = BatteryDpPlanner.Plan(night, O with { KeepModeEur = 0 }).Slots[0];
      if (first.Mode != free.Mode) Assert.Equal(running, first.Mode);
    }
  }
}

