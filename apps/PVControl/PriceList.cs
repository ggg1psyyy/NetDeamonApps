using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using NetDeamon.apps;
using Math = System.Math;

namespace NetDeamon.apps.PVControl
{
  /// <summary>
  /// A typed wrapper around a list of hourly price entries.
  /// All price-query operations are instance methods on the list itself rather than
  /// static helpers that require the list to be passed as a parameter.
  /// </summary>
  public class PriceList : IEnumerable<PriceTableEntry>
  {
    private readonly List<PriceTableEntry> _entries;

    public PriceList() => _entries = [];
    public PriceList(IEnumerable<PriceTableEntry> entries) => _entries = entries.ToList();

    /// <summary>Allows assigning a plain <c>List&lt;PriceTableEntry&gt;</c> wherever a <see cref="PriceList"/> is expected.</summary>
    public static implicit operator PriceList(List<PriceTableEntry> list) => new(list);

    public int Count => _entries.Count;

    public IEnumerator<PriceTableEntry> GetEnumerator() => _entries.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => _entries.GetEnumerator();

    // ── Resolution handling ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Splits every entry into consecutive 15-minute slots at the same price, so the list always
    /// has quarter-hourly granularity regardless of the source sensor's native resolution
    /// (an hourly entry becomes four identical-price 15-min entries).
    /// </summary>
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

    /// <summary>
    /// Returns a copy of this list with prices adjusted for the given billing resolution.
    /// QuarterHourly returns the list unchanged. Hourly averages all entries that share a
    /// calendar hour and applies that mean price to each of them (matches providers that
    /// bill the hourly EPEX average rather than the raw 15-min price).
    /// </summary>
    public PriceList WithResolution(PriceResolution resolution)
    {
      if (resolution == PriceResolution.QuarterHourly) return this;

      var result = new List<PriceTableEntry>();
      foreach (var hourGroup in _entries.GroupBy(p => new DateTime(p.StartTime.Year, p.StartTime.Month,
                 p.StartTime.Day, p.StartTime.Hour, 0, 0)))
      {
        float avg = hourGroup.Average(p => p.Price);
        result.AddRange(hourGroup.Select(p => new PriceTableEntry(p.StartTime, p.EndTime, avg)));
      }
      return new PriceList(result);
    }

    /// <summary>End of the last entry (the published horizon); null for an empty list.</summary>
    public DateTime? End => _entries.Count > 0 ? _entries.Max(p => p.EndTime) : null;

    /// <summary>
    /// A copy extended to <paramref name="until"/> with estimates for the quarters nobody has
    /// published yet: the price 24 h earlier (chained, so longer gaps fill too — the daily pattern
    /// repeats well enough), else the mean of that hour, else of all prices. Without this, until
    /// tomorrow's prices arrive (~13:00) the simulator's scans see no tomorrow at all and its cost
    /// comparisons price tomorrow with the flat mean. Estimates are never negative: negative prices
    /// trigger special handling (keeping battery room, curtailing export) that should only follow
    /// published prices.
    /// </summary>
    /// <param name="premium">Risk premium on the estimates (import +0.05, export −0.05): an
    /// estimated slot only wins over a published one when it's clearly better — waiting for an
    /// estimate that turns out wrong can cost a cheap published slot (live 2026-10-22 13:00: a
    /// grid charge moved from today 14:00 at 0.3327 to an estimated 02:00 at 0.3202, 3.9 % less).</param>
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

    /// <summary>Price at the given time (0 if no matching entry).</summary>
    public float GetPrice(DateTime time)
    {
      // The simulator calls this several times per slot for thousands of slots per planning
      // cycle; index once by quarter-hour (first entry wins, like the scan), scan only on a miss.
      _priceByQuarter ??= BuildQuarterIndex();
      return _priceByQuarter.TryGetValue(time.RoundToNearestQuarterHour(), out var price)
        ? price
        : _entries.FirstOrDefault(p => p.StartTime <= time && p.EndTime > time).Price;
    }

    private Dictionary<DateTime, float>? _priceByQuarter;
    private Dictionary<DateTime, float> BuildQuarterIndex()
    {
      var index = new Dictionary<DateTime, float>();
      foreach (var p in NormalizeToQuarterHourly())
        if (p.EndTime - p.StartTime == TimeSpan.FromMinutes(15) && p.StartTime == p.StartTime.RoundToNearestQuarterHour())
          index.TryAdd(p.StartTime, p.Price);
      return index;
    }

    /// <summary>True if any entry today after <paramref name="now"/> has a negative price.</summary>
    public bool NegativeImportUpcoming(DateTime now) =>
      _entries.Any(p => p.StartTime.Date == now.Date && p.Price < 0 && p.StartTime > now);

