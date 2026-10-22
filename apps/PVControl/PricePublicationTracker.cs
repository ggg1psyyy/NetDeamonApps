using System;
using System.Collections.Generic;
using System.Linq;

namespace NetDeamon.apps.PVControl;

/// <summary>When the next day's prices were first available on a day.</summary>
public sealed record PricePublication(DateTime Day, DateTime PublishedAt);

/// <summary>
/// Notes when tomorrow's prices first show up in PVControl's import price list each day (the
/// recorder doesn't keep the price list itself — too large — so this is the only record). Until
/// then the simulation works with estimated prices (PriceList.WithEstimatedPrices).
/// </summary>
public sealed class PricePublicationTracker
{
  public const int KeepDays = 14;
  readonly List<PricePublication> _days = [];

  public IReadOnlyList<PricePublication> Days => _days;

  public void Restore(IEnumerable<PricePublication> days)
  {
    _days.Clear();
    _days.AddRange(days.OrderBy(d => d.Day).TakeLast(KeepDays));
  }

  /// <summary>Today's publication time, null while tomorrow isn't published yet.</summary>
  public DateTime? PublishedToday(DateTime now) => _days.FirstOrDefault(d => d.Day == now.Date)?.PublishedAt;

  DateTime? _unpublishedSeenOn;

  /// <summary>Notes the first moment of a day the prices reach the end of tomorrow. True if noted.
  /// Only after tomorrow was seen missing earlier that day — after a restart in the afternoon the
  /// prices are just there, and the restart time isn't their publication time.</summary>
  public bool Observe(DateTime now, DateTime? pricesUntil)
  {
    if (pricesUntil is not { } until || until < now.Date.AddDays(2))
    {
      _unpublishedSeenOn = now.Date;
      return false;
    }
    if (_unpublishedSeenOn != now.Date || _days.Any(d => d.Day == now.Date))
      return false;
    _days.Add(new PricePublication(now.Date, now));
    if (_days.Count > KeepDays) _days.RemoveAt(0);
    return true;
  }
}
