using NetDeamon.apps.PVControl.WarmWater;
using Xunit;

namespace NetDeamonApps.Tests;

/// <summary>
/// WarmWaterControl: starting, stopping and re-targeting the one-time heating (planned run,
/// boost), plus the daily comfort windows. The one-time switch stays on after the heat pump
/// reached its setpoint (live 2026-10-12: run ended 15:54:34, switch on until switched off by hand
/// 15:55:07), and switching it off stops a running heating (2026-10-15: off 16:07:53 at 54.5 °C,
/// heating stopped 16:07:56). WarmWaterController's HA calls around it aren't unit tested (they
/// need a live Home Assistant).
/// </summary>
public class WarmWaterControlTests : TestBase
{
  static readonly DateTime Now = new(2026, 10, 20, 17, 0, 0);
  static readonly PlannedRun Run = new(Now.AddMinutes(-5), Now.AddMinutes(25), 38, 44.3, 1700);

  static ControlInput Input(double temp = 38, bool switchOn = false, bool active = false, double? setpoint = 57,
    WarmWaterMode mode = WarmWaterMode.Auto, DateTime? lastCommand = null, IReadOnlyList<PlannedRun>? runs = null,
    bool boost = false, DateTime? boostSince = null, bool boostSawRunning = false, DateTime? lastStart = null) =>
    new(Now, temp, switchOn, active, setpoint, lastCommand ?? lastStart, lastStart, mode, runs ?? [Run], 57, boost, 45, boostSince, boostSawRunning, 180);

  static ControlDecision Decide(ControlInput c) => WarmWaterControl.Decide(c);

  // ── Planned runs (Auto) ────────────────────────────────────────────────────────────────────

  [Fact]
  public void InsidePlannedRun_StartsAtRoundedUpTarget()
  {
    var d = Decide(Input());
    Assert.Equal(ControlCommand.Start, d.Command);
    Assert.Equal(44.5, d.Target);
  }

  [Fact]
  public void Running_AtPlannedSetpoint_DoesNothing() =>
    Assert.Equal(ControlCommand.None, Decide(Input(switchOn: true, active: true, setpoint: 44.5)).Command);

  [Fact]
  public void Running_PlanWantsMore_SetpointRaised()
  {
    var d = Decide(Input(switchOn: true, active: true, setpoint: 42));
    Assert.Equal(ControlCommand.SetSetpoint, d.Command);
    Assert.Equal(44.5, d.Target);
  }

  [Fact]
  public void Running_PlanWantsLess_SetpointNotLowered() =>
    Assert.Equal(ControlCommand.None, Decide(Input(switchOn: true, active: true, setpoint: 50)).Command);

  [Fact]
  public void Running_PlanHasNoRunNow_NeverStopped()
  {
    var later = Run with { Start = Now.AddHours(2), End = Now.AddHours(2.5) };
    Assert.Equal(ControlCommand.None, Decide(Input(temp: 41, switchOn: true, active: true, runs: [later])).Command);
  }

  [Fact]
  public void Running_PlanMadeAfterTheCyclesClock_NotStopped()
  {
    // Live 2026-10-20 16:26:15: the cycle read the clock (16:26:14.0), the replan then made a run
    // starting at 16:26:14.9 — "no current run" → switched off 3 min after the start.
    var replanned = Run with { Start = Now.AddSeconds(0.9), End = Now.AddMinutes(26) };
    var d = Decide(Input(temp: 40.3, switchOn: true, active: true, setpoint: 47.5, runs: [replanned], lastStart: Now.AddMinutes(-3)));
    Assert.Equal(ControlCommand.None, d.Command);
  }

  [Fact]
  public void AfterAHeating_NoPlannedStartWithin60Min()
  {
    Assert.Equal(ControlCommand.None, Decide(Input() with { LastStop = Now.AddMinutes(-59) }).Command);
    Assert.Equal(ControlCommand.Start, Decide(Input() with { LastStop = Now.AddMinutes(-60) }).Command);
  }

  [Fact]
  public void PlannedRunUnder3K_NotStarted()
  {
    Assert.Equal(ControlCommand.None, Decide(Input(temp: 41.5)).Command); // 41.5 → 44.3
    Assert.Equal(ControlCommand.Start, Decide(Input(temp: 41.3)).Command);
  }

  [Fact]
  public void RunFinished_SwitchLeftOn_SwitchesOff()
  {
    // The heat pump stopped at the setpoint, the switch is still on, the planned run is over.
    var d = Decide(Input(temp: 44.6, switchOn: true, active: false, runs: []));
    Assert.Equal(ControlCommand.Stop, d.Command);
  }

  [Fact]
  public void SwitchOnButNotHeating_DuringRun_ResetsSoTheNextCycleStartsCleanly()
  {
    // E.g. switch still on from an earlier run: switching it on again wouldn't start anything.
    var d = Decide(Input(temp: 38, switchOn: true, active: false));
    Assert.Equal(ControlCommand.Stop, d.Command);
  }

  [Fact]
  public void OwnStart_NotYetAcknowledged_WaitsUpToTenMinutes()
  {
    // Live 2026-10-15: "active" 5 s after the switch, compressor 144 s — a slower start must not
    // be reset after the 3-min command cooldown.
    Assert.Equal(ControlCommand.None, Decide(Input(switchOn: true, active: false, lastStart: Now.AddMinutes(-9))).Command);
    Assert.Equal(ControlCommand.Stop, Decide(Input(switchOn: true, active: false, lastStart: Now.AddMinutes(-10))).Command);
  }

