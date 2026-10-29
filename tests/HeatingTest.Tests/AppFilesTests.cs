using NetDeamon;
using Xunit;

namespace NetDeamonApps.Tests;

/// <summary><see cref="AppFiles.PathOf"/>: an app's files (DBs) lie in its own folder, with its YAML.</summary>
public class AppFilesTests
{
  [Fact]
  public void HeatingTest_DefaultDb_IsInItsPluginFolder() =>
    Assert.Equal("heating_test.db", new NetDeamon.apps.HeatingTest.HeatingTestConfig().DBLocation);
}
