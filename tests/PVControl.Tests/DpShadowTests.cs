using Microsoft.Data.Sqlite;
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
/// The DP shadow (<see cref="DpShadow"/>): the comparison of the two plans, their charge blocks and the per-quarter log.
/// Publishing the sensor (PVControl) needs HA and is checked live (sensor.pv_control_dp_shadow).
/// </summary>
public class DpShadowTests : TestBase, IDisposable
{
  static readonly DateTime Day = new(2026, 10, 30);
  readonly string _db = Path.Combine(Path.GetTempPath(), "dpshadow-" + Guid.NewGuid().ToString("N") + ".db");

  public void Dispose()
  {
    SqliteConnection.ClearAllPools();
    if (File.Exists(_db)) File.Delete(_db);
  }

  static SimulationInput Input(DateTime start, int soc)
  {
    var prices = new List<PriceTableEntry>();
    var load = new Dictionary<DateTime, int>();
    var pv = new Dictionary<DateTime, int>();
    for (var t = Day.AddDays(-1); t < Day.AddDays(3); t = t.AddMinutes(15))
    {
      prices.Add(new PriceTableEntry(t, t.AddMinutes(15), t.Hour is 23 or < 5 ? 0.39f : t.Hour is >= 16 and < 21 ? 0.51f : 0.44f));
      load[t] = 100;
      pv[t] = t.Hour >= 10 && t.Hour < 15 ? 240 : 0;
    }
    return new SimulationInput
    {
      StartTime = start, StartSocPercent = soc, BatteryCapacityWh = 11_520,
      AbsoluteMinSocPercent = 12, PreferredMinSocPercent = 20, EnforcePreferredSoc = false,
      MaxChargePowerAmps = 30, InverterEfficiency = 0.95f, ImportPrices = prices, ExportPrices = prices,
      LoadPredictionWh = load, PVPredictionWh = pv, ExtraLoads = [],
      EnableCheapForceCharge = false, OpportunisticDischarge = false, ForceChargeMaxPrice = 0.35f,
      ForceChargeTargetSocPercent = 95, CurrentMode = new InverterState(InverterModes.normal),
    };
  }

  [Fact]
  public void ModeAt_TheRunningQuarterOfThePlan_NotItsFirst()
  {
    static DpSlot Slot(DateTime t, InverterModes m) => new(t, 50, m, false, 0, 0, 0, 0, 0);
    var t0 = Day.AddHours(16).AddMinutes(45);
    var plan = new DpPlan([Slot(t0, InverterModes.normal), Slot(t0.AddMinutes(15), InverterModes.grid_only)], new PlanCost(0, 0, 0, 0), 50);
    Assert.Equal(InverterModes.normal, DpShadow.ModeAt(plan, t0.AddMinutes(14).AddSeconds(59)));
    // 17:00:00 — the plan computed at 16:59 already says grid_only for the new quarter (live 2026-11-15: the sensor
    // showed normal until the next run at 17:01 while the mode had switched)
    Assert.Equal(InverterModes.grid_only, DpShadow.ModeAt(plan, t0.AddMinutes(15)));
    Assert.Null(DpShadow.ModeAt(plan, t0.AddMinutes(30)));   // beyond the plan: nothing to show
  }

  [Fact]
  public void Charges_ConsecutiveQuartersAreOneBlock_WithEnergyAndMeanPrice()
  {
    var input = Input(Day.AddHours(22), 30);
    var blocks = DpShadow.Charges(input,
    [
      (Day.AddHours(22).AddMinutes(45), false, 0),
      (Day.AddHours(23), true, 2000),
      (Day.AddHours(23).AddMinutes(15), true, 1000),
      (Day.AddHours(23).AddMinutes(30), false, 0),
      (Day.AddDays(1).AddHours(3), true, 500),
    ]);
    Assert.Equal(2, blocks.Count);
    Assert.Equal(new ChargeBlock(Day.AddHours(23), Day.AddHours(23).AddMinutes(30), 3f, 0.39f), blocks[0]);
    Assert.Equal(Day.AddDays(1).AddHours(3).AddMinutes(15), blocks[1].End);
  }

