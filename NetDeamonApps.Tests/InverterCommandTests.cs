using NetDeamon.apps;
using NetDeamon.apps.PVControl;
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
    var first = InverterCommand.Next(null, InverterModes.force_charge, 7350);
    Assert.Equal(1, first.Seq);
    Assert.Equal(first, InverterCommand.Next(first, InverterModes.force_charge, 7350));
  }

  [Fact]
  public void Next_ModeOrPowerChanged_NewVersion()
  {
    var a = InverterCommand.Next(null, InverterModes.force_charge, 7350);
    var b = InverterCommand.Next(a, InverterModes.force_charge, 6860);
    var c = InverterCommand.Next(b, InverterModes.normal, 0);
    Assert.Equal((2, 3), (b.Seq, c.Seq));
  }

  [Fact]
  public void Next_AfterRestart_ContinuesTheVersion()
  {
    // HA still holds the version it applied before the restart: counting on avoids a false "in sync".
    Assert.Equal(58, InverterCommand.Next(null, InverterModes.normal, 0, restoredSeq: 57).Seq);
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
