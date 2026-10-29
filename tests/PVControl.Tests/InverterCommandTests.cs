using NetDeamon.apps;
using NetDeamon.apps.PVControl;
using NetDeamon.apps.PVControl.Simulator;
using Xunit;

namespace NetDeamonApps.Tests;

/// <summary>
/// The versioned inverter command (attributes of sensor.pv_control_mode) HA applies — remote-controlled
/// (SolaX VPP mode 8 via the *_direct entities, RAM) or for a bulk grid charge Manual Force Charge; the
/// reconciler compares its <c>seq</c> with the one it applied. Publishing and the HA side aren't unit tested.
/// </summary>
public class InverterCommandTests
{
  [Fact]
  public void Next_Unchanged_KeepsTheVersion()
  {
    var first = InverterCommand.Next(null, InverterModes.force_charge, 30, bulk: true, voltageV: 245);
    Assert.Equal((1, 7350), (first.Seq, first.BatteryPowerW));
    Assert.Equal(first, InverterCommand.Next(first, InverterModes.force_charge, 30, bulk: true, voltageV: 245));
  }

  [Fact]
  public void Next_ModeOrCurrentChanged_NewVersion()
  {
    var a = InverterCommand.Next(null, InverterModes.force_charge, 30, bulk: true, voltageV: 245);
    var b = InverterCommand.Next(a, InverterModes.force_charge, 28, bulk: false, voltageV: 245);   // throttled: no longer bulk
    var c = InverterCommand.Next(b, InverterModes.normal, 30, bulk: false, voltageV: 245);
    Assert.Equal((2, 6860, 3, 0), (b.Seq, b.BatteryPowerW, c.Seq, c.BatteryPowerW));
  }

  [Fact]
  public void Next_VoltageStepAlone_IsNotANewVersion()
  {
    // The measured battery voltage steps by 5 V with the SoC; a running charge isn't re-applied for that.
    // The next real change (the cap's limit) takes the voltage of that moment.
    var a = InverterCommand.Next(null, InverterModes.force_charge, 30, bulk: false, voltageV: 240);
    Assert.Equal(a, InverterCommand.Next(a, InverterModes.force_charge, 30, bulk: false, voltageV: 245));
    var b = InverterCommand.Next(a, InverterModes.force_charge, 28, bulk: false, voltageV: 245);
    Assert.Equal((2, 6860), (b.Seq, b.BatteryPowerW));
    // The battery held or on its own: the current doesn't matter.
    var hold = InverterCommand.Next(b, InverterModes.grid_only, 28, bulk: false, voltageV: 245);
    Assert.Equal(hold, InverterCommand.Next(hold, InverterModes.grid_only, 30, bulk: false, voltageV: 250));
  }

  [Fact]
  public void Next_AfterRestart_ContinuesTheVersion()
  {
    // HA still holds the version it applied before the restart: counting on avoids a false "in sync".
    Assert.Equal(58, InverterCommand.Next(null, InverterModes.normal, 30, bulk: false, voltageV: 245, restoredSeq: 57).Seq);
  }

  [Fact]
  public void FromPublished_RestoredCommand_UnchangedKeepsItsVersion()
  {
    // The sensor still holds the command from before the restart: the same command again isn't a new version
    // (every restart re-applied it to the inverter), a different one counts on from it.
    var restored = InverterCommand.FromPublished("force_charge", 7350, false, 57, voltageV: 245);
    Assert.Equal(new InverterCommand(InverterModes.force_charge, 7350, false, 57, 30), restored);
    Assert.Equal(57, InverterCommand.Next(restored, InverterModes.force_charge, 30, bulk: false, voltageV: 245, restoredSeq: 57).Seq);
    Assert.Equal(58, InverterCommand.Next(restored, InverterModes.normal, 30, bulk: false, voltageV: 245, restoredSeq: 57).Seq);
    Assert.Null(InverterCommand.FromPublished("unavailable", 0, false, 57, 245));
    Assert.Null(InverterCommand.FromPublished("grid_only", null, false, 57, 245));
  }

  [Theory]
  [InlineData(InverterModes.force_charge, 28.0, 7000)]           // grid charging at the import cap's limit
  [InlineData(InverterModes.force_charge_grid_only, 30.0, 7500)] // negative price: the hardware maximum
  [InlineData(InverterModes.force_discharge, 30.0, -7500)]       // discharging at the maximum
  [InlineData(InverterModes.grid_only, 30.0, 0)]                 // battery held
  [InlineData(InverterModes.normal, 30.0, 0)]                    // the inverter's own control
  public void PowerFor_ByMode(InverterModes mode, double currentA, int expectedW) =>
    Assert.Equal(expectedW, InverterCommand.PowerFor(mode, currentA, voltageV: 250));

  static SimulationSlot Slot(DateTime t, int soc, InverterModes mode, int chargeWh) =>
    new(t, soc, new InverterState(mode, ForceChargeReasons.None), 0, 100, 0, chargeWh, 0, chargeWh, 0);

