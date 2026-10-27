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
    var first = InverterCommand.Next(null, InverterModes.force_charge, 7350, bulk: true);
    Assert.Equal(1, first.Seq);
    Assert.Equal(first, InverterCommand.Next(first, InverterModes.force_charge, 7350, bulk: true));
  }

  [Fact]
  public void Next_ModeOrPowerChanged_NewVersion()
  {
    var a = InverterCommand.Next(null, InverterModes.force_charge, 7350, bulk: true);
    var b = InverterCommand.Next(a, InverterModes.force_charge, 6860, bulk: false);   // throttled: no longer bulk
    var c = InverterCommand.Next(b, InverterModes.normal, 0, bulk: false);
    Assert.Equal((2, 3), (b.Seq, c.Seq));
  }

  [Fact]
  public void Next_AfterRestart_ContinuesTheVersion()
  {
    // HA still holds the version it applied before the restart: counting on avoids a false "in sync".
    Assert.Equal(58, InverterCommand.Next(null, InverterModes.normal, 0, bulk: false, restoredSeq: 57).Seq);
  }

  [Theory]
  [InlineData(InverterModes.force_charge, 28.0, 7000)]           // grid charging at the import cap's limit
  [InlineData(InverterModes.force_charge_grid_only, 30.0, 7500)] // negative price: at most the hardware maximum
  [InlineData(InverterModes.force_charge, 35.0, 7500)]           // never above the maximum
  [InlineData(InverterModes.force_discharge, 12.0, -7500)]       // discharging at the maximum
  [InlineData(InverterModes.grid_only, 30.0, 0)]                 // battery held
  [InlineData(InverterModes.normal, 30.0, 0)]                    // the inverter's own control
  public void PowerFor_ByMode(InverterModes mode, double limitA, int expectedW) =>
    Assert.Equal(expectedW, InverterCommand.PowerFor(mode, limitA, maxA: 30, voltageV: 250));

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
    Assert.Equal((2300, 50, t0, t0.AddMinutes(30)), (running.EnergyWh, running.TargetSocPercent, running.Start, running.End));
    // Between charges: the next one (the SoC after it from the slot that follows).
    var next = InverterCommand.NextCharge(plan, t0.AddMinutes(35), 11_520)!.Value;
    Assert.Equal((1150, 59, t0.AddMinutes(45)), (next.EnergyWh, next.TargetSocPercent, next.Start));
    Assert.Null(InverterCommand.NextCharge(plan, t0.AddMinutes(61), 11_520));
  }

  [Fact]
  public void NextCharge_EndsWithThePlan_TargetFromItsEnergy()
  {
    var t0 = new DateTime(2026, 10, 27, 23, 45, 0);
    var charge = InverterCommand.NextCharge([Slot(t0, 30, InverterModes.force_charge, 1152)], t0, 11_520)!.Value;
    Assert.Equal(40, charge.TargetSocPercent);   // 30 % + 1152 Wh of 11.52 kWh
  }

  // The plan chooses the path when a charge starts (EnergySimulator.ChargesBulk); live a forced
  // charge is always bulk, a throttled one never (the remote-controlled path follows the cap).
  [Theory]
  [InlineData(InverterModes.force_charge, true, false, false, true)]
  [InlineData(InverterModes.force_charge, false, false, false, false)]
  [InlineData(InverterModes.force_charge, false, true, false, true)]           // user override
  [InlineData(InverterModes.force_charge, true, true, true, false)]            // import cap throttles
  [InlineData(InverterModes.force_charge_grid_only, true, false, false, false)] // negative price: always VPP
  [InlineData(InverterModes.normal, true, true, false, false)]
  public void BulkFor_PlanForcedThrottled(InverterModes mode, bool planBulk, bool forced, bool throttled, bool expected) =>
    Assert.Equal(expected, InverterCommand.BulkFor(mode, planBulk, forced, throttled));

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
