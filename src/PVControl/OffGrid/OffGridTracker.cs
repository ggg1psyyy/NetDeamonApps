using System;
using System.Collections.Generic;
using System.Linq;

namespace NetDeamon.apps.PVControl.OffGrid
{
  /// <summary>
  /// The inverter's off-grid phases (its grid status, e.g. <c>sensor.solax_grid_status</c> OffGrid): the house then runs
  /// from the battery through the EPS output. Loads that would drain it (the EV, planned warm-water runs) pause once the
  /// inverter has been off-grid for <c>delay</c>, and resume once it has been back on-grid for the same time — a short
  /// dip (live 2026-11-23 03:34:56: 8 s, a grid voltage dip the inverter tripped on) doesn't touch them, a flickering
  /// grid doesn't restart them. Pure: the cycle feeds it, tests drive it.
  /// </summary>
  public sealed class OffGridTracker
  {
    /// <summary>Start of the current off-grid phase; null while on-grid.</summary>
    public DateTime? OffGridSince { get; private set; }

    /// <summary>Since when the inverter is back on-grid after a pause; null while off-grid or not paused.</summary>
    public DateTime? OnGridSince { get; private set; }

    /// <summary>The loads are paused (off-grid for the delay, not yet on-grid again for it).</summary>
    public bool Paused { get; private set; }

    /// <summary>One cycle's grid status.</summary>
    /// <param name="now">The cycle's time.</param>
    /// <param name="offGrid">Off-grid; null if unknown (unavailable): the last state stays.</param>
    /// <param name="delay">How long off-grid before the loads pause, and on-grid before they resume.</param>
    /// <returns>Whether <see cref="Paused"/> changed.</returns>
    public bool Observe(DateTime now, bool? offGrid, TimeSpan delay)
    {
      bool was = Paused;
      if (offGrid == true)
      {
        OffGridSince ??= now;
        OnGridSince = null;
        if (now - OffGridSince.Value >= delay) Paused = true;
      }
      else if (offGrid == false)
      {
        OffGridSince = null;
        if (Paused)
        {
          OnGridSince ??= now;
          if (now - OnGridSince.Value >= delay) (Paused, OnGridSince) = (false, null);
        }
      }
      return Paused != was;
    }

    /// <summary>After a start: an off-grid phase that began before it counts from its real start (the status entity's
    /// last change), so a restart doesn't restart the delay.</summary>
    /// <param name="offGrid">Off-grid now.</param>
    /// <param name="since">When the status last changed; null: unknown (counts from the first cycle).</param>
    public void Seed(bool offGrid, DateTime? since)
    {
      if (offGrid) OffGridSince = since;
    }

    /// <summary>What a grid status means.</summary>
    /// <param name="state">The status entity's state.</param>
    /// <param name="offGridStates">States that mean off-grid (case doesn't matter).</param>
    /// <returns>True off-grid, false on-grid (any other state), null unknown (no state, unavailable, unknown).</returns>
    public static bool? IsOffGrid(string? state, IEnumerable<string> offGridStates) =>
      string.IsNullOrEmpty(state) || state is "unavailable" or "unknown" ? null
      : offGridStates.Contains(state, StringComparer.OrdinalIgnoreCase);

    /// <summary>Why the loads are paused (for the log and the loads' reason).</summary>
    public string? Reason => !Paused ? null
      : OffGridSince is { } off ? $"off-grid since {off:HH:mm}"
      : OnGridSince is { } on ? $"off-grid, back on-grid since {on:HH:mm}"
      : "off-grid";
  }
}