  [Fact]
  public void TankAlreadyNearTarget_DoesNotStart() =>
    Assert.Equal(ControlCommand.None, Decide(Input(temp: 43.5)).Command);

  [Fact]
  public void NoRunNow_SwitchOff_Waits()
  {
    var later = Run with { Start = Now.AddHours(1), End = Now.AddHours(1.5) };
    var d = Decide(Input(runs: [later]));
    Assert.Equal(ControlCommand.None, d.Command);
    Assert.Contains("18:00", d.Reason);
  }

  [Theory]
  [InlineData(WarmWaterMode.Off)]
  [InlineData(WarmWaterMode.PlanOnly)]
  public void NotAuto_HandsOff(WarmWaterMode mode)
  {
    Assert.Equal(ControlCommand.None, Decide(Input(mode: mode)).Command);
    Assert.Equal(ControlCommand.None, Decide(Input(mode: mode, switchOn: true, active: true, runs: [])).Command);
  }

  [Fact]
  public void CommandCooldown_NoCommandWithinThreeMinutes()
  {
    Assert.Equal(ControlCommand.None, Decide(Input(lastCommand: Now.AddMinutes(-2))).Command);
    Assert.Equal(ControlCommand.Start, Decide(Input(lastCommand: Now.AddMinutes(-3))).Command);
    // ...also not the reset right after a start, while the "active" sensor catches up.
    Assert.Equal(ControlCommand.None, Decide(Input(switchOn: true, lastCommand: Now.AddSeconds(-15))).Command);
  }

  // ── Boost ──────────────────────────────────────────────────────────────────────────────────

  [Theory]
  [InlineData(WarmWaterMode.Off)]
  [InlineData(WarmWaterMode.PlanOnly)]
  [InlineData(WarmWaterMode.Auto)]
  public void Boost_StartsInEveryMode(WarmWaterMode mode)
  {
    var d = Decide(Input(temp: 41, mode: mode, runs: [], boost: true, boostSince: Now));
    Assert.Equal(ControlCommand.Start, d.Command);
    Assert.Equal(45, d.Target);
    Assert.Null(d.EndBoost);
  }

  [Fact]
  public void Boost_Reached_EndsAndSwitchesOff()
  {
    var d = Decide(Input(temp: 44.8, switchOn: true, active: false, boost: true, boostSince: Now.AddMinutes(-15), boostSawRunning: true));
    Assert.NotNull(d.EndBoost);
    Assert.Equal(ControlCommand.Stop, d.Command);
  }

  [Fact]
  public void Boost_HeatPumpStoppedShortOfIt_EndsWithoutRestart()
  {
    // Ran, and stopped below the boost temperature (sensor lag) — done, don't start again.
    var d = Decide(Input(temp: 44, switchOn: true, active: false, boost: true, boostSince: Now.AddMinutes(-20), boostSawRunning: true));
    Assert.NotNull(d.EndBoost);
    Assert.Equal(ControlCommand.Stop, d.Command);
  }

  [Fact]
  public void Boost_ReachedWhileHeatingHigher_HeatingContinues()
  {
    // A planned run to 49 °C is going; the boost (45 °C) is reached on the way — boost over, but
    // the heating isn't stopped.
    var d = Decide(Input(temp: 45, switchOn: true, active: true, setpoint: 49, boost: true, boostSince: Now.AddMinutes(-10), boostSawRunning: true));
    Assert.NotNull(d.EndBoost);
    Assert.Equal(ControlCommand.None, d.Command);
  }

  [Fact]
  public void Boost_NotHeldBackByOffTime() =>
    Assert.Equal(ControlCommand.Start, Decide(Input(temp: 41, runs: [], boost: true, boostSince: Now) with { LastStop = Now.AddMinutes(-5) }).Command);

  [Fact]
  public void Boost_TimesOut() =>
    Assert.NotNull(Decide(Input(temp: 41, boost: true, boostSince: Now.AddMinutes(-180))).EndBoost);

  [Fact]
  public void Boost_OverridesARunningPlannedSetpoint()
  {
    var d = Decide(Input(temp: 42, switchOn: true, active: true, setpoint: 44.5, runs: [], boost: true, boostSince: Now, boostSawRunning: true));
    Assert.Equal(ControlCommand.SetSetpoint, d.Command);
    Assert.Equal(45, d.Target);
  }

  // ── Helpers ────────────────────────────────────────────────────────────────────────────────

  [Theory]
  [InlineData(44.3, 44.5)]
  [InlineData(44.5, 44.5)]
  [InlineData(56.8, 57)]
  [InlineData(58, 57)]
  public void SetpointFor_RoundsUpToHalfDegreesAndCaps(double target, double expected) =>
    Assert.Equal(expected, WarmWaterControl.SetpointFor(target, 57));

  [Fact]
  public void ComfortWindows_DailyUntilHorizon_IncludingTheOpenOne()
  {
    var now = new DateTime(2026, 10, 20, 7, 0, 0);
    var horizonEnd = new DateTime(2026, 10, 22, 0, 0, 0);
    var windows = WarmWaterControl.ComfortWindows([(new TimeOnly(6, 30), 60), (new TimeOnly(19, 0), 120)], now, horizonEnd, 43);

    Assert.Equal([
      new DateTime(2026, 10, 20, 6, 30, 0), new DateTime(2026, 10, 20, 19, 0, 0),
      new DateTime(2026, 10, 21, 6, 30, 0), new DateTime(2026, 10, 21, 19, 0, 0)], windows.Select(w => w.Start));
    Assert.All(windows, w => Assert.Equal(43, w.MinTemp));
    Assert.Equal(new DateTime(2026, 10, 20, 21, 0, 0), windows[1].End);
  }
}
