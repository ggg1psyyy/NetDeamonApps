using System;

namespace NetDeamon.apps.PVControl.ImportCap;

/// <summary>The month's highest 15-min import (what the network power price counts); starts over each month.</summary>
public sealed class MonthPeakTracker
{
  /// <summary>The month's peak; null before the first finished quarter of the month.</summary>
  public QuarterResult? Peak { get; private set; }

  /// <summary>Restores the peak after a restart from one of several sources (the sensor, the DataLogger):
  /// the higher one of the same month wins; ignored if impossible.</summary>
  /// <param name="peak">Stored peak.</param>
  public void Restore(QuarterResult peak)
  {
    if (!Plausible(peak)) return;
    if (Peak is { } p && SameMonth(p.Start, peak.Start) && p.AvgKw >= peak.AvgKw) return;
    if (Peak is { } q && q.Start > peak.Start && !SameMonth(q.Start, peak.Start)) return;   // a later month's peak stays
    Peak = peak;
  }

  /// <summary>A DataLogger hourly row's highest 15-min import as a peak (the row's timestamp is its hour's last second;
  /// the quarter within the hour isn't stored, so the hour's start stands for it).</summary>
  /// <param name="hourEnd">The row's timestamp.</param>
  /// <param name="kw">Its <c>maxquarterimport</c>, kW.</param>
  public static QuarterResult FromHourlyRow(DateTime hourEnd, double kw) => new(hourEnd.AddSeconds(1).AddHours(-1), kw);

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