    // ── Window queries ───────────────────────────────────────────────────────────────────────

    /// <summary>The cheapest entry within today (midnight to midnight).</summary>
    public PriceTableEntry GetCheapestWindowToday(DateTime now) =>
      _entries.Where(p => p.StartTime >= now.Date && p.EndTime <= now.Date.AddDays(1))
              .OrderBy(p => p.Price).FirstOrDefault();

    /// <summary>Cheapest entry in today's window (using current time).</summary>
    public PriceTableEntry CheapestToday => GetCheapestWindowToday(DateTime.Now);

    /// <summary>Most expensive entry in today's window.</summary>
    public PriceTableEntry MostExpensiveToday
    {
      get
      {
        var now = DateTime.Now;
        return _entries.Where(p => p.StartTime >= now.Date && p.EndTime <= now.Date.AddDays(1))
                       .OrderBy(p => p.Price).LastOrDefault();
      }
    }

    /// <summary>Cheapest entry across the entire list.</summary>
    public PriceTableEntry CheapestTotal => _entries.OrderBy(p => p.Price).First();

    /// <summary>True if the current moment falls within the cheapest entry of today.</summary>
    public bool IsNowCheapest
    {
      get { var c = CheapestToday; var now = DateTime.Now; return now > c.StartTime && now < c.EndTime; }
    }

    /// <summary>True if the current moment falls within the cheapest entry of the full list.</summary>
    public bool IsNowCheapestTotal
    {
      get { var c = CheapestTotal; var now = DateTime.Now; return now > c.StartTime && now < c.EndTime; }
    }

    // ── Charging window search ───────────────────────────────────────────────────────────────

    /// <summary>
    /// The cheapest upcoming window to use for force-charging.
    /// If <see cref="NeedToChargeResult.NeedToCharge"/> is true the search is limited to hours
    /// before <see cref="NeedToChargeResult.LatestChargeTime"/>; otherwise the globally cheapest
    /// upcoming hour is returned.
    /// </summary>
    public PriceTableEntry GetBestChargeWindow(NeedToChargeResult need, DateTime now)
    {
      // Candidates must not have already ended. Filtering by a calendar-hour floor instead (as
      // this used to) leaves an earlier, already-elapsed quarter-hour in the SAME hour eligible
      // — and since entries are always quarter-hourly (PriceList.NormalizeToQuarterHourly) an
      // hourly-native source normalizes to several consecutive entries tied at the same price,
      // so OrderBy(Price)'s stable sort keeps picking that stale earliest-tied entry for the
      // whole rest of the hour. The caller's `now >= StartTime && now < EndTime` check then
      // fails for a window that's already over, so the charge doesn't start until the NEXT,
      // genuinely different (and possibly pricier) bracket arrives — even though later
      // equally-cheap quarter-hours in the SAME bracket were still available right now.
      var upcoming = _entries.Where(p => p.EndTime > now)
                             .OrderBy(p => p.StartTime).ToList();
      if (need.NeedToCharge)
        return upcoming.Where(p => p.StartTime <= need.LatestChargeTime).OrderBy(p => p.Price).FirstOrDefault();
      return upcoming.OrderBy(p => p.Price).FirstOrDefault();
    }

    // ── Ranking / percentile ─────────────────────────────────────────────────────────────────

    /// <summary>Rank of the entry covering the given time (1 = cheapest).</summary>
    public int GetPriceRank(DateTime time)
    {
      var ordered = _entries.OrderBy(p => p.Price).ToList();
      var entry = _entries.FirstOrDefault(p => p.StartTime <= time && p.EndTime > time);
      return ordered.IndexOf(entry) + 1;
    }

    /// <summary>Price percentile of the entry covering the given time (0 = cheapest, 100 = most expensive).</summary>
    public int GetPricePercentage(DateTime time)
    {
      if (_entries.Count == 0) return -1;
      float minPrice = _entries.Min(p => p.Price);
      float maxPrice = _entries.Max(p => p.Price);
      var entry = _entries.FirstOrDefault(p => p.StartTime <= time && p.EndTime > time);
      return maxPrice - minPrice == 0 ? 0 : (int)Math.Round((entry.Price - minPrice) / (maxPrice - minPrice) * 100, 0);
    }

    /// <summary>Rank of the current hour (1 = cheapest).</summary>
    public int CurrentPriceRank => GetPriceRank(DateTime.Now);

    /// <summary>Price percentile of the current hour (0 = cheapest, 100 = most expensive).</summary>
    public int CurrentPricePercentage => GetPricePercentage(DateTime.Now);

    // ── Local maxima ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns entries that are local price peaks (higher than both neighbours) within
    /// the given time range. Used to identify opportunistic discharge windows.
    /// </summary>
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
