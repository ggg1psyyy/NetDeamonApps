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
    // 04:15–05:00 to ~47 %: nothing near a minimum afterwards — the night's 0.396 is cheaper than the afternoon's grid
    Assert.Equal(ForceChargeReasons.CheaperThanLater, timeline.Single(s => s.Time == new DateTime(2026, 11, 1, 4, 30, 0)).State.ModeReason);
    Assert.DoesNotContain(timeline, s => s.State.ModeReason == ForceChargeReasons.DpPlan);
    Assert.All(timeline, s => Assert.Equal(s.State.Mode == InverterModes.normal, s.State.ModeReason == ForceChargeReasons.None));
  }
}

