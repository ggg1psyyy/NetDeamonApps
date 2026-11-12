using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using NetDeamon.apps;
using Math = System.Math;

namespace NetDeamon.apps.PVControl
{
  /// <summary>Prices (€/kWh) by time range, with the price queries.</summary>
  public class PriceList : IEnumerable<PriceTableEntry>
  {
    private readonly List<PriceTableEntry> _entries;

    /// <summary>An empty list.</summary>
    public PriceList() => _entries = [];
    /// <summary>A list of the given entries.</summary>
    /// <param name="entries">Entries, copied.</param>
    public PriceList(IEnumerable<PriceTableEntry> entries) => _entries = entries.ToList();

    /// <summary>Converts a plain list.</summary>
    /// <param name="list">Entries, copied.</param>
    public static implicit operator PriceList(List<PriceTableEntry> list) => new(list);

    /// <summary>Number of entries.</summary>
    public int Count => _entries.Count;

    /// <summary>Enumerates the entries in stored order.</summary>
    public IEnumerator<PriceTableEntry> GetEnumerator() => _entries.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => _entries.GetEnumerator();

    /// <summary>Whether two lists hold the same entries (times and prices) in the same order.</summary>
    /// <param name="other">The other list.</param>
    public bool SameAs(PriceList other) =>
      _entries.Count == other._entries.Count
      && _entries.Zip(other._entries).All(p => p.First.StartTime == p.Second.StartTime && p.First.EndTime == p.Second.EndTime && p.First.Price == p.Second.Price);

    // ── Resolution handling ──────────────────────────────────────────────────────────────────

    /// <summary>Splits every entry into 15-min entries at the same price.</summary>
    /// <returns>A new quarter-hourly list.</returns>
    public PriceList NormalizeToQuarterHourly()
    {
      var result = new List<PriceTableEntry>();
      foreach (var e in _entries)
      {
        var slotStart = e.StartTime;
        while (slotStart < e.EndTime)
        {
          var slotEnd = slotStart.AddMinutes(15) < e.EndTime ? slotStart.AddMinutes(15) : e.EndTime;
          result.Add(new PriceTableEntry(slotStart, slotEnd, e.Price));
          slotStart = slotEnd;
        }
      }
      return new PriceList(result);
    }

    /// <summary>Prices as billed at a resolution: Hourly gives every entry its hour's mean price.</summary>
    /// <param name="resolution">Billing resolution of the provider.</param>
    /// <returns>This list for QuarterHourly, else a new list.</returns>
    public PriceList WithResolution(PriceResolution resolution)
    {
      if (resolution == PriceResolution.QuarterHourly) return this;

      var result = new List<PriceTableEntry>();
      foreach (var hourGroup in _entries.GroupBy(p => p.StartTime.Floor(60)))
      {
        float avg = hourGroup.Average(p => p.Price);
        result.AddRange(hourGroup.Select(p => new PriceTableEntry(p.StartTime, p.EndTime, avg)));
      }
      return new PriceList(result);
    }

    /// <summary>End of the last entry (the published horizon); null if empty.</summary>
    public DateTime? End => _entries.Count > 0 ? _entries.Max(p => p.EndTime) : null;

    /// <summary>
    /// Extends the list with estimates for unpublished quarters: the price 24 h earlier (chained),
    /// else that hour's mean, else the overall mean; never below 0.
    /// </summary>
    /// <param name="until">Extend up to here (exclusive).</param>
    /// <param name="premium">Factor added to the estimates (import +0.05, export −0.05), so a published price wins over an estimate unless the estimate is clearly better.</param>
    /// <returns>This list if it already reaches <paramref name="until"/>, else an extended copy.</returns>
    public PriceList WithEstimatedPrices(DateTime until, float premium = 0)
    {
      if (_entries.Count == 0 || End is not { } end || end >= until) return this;
      var quarters = NormalizeToQuarterHourly()._entries;
      var byStart = new Dictionary<DateTime, float>();
      foreach (var q in quarters) byStart.TryAdd(q.StartTime, q.Price);
      var byHour = quarters.GroupBy(q => q.StartTime.Hour).ToDictionary(g => g.Key, g => g.Average(q => q.Price));
      float overall = quarters.Average(q => q.Price);

      var result = new List<PriceTableEntry>(_entries);
      for (var t = end; t < until; t = t.AddMinutes(15))
      {
        float price = byStart.TryGetValue(t.AddDays(-1), out float dayBefore) ? dayBefore
          : byHour.TryGetValue(t.Hour, out float hourMean) ? hourMean
          : overall;
        price = Math.Max(0, price);
        byStart[t] = price; // the chain continues from the plain estimate (premium not compounded)
        result.Add(new PriceTableEntry(t, t.AddMinutes(15), price * (1 + premium)));
      }
      return new PriceList(result);
    }

    // ── Point-in-time queries ────────────────────────────────────────────────────────────────

    /// <summary>The one price lookup: the price of the entry covering a time.</summary>
    /// <param name="time">Time to look up.</param>
    /// <param name="price">The price; 0 if none.</param>
    /// <returns>False if no entry covers <paramref name="time"/>.</returns>
    public bool TryGetPrice(DateTime time, out float price)
    {
      // The simulator calls this several times per slot for thousands of slots per planning
      // cycle; index once by quarter-hour (first entry wins, like the scan), scan only on a miss.
      _priceByQuarter ??= BuildQuarterIndex();
      if (_priceByQuarter.TryGetValue(time.RoundToNearestQuarterHour(), out price))
        return true;
      var entry = EntryAt(time);
      price = entry?.Price ?? 0;
      return entry is not null;
    }

