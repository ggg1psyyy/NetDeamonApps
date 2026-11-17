using Xunit;

namespace NetDeamonApps.Tests;

/// <summary>PVControl's and DataLogger's YAML (the sample, and the local config when present): every key binds to a
/// property of the config class — NetDaemon ignores one that doesn't, a forgotten rename would run on the code's
/// default without a word.</summary>
public class ConfigFileTests
{
  [Theory]
  [InlineData("PVControl", typeof(NetDeamon.apps.PVControl.PVConfig))]
  [InlineData("DataLogger", typeof(PVControl.DataLoggerConfig))]
  public void EveryKeyBindsToTheConfigClass(string app, Type config)
  {
    foreach (var file in ConfigKeys.Files(app))
    {
      var unmatched = ConfigKeys.Unmatched(File.ReadAllText(file), config);
      Assert.True(unmatched.Count == 0, $"{Path.GetFileName(file)}: keys no property takes: {string.Join(", ", unmatched)}");
    }
  }

  [Fact]
  public void Unmatched_FindsAnOldKeyAlsoInAList()
  {
    const string yaml = """
      NetDeamon.apps.PVControl.PVConfig:
        ImportTariff: Market
        ImportTariffDefault: Market
        SchedulableLoads:
          - Name: EV
            AvgPowerW: 1800
            AvgPowerWDefault: 1800
      """;
    Assert.Equal(["PVConfig.ImportTariff", "PVConfig.SchedulableLoads[].AvgPowerW"],
      ConfigKeys.Unmatched(yaml, typeof(NetDeamon.apps.PVControl.PVConfig)));
  }

  [Fact]
  public void Unmatched_AnotherAppsConfigName() =>
    Assert.Equal(["PVControl.DataLoggerConfig"], ConfigKeys.Unmatched("PVControl.DataLoggerConfig:\n  DBLocation: x\n", typeof(NetDeamon.apps.PVControl.PVConfig)));
}
