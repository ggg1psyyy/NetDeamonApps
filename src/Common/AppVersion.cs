using System.Reflection;

namespace NetDeamon.apps;

/// <summary>
/// Version of a build, set from git at build time (Directory.Build.targets, target GitVersion):
/// "0.5.&lt;commit count&gt;+&lt;short hash&gt;", with ".dirty" appended when the build had uncommitted
/// changes (e.g. "0.5.233+1bdea38.dirty"). Shown as the "Firmware" of the HA devices and as the state
/// of sensor.pv_control_version / sensor.heating_test_version.
/// </summary>
public static class AppVersion
{
  /// <summary>The version of an app's own assembly (each app plugin is built and deployed on its own).</summary>
  /// <param name="assembly">The app's assembly, e.g. <c>GetType().Assembly</c>.</param>
  /// <returns>The version; "unknown" without one.</returns>
  public static string Of(Assembly assembly) =>
    assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
}
