using Xunit;

namespace NetDeamonApps.Tests;

/// <summary>HeatingTest's YAML (sample, and the local config when present): every key binds to its config class.</summary>
public class ConfigFileTests
{
  [Fact]
  public void EveryKeyBindsToTheConfigClass()
  {
    foreach (var file in ConfigKeys.Files("HeatingTest"))
      Assert.Empty(ConfigKeys.Unmatched(File.ReadAllText(file), typeof(NetDeamon.apps.HeatingTest.HeatingTestConfig)));
  }
}
