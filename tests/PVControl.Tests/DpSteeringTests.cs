using NetDeamon.apps;
using NetDeamon.apps.PVControl;
using NetDeamon.apps.PVControl.Optimizer;
using NetDeamon.apps.PVControl.Simulator;
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace NetDeamonApps.Tests;

/// <summary>
/// select.pv_control_planner (<see cref="DpSteering"/>): the DP plan as the steering plan — its slots feed the same command
/// chain as the greedy simulation's (current quarter, next grid charge) — and the fallback to the greedy plan when the
/// DP plan is missing or stale. The cycle itself (HouseEnergy.ProposedState, publishing) needs HA.
/// </summary>
public class DpSteeringTests : TestBase
{
  static readonly DateTime Day = new(2026, 10, 30);

  static SimulationInput Input()
  {
    var prices = new List<PriceTableEntry>();
    var load = new Dictionary<DateTime, int>();
    var pv = new Dictionary<DateTime, int>();
    for (var t = Day.AddDays(-1); t < Day.AddDays(3); t = t.AddMinutes(15))
    {
      prices.Add(new PriceTableEntry(t, t.AddMinutes(15), t.Hour is 23 or < 5 ? 0.39f : 0.46f));
      load[t] = 100;
      pv[t] = t.Hour >= 10 && t.Hour < 15 ? 240 : 0;
    }
    return new SimulationInput
    {
      StartTime = Day.AddHours(22), StartSocPercent = 29, BatteryCapacityWh = 11_520,
      AbsoluteMinSocPercent = 12, PreferredMinSocPercent = 20, EnforcePreferredSoc = false,
      MaxChargePowerAmps = 30, InverterEfficiency = 0.95f, ImportPrices = prices,
      ExportPrices = prices.Select(p => new PriceTableEntry(p.StartTime, p.EndTime, p.Price / 2)).ToList(),
      LoadPredictionWh = load, PVPredictionWh = pv, ExtraLoads = [],
      EnableCheapForceCharge = false, OpportunisticDischarge = false, ForceChargeMaxPrice = 0.35f,
      ForceChargeTargetSocPercent = 95, CurrentMode = new InverterState(InverterModes.normal),
    };
  }

  [Fact]
  public void ToTimeline_TheDpsModesPathsAndFlows_WithTheQuartersPvAndLoad()
  {
    var input = Input();
    var greedy = EnergySimulator.Simulate(input);
    var dp = BatteryDpPlanner.Plan(input);
    var timeline = DpSteering.ToTimeline(dp, greedy);
    Assert.Equal(dp.Slots.Count, timeline.Count);
    for (int i = 0; i < timeline.Count; i++)
    {
      var (t, d, g) = (timeline[i], dp.Slots[i], greedy.Slots[i]);
      Assert.Equal(d.Time, t.Time);
      Assert.Equal(d.SocPct, t.SoC);
      Assert.Equal(d.Mode, t.State.Mode);
      Assert.Equal(d.Bulk, t.State.Bulk);
      Assert.Equal(DpSteering.ReasonOf(dp, dp.Slots.ToList().IndexOf(d), input, greedy.Slots.ToDictionary(x => x.Time)), t.State.ModeReason);
      Assert.Equal(d.Mode == InverterModes.normal, t.State.ModeReason == ForceChargeReasons.None);
      Assert.Equal((d.GridImportWh, d.GridExportWh, d.BatteryChargeWh, d.BatteryDischargeWh), (t.GridImportWh, t.GridExportWh, t.BatteryChargeWh, t.BatteryDischargeWh));
      Assert.Equal((g.PVWh, g.LoadWh, g.State.ExportAllowed), (t.PVWh, t.LoadWh, t.State.ExportAllowed));
    }
  }

  [Fact]
  public void NextCharge_FromTheDpTimeline_IsTheDpsCharge()
  {
    // The command chain's next grid charge (InverterCommand.NextCharge) on the DP timeline: the DP's first charge block.
    var input = Input();
    var greedy = EnergySimulator.Simulate(input);
    var dp = BatteryDpPlanner.Plan(input);
    var block = DpShadow.Charges(input, dp.Slots.Select(s => (s.Time, s.Mode == InverterModes.force_charge, s.GridImportWh)))[0];
    var next = InverterCommand.NextCharge(DpSteering.ToTimeline(dp, greedy), input.StartTime, input.BatteryCapacityWh);
    Assert.NotNull(next);
    Assert.Equal(block.Start, next!.Value.Start);
    Assert.Equal(block.End, next.Value.End);
    Assert.Equal(DpSteering.ToTimeline(dp, greedy).First(s => s.Time == block.Start).State.ModeReason, next.Value.Reason);
  }

