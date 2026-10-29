using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using Microsoft.Extensions.DependencyInjection;

namespace NetDeamon;

/// <summary>An app plugin: a folder with an assembly of the same name.</summary>
/// <param name="Name">Folder (and assembly) name.</param>
/// <param name="AssemblyPath">Full path of the plugin's main assembly.</param>
public sealed record AppPlugin(string Name, string AssemblyPath);

/// <summary>
/// Loads NetDaemon app plugins at start: every <c>apps/plugins/&lt;Name&gt;/&lt;Name&gt;.dll</c> next to the host
/// (and folders given with <c>--plugins &lt;folder&gt;</c>), each registered like the host's own apps
/// (<c>AddAppsFromAssembly</c>). A plugin's YAML lies in its folder, under <c>./apps</c>, where NetDaemon reads
/// app configs anyway.
/// <para>Plugins load into the default load context: NetDaemon's types (<c>[NetDaemonApp]</c>, <c>IHaContext</c>)
/// always come from the host — a plugin's own copy of them is never loaded, so its apps are the host's types.
/// What only the plugin has (e.g. ImageSharp, SQLite with its native library) is resolved from its folder (<see cref="AssemblyDependencyResolver"/>).
/// A plugin built against another NetDaemon version than the host is skipped (<see cref="Incompatibility"/>).</para>
/// </summary>
public static class PluginLoader
{
  /// <summary>Folder of the deployed plugins, relative to the host.</summary>
  public const string PluginsFolder = "apps/plugins";

  /// <summary>
  /// The plugins in a folder: the folder itself if it holds a plugin (<see cref="PluginIn"/> — e.g. one plugin's
  /// build output, for running it alone), otherwise each subfolder that does. Ordered by name.
  /// </summary>
  /// <param name="folder">A plugin folder or a folder of plugin folders.</param>
  /// <returns>The plugins; none if the folder doesn't exist.</returns>
  public static IReadOnlyList<AppPlugin> Discover(string folder)
  {
    if (!Directory.Exists(folder)) return [];
    if (PluginIn(folder) is { } single) return [single];
    return Directory.GetDirectories(folder)
      .Select(PluginIn).Where(p => p is not null).Select(p => p!)
      .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
      .ToList();
  }

  /// <summary>The plugin of a folder: <c>&lt;FolderName&gt;.dll</c> in it (deployed: <c>apps/plugins/&lt;Name&gt;/</c>), else the one
  /// assembly with a <c>.deps.json</c> (a project's build output, e.g. <c>bin/Debug/net10.0</c>).</summary>
  /// <param name="folder">The folder.</param>
  private static AppPlugin? PluginIn(string folder)
  {
    var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)));
    var dll = Path.Combine(folder, name + ".dll");
    if (File.Exists(dll)) return new AppPlugin(name, Path.GetFullPath(dll));
    var built = Directory.GetFiles(folder, "*.deps.json")
      .Select(d => Path.Combine(folder, Path.GetFileName(d)[..^".deps.json".Length] + ".dll")).Where(File.Exists).ToList();
    return built.Count == 1 ? new AppPlugin(Path.GetFileNameWithoutExtension(built[0]), Path.GetFullPath(built[0])) : null;
  }

  /// <summary>
  /// Why a plugin can't run in this host: a NetDaemon assembly it references in another major.minor version
  /// than the host has (the host provides NetDaemon's types; a plugin built against other ones would find
  /// members missing at run time).
  /// </summary>
  /// <param name="referenced">The plugin's referenced assemblies.</param>
  /// <param name="host">The host's loaded assemblies (name → version).</param>
  /// <returns>The reason; null if compatible.</returns>
  public static string? Incompatibility(IEnumerable<AssemblyName> referenced, IReadOnlyDictionary<string, Version> host)
  {
    foreach (var r in referenced)
      if (r.Name is { } name && name.StartsWith("NetDaemon.", StringComparison.Ordinal) && r.Version is { } v
          && host.TryGetValue(name, out var hv) && (hv.Major != v.Major || hv.Minor != v.Minor))
        return $"built against {name} {v.Major}.{v.Minor}, the host has {hv.Major}.{hv.Minor}";
    return null;
  }

  /// <summary>Plugin folders given on the command line: every value after <c>--plugins</c>.</summary>
  /// <param name="args">Command line.</param>
  public static IReadOnlyList<string> FoldersFromArgs(IReadOnlyList<string> args) =>
    args.Select((a, i) => (a, i)).Where(x => x.a == "--plugins" && x.i + 1 < args.Count).Select(x => args[x.i + 1]).ToList();

  /// <summary>
  /// Registers the apps of every compatible plugin (the deployed <see cref="PluginsFolder"/> and the
  /// <c>--plugins</c> folders); an incompatible or broken plugin is reported and skipped, the host and the
  /// other plugins start anyway.
  /// </summary>
  /// <param name="services">The host's services.</param>
  /// <param name="args">Command line.</param>
  /// <param name="log">Where to report (the host's logging isn't built yet at this point).</param>
  /// <returns>The services.</returns>
  public static IServiceCollection AddAppPlugins(this IServiceCollection services, IReadOnlyList<string> args, Action<string>? log = null)
  {
    log ??= Console.WriteLine;
    var folders = new[] { Path.Combine(AppContext.BaseDirectory, PluginsFolder) }.Concat(FoldersFromArgs(args));
    var host = AssemblyLoadContext.Default.Assemblies
      .Select(a => a.GetName()).Where(n => n.Name is not null && n.Version is not null)
      .GroupBy(n => n.Name!).ToDictionary(g => g.Key, g => g.First().Version!);
    foreach (var plugin in folders.SelectMany(Discover).DistinctBy(p => p.AssemblyPath))
    {
      try
      {
        var resolver = new AssemblyDependencyResolver(plugin.AssemblyPath);
        // Only reached for what the host doesn't have itself: the plugin's own dependencies, managed and native
        // (e.g. SQLite's e_sqlite3 under the plugin's runtimes/<rid>/native/).
        AssemblyLoadContext.Default.Resolving += (context, name) =>
          resolver.ResolveAssemblyToPath(name) is { } path ? context.LoadFromAssemblyPath(path) : null;
        AssemblyLoadContext.Default.ResolvingUnmanagedDll += (_, name) =>
          resolver.ResolveUnmanagedDllToPath(name) is { } path ? NativeLibrary.Load(path) : IntPtr.Zero;
        var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(plugin.AssemblyPath);
        if (Incompatibility(assembly.GetReferencedAssemblies(), host) is { } reason)
        {
          log($"Plugin {plugin.Name} skipped: {reason}");
          continue;
        }
        services.AddAppsFromAssembly(assembly);
        log($"Plugin {plugin.Name} loaded ({assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "no version"})");
      }
      catch (Exception ex)
      {
        log($"Plugin {plugin.Name} skipped: {ex.GetType().Name}: {ex.Message}");
      }
    }
    return services;
  }
}
