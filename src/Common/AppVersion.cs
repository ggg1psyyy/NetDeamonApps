using System.Reflection;

namespace NetDeamon.apps;

/// <summary>
/// Version of the deployed build, set from git at build time (NetDeamonApps.csproj, target
/// GitVersion): "0.5.&lt;commit count&gt;+&lt;short hash&gt;", with ".dirty" appended when the build had
/// uncommitted changes (e.g. "0.5.233+1bdea38.dirty"). Shown as the "Firmware" of the HA devices
/// and as the state of sensor.pv_control_version / sensor.heating_test_version.
/// </summary>
public static class AppVersion
{
  public static string Current { get; } =
    typeof(AppVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
}
