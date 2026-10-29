using System.Reflection;
using NetDeamon;
using Xunit;

namespace NetDeamonApps.Tests;

/// <summary>
/// The host's plugin loader (<see cref="PluginLoader"/>): finding plugin folders, the NetDaemon version gate,
/// the --plugins arguments. Loading a plugin into the running process and NetDaemon registering its apps is
/// proven live (the first plugin's version sensor and entities), not here — it would load assemblies into the
/// test runner's own process.
/// </summary>
public class PluginLoaderTests : IDisposable
{
  private readonly string _root = Path.Combine(Path.GetTempPath(), "plugins-" + Guid.NewGuid().ToString("N"));

  public PluginLoaderTests() => Directory.CreateDirectory(_root);
  public void Dispose() => Directory.Delete(_root, recursive: true);

  private string Plugin(string name, bool withDll = true)
  {
    var dir = Path.Combine(_root, name);
    Directory.CreateDirectory(dir);
    if (withDll) File.WriteAllText(Path.Combine(dir, name + ".dll"), "");
    return dir;
  }

  [Fact]
  public void Discover_EachFolderWithItsNamedAssembly_ByName()
  {
    Plugin("MidiControl");
    Plugin("HeatingTest");
    Plugin("Notes", withDll: false);                                  // no Notes.dll: not a plugin
    File.WriteAllText(Path.Combine(Plugin("Odd", withDll: false), "Other.dll"), ""); // the dll must carry the folder's name
    Assert.Equal(["HeatingTest", "MidiControl"], PluginLoader.Discover(_root).Select(p => p.Name));
  }

  [Fact]
  public void Discover_ADeployedPluginFolderGivenDirectly_IsThatPlugin()
  {
    var dir = Plugin("MidiControl");
    var found = Assert.Single(PluginLoader.Discover(dir));
    Assert.Equal("MidiControl", found.Name);
    Assert.Equal(Path.GetFullPath(Path.Combine(dir, "MidiControl.dll")), found.AssemblyPath);
  }

  [Fact]
  public void Discover_AProjectsBuildOutput_ByItsDepsJson()
  {
    // `--plugins src/MidiControl/bin/Debug/net10.0`: the folder is named net10.0; the project's own assembly is the
    // one with a .deps.json (its dependencies have none).
    var dir = Path.Combine(_root, "net10.0");
    Directory.CreateDirectory(dir);
    foreach (var f in new[] { "MidiControl.dll", "MidiControl.deps.json", "SixLabors.ImageSharp.dll", "NetDaemon.AppModel.dll" })
      File.WriteAllText(Path.Combine(dir, f), "");
    var found = Assert.Single(PluginLoader.Discover(dir));
    Assert.Equal("MidiControl", found.Name);
  }

  [Fact]
  public void Discover_MissingFolder_None() =>
    Assert.Empty(PluginLoader.Discover(Path.Combine(_root, "nope")));

  static AssemblyName Ref(string name, string version) => new(name) { Version = Version.Parse(version) };
  static readonly Dictionary<string, Version> Host = new() { ["NetDaemon.AppModel"] = new(26, 43, 1, 0), ["NetDaemon.HassModel"] = new(26, 43, 1, 0) };

  [Fact]
  public void Incompatibility_SameMajorMinor_OtherAssembliesIgnored() =>
    Assert.Null(PluginLoader.Incompatibility([Ref("NetDaemon.AppModel", "26.43.0.0"), Ref("SixLabors.ImageSharp", "2.0.0.0"), Ref("System.Runtime", "10.0.0.0")], Host));

  [Fact]
  public void Incompatibility_OtherNetDaemonVersion_Reason()
  {
    var reason = PluginLoader.Incompatibility([Ref("NetDaemon.HassModel", "26.40.0.0")], Host);
    Assert.Contains("NetDaemon.HassModel 26.40", reason);
    Assert.Contains("26.43", reason);
  }

  [Fact]
  public void FoldersFromArgs_EveryValueAfterPlugins() =>
    Assert.Equal(["a", "b"], PluginLoader.FoldersFromArgs(["--plugins", "a", "--other", "x", "--plugins", "b", "--plugins"]));
}
