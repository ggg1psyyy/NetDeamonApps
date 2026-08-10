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

    // ── Point-in-time queries ────────────────────────────────────────────────────────────────

    /// <summary>Price at the given time (0 if no matching entry).</summary>
    public float GetPrice(DateTime time) =>
      _entries.FirstOrDefault(p => p.StartTime <= time && p.EndTime > time).Price;

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
      var upcoming = _entries.Where(p => p.StartTime >= now.Date.AddHours(now.Hour))
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
