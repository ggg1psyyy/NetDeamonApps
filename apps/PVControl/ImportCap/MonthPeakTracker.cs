using System;

namespace NetDeamon.apps.PVControl.ImportCap;

/// <summary>The month's highest 15-min import (what the network power price counts); starts over each month.</summary>
public sealed class MonthPeakTracker
{
  /// <summary>The month's peak; null before the first finished quarter of the month.</summary>
  public QuarterResult? Peak { get; private set; }

  /// <summary>Restores the peak after a restart (ignored if it is from another month than the next observation, or impossible).</summary>
  /// <param name="peak">Stored peak.</param>
  public void Restore(QuarterResult peak)
  {
    if (Plausible(peak)) Peak = peak;
  }

  /// <summary>Takes a finished quarter.</summary>
  /// <param name="quarter">The quarter.</param>
  /// <returns>True if it is the month's new peak.</returns>
  public bool Observe(QuarterResult quarter)
  {
    if (!Plausible(quarter)) return false;
    if (Peak is { } p && SameMonth(p.Start, quarter.Start) && p.AvgKw >= quarter.AvgKw)
      return false;
    Peak = quarter;
    return true;
  }

  /// <summary>The peak if it belongs to the month of a time, else null (a new month without a finished quarter yet).</summary>
  /// <param name="now">Current time.</param>
  public QuarterResult? PeakIn(DateTime now) => Peak is { } p && SameMonth(p.Start, now) ? p : null;

  /// <summary>A quarter a house connection can import (<see cref="ImportCapGuard.MaxPlausibleW"/>); more is a counter error.</summary>
  static bool Plausible(QuarterResult q) => q.AvgKw >= 0 && q.AvgKw * 1000 <= ImportCapGuard.MaxPlausibleW;

  static bool SameMonth(DateTime a, DateTime b) => a.Year == b.Year && a.Month == b.Month;
}
