using NetDeamon.apps;
using NetDeamon.apps.PVControl;
using NetDeamon.apps.PVControl.ImportCap;
using Xunit;

namespace NetDeamonApps.Tests;

/// <summary>
/// Import cap (SNE-G-V from 2027: the network power price counts the month's highest 15-min import):
/// the import counters in priority order, the running quarter, the month's peak and the live guard.
/// Reading HA and publishing the limit in PVControl aren't unit tested.
/// </summary>
public class ImportCapTests
{
  static readonly DateTime T0 = new(2026, 10, 25, 9, 30, 0);

  // ── PriorityCounter ──────────────────────────────────────────────────────────────────

  [Fact]
  public void Counter_FirstSourceUsedWhileItWorks()
  {
    var c = new PriorityCounter(2);
    c.Update([8847.0, 7884.0]);
    c.Update([8847.5, 7884.4]);
    Assert.Equal(0, c.ActiveIndex);
    Assert.Equal(8847.5, c.ActiveValue);
  }

  [Fact]
  public void Counter_FirstUnavailable_FallbackUntilItIsBack()
  {
    var c = new PriorityCounter(2);
    c.Update([8847.0, 7884.0]);
    c.Update([null, 7884.4]);
    Assert.Equal(1, c.ActiveIndex);
    c.Update([8847.6, 7884.6]);
    Assert.Equal(0, c.ActiveIndex);
  }

  [Fact]
  public void Counter_NotChangingWithoutImport_IsNoFailure()
  {
    var c = new PriorityCounter(2);
    c.Update([8847.0, 7884.0]);
    c.Update([8847.0, 7884.0]);
    c.Update([8847.0, 7884.1]); // the inverter's 0.1-kWh step alone
    Assert.Equal(0, c.ActiveIndex);
  }

  [Fact]
  public void Counter_FrozenWhileTheNextCounts_Fails()
  {
    var c = new PriorityCounter(2);
    c.Update([8847.0, 7884.0]);
    c.Update([8847.0, 7884.3]); // wM-Bus telegrams stopped, the inverter counts on
    Assert.True(c.Failed(0));
    Assert.Equal(1, c.ActiveIndex);
  }

  [Fact]
  public void Counter_IncreaseFromEachSourcesOwnStart()
  {
    var c = new PriorityCounter(2);
    c.Update([8847.0, 7884.0]);
    var start = c.Snapshot();
    c.Update([null, 7884.6]);
    Assert.Equal(0.6, c.IncreaseSince(start)!.Value, 3); // not 8847 − 7884
  }

  [Fact]
  public void Counter_SnapshotFromStoredTotal_BelongsToTheCounterItFits()
  {
    var c = new PriorityCounter(2);
    c.Update([8850.0, 7886.0]);
    var start = c.SnapshotFrom(8847.0); // stored before a restart, from the wM-Bus meter
    Assert.Equal(3.0, c.IncreaseSince(start)!.Value, 3);
    Assert.Equal(7886.0, start[1]);
  }

  // ── QuarterImport ────────────────────────────────────────────────────────────────────

  [Fact]
  public void Quarter_FirstAfterStartIsIncomplete_NextOneFinishesWithItsMean()
  {
    var q = new QuarterImport(1);
    Assert.Null(q.Update(T0.AddMinutes(5), [100.0]));
    Assert.False(q.Complete);
    q.Update(T0.AddMinutes(14).AddSeconds(50), [100.4]);
    Assert.Null(q.Update(T0.AddMinutes(15).AddSeconds(5), [100.5])); // the first quarter wasn't tracked from its start
    Assert.True(q.Complete);
    q.Update(T0.AddMinutes(20), [101.0]);
    Assert.Equal(600, q.ImportedWh!.Value, 0); // from 100.4, the last reading before the quarter
    var done = q.Update(T0.AddMinutes(30).AddSeconds(5), [102.9]);
    Assert.Equal(T0.AddMinutes(15), done!.Value.Start);
    Assert.Equal((102.9 - 100.4) * 4, done.Value.AvgKw, 3); // from the reading before the quarter began
  }