  [Fact]
  public void Charges_MeanPriceIsWeightedByEnergy_WithoutImportThePlainMean()
  {
    var input = Input(Day.AddHours(22), 30);
    var blocks = DpShadow.Charges(input,
    [
      (Day.AddHours(22).AddMinutes(45), true, 3000),   // 0.44
      (Day.AddHours(23), true, 1000),                  // 0.39
      (Day.AddHours(23).AddMinutes(15), false, 0),
      (Day.AddHours(22).AddDays(1), true, 0),          // charging from PV only: 0.44
      (Day.AddHours(23).AddDays(1), true, 0),          // 0.39
    ]);
    Assert.Equal((0.44f * 3 + 0.39f) / 4, blocks[0].AvgPrice, 4);   // not the plain 0.415
    Assert.Equal((0.44f + 0.39f) / 2, blocks[1].AvgPrice, 4);
  }

  [Fact]
  public void Report_ComparesBothPlansUnderTheOneModel()
  {
    var input = Input(Day.AddHours(22), 29);
    var greedy = EnergySimulator.Simulate(input);
    var dp = BatteryDpPlanner.Plan(input);
    var r = DpShadow.Report(greedy, dp, Day.AddHours(22), 42, new DpOptions());
    Assert.Equal(PlanEvaluation.Evaluate(greedy, new DpOptions()).Total, r.Greedy.Total, 4);
    Assert.Equal(dp.Cost.Total, r.Dp.Total, 4);
    Assert.Equal(r.Greedy.Total - r.Dp.Total, r.Saving, 4);
    Assert.Equal(greedy.Slots[0].State.Mode == dp.Slots[0].Mode, r.AgreeNow);
    Assert.Equal(29, r.SocNow);
  }

  [Fact]
  public async Task RunAsync_LogsOneRowPerQuarter_AndRespectsTheInterval()
  {
    var shadow = new DpShadow(new DpShadowConfig { IntervalMinutes = 5 }, _db);
    var sim = EnergySimulator.Simulate(Input(Day.AddHours(22), 29));
    Assert.True(shadow.Due(Day.AddHours(22)));
    Assert.NotNull(await shadow.RunAsync(sim, Day.AddHours(22).AddMinutes(1)));
    Assert.False(shadow.Due(Day.AddHours(22).AddMinutes(4)));
    Assert.True(shadow.Due(Day.AddHours(22).AddMinutes(6)));
    await shadow.RunAsync(sim, Day.AddHours(22).AddMinutes(7));    // same quarter: no second row
    await shadow.RunAsync(sim, Day.AddHours(22).AddMinutes(16));   // next quarter
    await using var con = new SqliteConnection($"Data Source={_db};Pooling=False");
    await con.OpenAsync();
    await using var cmd = con.CreateCommand();
    cmd.CommandText = "SELECT COUNT(*) FROM shadow";
    Assert.Equal(2L, (long)(await cmd.ExecuteScalarAsync())!);
  }

  [Fact]
  public async Task Steering_ANewQuarter_DueAtOnceOnce()
  {
    // The steering keeps the running mode until the DP has planned the new quarter (DpSteering.KeepRunningUntilReplanned):
    // due in the new quarter's first cycle, not a minute later; once per quarter.
    var shadow = new DpShadow(new DpShadowConfig { IntervalMinutes = 5 }, _db);
    var before = Day.AddHours(22).AddMinutes(14).AddSeconds(50);
    await shadow.RunAsync(EnergySimulator.Simulate(Input(before, 29)), before);
    Assert.False(shadow.Due(before.AddSeconds(5), steering: true));                  // same quarter, interval not passed
    Assert.True(shadow.Due(before.AddSeconds(15), steering: true));                  // 22:15:05: the plan is from 22:00
    Assert.False(shadow.Due(before.AddSeconds(15), steering: false));                // only while it steers
    var after = before.AddSeconds(15);
    await shadow.RunAsync(EnergySimulator.Simulate(Input(after, 29)), after);
    Assert.False(shadow.Due(after.AddSeconds(15), steering: true));                  // planned this quarter
    // a run in the new quarter whose plan is still from the quarter before (its simulation started before the
    // boundary): not retried every cycle — once per quarter
    var stale = new DpShadow(new DpShadowConfig { IntervalMinutes = 5 }, _db);
    await stale.RunAsync(EnergySimulator.Simulate(Input(before, 29)), after);
    Assert.True(DpShadow.PlanFromAnEarlierQuarter(stale.LastPlan, after.AddSeconds(15)));
    Assert.False(stale.Due(after.AddSeconds(15), steering: true));
  }

