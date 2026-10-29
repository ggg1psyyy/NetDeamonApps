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
    // From the reading before the quarter began (14:50) to the first after it (30:05): 915 s, scaled to the 900 s the meter bills.
    Assert.Equal((102.9 - 100.4) * 4 * 900 / 915, done.Value.AvgKw, 3);
  }

  [Fact]
  public void Quarter_LiveIncludesTheExtraSeconds_FinishedIsScaledTo15Minutes()
  {
    // The live guard counts from 01:59:50 (the last cycle before the quarter): a safe overestimate. The finished
    // quarter is 9.72 kW counted over 915 s → 9.56 kW over the 900 s the meter bills (the month peak, the warning).
    var q = new QuarterImport(1);
    q.Update(T0.AddSeconds(-10), [1000.000]);
    q.Update(T0.AddSeconds(5), [1000.040]);
    Assert.Equal(40, q.ImportedWh!.Value, 0);
    var done = q.Update(T0.AddMinutes(15).AddSeconds(5), [1002.430]);
    Assert.Equal(2.43 * 4 * 900 / 915, done!.Value.AvgKw, 3);
  }

  [Fact]
  public void Quarter_NoCounterReadingAtItsStart_IsIncomplete()
  {
    var q = new QuarterImport(1);
    q.Update(T0.AddSeconds(-10), [null]);  // the counter unavailable right before the quarter
    q.Update(T0.AddSeconds(5), [1000.0]);
    Assert.False(q.Complete);
    Assert.Null(q.ImportedWh);
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

  // Clock changes (the machine's time zone must be Vienna-like, as for DataLoggerPeriodTests): the local
  // quarters 02:45 → 02:00 (October) and 01:45 → 03:00 (March) aren't consecutive, as UTC instants they are.
  [Theory]
  [InlineData(2026, 10, 25, 0, 40, 2, 45)] // 02:40 CEST; the quarter 02:45 CEST ends at 02:00 CET (01:00 UTC)
  [InlineData(2026, 3, 29, 0, 40, 1, 45)]  // 01:40 CET; the quarter 01:45 CET ends at 03:00 CEST (01:00 UTC)
  public void Quarter_AcrossTheClockChange_StillFinishes(int y, int m, int d, int h, int min, int localHour, int localMinute)
  {
    var q = new QuarterImport(1);
    var utc = new DateTime(y, m, d, h, min, 0, DateTimeKind.Utc);
    q.Update(utc, [100.0]);
    q.Update(utc.AddMinutes(4).AddSeconds(50), [100.2]);
    Assert.Null(q.Update(utc.AddMinutes(5).AddSeconds(5), [100.3]));
    Assert.True(q.Complete);
    var done = q.Update(utc.AddMinutes(20).AddSeconds(5), [101.0]);
    Assert.NotNull(done);
    Assert.Equal(new DateTime(y, m, d, localHour, localMinute, 0), done.Value.Start);
    Assert.Equal((101.0 - 100.2) * 4 * 900 / 915, done.Value.AvgKw, 3);
    Assert.Equal(5, q.ElapsedSeconds(utc.AddMinutes(20).AddSeconds(5)), 0);
  }

  // A closed period (DataLogger hour): the wM-Bus receiver lost its antenna 2026-10-28 — online, but a
  // receiver without telegrams keeps its last value; it must not log 0 kW while the inverter counts.
  [Theory]
  [InlineData(1.20, 1.2, 0)]     // both count: the first (billing meter) is used
  [InlineData(0.00, 0.0, 0)]     // no import at all: the first is fine
  [InlineData(0.00, 1.1, 1)]     // frozen while the inverter counts: the fallback
  [InlineData(0.85, 1.1, 1)]     // froze mid-hour: 0.25 kWh missing — the fallback
  [InlineData(1.00, 1.1, 0)]     // the inverter's 0.1-kWh steps: within the tolerance
  public void FirstWorking_FrozenSourceLosesToTheNextThatCounted(double meter, double inverter, int expected) =>
    Assert.Equal(expected, PriorityCounter.FirstWorking([meter, inverter]));

  [Fact]
  public void FirstWorking_NoReadings_NextSourceOrNone()
  {
    Assert.Equal(1, PriorityCounter.FirstWorking([null, 0.5]));
    Assert.Equal(-1, PriorityCounter.FirstWorking([null, null]));
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
    Assert.False(m.Observe(new QuarterResult(T0, 9.9)));   // an older month's quarter doesn't replace November's peak
    Assert.Equal(2.0, m.PeakIn(new DateTime(2026, 11, 1, 0, 5, 0))!.Value.AvgKw);
  }

  // Live 2026-10-27/28: the sensor restore read kW as W (2.82 → 2820) and was rejected, so every
  // restart started the month over; the real peak was 9.60 kW. Two sources now, the higher one wins.
  [Fact]
  public void MonthPeak_RestoreFromTwoSources_HigherOfTheMonthWins()
  {
    var m = new MonthPeakTracker();
    m.Restore(new QuarterResult(new DateTime(2026, 10, 28, 9, 15, 0), 0.0));                               // the sensor after the bug
    m.Restore(MonthPeakTracker.FromHourlyRow(new DateTime(2026, 10, 26, 0, 59, 59), 9.602));               // the DataLogger
    Assert.Equal((new DateTime(2026, 10, 26, 0, 0, 0), 9.602), (m.Peak!.Value.Start, m.Peak.Value.AvgKw));
    m.Restore(new QuarterResult(new DateTime(2026, 10, 27, 0, 0, 0), 4.95));                               // lower: ignored
    Assert.Equal(9.602, m.Peak!.Value.AvgKw);
    m.Restore(new QuarterResult(new DateTime(2026, 9, 30, 20, 0, 0), 12.0));                               // an older month: ignored
    Assert.Equal(9.602, m.Peak!.Value.AvgKw);
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
    // Budget 2450 − 793 − 313 (reaction) Wh over the 510 s left = 9490 W; less the house 3142 W and the EV 1930 W → 4418 W / 245 V.
    Assert.Equal(9490, d.AllowedW);
    Assert.Equal(18.0, d.ChargeLimitA, 1);
    Assert.False(d.PauseExtraLoads);
  }

  [Fact]
  public void Guard_EvChargingOutsidePvControl_StillCountsAgainstTheBattery()
  {
    // The charger switched on by hand (charge_now off) draws 1.92 kW: import like any other, not battery headroom.
    var asked = ImportCapGuard.Decide(Live(300, 9513 * 300 / 3600.0, 12513, 5324, evOn: true));
    var byHand = ImportCapGuard.Decide(Live(300, 9513 * 300 / 3600.0, 12513, 5324, evOn: false));
    Assert.InRange(byHand.ChargeLimitA, asked.ChargeLimitA - 0.1, asked.ChargeLimitA + 0.1); // 1922 W measured vs 1930 W nominal
    Assert.InRange(byHand.HeadroomW, asked.HeadroomW - 10, asked.HeadroomW + 10);
    Assert.False(byHand.PauseExtraLoads); // nothing of PVControl's to pause
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

  // Every change is an EEPROM write on the inverter (live 2026-10-26 05:37–06:06 ~40 changes in 0.1-A
  // steps). Lowered at once, by whole amps, as far as the room needs; raised only with twice the headroom.
  [Theory]
  [InlineData(null, 12.3, true, 12.0)]   // first value, whole amps
  [InlineData(null, 41.0, true, 30.0)]   // never above the maximum
  [InlineData(20.0, 12.3, true, 12.0)]   // lower at once
  [InlineData(29.0, 29.4, true, 29.0)]   // same whole amp: kept, no write
  [InlineData(29.0, 28.6, true, 28.0)]   // any need below the published amp lowers it (the cap wins)
  [InlineData(29.0, 27.9, true, 27.0)]   // a whole amp lower
  [InlineData(12.0, 14.2, true, 12.0)]   // 2 A of room: a 1-A raise isn't twice covered
  [InlineData(12.0, 16.4, true, 14.0)]   // 4.4 A of room: up 2 A
  [InlineData(29.0, 30.9, true, 29.0)]   // back to the maximum needs room for 31 A (30 + the 1 A raise)
  [InlineData(29.0, 31.0, true, 30.0)]
  [InlineData(27.0, 30.0, true, 27.0)]   // just the maximum's room after a 3-A cut: stays
  [InlineData(27.0, 33.0, true, 30.0)]   // twice the 3 A: back to the maximum
  [InlineData(27.0, 22.0, false, 30.0)]  // not grid charging: maximum, whatever the guard decided
  [InlineData(30.0, 27.4, false, 30.0)]  // and nothing to write
  public void Guard_NextLimit(double? previous, double room, bool gridCharging, double expected) =>
    Assert.Equal(expected, ImportCapGuard.NextLimit(previous, room, 30, gridCharging));

  [Fact]
  public void Guard_BulkChargeWithTheEvAtTheCap_OneCutThenSteady()
  {
    // Live 2026-10-29 02:15: bulk 7.52 kW + EV 1.92 kW + house → 9.72 kW import, 80 W under the cap. The guard
    // wanted 29.9 A; the room then moved 29.4–30.4 A with the import. One cut to 29 A, kept while the EV runs.
    double? limit = 30;
    var limits = new List<double>();
    foreach (double room in new[] { 29.9, 30.4, 29.4, 30.2, 29.8, 30.4, 30.6, 29.5 })
    {
      limit = ImportCapGuard.NextLimit(limit, room, 30, gridCharging: true);
      limits.Add(limit.Value);
    }
    Assert.Equal([29, 29, 29, 29, 29, 29, 29, 29], limits);
    // The EV stops: 1.9 kW / 245 V ≈ 7.8 A more room → back to the maximum.
    Assert.Equal(30, ImportCapGuard.NextLimit(limit, 38.2, 30, gridCharging: true));
  }

  [Fact]
  public void Guard_PlanRoom_ChargeStartsBelowTheEdge_AndStays()
  {
    // The same night from the plan: 9800 − house 450 − EV 1930 = 7420 W. At 245 V that's 30.3 A — the charge
    // would start at 30 A and be cut a moment later. Less the margin: 28 A from the start, and it holds while
    // the live room hovers around 29–30 A.
    double planA = ImportCapGuard.PlanRoomA(7420, 245);
    Assert.Equal(28, ImportCapGuard.NextLimit(null, Math.Min(30.3, planA), 30, gridCharging: true));
    Assert.Equal(28, ImportCapGuard.NextLimit(28, Math.Min(30.4, planA), 30, gridCharging: true));
    Assert.Equal(0, ImportCapGuard.PlanRoomA(300, 245));
  }

  [Fact]
  public void Guard_RoomA_NotClampedToTheMaximum()
  {
    // 09:30 without the heat pump: the cap leaves more than 30 A — the raise rule needs to see how much.
    var d = ImportCapGuard.Decide(Live(300, 9513 * 300 / 3600.0, 9513, 2324));
    Assert.Equal(30, d.ChargeLimitA);
    Assert.True(d.RoomA > 30, $"room {d.RoomA} A");
  }

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
    // The cap configured without a counter: the guard is blind — an error, not silence.
    Assert.Equal(HealthLevel.error, Assert.Single(HealthChecks.ImportCap(9800, -1, [], null, T0)).Level);
  }
}