  [Fact]
  public void Choose_GreedySelected_GreedySteers()
  {
    var input = Input();
    var (timeline, effective, stale) = DpSteering.Choose(Planner.greedy, BatteryDpPlanner.Plan(input), Day.AddHours(22), Day.AddHours(22), EnergySimulator.Simulate(input));
    Assert.Null(timeline);
    Assert.Equal(Planner.greedy, effective);
    Assert.False(stale);
  }

  [Fact]
  public void Choose_DpSelectedAndFresh_DpSteers()
  {
    var input = Input();
    var (timeline, effective, stale) = DpSteering.Choose(Planner.dp, BatteryDpPlanner.Plan(input), Day.AddHours(22), Day.AddHours(22).AddMinutes(9), EnergySimulator.Simulate(input));
    Assert.NotNull(timeline);
    Assert.Equal(Planner.dp, effective);
    Assert.False(stale);
  }

  [Theory]
  [InlineData(11)]   // older than DpSteering.MaxPlanAge
  [InlineData(-1)]   // no plan yet
  public void Choose_DpSelectedButStaleOrMissing_GreedySteers(int ageMinutes)
  {
    var input = Input();
    var plan = ageMinutes < 0 ? null : BatteryDpPlanner.Plan(input);
    var (timeline, effective, stale) = DpSteering.Choose(Planner.dp, plan, plan is null ? null : Day.AddHours(22),
      Day.AddHours(22).AddMinutes(Math.Max(0, ageMinutes)), EnergySimulator.Simulate(input));
    Assert.Null(timeline);
    Assert.Equal(Planner.greedy, effective);
    Assert.True(stale);
  }

  [Fact]
  public void DpPlannerHealth_OnlyWhenSelectedAndStale()
  {
    Assert.Empty(HealthChecks.DpPlanner(dpSelected: false, stale: false));
    Assert.Empty(HealthChecks.DpPlanner(dpSelected: true, stale: false));
    Assert.Equal(HealthLevel.degraded, Assert.Single(HealthChecks.DpPlanner(dpSelected: true, stale: true)).Level);
  }

  [Fact]
  public async Task Shadow_RunsEveryMinuteWhileSteering()
  {
    var db = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dpsteer-" + Guid.NewGuid().ToString("N") + ".db");
    try
    {
      var shadow = new DpShadow(new DpShadowConfig { IntervalMinutes = 5 }, db);
      await shadow.RunAsync(EnergySimulator.Simulate(Input()), Day.AddHours(22));
      Assert.True(shadow.Due(Day.AddHours(22).AddMinutes(2), steering: true));    // every minute while it steers
      Assert.False(shadow.Due(Day.AddHours(22).AddMinutes(2), steering: false));  // every 5 as a shadow
    }
    finally
    {
      Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
      if (System.IO.File.Exists(db)) System.IO.File.Delete(db);
    }
  }

  [Fact]
  public void Reasons_TonightsPlan()
  {
    // Live 2026-10-31 19:45 (the stored planning situation, the DP steering from ~20:15): each charge and hold gets
    // the reason it has in the plan — not "DpPlan".
    var bytes = System.IO.File.ReadAllBytes(System.IO.Path.Combine(AppContext.BaseDirectory, "Data", "forecast_snapshot_2026-10-31_1945.json.gz"));
    var input = ForecastSnapshot.FromBytes(bytes).ToInput();
    var greedy = EnergySimulator.Simulate(input);
    var timeline = DpSteering.ToTimeline(BatteryDpPlanner.Plan(input), greedy);
    // 21:00 at 13 %: without the charge the evening runs under the absolute minimum before 23:00
    Assert.Equal(ForceChargeReasons.AbsoluteMinimum, timeline.Single(s => s.Time == new DateTime(2026, 10, 31, 21, 0, 0)).State.ModeReason);
    // the night: the battery held while the EV runs from the grid
    Assert.Equal(ForceChargeReasons.LoadFromGrid, timeline.Single(s => s.Time == new DateTime(2026, 11, 1, 1, 0, 0)).State.ModeReason);
    // 04:45: nothing near a minimum afterwards — the night's 0.396 beats the evening's 0.44 even after the round trip
    Assert.Equal(ForceChargeReasons.CheaperThanLater, timeline.Single(s => s.Time == new DateTime(2026, 11, 1, 4, 45, 0)).State.ModeReason);
    Assert.DoesNotContain(timeline, s => s.State.ModeReason == ForceChargeReasons.DpPlan);
    Assert.All(timeline, s => Assert.Equal(s.State.Mode == InverterModes.normal, s.State.ModeReason == ForceChargeReasons.None));
  }