  [Fact]
  public async Task RequestRun_DueAtOnce_ClearedByTheRun()
  {
    // A user change asks for a run: due before the interval has passed, cleared when the run starts.
    var shadow = new DpShadow(new DpShadowConfig { IntervalMinutes = 5 }, _db);
    var sim = EnergySimulator.Simulate(Input(Day.AddHours(22), 29));
    await shadow.RunAsync(sim, Day.AddHours(22));
    Assert.False(shadow.Due(Day.AddHours(22).AddSeconds(30), steering: true));
    shadow.RequestRun();
    Assert.True(shadow.RunRequested);
    Assert.True(shadow.Due(Day.AddHours(22).AddSeconds(30), steering: true));
    await shadow.RunAsync(sim, Day.AddHours(22).AddSeconds(30));
    Assert.False(shadow.RunRequested);
    Assert.False(shadow.Due(Day.AddHours(22).AddSeconds(45), steering: true));
  }

  [Fact]
  public void Disabled_NeverDue() =>
    Assert.False(new DpShadow(new DpShadowConfig { Enabled = false }, _db).Due(Day));

  [Fact]
  public void PredictedSoc_TheGreedySensorsFormat_PastAsIs_PlanFromNow()
  {
    // today 00:00 → tomorrow 23:45 like sensor.pv_control_info_predicted_soc; before now the same actual SoC.
    var now = Day.AddHours(22);
    var input = Input(now, 29);
    var greedy = EnergySimulator.Simulate(input);
    var dp = BatteryDpPlanner.Plan(input);
    var todayAndTomorrow = new Dictionary<DateTime, int>();
    for (var t = Day; t < Day.AddDays(2); t = t.AddMinutes(15))
      todayAndTomorrow[t] = t < now ? 50 : greedy.Slots.FirstOrDefault(s => s.Time == t)?.SoC ?? 0;
    var entries = DpShadow.PredictedSoc(dp, todayAndTomorrow, greedy);
    Assert.Equal(192, entries.Count);
    Assert.All(entries.Where(e => e.Time < now), e => { Assert.Equal("past", e.Mode); Assert.Equal(50, e.Soc); });
    var fromNow = entries.Where(e => e.Time >= now).ToList();
    Assert.Equal(dp.Slots.Select(s => s.SocPct), fromNow.Select(e => e.Soc));
    Assert.Equal(dp.Slots.Select(s => s.Mode.ToString()), fromNow.Select(e => e.Mode));
    // the battery value per quarter from now on (the line explaining the holds), none for the past
    Assert.Equal(dp.Slots.Select(s => (float?)s.BatteryValue), fromNow.Select(e => e.BatteryValue));
    Assert.All(entries.Where(e => e.Time < now), e => Assert.Null(e.BatteryValue));
  }

