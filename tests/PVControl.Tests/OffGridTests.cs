using NetDeamon.apps.PVControl;
using NetDeamon.apps.PVControl.OffGrid;
using NetDeamon.apps.PVControl.WarmWater;
using Xunit;

namespace NetDeamonApps.Tests;

/// <summary>
/// The inverter's off-grid phases (<see cref="OffGridTracker"/>): the EV and planned warm-water runs pause after 5 min
/// off-grid and resume after 5 min on-grid again; health degraded meanwhile. Reading the grid status and publishing
/// (PVControl.ObserveOffGrid, the loads' charge_now) read live HA and aren't unit tested.
/// </summary>
public class OffGridTests : TestBase
{
  static readonly DateTime T0 = new(2026, 11, 23, 3, 34, 56);
  static readonly TimeSpan Delay = TimeSpan.FromMinutes(5);

  [Fact]
  public void AShortDip_NeverPauses()
  {
    // Live 2026-11-23 03:34:56–03:35:04: the inverter tripped on a grid voltage dip, 8 s off-grid.
    var t = new OffGridTracker();
    Assert.False(t.Observe(T0, true, Delay));
    Assert.Equal(T0, t.OffGridSince);
    Assert.False(t.Observe(T0.AddSeconds(8), false, Delay));
    Assert.False(t.Paused);
    Assert.Null(t.OffGridSince);
  }

  [Fact]
  public void FiveMinutesOffGrid_Pauses_FiveMinutesOnGrid_Resumes()
  {
    var t = new OffGridTracker();
    t.Observe(T0, true, Delay);
    Assert.False(t.Observe(T0.AddMinutes(4.9), true, Delay));
    Assert.True(t.Observe(T0.AddMinutes(5), true, Delay));
    Assert.True(t.Paused);
    Assert.Equal("off-grid since 03:34", t.Reason);
    var back = T0.AddMinutes(20);
    Assert.False(t.Observe(back, false, Delay));                    // back, not yet stable
    Assert.True(t.Paused);
    Assert.StartsWith("off-grid, back on-grid since", t.Reason);
    Assert.False(t.Observe(back.AddMinutes(4.9), false, Delay));
    Assert.True(t.Observe(back.AddMinutes(5), false, Delay));
    Assert.False(t.Paused);
    Assert.Null(t.Reason);
  }

  [Fact]
  public void AFlickeringGrid_StaysPaused_TheOnGridTimeStartsAgain()
  {
    var t = new OffGridTracker();
    t.Observe(T0, true, Delay);
    t.Observe(T0.AddMinutes(6), true, Delay);
    t.Observe(T0.AddMinutes(7), false, Delay);                      // back for 3 min
    t.Observe(T0.AddMinutes(10), true, Delay);                      // gone again
    Assert.True(t.Paused);
    t.Observe(T0.AddMinutes(11), false, Delay);
    Assert.False(t.Observe(T0.AddMinutes(15), false, Delay));       // 4 min since the last return
    Assert.True(t.Observe(T0.AddMinutes(16), false, Delay));
  }

  [Fact]
  public void Unknown_KeepsTheState_ARestartCountsFromTheRealStart()
  {
    var t = new OffGridTracker();
    t.Observe(T0, true, Delay);
    t.Observe(T0.AddMinutes(6), true, Delay);
    Assert.False(t.Observe(T0.AddMinutes(7), null, Delay));         // the status unavailable: no change
    Assert.True(t.Paused);
    // after a start: off-grid since 03:31 (the status's last change) → paused at 03:36, not 5 min after the start
    var r = new OffGridTracker();
    r.Seed(true, T0.AddMinutes(-4));
    Assert.True(r.Observe(T0.AddMinutes(1), true, Delay));
    var on = new OffGridTracker();
    on.Seed(false, T0.AddMinutes(-4));
    Assert.Null(on.OffGridSince);
  }

  [Theory]
  [InlineData("OffGrid", true)]
  [InlineData("offgrid", true)]          // case doesn't matter
  [InlineData("OnGrid", false)]
  [InlineData("Checking", false)]        // any state not in the list: on-grid (after a trip the loads run from the grid)
  [InlineData("unavailable", null)]
  [InlineData("unknown", null)]
  [InlineData(null, null)]
  public void IsOffGrid_TheStatusText(string? state, bool? expected) =>
    Assert.Equal(expected, OffGridTracker.IsOffGrid(state, ["OffGrid"]));

  [Fact]
  public void Health_DegradedWhileOffGrid_AndWhileStillPaused()
  {
    Assert.Empty(HealthChecks.OffGrid(null, null));
    var off = Assert.Single(HealthChecks.OffGrid(T0, null));
    Assert.Equal(HealthLevel.degraded, off.Level);
    Assert.Contains("off-grid since 03:34", off.Message);
    Assert.Contains("paused", Assert.Single(HealthChecks.OffGrid(T0, "off-grid since 03:34")).Message);
    Assert.Contains("stay paused", Assert.Single(HealthChecks.OffGrid(null, "off-grid, back on-grid since 03:55")).Message);
  }

  // ── Warm water off-grid: no planned start; a running heating finishes, the boost is allowed ──

  static readonly DateTime Now = new(2026, 11, 23, 17, 0, 0);
  static readonly PlannedRun Run = new(Now.AddMinutes(-5), Now.AddMinutes(25), 38, 44.3, 1700);

  static ControlInput Input(double temp = 38, bool switchOn = false, bool active = false, bool boost = false, string? offGrid = "off-grid since 16:50") =>
    new(Now, temp, switchOn, active, 57, null, null, WarmWaterMode.Auto, [Run], 57, boost, 45, boost ? Now.AddMinutes(-1) : null, false, 180,
      OffGridReason: offGrid);

  [Fact]
  public void WarmWater_OffGrid_NoPlannedStart()
  {
    Assert.Equal(ControlCommand.Start, WarmWaterControl.Decide(Input(offGrid: null)).Command);
    var d = WarmWaterControl.Decide(Input());
    Assert.Equal(ControlCommand.None, d.Command);
    Assert.Contains("off-grid since 16:50", d.Reason);
  }

  [Fact]
  public void WarmWater_OffGrid_ARunningHeatingAndTheBoost_AsBefore()
  {
    Assert.Equal(ControlCommand.Start, WarmWaterControl.Decide(Input(boost: true)).Command);            // the boost starts
    Assert.NotEqual(ControlCommand.Stop, WarmWaterControl.Decide(Input(switchOn: true, active: true)).Command);   // a running heating isn't stopped
  }
}
