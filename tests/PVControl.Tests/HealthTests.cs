using NetDeamon.apps.PVControl;
using Xunit;

namespace NetDeamonApps.Tests;

/// <summary>
/// sensor.pv_control_health: the debounce (<see cref="HealthMonitor"/>) and the checks
/// (<see cref="HealthChecks"/>). Reading the HA entities and the DB for them, and publishing the
/// sensor (PVControl.CollectHealthIssues / PublishHealthAsync), need a live HA and aren't tested.
/// </summary>
public class HealthTests
{
  static readonly DateTime T0 = new(2026, 10, 21, 13, 28, 15);
  static HealthMonitor Monitor() => new(TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1));
  static readonly HealthIssue SocHeld = new("soc", HealthLevel.degraded, "Battery SoC unknown");

  // ── HealthMonitor ──────────────────────────────────────────────────────────────────────────

  // Live 2026-10-28 12:19:46: NetDeamon re-initialised the app on HA's reconnect, the monitor started
  // empty, the level read "ok" and 45 s later "degraded" again — two notifications for one problem.
  static readonly ActiveIssue LoggedBefore = new("datalogger", HealthLevel.degraded, "DataLogger: last hourly row 28.10. 09:59", T0.AddMinutes(-2));
  static readonly HealthIssue LoggerStillLate = new("datalogger", HealthLevel.degraded, "DataLogger: last hourly row 28.10. 09:59");

  [Fact]
  public void Restore_LastingProblem_StaysDegradedThroughTheRestart()
  {
    var m = Monitor();
    m.Restore(T0, [LoggedBefore]);
    var s = m.Evaluate(T0.AddSeconds(15), [LoggerStillLate]);   // first cycle: no 30-s wait, no "ok" in between
    Assert.Equal(HealthLevel.degraded, s.Level);
    Assert.Equal(T0.AddMinutes(-2), Assert.Single(s.Issues).Since);
  }

  [Fact]
  public void Restore_ProblemGone_ClearsAfterItsMinute()
  {
    var m = Monitor();
    m.Restore(T0, [LoggedBefore]);
    Assert.Equal(HealthLevel.degraded, m.Evaluate(T0.AddSeconds(30), []).Level);
    Assert.Equal(HealthLevel.ok, m.Evaluate(T0.AddSeconds(75), []).Level);
  }

  [Fact]
  public void SingleCycleBlip_Live132815_StaysOk()
  {
    // SolaX sensors unknown 13:28:02–13:28:17: seen in one cycle only.
    var m = Monitor();
    Assert.Equal(HealthLevel.ok, m.Evaluate(T0, [SocHeld]).Level);
    Assert.Equal(HealthLevel.ok, m.Evaluate(T0.AddSeconds(15), []).Level);
    Assert.Equal(HealthLevel.ok, m.Evaluate(T0.AddSeconds(30), []).Level);
  }

  [Fact]
  public void TwoSeparateBlips_DontAddUp()
  {
    var m = Monitor();
    m.Evaluate(T0, [SocHeld]);
    m.Evaluate(T0.AddSeconds(15), []);
    Assert.Equal(HealthLevel.ok, m.Evaluate(T0.AddSeconds(30), [SocHeld]).Level);
  }

  [Fact]
  public void PresentFor30s_Counts()
  {
    var m = Monitor();
    m.Evaluate(T0, [SocHeld]);
    Assert.Equal(HealthLevel.ok, m.Evaluate(T0.AddSeconds(15), [SocHeld]).Level);
    var s = m.Evaluate(T0.AddSeconds(30), [SocHeld]);
    Assert.Equal(HealthLevel.degraded, s.Level);
    Assert.Equal(T0, Assert.Single(s.Issues).Since);
  }

  [Fact]
  public void Counting_ClearsOnlyAfterOneMinuteGone()
  {
    var m = Monitor();
    for (int i = 0; i <= 2; i++) m.Evaluate(T0.AddSeconds(15 * i), [SocHeld]); // counting from +30 s
    Assert.Equal(HealthLevel.degraded, m.Evaluate(T0.AddSeconds(45), []).Level);
    Assert.Equal(HealthLevel.degraded, m.Evaluate(T0.AddSeconds(75), []).Level);
    Assert.Equal(HealthLevel.ok, m.Evaluate(T0.AddSeconds(90), []).Level);
  }

  [Fact]
  public void EscalateAfter_DegradedBecomesError()
  {
    var m = Monitor();
    var issue = HealthChecks.InverterData(["sensor.solax_run_mode"]).Single();
    HealthStatus? s = null;
    for (int i = 0; i <= 7; i++) s = m.Evaluate(T0.AddSeconds(15 * i), [issue]); // up to 1:45
    Assert.Equal(HealthLevel.degraded, s!.Level);
    Assert.Equal(HealthLevel.error, m.Evaluate(T0.AddMinutes(2), [issue]).Level);
  }

  [Fact]
  public void Level_IsTheWorstCountingIssue()
  {
    var m = Monitor();
    HealthIssue[] issues = [SocHeld, new("cycle", HealthLevel.error, "3 cycle(s) failed")];
    m.Evaluate(T0, issues);
    var s = m.Evaluate(T0.AddSeconds(30), issues);
    Assert.Equal(HealthLevel.error, s.Level);
    Assert.Equal("cycle", s.Issues[0].Key); // worst first
  }

  // ── HealthChecks ───────────────────────────────────────────────────────────────────────────

  [Fact]
  public void BatterySoC_HeldIsDegraded_UnavailableIsError()
  {
    Assert.Empty(HealthChecks.BatterySoC(true, false, T0));
    Assert.Equal(HealthLevel.degraded, HealthChecks.BatterySoC(false, false, T0).Single().Level);
    Assert.Equal(HealthLevel.error, HealthChecks.BatterySoC(false, true, T0).Single().Level);
  }

  const string Used = "sensor.epex_spot_data_market_price";

  [Fact]
  public void Prices_Live20261021_SourceDisabled_Degraded()
  {
    // _3 belonged to the disabled ENTSO-E entry: no data at all.
    var issue = HealthChecks.ImportPrices(T0, ["sensor.epex_spot_data_market_price_3"], Used, new DateTime(2026, 10, 22, 0, 0, 0)).Single();
    Assert.Equal("price_source", issue.Key);
    Assert.Equal(HealthLevel.degraded, issue.Level);
  }

  [Fact]
  public void Prices_OtherSourceThanFirstUsed_NoIssue() =>
    // All sources have data, the used one isn't the first in the list (it reaches further): fine.
    Assert.Empty(HealthChecks.ImportPrices(T0, [], "sensor.epex_spot_data_market_price_4", new DateTime(2026, 10, 23, 0, 0, 0)));

  [Fact]
  public void Prices_NoneForNow_Error() =>
    Assert.Equal(HealthLevel.error, HealthChecks.ImportPrices(T0, [], Used, T0.Date.AddHours(13)).Single().Level);

  [Fact]
  public void Prices_TomorrowMissingAfter16()
  {
    var todayOnly = new DateTime(2026, 10, 22, 0, 0, 0);
    Assert.Empty(HealthChecks.ImportPrices(T0.Date.AddHours(15.9), [], Used, todayOnly));
    Assert.Equal("prices_tomorrow", HealthChecks.ImportPrices(T0.Date.AddHours(16), [], Used, todayOnly).Single().Key);
    Assert.Empty(HealthChecks.ImportPrices(T0.Date.AddHours(16), [], Used, todayOnly.AddDays(1)));
  }

  [Fact]
  public void Cycle_OneFailureDegraded_ThreeError()
  {
    Assert.Empty(HealthChecks.Cycle(0, null));
    Assert.Equal(HealthLevel.degraded, HealthChecks.Cycle(1, "x").Single().Level);
    Assert.Equal(HealthLevel.error, HealthChecks.Cycle(3, "x").Single().Level);
  }

  [Fact]
  public void DataLogger_HourlyRowOlderThanTwoHours_Degraded()
  {
    var last = new DateTime(2026, 10, 21, 12, 59, 59);
    Assert.Empty(HealthChecks.DataLogger(new DateTime(2026, 10, 21, 14, 59, 0), last));
    Assert.Single(HealthChecks.DataLogger(new DateTime(2026, 10, 21, 15, 0, 0), last));
    Assert.Single(HealthChecks.DataLogger(T0, null));
  }

  [Fact]
  public void Forecasts_InvalidIsError_MissingSourceDegraded()
  {
    Assert.Empty(HealthChecks.Forecasts(true, true, []));
    Assert.Equal(HealthLevel.degraded, HealthChecks.Forecasts(true, true, ["sensor.energy_production_today_4"]).Single().Level);
    Assert.Equal(HealthLevel.error, HealthChecks.Forecasts(false, true, []).Single().Level);
  }

  [Fact]
  public void WarmWater_HeatPumpUnavailable_Degraded()
  {
    Assert.Empty(HealthChecks.WarmWater(false, false));
    Assert.Equal(HealthLevel.degraded, HealthChecks.WarmWater(true, false).Single().Level);
  }
}
