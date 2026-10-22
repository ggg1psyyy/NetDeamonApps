using System;
using System.Collections.Generic;
using System.Linq;

namespace NetDeamon.apps.PVControl.LoadModel;

/// <summary>
/// Whether the heat pump heats the house (its mode is one of the heating modes) and since when.
/// An unavailable/unknown mode (KNX dropouts) keeps the last known one, and only a real change
/// from not heating to heating sets <see cref="HeatingOnSince"/> — HA's LastChanged also moves on
/// "unavailable → heating", which made a dropout look like "just switched on".
/// </summary>
public sealed class HeatPumpModeTracker(IReadOnlyCollection<string> heatingModes)
{
  /// <summary>Null until a mode was known.</summary>
  public bool? HeatingOn { get; private set; }
  /// <summary>When heating was switched on — null when unknown (then it counts as long ago).</summary>
  public DateTime? HeatingOnSince { get; private set; }

  static bool Known(string? mode) => !string.IsNullOrEmpty(mode) && mode is not "unknown" and not "unavailable";

  /// <summary>The live mode at <paramref name="now"/>.</summary>
  public void Observe(DateTime now, string? mode)
  {
    if (!Known(mode)) return;
    bool on = heatingModes.Contains(mode!);
    if (HeatingOn is { } was && on != was)
      HeatingOnSince = on ? now : null;
    HeatingOn = on;
  }

  /// <summary>Starts from DataLogger's hourly modes (time order, hour start): the last change to
  /// heating in there becomes <see cref="HeatingOnSince"/> — so a restart doesn't forget it.</summary>
  public void Seed(IEnumerable<(DateTime Hour, string? Mode)> logged)
  {
    bool? was = null;
    DateTime? since = null;
    foreach (var (hour, mode) in logged)
    {
      if (!Known(mode)) continue;
      bool on = heatingModes.Contains(mode!);
      if (was == false && on) since = hour;
      if (!on) since = null;
      was = on;
    }
    if (was is null) return;
    HeatingOn = was;
    HeatingOnSince = since;
  }
}
