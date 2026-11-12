using System.Xml.Linq;
using Xunit;

namespace NetDeamonApps.Tests;

/// <summary>
/// The libraries the plugins share (Common, Planning) are shipped by the host and loaded once: the host references
/// them, a plugin only compiles against them (Private=false, ExcludeAssets=runtime). A plugin that copied its own would
/// put a second copy next to it — and with it a second set of types. Checked on the project files (a publish would be
/// the live proof, see deploy/README.md).
/// </summary>
public class SharedLibraryTests
{
  static readonly string[] Shared = ["Common", "Planning"];

  static DirectoryInfo Src()
  {
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir != null && !File.Exists(Path.Combine(dir.FullName, "src", "AppPlugin.props"))) dir = dir.Parent;
    Assert.NotNull(dir);
    return new DirectoryInfo(Path.Combine(dir.FullName, "src"));
  }

  static IEnumerable<XElement> References(string file) =>
    XDocument.Load(file).Descendants("ProjectReference");

  static string? SharedName(XElement reference) =>
    Shared.FirstOrDefault(s => (reference.Attribute("Include")?.Value ?? "").Replace('\\', '/').EndsWith($"{s}/{s}.csproj"));

  [Fact]
  public void TheHostReferencesEverySharedLibrary()
  {
    var host = References(Path.Combine(Src().FullName, "Host", "Host.csproj")).Select(SharedName).ToHashSet();
    foreach (var s in Shared) Assert.Contains(s, host);
  }

  [Fact]
  public void PluginsReferenceTheSharedLibraries_ForCompilingOnly()
  {
    var src = Src();
    var files = src.GetDirectories().Where(d => d.Name is not "Host" && !Shared.Contains(d.Name))
      .SelectMany(d => d.GetFiles("*.csproj")).Select(f => f.FullName)
      .Append(Path.Combine(src.FullName, "AppPlugin.props"));
    int checkedRefs = 0;
    foreach (var file in files)
      foreach (var r in References(file).Where(r => SharedName(r) is not null))
      {
        checkedRefs++;
        Assert.True(r.Attribute("Private")?.Value == "false" && r.Attribute("ExcludeAssets")?.Value == "runtime",
          $"{Path.GetFileName(file)} references {SharedName(r)} with its own copy (needs Private=\"false\" ExcludeAssets=\"runtime\")");
      }
    Assert.True(checkedRefs >= 2);   // Common (AppPlugin.props) and Planning (PVControl) at least
  }
}
