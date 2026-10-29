using NetDeamon;
using Xunit;

namespace NetDeamonApps.Tests;

/// <summary><see cref="AppFiles.PathOf"/>: an app's files (DBs) lie in its own folder, with its YAML.</summary>
public class AppFilesTests
{
  [Fact]
  public void PathOf_Relative_InTheAppAssemblysFolder()
  {
    var app = typeof(AppFiles).Assembly;
    Assert.Equal(Path.Combine(Path.GetDirectoryName(app.Location)!, "app.db"), AppFiles.PathOf("app.db", app));
  }

  [Fact]
  public void PathOf_Absolute_Unchanged()
  {
    var absolute = Path.Combine(Path.GetTempPath(), "x.db");
    Assert.Equal(absolute, AppFiles.PathOf(absolute, typeof(AppFiles).Assembly));
  }
}
