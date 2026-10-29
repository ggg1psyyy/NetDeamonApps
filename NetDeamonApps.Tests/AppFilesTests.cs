using NetDeamon;
using Xunit;

namespace NetDeamonApps.Tests;

/// <summary><see cref="AppFiles.PathOf"/>: an app's files (DBs) lie in its own folder, with its YAML.</summary>
public class AppFilesTests
{
  [Fact]
  public void PathOf_Relative_InTheAppAssemblysFolder()
  {
    var app = typeof(NetDeamon.apps.HeatingTest.HeatingTestConfig).Assembly;
    Assert.Equal(Path.Combine(Path.GetDirectoryName(app.Location)!, "heating_test.db"), AppFiles.PathOf("heating_test.db", app));
  }

  [Fact]
  public void PathOf_Absolute_Unchanged()
  {
    var absolute = Path.Combine(Path.GetTempPath(), "x.db");
    Assert.Equal(absolute, AppFiles.PathOf(absolute, typeof(AppFiles).Assembly));
  }

  [Fact]
  public void HeatingTest_DefaultDb_IsInItsPluginFolder() =>
    Assert.Equal("heating_test.db", new NetDeamon.apps.HeatingTest.HeatingTestConfig().DBLocation);
}