  [Fact]
  public void Quarter_GapBeforeItsStart_IsIncomplete()
  {
    var q = new QuarterImport(1);
    q.Update(T0.AddMinutes(1), [100.0]);
    q.Update(T0.AddMinutes(20), [101.0]); // NetDaemon was down over the quarter boundary
    Assert.False(q.Complete);
    Assert.Null(q.ImportedWh);
  }

  [Fact]
  public void Quarter_FallbackMidQuarter_CountsFromItsOwnStart()
  {
    var q = new QuarterImport(2);
    q.Update(T0.AddSeconds(-10), [8847.0, 7884.0]);
    q.Update(T0.AddSeconds(5), [8847.0, 7884.0]);
    q.Update(T0.AddMinutes(5), [null, 7884.8]);
    Assert.Equal(800, q.ImportedWh!.Value, 0);
  }

  // ── MonthPeakTracker ─────────────────────────────────────────────────────────────────

  [Fact]
  public void MonthPeak_KeepsTheHighest_StartsOverInANewMonth()
  {
    var m = new MonthPeakTracker();
    Assert.True(m.Observe(new QuarterResult(T0, 9.5)));
    Assert.False(m.Observe(new QuarterResult(T0.AddHours(1), 7.0)));
    Assert.Equal(9.5, m.PeakIn(T0)!.Value.AvgKw);
    Assert.Null(m.PeakIn(new DateTime(2026, 11, 1, 0, 5, 0)));
    Assert.True(m.Observe(new QuarterResult(new DateTime(2026, 11, 1, 0, 0, 0), 2.0)));
  }

  [Fact]
  public void MonthPeak_ImpossibleQuarter_Ignored()
  {
    var m = new MonthPeakTracker();
    m.Restore(new QuarterResult(T0, 31_550)); // a Wh-as-kWh reading stored before the fix
    Assert.Null(m.Peak);
    Assert.False(m.Observe(new QuarterResult(T0, 120)));
    Assert.True(m.Observe(new QuarterResult(T0, 2.4)));
  }

  // ── ImportCapGuard ───────────────────────────────────────────────────────────────────

  static GuardInput Live(double elapsed, double? importedWh, int gridW, int houseW, int evW = 1922, bool evOn = true, bool evPaused = false, int pvW = 260) =>
    new(CapW: 9800, ElapsedSeconds: elapsed, ImportedWh: importedWh, GridImportW: gridW, HouseLoadW: houseW, PvW: pvW,
      ExtraLoadsW: evW, ExtraLoadsOn: evOn, ExtraLoadsNominalW: 1930, ExtraLoadsPaused: evPaused,
      MaxChargeA: 30, VoltageV: 245, ReactionSeconds: 90);

  [Fact]
  public void Guard_LiveCase_2026_10_25_0930_NoLimitNeeded()
  {
    // 9.5 kW measured: force charge 7.45 kW + EV 1.92 kW + house — under the cap.
    var d = ImportCapGuard.Decide(Live(300, 9513 * 300 / 3600.0, 9513, 2324));
    Assert.Equal(30, d.ChargeLimitA);
    Assert.False(d.PauseExtraLoads);
  }

  [Fact]
  public void Guard_HeatPumpStartsDuringForceCharge_BatteryYieldsToTheCap()
  {
    // Same as 09:30 plus the heat pump (3 kW): the battery gets what's left of 9.8 kW.
    var d = ImportCapGuard.Decide(Live(300, 9513 * 300 / 3600.0, 12513, 5324));
    double batteryW = d.ChargeLimitA * 245;
    Assert.InRange(5324 - 1922 - 260 + 1930 + batteryW, d.AllowedW - 245, d.AllowedW);
    Assert.False(d.PauseExtraLoads);
  }

