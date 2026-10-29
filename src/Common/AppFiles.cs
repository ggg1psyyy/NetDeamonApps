using System;
using System.IO;
using System.Reflection;

namespace NetDeamon;

/// <summary>Where an app keeps its own files (DBs): next to its assembly, i.e. in its plugin folder with its YAML.</summary>
public static class AppFiles
{
  /// <summary>
  /// The full path of an app's file: a relative location lies in the app assembly's folder
  /// (deployed: <c>apps/plugins/&lt;Name&gt;/</c>), an absolute one stays as it is.
  /// </summary>
  /// <param name="location">The configured location (e.g. <c>heating_test.db</c>).</param>
  /// <param name="app">The app's assembly.</param>
  /// <returns>The full path.</returns>
  public static string PathOf(string location, Assembly app) =>
    Path.IsPathRooted(location) ? location
      : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(app.Location) ?? AppContext.BaseDirectory, location));
}