    /// <summary>The entry covering a time.</summary>
    /// <param name="time">Time to look up.</param>
    /// <returns>The entry; null if none.</returns>
    private PriceTableEntry? EntryAt(DateTime time) =>
      _entries.Cast<PriceTableEntry?>().FirstOrDefault(p => p!.Value.Covers(time));

    /// <summary>Price at a time (<see cref="TryGetPrice"/>).</summary>
    /// <param name="time">Time to look up.</param>
    /// <returns>The price; 0 if no entry covers it.</returns>
    public float GetPrice(DateTime time) => TryGetPrice(time, out float price) ? price : 0;

    /// <summary>Price at a time (<see cref="TryGetPrice"/>), the mean outside the list — for costs, where 0 would look free.</summary>
    /// <param name="time">Time to look up.</param>
    /// <returns>The price; <see cref="Mean"/> if no entry covers it.</returns>
    public float GetPriceOrMean(DateTime time) => TryGetPrice(time, out float price) ? price : Mean;

    /// <summary>Mean price of all entries; 0 if empty.</summary>
    public float Mean => _mean ??= _entries.Count > 0 ? _entries.Average(p => p.Price) : 0f;
    private float? _mean;

    private Dictionary<DateTime, float>? _priceByQuarter;
    /// <summary>Price by quarter-hour start for <see cref="TryGetPrice"/> (first entry wins).</summary>
    /// <returns>The index.</returns>
    private Dictionary<DateTime, float> BuildQuarterIndex()
    {
      var index = new Dictionary<DateTime, float>();
      foreach (var p in NormalizeToQuarterHourly())
        if (p.EndTime - p.StartTime == TimeSpan.FromMinutes(15) && p.StartTime == p.StartTime.RoundToNearestQuarterHour())
          index.TryAdd(p.StartTime, p.Price);
      return index;
    }

    /// <summary>Whether a negative price is still coming today.</summary>
    /// <param name="now">Current time.</param>
    /// <returns>True if an entry starting after <paramref name="now"/> today is below 0.</returns>
    public bool NegativeImportUpcoming(DateTime now) =>
      _entries.Any(p => p.StartTime.Date == now.Date && p.Price < 0 && p.StartTime > now);

    // ── Window queries ───────────────────────────────────────────────────────────────────────

    /// <summary>Today's entries (midnight to midnight), cheapest first.</summary>
    /// <param name="now">Any time of today.</param>
    private IEnumerable<PriceTableEntry> TodayByPrice(DateTime now) =>
      _entries.Where(p => p.StartTime >= now.Date && p.EndTime <= now.Date.AddDays(1)).OrderBy(p => p.Price);

    /// <summary>Cheapest entry of today (now); default if there is none.</summary>
    public PriceTableEntry CheapestToday => TodayByPrice(DateTime.Now).FirstOrDefault();

    /// <summary>Most expensive entry of today (now).</summary>
    public PriceTableEntry MostExpensiveToday => TodayByPrice(DateTime.Now).LastOrDefault();

    // ── Charging window search ───────────────────────────────────────────────────────────────

    /// <summary>Cheapest entry not over yet; with a charge need only those starting by its latest charge time.</summary>
    /// <param name="need">Charge need of the simulation.</param>
    /// <param name="now">Current time.</param>
    /// <returns>The entry; default if there is none.</returns>
    public PriceTableEntry GetBestChargeWindow(NeedToChargeResult need, DateTime now)
    {
      // Only entries not over yet: with an hour's four equal quarters, an elapsed one would win
      // the price tie and the caller's "now in window" check would fail for the rest of the hour.
      var upcoming = _entries.Where(p => p.EndTime > now)
                             .OrderBy(p => p.StartTime).ToList();
      if (need.NeedToCharge)
        return upcoming.Where(p => p.StartTime <= need.LatestChargeTime).OrderBy(p => p.Price).FirstOrDefault();
      return upcoming.OrderBy(p => p.Price).FirstOrDefault();
    }

    // ── Local maxima ─────────────────────────────────────────────────────────────────────────

    /// <summary>Entries priced higher than both neighbours (price peaks).</summary>
    /// <param name="start">Only entries starting at or after this; default = unbounded.</param>
    /// <param name="end">Only entries ending at or before this; default = unbounded.</param>
    /// <returns>The peaks in time order.</returns>
    public List<PriceTableEntry> GetLocalMaxima(DateTime start = default, DateTime end = default)
    {
      if (start == default) start = DateTime.MinValue;
      if (end == default) end = DateTime.MaxValue;
      List<PriceTableEntry> maxima = [];
      var actList = _entries.Where(t => t.StartTime >= start && t.EndTime <= end)
                            .OrderBy(t => t.StartTime).ToList();
      if (actList.Count > 2)
      {
        for (int i = 1; i < actList.Count - 1; i++)
        {
          if (actList[i].Price > actList[i - 1].Price && actList[i].Price > actList[i + 1].Price)
            maxima.Add(actList[i]);
        }
      }
      return maxima;
    }
  }
}
