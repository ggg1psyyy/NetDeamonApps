using NetDeamon.apps.PVControl;
using Xunit;

namespace NetDeamonApps.Tests;

/// <summary>
/// HouseEnergy.IsReturnToNormalMode — when the inverter reset signal is sent. Live 2026-10-19:
/// sensor.solax_run_mode went Normal Mode → unknown (14:03:20) → Normal Mode (14:03:34) during a
/// SolaX integration dropout; "unknown" isn't stored, so the handler saw Normal → Normal and sent
/// 2 needless reset signals.
/// </summary>
public class InverterRunModeTests : TestBase
{
  const string Normal = "Normal Mode";

  [Fact]
  public void SameModeAfterSensorDropout_NoReset() =>
    Assert.False(HouseEnergy.IsReturnToNormalMode(Normal, Normal, Normal));

  [Theory]
  [InlineData("Manual Mode")]
  [InlineData("Idle Mode")]
  [InlineData("unknown")]
  public void BackToNormalFromAnotherMode_Reset(string previous) =>
    Assert.True(HouseEnergy.IsReturnToNormalMode(previous, Normal, Normal));

  [Fact]
  public void BackToNormalFromRemoteMode_NoReset() =>
    Assert.False(HouseEnergy.IsReturnToNormalMode("Normal (R)", Normal, Normal));

  [Fact]
  public void ChangeToAnotherMode_NoReset() =>
    Assert.False(HouseEnergy.IsReturnToNormalMode(Normal, "Manual Mode", Normal));
}