  [Fact]
  public void Guard_OverBudgetMidQuarter_BelowTheCapForTheRest()
  {
    // 10.4 kW for 7.5 min: the rest of the quarter must stay under the cap to average 9.8.
    var d = ImportCapGuard.Decide(Live(450, 1300, 10400, 3000));
    Assert.True(d.AllowedW < 9800);
    double rest = Energy.Wh(10400, TimeSpan.FromSeconds(90)) + Energy.Wh(d.AllowedW, TimeSpan.FromSeconds(360));
    Assert.True(1300 + rest <= 2450 + 1, $"quarter ends at {(1300 + rest) * 4 / 1000:F2} kW");
  }

  [Fact]
  public void Guard_LateInTheQuarter_ChangeOnlyMattersForTheNextOne()
  {
    // 60 s left, less than the reaction time: the limit is for the next quarter (full cap).
    var d = ImportCapGuard.Decide(Live(840, 2400, 9600, 2300));
    Assert.Equal(9800, d.AllowedW);
    Assert.True(d.ProjectedKw > 9.8);
  }

  [Fact]
  public void Guard_BatteryAtZeroStillOver_PausesTheEv_ResumesWithMargin()
  {
    var pause = ImportCapGuard.Decide(Live(60, 160, 9900, 8500 + 1922));
    Assert.True(pause.PauseExtraLoads);
    Assert.Equal(6.3, pause.ChargeLimitA, 1); // the rest after the house's 8.24 kW net

    var stillTight = ImportCapGuard.Decide(Live(60, 160, 7500, 7500 + 260, evW: 0, evPaused: true));
    Assert.True(stillTight.PauseExtraLoads); // 2.3 kW room < 1.93 kW + margin
    var room = ImportCapGuard.Decide(Live(60, 160, 7000, 7000 + 260, evW: 0, evPaused: true));
    Assert.False(room.PauseExtraLoads);
  }

  [Fact]
  public void Guard_ImplausibleCounter_EstimatedFromThePower()
  {
    // Live 2026-10-25 11:30: counters read in Wh as kWh — 7 Wh looked like 7 kWh after 45 s, and the
    // battery was held at 0 A. Impossible amounts are replaced by the estimate from the grid power.
    var d = ImportCapGuard.Decide(Live(45, 7000, 600, 2324, evW: 0, evOn: false));
    Assert.Equal(30, d.ChargeLimitA);
    Assert.Contains("implausible", d.Reason);
  }

  [Fact]
  public void Guard_HeadroomForANewLoad_WithTheBatteryYielding()
  {
    var d = ImportCapGuard.Decide(Live(60, 160, 9300, 2324));
    Assert.Equal(9800 - (2324 - 1922 - 260) - 1930, d.HeadroomW);
  }

  [Theory]
  [InlineData(null, 12.3, 12.3)]  // first value
  [InlineData(20.0, 12.3, 12.3)]  // lower at once
  [InlineData(12.3, 12.8, 12.3)]  // small raise skipped
  [InlineData(12.3, 13.4, 13.4)]  // a step worth a write
  [InlineData(29.5, 30.0, 30.0)]  // back to the maximum
  public void Guard_NextLimit(double? previous, double decided, double expected) =>
    Assert.Equal(expected, ImportCapGuard.NextLimit(previous, decided, 30));

  // ── Health ───────────────────────────────────────────────────────────────────────────

  [Fact]
  public void Health_FallbackCounterDegraded_NoneError_RecentOverCapDegraded()
  {
    string[] ids = ["sensor.wmbus", "sensor.solax"];
    Assert.Empty(HealthChecks.ImportCap(9800, 0, ids, null, T0));
    Assert.Equal(HealthLevel.degraded, Assert.Single(HealthChecks.ImportCap(9800, 1, ids, null, T0)).Level);
    Assert.Equal(HealthLevel.error, Assert.Single(HealthChecks.ImportCap(9800, -1, ids, null, T0)).Level);
    Assert.Single(HealthChecks.ImportCap(9800, 0, ids, new QuarterResult(T0, 10.1), T0.AddMinutes(30)));
    Assert.Empty(HealthChecks.ImportCap(9800, 0, ids, new QuarterResult(T0, 10.1), T0.AddHours(2)));
    Assert.Empty(HealthChecks.ImportCap(0, -1, ids, null, T0));
  }
}
