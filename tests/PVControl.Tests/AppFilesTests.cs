using NetDeamon;
using Xunit;

namespace NetDeamonApps.Tests;

/// <summary><see cref="AppFiles.PathOf"/>: an app's files (DBs) lie in its own folder, with its YAML.</summary>
public class AppFilesTests
{
  [Fact]
  public void PVControl_ReadsTheDataLoggerPluginsDb()
  {
    // Deployed, both plugins lie side by side in apps/plugins/: PVControl reaches the DataLogger's DB from its own folder.
    var config = new NetDeamon.apps.PVControl.PVConfig { DBLocation = NetDeamon.apps.PVControl.PVConfig.DefaultDBLocation };
    var pvControl = Path.GetDirectoryName(typeof(NetDeamon.apps.PVControl.PVConfig).Assembly.Location)!;
    Assert.Equal(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(pvControl)!, "DataLogger", PVControl.DataLoggerConfig.DefaultDBLocation)), config.DBFullLocation);
  }

  [Fact]
  public void PVControl_CostDb_IsInItsPluginFolder()
  {
    var config = new NetDeamon.apps.PVControl.PVConfig { EnergyCostDBLocation = "energy_costs.db" };
    var pvControl = Path.GetDirectoryName(typeof(NetDeamon.apps.PVControl.PVConfig).Assembly.Location)!;
    Assert.Equal(Path.Combine(pvControl, "energy_costs.db"), config.EnergyCostDBFullLocation);
  }
}
