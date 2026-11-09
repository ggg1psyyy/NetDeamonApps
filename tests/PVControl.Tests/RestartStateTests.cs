using NetDeamon.apps;
using NetDeamon.apps.PVControl;
using NetDeamon.apps.PVControl.ImportCap;
using NetDeamon.apps.PVControl.Managers;
using NetDeamon.apps.PVControl.Optimizer;
using NetDeamon.apps.PVControl.WarmWater;
using NetDeamon.apps.PVControl.Simulator;
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace NetDeamonApps.Tests;

/// <summary>
/// After a restart the planners continue from the state that ran (<see cref="InverterCommand.RunningStateFromPublished"/>
/// from sensor.pv_control_mode). Live 2026-11-09 23:27: a restart during the night's bulk charge (25 A next to the EV)
/// started both planners from "normal" — the first DP plan saw no charge running, chose VPP, and the charge went on at
/// 3.9 kW. Likewise a load's session (<see cref="LoadSessionState"/>), the warm-water control's timers
/// (<see cref="WarmWaterControlState"/>) and the running import quarter (<see cref="QuarterImport.Seed"/>, from HA
/// history). Reading the sensors and the history at startup (PVControl, WarmWaterController) needs live HA.
/// </summary>
public class RestartStateTests
{
  [Fact]
  public void Published_ThePlansOwnState_WithItsPath()
  {
    var charge = Assert.NotNull(InverterCommand.RunningStateFromPublished("force_charge", "AbsoluteMinimum", true, true, true));
    Assert.Equal((InverterModes.force_charge, ForceChargeReasons.AbsoluteMinimum, true), (charge.Mode, charge.ModeReason, charge.Bulk));
    var hold = Assert.NotNull(InverterCommand.RunningStateFromPublished("grid_only", "SavedForLater", true, true, false));
    Assert.Equal((InverterModes.grid_only, false, false), (hold.Mode, hold.Bulk, hold.ExportAllowed));   // only a charge has a path
    Assert.Equal(InverterModes.normal, InverterCommand.RunningStateFromPublished("normal", null, false, true, true)!.Value.Mode);
  }

  [Theory]
  [InlineData("force_charge", "UserMode")]      // the user override isn't the plan's state
  [InlineData("force_discharge", "BugFixMode")] // nor the bug-fix kick
  [InlineData("unknown", null)]
  [InlineData(null, null)]
  public void Published_NotThePlans_StartsAsBefore(string? mode, string? reason) =>
    Assert.Null(InverterCommand.RunningStateFromPublished(mode, reason, true, true, true));

  [Fact]
  public void RestartDuringABulkCharge_TheDpKeepsBulk()
  {
    // 23:27:30, 49 %, the EV from the grid since 23:00: the DP plans from the restored state.
    var snap = ForecastSnapshot.FromBytes(System.IO.File.ReadAllBytes(
      System.IO.Path.Combine(AppContext.BaseDirectory, "Data", "forecast_snapshot_2026-11-09_2215.json.gz")));
    var now = new DateTime(2026, 11, 9, 23, 27, 30);
    var ev = new ExtraLoad { Name = "EV Charger", StartTime = new DateTime(2026, 11, 9, 23, 0, 0), EndTime = new DateTime(2026, 11, 10, 1, 42, 0), PowerW = 1839, FromGrid = true };
    var input = (snap with { StartTime = now, StartSocPercent = 49, CurrentMode = InverterModes.normal }).ToInput().WithExtraLoads([ev]);
    var greedy = EnergySimulator.Simulate(input);
    var running = InverterCommand.RunningStateFromPublished("force_charge", "AbsoluteMinimum", true, true, true)!.Value;
    var dp = BatteryDpPlanner.Plan(DpSteering.RunningInput(input, running), new DpOptions());
    var (timeline, effective, _) = DpSteering.Choose(Planner.dp, dp, now, now, greedy, running);
    Assert.Equal(Planner.dp, effective);
    Assert.NotNull(timeline);
    var first = timeline[0].State;
    Assert.Equal(InverterModes.force_charge, first.Mode);
    Assert.True(first.Bulk, "the running bulk charge keeps its path");
  }

  static readonly DateTime Now = new(2026, 11, 9, 23, 27, 30);

  static SchedulableLoadRuntime Ev() => new(new SchedulableLoadConfig { Name = "EV Charger", Priority = 10, MinWindowMinutes = 15, MinOffMinutes = 15 });

  static LoadSessionState Through(LoadSessionState s) => RestoreState.FromJson<LoadSessionState>(RestoreState.ToJson(s))!;

  [Fact]
  public void LoadSession_RunningGoesOnAsRunning_WithItsLatchAndSessions()
  {
    var published = new LoadSessionState(true, Now.AddMinutes(-27), LoadSchedulingMode.PriorityPlus, null, null,
      [new SessionWindow(Now.AddMinutes(-27), Now.AddMinutes(135), 1839, true)]);
    var after = Ev();
    Through(published).ApplyTo(after, Now);
    Assert.True(after.ChargeNow);
    Assert.Equal(Now.AddMinutes(-27), after.SessionStartTime);
    Assert.Equal(LoadSchedulingMode.PriorityPlus, after.SessionStartMode);
    var s = Assert.Single(after.ExtraLoads);
    Assert.Equal((Now.AddMinutes(135), 1839, true, "EV Charger"), (s.EndTime, s.PowerW, s.FromGrid, s.Name));
    Assert.Equal(Now.AddMinutes(135), after.PredictedEnd);
    Assert.True(after.TakeSettingsRestore());    // the latches' settings: today's, at the first plan
    Assert.False(after.TakeSettingsRestore());
    // published again as it came back
    var again = LoadSessionState.Of(after);
    Assert.Equal((published.ChargeNow, published.StartTime, published.StartMode), (again.ChargeNow, again.StartTime, again.StartMode));
    Assert.Equal(published.Sessions, again.Sessions);
  }

