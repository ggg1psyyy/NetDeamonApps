using System.Collections;
using System.Reflection;
using YamlDotNet.RepresentationModel;

namespace NetDeamonApps.Tests;

/// <summary>
/// The apps' YAML configs: the sample in git (src/&lt;App&gt;/&lt;App&gt;.sample.yaml) and the real one, local only (not in
/// git; present on the dev machine, absent in a fresh clone). NetDaemon binds a YAML to its config class and ignores a
/// key no property takes — a renamed key forgotten in one file would silently run on the code's default — so
/// <see cref="Unmatched"/> lists those keys.
/// </summary>
public static class ConfigKeys
{
  /// <summary>The repository root (the folder with src/).</summary>
  public static string Root()
  {
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir != null && !File.Exists(Path.Combine(dir.FullName, "src", "AppPlugin.props")))
      dir = dir.Parent;
    return dir?.FullName ?? throw new InvalidOperationException("repository root not found");
  }

  /// <summary>An app's sample and, when present, its local config.</summary>
  /// <param name="app">The app (its folder and file name).</param>
  public static IEnumerable<string> Files(string app)
  {
    string folder = Path.Combine(Root(), "src", app);
    yield return Path.Combine(folder, app + ".sample.yaml");
    string local = Path.Combine(folder, app + ".yaml");
    if (File.Exists(local)) yield return local;
  }

  /// <summary>Keys of a YAML config that no writable property of its config class takes (case-insensitive, as the
  /// binding); also a top-level key other than the class's full name.</summary>
  /// <param name="yaml">The YAML text.</param>
  /// <param name="configType">The app's config class.</param>
  /// <returns>Key paths, e.g. "PVConfig.ImportTariff"; empty when every key binds.</returns>
  public static List<string> Unmatched(string yaml, Type configType)
  {
    var stream = new YamlStream();
    stream.Load(new StringReader(yaml));
    var bad = new List<string>();
    foreach (var (key, value) in ((YamlMappingNode)stream.Documents[0].RootNode).Children)
    {
      string name = ((YamlScalarNode)key).Value!;
      if (name != configType.FullName) { bad.Add(name); continue; }
      Walk(value, configType, configType.Name, bad);
    }
    return bad;
  }

  static void Walk(YamlNode node, Type type, string path, List<string> bad)
  {
    type = Nullable.GetUnderlyingType(type) ?? type;
    if (node is YamlSequenceNode seq)
    {
      if (ElementType(type) is { } element)
        foreach (var item in seq) Walk(item, element, path + "[]", bad);
      return;
    }
    if (node is not YamlMappingNode map || typeof(IDictionary).IsAssignableFrom(type)) return;
    foreach (var (key, value) in map.Children)
    {
      string name = ((YamlScalarNode)key).Value!;
      var prop = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
      if (prop is null || !prop.CanWrite) { bad.Add(path + "." + name); continue; }
      Walk(value, prop.PropertyType, path + "." + name, bad);
    }
  }

  static Type? ElementType(Type type) =>
    type.IsArray ? type.GetElementType()
    : type.GetInterfaces().Append(type).FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>))
      ?.GetGenericArguments()[0];
}