  [Fact]
  public void NextCharge_RunningOrNext_EnergyTargetSocAndEnd()
  {
    var t0 = new DateTime(2026, 10, 27, 0, 0, 0);
    SimulationSlot[] plan = [Slot(t0, 30, InverterModes.force_charge, 1150), Slot(t0.AddMinutes(15), 40, InverterModes.force_charge, 1150),
      Slot(t0.AddMinutes(30), 50, InverterModes.normal, 0), Slot(t0.AddMinutes(45), 49, InverterModes.force_charge, 1150),
      Slot(t0.AddMinutes(60), 59, InverterModes.normal, 0)];

    var running = InverterCommand.NextCharge(plan, t0.AddMinutes(5), 11_520)!.Value;
    Assert.Equal((2300, 50, t0, t0.AddMinutes(30), ForceChargeReasons.None), (running.EnergyWh, running.TargetSocPercent, running.Start, running.End, running.Reason));
    // Between charges: the next one (the SoC after it from the slot that follows).
    var next = InverterCommand.NextCharge(plan, t0.AddMinutes(35), 11_520)!.Value;
    Assert.Equal((1150, 59, t0.AddMinutes(45)), (next.EnergyWh, next.TargetSocPercent, next.Start));
    Assert.Null(InverterCommand.NextCharge(plan, t0.AddMinutes(61), 11_520));
  }

  [Fact]
  public void SteadyTarget_JitterBelowTheStep_KeepsTheShownValue()
  {
    // Live 2026-10-28 night: the 02:00 charge's target moved 56–59 % between 15-s cycles.
    var now = new DateTime(2026, 10, 28, 22, 0, 0);
    var end = new DateTime(2026, 10, 29, 3, 0, 0);
    PlannedCharge Charge(int target, DateTime? until = null) => new(4000, target, end.AddHours(-1), until ?? end);
    var shown = InverterCommand.SteadyTarget(null, Charge(57), now);
    var seen = new List<int>();
    foreach (int t in new[] { 58, 56, 57, 58, 56 })
      seen.Add((shown = InverterCommand.SteadyTarget(shown, Charge(t), now))!.Value.TargetSocPercent);
    Assert.Equal([57, 57, 57, 57, 57], seen);
    Assert.Equal(60, InverterCommand.SteadyTarget(shown, Charge(60), now)!.Value.TargetSocPercent);                 // a real change: at once
    Assert.Equal(58, InverterCommand.SteadyTarget(shown, Charge(58, end.AddDays(1)), end.AddMinutes(1))!.Value.TargetSocPercent); // the next charge
    Assert.Null(InverterCommand.SteadyTarget(shown, null, now));
  }

  [Fact]
  public void NextCharge_EndsWithThePlan_TargetFromItsEnergy()
  {
    var t0 = new DateTime(2026, 10, 27, 23, 45, 0);
    var charge = InverterCommand.NextCharge([Slot(t0, 30, InverterModes.force_charge, 1152)], t0, 11_520)!.Value;
    Assert.Equal(40, charge.TargetSocPercent);   // 30 % + 1152 Wh of 11.52 kWh
  }

  // The plan chooses the path when a charge starts (EnergySimulator.ChargesBulk); live a forced charge is
  // always bulk. The import cap doesn't change the path (live 2026-10-29 02:15–02:42: a 0.1-A cut switched
  // bulk → VPP, 7.5 → 4.9 kW, which cleared the cut — 32 work-mode changes); a bulk charge follows it with its current.
  [Theory]
  [InlineData(InverterModes.force_charge, true, false, true)]
  [InlineData(InverterModes.force_charge, false, false, false)]
  [InlineData(InverterModes.force_charge, false, true, true)]            // user override
  [InlineData(InverterModes.force_charge_grid_only, true, false, false)] // negative price: always VPP
  [InlineData(InverterModes.normal, true, true, false)]
  public void BulkFor_PlanOrForced(InverterModes mode, bool planBulk, bool forced, bool expected) =>
    Assert.Equal(expected, InverterCommand.BulkFor(mode, planBulk, forced));

  [Fact]
  public void HoldMinimumRun_StartedChargeRunsAMinute_UserChangesAtOnce()
  {
    var now = new DateTime(2026, 10, 27, 1, 27, 30);
    var charging = new InverterState(InverterModes.force_charge, ForceChargeReasons.GoingUnderAbsoluteMinima);
    var normal = new InverterState(InverterModes.normal);
    // Live 2026-10-27 01:31:15–01:31:30: started and stopped within 15 s.
    Assert.Equal(InverterModes.force_charge, InverterCommand.HoldMinimumRun(normal, charging, now.AddSeconds(-15), now).Mode);
    Assert.Equal(InverterModes.normal, InverterCommand.HoldMinimumRun(normal, charging, now.AddSeconds(-60), now).Mode);
    Assert.Equal(InverterModes.normal, InverterCommand.HoldMinimumRun(new InverterState(InverterModes.normal, ForceChargeReasons.UserMode), charging, now.AddSeconds(-5), now).Mode);
    Assert.Equal(InverterModes.force_charge, InverterCommand.HoldMinimumRun(charging, normal, now.AddSeconds(-5), now).Mode); // starting isn't delayed
  }

  [Fact]
  public void Health_OutOfSyncDegradedAfterTwoMinutes_BlockedError()
  {
    var now = new DateTime(2026, 10, 26, 10, 0, 0);
    Assert.Empty(HealthChecks.InverterSync(false, now.AddHours(-1), true, now));
    Assert.Empty(HealthChecks.InverterSync(true, null, false, now));
    Assert.Empty(HealthChecks.InverterSync(true, now.AddSeconds(-90), false, now));
    Assert.Equal(HealthLevel.degraded, Assert.Single(HealthChecks.InverterSync(true, now.AddMinutes(-3), false, now)).Level);
    Assert.Equal(HealthLevel.error, Assert.Single(HealthChecks.InverterSync(true, null, true, now)).Level);
  }
}
