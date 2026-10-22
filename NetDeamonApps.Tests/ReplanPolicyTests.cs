using NetDeamon.apps.PVControl.Managers;
using NetDeamon.apps.PVControl.Simulator;
using System;
using Xunit;

namespace NetDeamonApps.Tests;

/// <summary>
/// When a schedulable load's window search runs again (ReplanPolicy) and the house-load
/// deviation trigger. The wiring into HouseEnergy.RunSimulation / PVControl needs live HA.
/// </summary>
public class ReplanPolicyTests
{
  static readonly DateTime T0 = new(2026, 10, 18, 18, 1, 0);
  static readonly ReplanSnapshot Plan = new(T0, HouseSoc: 60, Level: 20, Drawing: true);

  [Fact]
  public void NoPlanYet_Replans() =>
    Assert.Equal("first plan", ReplanPolicy.Reason(null, Plan, 5, null));

  [Fact]
  public void NothingChanged_WithinInterval_KeepsDecision() =>
    Assert.Null(ReplanPolicy.Reason(Plan, Plan with { Time = T0.AddMinutes(4) }, 5, null));

  [Fact]
  public void IntervalElapsed_Replans() =>
    Assert.Equal("interval", ReplanPolicy.Reason(Plan, Plan with { Time = T0.AddMinutes(5) }, 5, null));

  [Fact]
  public void IntervalIsConfigurable() =>
    Assert.Null(ReplanPolicy.Reason(Plan, Plan with { Time = T0.AddMinutes(9) }, 10, null));

  [Fact]
  public void Request_Replans() =>
    Assert.Equal("user changed x", ReplanPolicy.Reason(Plan, Plan with { Time = T0.AddSeconds(15) }, 5, "user changed x"));

  [Theory]
  [InlineData(0, 61, 20, true, "house battery SoC changed")]
  [InlineData(0, 60, 21, true, "level changed")]
  [InlineData(0, 60, 20, false, "stopped drawing power")]
  [InlineData(14, 60, 20, true, "new quarter-hour")] // 18:01 → 18:15
  public void Triggers_Replan(int addMinutes, int soc, float level, bool drawing, string expected) =>
    Assert.Equal(expected, ReplanPolicy.Reason(Plan, new ReplanSnapshot(T0.AddMinutes(addMinutes), soc, level, drawing), 60, null));

  [Fact]
  public void LatchEnded_Replans() =>
    Assert.Equal("latch ended", ReplanPolicy.Reason(Plan with { Latched = true }, Plan with { Time = T0.AddMinutes(1) }, 5, null));

  // ── Min-off latch ────────────────────────────────────────────────────────────────────

  [Fact]
  public void OffLatch_HoldsForMinOffMinutes()
  {
    // Live 2026-10-18: stopped 17:22:30, restarted one minute later.
    var stop = new DateTime(2026, 10, 18, 17, 22, 30);
    Assert.Equal(stop.AddMinutes(15), SchedulableLoadRuntime.OffLatchUntil(stop, 15, stop.AddMinutes(1), true));
    Assert.Null(SchedulableLoadRuntime.OffLatchUntil(stop, 15, stop.AddMinutes(15), true));
  }

  [Theory]
  [InlineData(false, 15)] // mode or settings changed since the stop
  [InlineData(true, 0)]   // disabled
  public void OffLatch_BypassedOrDisabled(bool sameModeAndSettings, int minOff)
  {
    var stop = new DateTime(2026, 10, 18, 17, 22, 30);
    Assert.Null(SchedulableLoadRuntime.OffLatchUntil(stop, minOff, stop.AddMinutes(1), sameModeAndSettings));
  }

  [Fact]
  public void OffLatch_NoRecordedStop_DoesNotHold() =>
    Assert.Null(SchedulableLoadRuntime.OffLatchUntil(null, 15, T0, true));

