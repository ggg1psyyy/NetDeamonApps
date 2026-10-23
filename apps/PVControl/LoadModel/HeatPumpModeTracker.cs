using System;
using System.Collections.Generic;
using System.Linq;

namespace NetDeamon.apps.PVControl.LoadModel;

/// <summary>Whether the heat pump heats the house and since when; unknown/unavailable modes keep the last known one.</summary>
/// <param name="heatingModes">Modes that count as heating.</param>
public sealed class HeatPumpModeTracker(IReadOnlyCollection<string> heatingModes)
{
  /// <summary>Null until a mode was known.</summary>
  public bool? HeatingOn { get; private set; }
  /// <summary>When heating was switched on — null when unknown (then it counts as long ago).</summary>
  public DateTime? HeatingOnSince { get; private set; }

  static bool Known(string? mode) => !string.IsNullOrEmpty(mode) && mode is not "unknown" and not "unavailable";

  /// <summary>Takes the live mode; only a real change sets <see cref="HeatingOnSince"/>.</summary>
  /// <param name="now">Current time.</param>
  /// <param name="mode">Heat pump mode; unknown/unavailable is ignored.</param>
  public void Observe(DateTime now, string? mode)
  {
    if (!Known(mode)) return;
    bool on = heatingModes.Contains(mode!);
    if (HeatingOn is { } was && on != was)
      HeatingOnSince = on ? now : null;
    HeatingOn = on;
  }

  /// <summary>Starts from DataLogger's hourly modes, so a restart keeps <see cref="HeatingOnSince"/>.</summary>
  /// <param name="logged">(hour start, mode) in time order.</param>
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