  [Fact]
  public void LoadSession_EndedWhileDown_StoppedAtItsEnd_ForTheMinOff()
  {
    var published = new LoadSessionState(true, Now.AddHours(-2), LoadSchedulingMode.PriorityPlus, null, null,
      [new SessionWindow(Now.AddHours(-2), Now.AddMinutes(-5), 1839, false)]);
    var after = Ev();
    Through(published).ApplyTo(after, Now);
    Assert.False(after.ChargeNow);
    Assert.Empty(after.ExtraLoads);
    Assert.Null(after.SessionStartTime);
    Assert.Equal(Now.AddMinutes(-5), after.SessionStopTime);   // min-off from its end
    Assert.Equal(LoadSchedulingMode.PriorityPlus, after.SessionStopMode);
  }

  [Fact]
  public void LoadSession_PlannedLaterStart_KeptNotRunning()
  {
    var published = new LoadSessionState(false, null, null, Now.AddMinutes(-40), LoadSchedulingMode.PriorityPlus,
      [new SessionWindow(Now.AddMinutes(33), Now.AddHours(3), 1839, true)]);
    var after = Ev();
    Through(published).ApplyTo(after, Now);
    Assert.False(after.ChargeNow);
    Assert.Equal(Now.AddMinutes(33), Assert.Single(after.ExtraLoads).StartTime);   // the sticky planned start
    Assert.Equal(Now.AddMinutes(-40), after.SessionStopTime);
  }

  [Theory]
  [InlineData(null)]
  [InlineData("")]
  [InlineData("not json")]
  [InlineData("{\"Other\":1}")]
  public void RestoreState_UnreadableOrOld_StartsAsBefore(string? json)
  {
    var state = RestoreState.FromJson<LoadSessionState>(json);
    if (state is null) return;
    var load = Ev();
    state.ApplyTo(load, Now);   // an object of another shape: nothing to restore, and no failure at startup
    Assert.False(load.ChargeNow);
    Assert.Empty(load.ExtraLoads);
  }

  [Fact]
  public void WarmWaterControl_TimersAndBoost_RoundTrip()
  {
    var state = new WarmWaterControlState(Now.AddMinutes(-4), Now.AddMinutes(-4), Now.AddMinutes(-70), true, Now.AddMinutes(-10), true);
    var back = RestoreState.FromJson<WarmWaterControlState>(RestoreState.ToJson(state));
    Assert.Equal(state, back);
    var ww = new WarmWaterController(new WarmWaterConfig());
    ww.RestoreControl(back!);
    Assert.Equal(state, ww.ControlState);
    Assert.True(ww.BoostOn);
  }

  [Fact]
  public void ImportQuarter_SeededFromHistory_KnowsWhatItImported()
  {
    // Restart at 23:27:30 during the charge: the meter read 1000.000 kWh at 23:15 (history), 1001.62 now.
    var history = new List<(DateTime, string?)> { (Now.AddMinutes(-13.5), "999.950"), (Now.AddMinutes(-12.6), "1000.000"),
      (Now.AddMinutes(-12.4), "unavailable"), (Now.AddMinutes(-11), "1000.100") };
    double? atStart = QuarterImport.CounterAt(history, new DateTime(2026, 11, 9, 23, 15, 0), 1);
    Assert.Equal(1000.000, atStart!.Value, 3);
    Assert.Equal(1000.0, QuarterImport.CounterAt([(Now.AddMinutes(-13), "1000000")], new DateTime(2026, 11, 9, 23, 15, 0), 0.001)!.Value, 3);   // Wh → kWh
    Assert.Null(QuarterImport.CounterAt([(Now.AddMinutes(-5), "1000.5")], new DateTime(2026, 11, 9, 23, 15, 0), 1));   // nothing before the start

    var q = new QuarterImport(2);
    q.Seed(Now, [atStart, null]);
    q.Update(Now, [1001.620, null]);
    Assert.True(q.Complete);
    Assert.Equal(1620, q.ImportedWh!.Value, 0);
    // The quarter finishes and counts (for the month's peak): 23:15–23:30.
    var done = q.Update(new DateTime(2026, 11, 9, 23, 30, 5), [1002.200, null]);
    Assert.Equal(new DateTime(2026, 11, 9, 23, 15, 0), done!.Value.Start);
    Assert.Equal(2.2 * 4 * 900 / 905, done.Value.AvgKw, 3);
  }

  [Fact]
  public void ImportQuarter_NoHistory_AsBefore()
  {
    var q = new QuarterImport(1);
    q.Seed(Now, [null]);
    q.Update(Now, [1001.6]);
    Assert.False(q.Complete);
  }
}