  [Fact]
  public void DelayUntil_MovesEarlierSessionsKeepingTheirLength()
  {
    var notBefore = new DateTime(2026, 10, 18, 17, 45, 0);
    var early = new ExtraLoad { Name = "EV", StartTime = new DateTime(2026, 10, 18, 17, 15, 0), EndTime = new DateTime(2026, 10, 18, 19, 15, 0), PowerW = 1850 };
    var later = new ExtraLoad { Name = "EV", StartTime = new DateTime(2026, 10, 18, 23, 0, 0), EndTime = new DateTime(2026, 10, 19, 1, 0, 0), PowerW = 1850 };

    var moved = SchedulableLoadRuntime.DelayUntil([early, later], notBefore);

    Assert.Equal(notBefore, moved[0].StartTime);
    Assert.Equal(notBefore.AddHours(2), moved[0].EndTime);
    Assert.Same(later, moved[1]);
  }

  // ── LoadDeviationTracker ─────────────────────────────────────────────────────────────

  [Fact]
  public void Deviation_FiresAfterFiveMinutes_ThenStartsOver()
  {
    var d = new LoadDeviationTracker();
    Assert.False(d.Observe(T0, 1500, 500));                  // 1000 W over: starts the window
    Assert.False(d.Observe(T0.AddMinutes(4), 1500, 500));
    Assert.True(d.Observe(T0.AddMinutes(5), 1500, 500));     // fires
    Assert.False(d.Observe(T0.AddMinutes(6), 1500, 500));    // new window
    Assert.True(d.Observe(T0.AddMinutes(10), 1500, 500));
  }

  [Fact]
  public void Deviation_BackOnForecast_Resets()
  {
    var d = new LoadDeviationTracker();
    d.Observe(T0, 1500, 500);
    d.Observe(T0.AddMinutes(4), 600, 500);                  // back within 500 W
    Assert.False(d.Observe(T0.AddMinutes(5), 1500, 500));
  }

  [Theory]
  [InlineData(900, 500)]   // 400 W off: under 500 W
  [InlineData(2600, 2000)] // 600 W off but only 30 % of the forecast
  public void Deviation_SmallOrRelativelySmall_Ignored(int actual, int predicted)
  {
    var d = new LoadDeviationTracker();
    d.Observe(T0, actual, predicted);
    Assert.False(d.Observe(T0.AddMinutes(10), actual, predicted));
  }

  // Between searches the latch counter in the reason is counted live for display.
  // Live 2026-10-22: "Latched (EV Charger: 1/15 min min-on)" stayed from 15:15 until the next search.

  [Fact]
  public void LatchMinutes_MinOn_CountedFromSessionStart()
  {
    var start = new DateTime(2026, 10, 22, 15, 14, 30);
    var reason = "Latched (EV Charger: 1/15 min min-on)";
    Assert.Equal("Latched (EV Charger: 4/15 min min-on)", SchedulableLoadRuntime.WithLiveLatchMinutes(reason, start.AddMinutes(4), start, null));
    Assert.Equal("Latched (EV Charger: 15/15 min min-on)", SchedulableLoadRuntime.WithLiveLatchMinutes(reason, start.AddMinutes(40), start, null)); // capped
  }

  [Fact]
  public void LatchMinutes_MinOff_CountedFromStop_RestOfTheTextKept()
  {
    var stop = new DateTime(2026, 10, 22, 15, 0, 0);
    var reason = "Off-latched (EV Charger: 2/10 min min-off) — Charging (Priority EV Charger)";
    Assert.Equal("Off-latched (EV Charger: 7/10 min min-off) — Charging (Priority EV Charger)",
      SchedulableLoadRuntime.WithLiveLatchMinutes(reason, stop.AddMinutes(7), null, stop));
  }

  [Fact]
  public void LatchMinutes_NoLatchOrNoTime_Unchanged()
  {
    var now = new DateTime(2026, 10, 22, 15, 0, 0);
    Assert.Equal("No valid window (Priority EV Charger: 29%)", SchedulableLoadRuntime.WithLiveLatchMinutes("No valid window (Priority EV Charger: 29%)", now, now, now));
    Assert.Equal("Latched (EV Charger: 1/15 min min-on)", SchedulableLoadRuntime.WithLiveLatchMinutes("Latched (EV Charger: 1/15 min min-on)", now, null, null));
  }
}
