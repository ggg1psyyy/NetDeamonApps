using NetDeamon.apps;
using NetDeamon.apps.PVControl;
using NetDeamon.apps.PVControl.Optimizer;
using NetDeamon.apps.PVControl.Simulator;
using System;
using System.Linq;
using Xunit;

namespace NetDeamonApps.Tests;

/// <summary>
/// After a restart the planners continue from the state that ran (<see cref="InverterCommand.RunningStateFromPublished"/>
/// from sensor.pv_control_mode). Live 2026-11-09 23:27: a restart during the night's bulk charge (25 A next to the EV)
/// started both planners from "normal" — the first DP plan saw no charge running, chose VPP, and the charge went on at
/// 3.9 kW. Reading the sensor at startup (PVControl.RestoreInverterCommand) needs live HA.
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
}