  /// <summary>An input with everything a snapshot has to carry: hourly prices, a grid-fed EV session and a battery-fed
  /// extra load, the learned VPP curve, the import cap, a running charge.</summary>
  static SimulationInput Rich()
  {
    var b = Input(Day.AddHours(21).AddMinutes(7), 31);
    var hourly = new List<PriceTableEntry>();
    for (var t = Day.AddDays(-1); t < Day.AddDays(2); t = t.AddHours(1))
      hourly.Add(new PriceTableEntry(t, t.AddHours(1), 0.30f + 0.01f * (t.Hour % 7)));
    return new SimulationInput
    {
      StartTime = b.StartTime, StartSocPercent = b.StartSocPercent, BatteryCapacityWh = b.BatteryCapacityWh,
      AbsoluteMinSocPercent = 12, PreferredMinSocPercent = 25, EnforcePreferredSoc = false,
      MaxChargePowerAmps = 30, InverterEfficiency = 0.95f, ChargeVoltageV = 240, MaxGridImportW = 9800,
      ImportPrices = hourly, ExportPrices = b.ExportPrices, PublishedPricesUntil = Day.AddDays(1),
      LoadPredictionWh = b.LoadPredictionWh, PVPredictionWh = b.PVPredictionWh,
      ExtraLoads =
      [
        new ExtraLoad { Name = "EV Charger", StartTime = Day.AddHours(23), EndTime = Day.AddDays(1).AddHours(2), PowerW = 1850, FromGrid = true },
        new ExtraLoad { Name = "Warm water", StartTime = Day.AddDays(1).AddHours(13), EndTime = Day.AddDays(1).AddHours(14), PowerW = 2000 },
      ],
      EnableCheapForceCharge = false, OpportunisticDischarge = false, ForceChargeMaxPrice = 0.35f, ForceChargeTargetSocPercent = 95,
      CurrentMode = new InverterState(InverterModes.force_charge, ForceChargeReasons.GoingUnderPreferredMinima) { Bulk = false },
      VppChargePowerW = soc => 5780 - 34 * soc,
    };
  }

  [Fact]
  public void Snapshot_RebuildsTheSameSimulationAndDpPlan()
  {
    var input = Rich();
    var bytes = ForecastSnapshot.From(input).ToBytes();
    var again = ForecastSnapshot.FromBytes(bytes).ToInput();
    // every slot equal: time, SoC, mode (incl. reason, bulk), all flows
    Assert.Equal(EnergySimulator.Simulate(input).Slots, EnergySimulator.Simulate(again).Slots);
    Assert.Equal(BatteryDpPlanner.Plan(input).Slots, BatteryDpPlanner.Plan(again).Slots);
    Assert.InRange(bytes.Length, 100, 8_000);   // a row per quarter: about 0.5 MB per week at most
  }

  [Fact]
  public async Task RunAsync_StoresTheSnapshotPerQuarter_ReadBackForAReplay()
  {
    var shadow = new DpShadow(new DpShadowConfig(), _db);
    var sim = EnergySimulator.Simulate(Rich());
    await shadow.RunAsync(sim, Day.AddHours(21).AddMinutes(7));
    var stored = await ForecastSnapshotStore.ReadAllAsync(_db);
    var (quarter, snapshot) = Assert.Single(stored);
    Assert.Equal(Day.AddHours(21), quarter);
    Assert.Equal(31, snapshot.StartSocPercent);
    Assert.Equal(2, snapshot.ExtraLoads.Count);
  }

  [Fact]
  public async Task RunAsync_StoresTheTariffsAndTheOptionsWithTheSnapshot()
  {
    var config = new DpShadowConfig { ChargeStartEur = 0.02f };
    var shadow = new DpShadow(config, _db);
    var sim = EnergySimulator.Simulate(Rich());
    var tariffs = TariffPrices.Of("Market", Day.AddDays(1), [("Market", sim.Input.ImportPrices), ("Fixed", sim.Input.ImportPrices)]);
    await shadow.RunAsync(sim, Day.AddHours(21).AddMinutes(7), tariffs: tariffs);
    var (_, snapshot) = (await ForecastSnapshotStore.ReadLatestAsync(_db))!.Value;
    Assert.Equal(new[] { "Market", "Fixed" }, snapshot.ImportTariffs!.Import.Keys);
    Assert.Equal(Day.AddDays(1), snapshot.ImportTariffs.NextSwitch);
    Assert.Equal(config.Options, snapshot.Options);
  }
}
