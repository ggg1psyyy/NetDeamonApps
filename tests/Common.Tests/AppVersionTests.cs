using NetDeamon.apps;
using Xunit;

namespace NetDeamonApps.Tests;

/// <summary>
/// The build version from git (Directory.Build.targets, target GitVersion) shown as the devices'
/// "Firmware" and in sensor.pv_control_version — to see which build is deployed (live 2026-10-21
/// a build made from a half-split working tree was deployed unnoticed).
/// </summary>
public class AppVersionTests
{
  [Fact]
  public void Current_IsCommitCountAndHash() =>
    Assert.Matches(@"^0\.5\.\d+\+[0-9a-f]{7}(\.dirty)?$", AppVersion.Of(typeof(AppVersion).Assembly));
}