  [Fact]
  public void BlockPaths_OneBlockOnePath_ARunningChargeKeepsItsPath()
  {
    // The DP may alternate bulk and VPP within a charge (2026-11-02 12:15: B, VPP, B, VPP); every switch between Manual
    // Mode and VPP writes EEPROM. A block runs bulk throughout where VPP (1000 Wh per quarter here) can't deliver its
    // planned charge by more than 115 Wh (1 %), else VPP — also where the DP tipped a quarter to bulk for a few Wh (2026-10-31 04:45: 1213 Wh vs
    // VPP 1196); a charge running now keeps its path for its block.
    var t0 = Day.AddHours(23);
    DpSlot S(int q, InverterModes m, bool bulk, int ch) => new(t0.AddMinutes(15 * q), 30, m, bulk, 0, 0, 0, ch, 0);
    int Vpp(DpSlot s) => 1000;
    float Price(DateTime t) => (float)(t - t0).TotalMinutes;   // every quarter its own price: no room next to a block
    var slots = new List<DpSlot>
    {
      S(0, InverterModes.force_charge, true, 1800), S(1, InverterModes.force_charge, false, 900), S(2, InverterModes.force_charge, true, 1800),
      S(3, InverterModes.grid_only, false, 0),
      S(4, InverterModes.force_charge, false, 900), S(5, InverterModes.force_charge, false, 900),
      S(6, InverterModes.normal, false, 0),
      S(7, InverterModes.force_charge, true, 1017),   // bulk for 17 Wh: VPP is enough
    };
    Assert.Equal([true, true, true, false, false, false, false, false],
      DpSteering.BlockPaths(slots, new InverterState(InverterModes.normal), Vpp, 115, Price));
    // the first block is the running charge, on VPP: it stays VPP
    Assert.Equal([false, false, false, false, false, false, false, false],
      DpSteering.BlockPaths(slots, new InverterState(InverterModes.force_charge) { Bulk = false }, Vpp, 115, Price));
    // a running bulk charge stays bulk although the DP planned VPP for its block
    Assert.Equal([true, true],
      DpSteering.BlockPaths([S(0, InverterModes.force_charge, false, 500), S(1, InverterModes.force_charge, false, 500)],
        new InverterState(InverterModes.force_charge) { Bulk = true }, Vpp, 115, Price));
  }

  [Fact]
  public void BlockPaths_VppMayUseTheRestOfThePriceSlot()
  {
    // 2026-10-31 night: 03:00 bulk 1455 Wh + 03:15 VPP 1179 Wh against VPP 1296 + 1179 — short by 159 Wh, but 03:30 and
    // 03:45 cost the same 0.396 and are free: VPP is enough within the price slot (the next replan charges there).
    var t0 = Day.AddHours(3);
    DpSlot S(int q, InverterModes m, bool bulk, int ch) => new(t0.AddMinutes(15 * q), 20, m, bulk, 0, 0, 0, ch, 0);
    var slots = new List<DpSlot> { S(0, InverterModes.force_charge, true, 1455), S(1, InverterModes.force_charge, false, 1179),
      S(2, InverterModes.grid_only, false, 0), S(3, InverterModes.grid_only, false, 0), S(4, InverterModes.normal, false, 0) };
    int Vpp(DpSlot s) => 1240;
    Assert.Equal([false, false, false, false, false],
      DpSteering.BlockPaths(slots, new InverterState(InverterModes.normal), Vpp, 115, t => t.Hour == 3 ? 0.396f : 0.45f));
    // the hour after is dearer: no room there, VPP falls short — bulk
    Assert.Equal([true, true, false, false, false],
      DpSteering.BlockPaths(slots, new InverterState(InverterModes.normal), Vpp, 115, t => t < t0.AddMinutes(30) ? 0.396f : 0.45f));
  }
}

