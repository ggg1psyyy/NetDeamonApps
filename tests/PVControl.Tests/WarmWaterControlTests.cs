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

  // ── The heat pump's start hysteresis ─────────────────────────────────────────────────────────

  [Fact]
  public void Hysteresis_APlannedRunUnderIt_NotStarted()
  {
    // Run 38 → 44.3 °C: 6.3 K starts; at 39.5 °C only 4.8 K — the heat pump wouldn't take it.
    Assert.Equal(ControlCommand.Start, Decide(Input() with { StartHysteresisK = 5.5 }).Command);
    var d = Decide(Input(temp: 39.5) with { StartHysteresisK = 5.5 });
    Assert.Equal(ControlCommand.None, d.Command);
    Assert.Contains("too small", d.Reason);
  }

  [Fact]
  public void Hysteresis_AnArmedPlannedStartThatNeverHeats_SwitchedOffAfterTheAckTime()
  {
    var armed = Input(temp: 44, switchOn: true, active: true, setpoint: 47) with { RunSawHeating = false, StartHysteresisK = 5.5, OwnStart = true };
    Assert.Equal(ControlCommand.None, Decide(armed with { LastStart = Now.AddMinutes(-5), LastCommand = Now.AddMinutes(-5) }).Command);
    var d = Decide(armed with { LastStart = Now.AddMinutes(-11), LastCommand = Now.AddMinutes(-11) });
    Assert.Equal(ControlCommand.Stop, d.Command);
    Assert.Contains("starts at ≤ 41.5", d.Reason);
    // a start that is merely slow (the tank well below setpoint − hysteresis): stays armed, no "starts at"
    var slow = Decide(armed with { Temp = 31, Setpoint = 45, LastStart = Now.AddMinutes(-11), LastCommand = Now.AddMinutes(-11) });
    Assert.Equal(ControlCommand.None, slow.Command);
    Assert.DoesNotContain("starts at", slow.Reason);
    // armed by hand or at the heat pump (not our start), or not in Auto: left alone — however old the last start
    Assert.Equal(ControlCommand.None, Decide(armed with { OwnStart = false, LastStart = Now.AddHours(-5), LastCommand = Now.AddHours(-5) }).Command);
    Assert.Equal(ControlCommand.None, Decide(armed with { OwnStart = false, Mode = WarmWaterMode.PlanOnly, LastStart = Now.AddMinutes(-11), LastCommand = Now.AddMinutes(-11) }).Command);
    // our own (boost) start left armed when the boost is over: switched off in any mode
    Assert.Equal(ControlCommand.Stop, Decide(armed with { Mode = WarmWaterMode.PlanOnly, LastStart = Now.AddMinutes(-11), LastCommand = Now.AddMinutes(-11) }).Command);
    // one that heated is never stopped (a pause in a running heating isn't "not taken")
    Assert.Equal(ControlCommand.None, Decide(armed with { RunSawHeating = true, LastStart = Now.AddMinutes(-30), LastCommand = Now.AddMinutes(-30) }).Command);
  }

  [Fact]
  public void Hysteresis_AnArmedBoost_StaysAndSaysWhenItStarts()
  {
    var d = Decide(Input(temp: 44.3, switchOn: true, active: true, setpoint: 47, boost: true, boostSince: Now.AddMinutes(-30),
      lastStart: Now.AddMinutes(-30)) with { BoostTemp = 47, RunSawHeating = false, StartHysteresisK = 5.5 });
    Assert.Equal(ControlCommand.None, d.Command);
    Assert.Contains("Boost: waiting for the heat pump (starts at ≤ 41.5 °C)", d.Reason);
  }

  [Fact]
  public void Hysteresis_ABoostRaisedOverAnArmedStart_RaisesTheSetpoint()
  {
    var d = Decide(Input(temp: 44, switchOn: true, active: true, setpoint: 47, boost: true, boostSince: Now.AddMinutes(-10),
      lastStart: Now.AddMinutes(-10)) with { BoostTemp = 50, RunSawHeating = false, StartHysteresisK = 5.5, OwnStart = true });
    Assert.Equal((ControlCommand.SetSetpoint, (double?)50), (d.Command, d.Target));
  }

  [Fact]
  public void RunTrack_OwnStartAndHeated_TheOffTimeOnlyAfterAHeatingThatHeated()
  {
    var t0 = Now;
    var track = new RunTrack(false, false, false, null).After(ControlCommand.Start);
    Assert.True(track.OwnStart);
    track = track.Observe(t0, oneTimeActive: true, heating: false);           // armed, waiting
    Assert.Equal((true, false, true), (track.WasActive, track.RunSawHeating, track.OwnStart));
    var notTaken = track.After(ControlCommand.Stop).Observe(t0.AddMinutes(11), false, false);
    Assert.Equal((false, (DateTime?)null), (notTaken.OwnStart, notTaken.LastStop));   // no off-time from a start never heated
    var heated = track.Observe(t0.AddMinutes(20), true, true).Observe(t0.AddMinutes(25), true, false);   // a pause stays "heated"
    Assert.True(heated.RunSawHeating);
    var ended = heated.Observe(t0.AddMinutes(40), false, false);
    Assert.Equal((false, false, (DateTime?)t0.AddMinutes(40)), (ended.OwnStart, ended.RunSawHeating, ended.LastStop));
    // a start that never went active: our stop clears the flag, the next hand-armed heating isn't ours
    var never = new RunTrack(false, false, false, null).After(ControlCommand.Start).Observe(t0, false, false).After(ControlCommand.Stop);
    Assert.False(never.Observe(t0.AddMinutes(30), true, false).OwnStart);
  }

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

  // ── Refit schedule (WarmWaterController.RefitDue; the refit itself reads HA history) ──

  [Fact]
  public void RefitDue_DailyAfterASuccess_HourlyRetryAfterAFailure()
  {
    var t = new DateTime(2026, 10, 29, 10, 0, 0);
    Assert.True(WarmWaterController.RefitDue(null, null, t));                          // first start
    Assert.False(WarmWaterController.RefitDue(t, t, t.AddHours(23)));                  // fitted: not before a day
    Assert.True(WarmWaterController.RefitDue(t, t, t.AddDays(1)));
    Assert.False(WarmWaterController.RefitDue(null, t, t.AddMinutes(30)));             // failed: not every cycle…
    Assert.True(WarmWaterController.RefitDue(null, t, t.AddHours(1)));                 // …but again after an hour, not a day
    Assert.True(WarmWaterController.RefitDue(t.AddDays(-2), t, t.AddHours(1)));        // an old fit, the last try failed
  }

  [Fact]
  public void AwaitsFirstFit_UntilTheFirstRefitFinished_SuccessOrNot()
  {
    var t = new DateTime(2026, 11, 7, 14, 0, 0);
    Assert.True(WarmWaterController.AwaitsFirstFit(null, attemptFinished: false));   // just started: no plan, no control
    Assert.False(WarmWaterController.AwaitsFirstFit(t, attemptFinished: true));      // learned
    Assert.False(WarmWaterController.AwaitsFirstFit(null, attemptFinished: true));   // failed: the default model, retried hourly
  }
}

