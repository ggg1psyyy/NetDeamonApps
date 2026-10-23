using System;
using System.Collections.Generic;
using System.Linq;

namespace NetDeamon.apps.PVControl;

/// <summary>When the next day's prices were first available on a day.</summary>
/// <param name="Day">The day (date).</param>
/// <param name="PublishedAt">Time they were first seen.</param>
public sealed record PricePublication(DateTime Day, DateTime PublishedAt);

/// <summary>Records when tomorrow's prices first show up each day (HA's recorder doesn't keep the price list).</summary>
public sealed class PricePublicationTracker
{
  /// <summary>Days kept.</summary>
  public const int KeepDays = 14;
  readonly DayLog<PricePublication> _days = new(KeepDays, d => d.Day);

  /// <summary>Recorded days, oldest first.</summary>
  public IReadOnlyList<PricePublication> Days => _days.Items;

  /// <summary>Restores recorded days after a restart (the last <see cref="KeepDays"/>).</summary>
  /// <param name="days">Days from the sensor's attribute.</param>
  public void Restore(IEnumerable<PricePublication> days) => _days.Restore(days);

  /// <summary>Today's publication time.</summary>
  /// <param name="now">Current time.</param>
  /// <returns>The time; null while tomorrow isn't published.</returns>
  public DateTime? PublishedToday(DateTime now) => _days.Items.FirstOrDefault(d => d.Day == now.Date)?.PublishedAt;

  DateTime? _unpublishedSeenOn;

  /// <summary>Notes the first moment of a day the prices reach the end of tomorrow — only after they were seen missing that day (not at a restart).</summary>
  /// <param name="now">Current time.</param>
  /// <param name="pricesUntil">End of the published prices.</param>
  /// <returns>True if a publication was noted.</returns>
  public bool Observe(DateTime now, DateTime? pricesUntil)
  {
    if (pricesUntil is not { } until || until < now.Date.AddDays(2))
    {
      _unpublishedSeenOn = now.Date;
      return false;
    }
    if (_unpublishedSeenOn != now.Date || _days.Has(now.Date))
      return false;
    _days.Add(new PricePublication(now.Date, now));
    return true;
  }
}
