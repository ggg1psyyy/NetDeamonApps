using NetDeamon.apps;
using NetDeamon.apps.PVControl;
using NetDeamon.apps.PVControl.Simulator;
using Xunit;

namespace NetDeamonApps.Tests;

/// <summary>
/// The versioned inverter command (sensor.pv_control_inverter_command) HA applies in RAM (SolaX VPP
/// mode 8 via the *_direct entities) instead of EEPROM work-mode and charge-current writes; the
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

  static SimulationSlot Slot(DateTime t, InverterModes mode, int chargeWh) =>
    new(t, 30, new InverterState(mode, ForceChargeReasons.None), 0, 100, 0, chargeWh, 0, chargeWh, 0);

  [Fact]
  public void PlannedChargeWh_ContiguousChargeFromTheRunningSlot()
  {
    var t0 = new DateTime(2026, 10, 27, 0, 0, 0);
    SimulationSlot[] plan = [Slot(t0, InverterModes.force_charge, 1800), Slot(t0.AddMinutes(15), InverterModes.force_charge, 1800),
      Slot(t0.AddMinutes(30), InverterModes.normal, 0), Slot(t0.AddMinutes(45), InverterModes.force_charge, 1800)];
    Assert.Equal(3600, InverterCommand.PlannedChargeWh(plan, t0.AddMinutes(5)));   // the later charge after a gap doesn't count
    Assert.Equal(1800, InverterCommand.PlannedChargeWh(plan, t0.AddMinutes(20)));
    Assert.Equal(0, InverterCommand.PlannedChargeWh(plan, t0.AddMinutes(35)));
  }

  // SolaX: VPP charges the battery with at most ~5.1 kW (20–21 A, measured 2026-10-26 and in all
  // negative-price charges since spring); Manual Force Charge gives 30 A (7.4–7.6 kW). Bulk charges
  // take the Manual path, small top-ups VPP.
  [Theory]
  [InlineData(InverterModes.normal, false, 6000, false, true)]        // start, ≥ 50 % of 11.52 kWh = 5.76 kWh
  [InlineData(InverterModes.normal, false, 2000, false, false)]       // small top-up
  [InlineData(InverterModes.force_charge, true, 1000, false, true)]   // kept while the session lasts (energy shrinks)
  [InlineData(InverterModes.force_charge, false, 9000, false, false)] // a small session stays small
  [InlineData(InverterModes.force_charge, true, 4000, true, false)]   // the import cap throttles: off for now…
  public void BulkFor_DecidedAtTheStart_KeptForTheSession(InverterModes previous, bool sessionBulk, int plannedWh, bool throttled, bool expected)
  {
    var (bulk, session) = InverterCommand.BulkFor(InverterModes.force_charge, previous, sessionBulk, plannedWh, 11_520, 50, throttled);
    Assert.Equal(expected, bulk);
    if (previous == InverterModes.force_charge) Assert.Equal(sessionBulk, session); // …but the session stays bulk
  }

  [Fact]
  public void BulkFor_StartIsBulkFromHalfTheBattery_NeverOutsideForceCharge()
  {
    Assert.True(InverterCommand.BulkFor(InverterModes.force_charge, InverterModes.normal, false, 6000, 11_520, 50, false).Bulk);
    Assert.False(InverterCommand.BulkFor(InverterModes.force_charge_grid_only, InverterModes.normal, false, 9000, 11_520, 50, false).Bulk);
    Assert.False(InverterCommand.BulkFor(InverterModes.force_charge, InverterModes.normal, false, 9000, 11_520, 0, false).Bulk);
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
