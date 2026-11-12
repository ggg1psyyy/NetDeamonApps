using System;
using System.Collections.Generic;
using System.Linq;

namespace NetDeamon.apps
{
  /// <summary>Helpers for prediction windows (Wh per 15-min slot) and quarter-hour times.</summary>
  public static class PVControlExtensions
  {
    /// <summary>Adds a second forecast to the first per time (e.g. two PV roof sides).</summary>
    /// <param name="list1">First forecast; modified in place.</param>
    /// <param name="list2">Forecast to add; missing times are added.</param>
    /// <returns>The sum, ordered by time.</returns>
    public static Dictionary<DateTime, int> CombineForecastLists(this Dictionary<DateTime, int> list1, Dictionary<DateTime, int> list2)
    {
      foreach (var item in list2)
      {
        if (list1.ContainsKey(item.Key))
          list1[item.Key] += item.Value;
        else
          list1.Add(item.Key, item.Value);
      }
      return list1.OrderBy(o => o.Key).ToDictionary();
    }
    // The three helpers below run inside the simulator's per-slot loop (thousands of times per
    // planning cycle), so they're single passes without allocation. "First" = first in the
    // dictionary's enumeration order among entries with start <= key <= end (default = unbounded).
    /// <summary>Whether a time lies in [start, end]; default bounds are open.</summary>
    /// <param name="key">Time to test.</param>
    /// <param name="start">Start (inclusive); default = unbounded.</param>
    /// <param name="end">End (inclusive); default = unbounded.</param>
    private static bool InRange(DateTime key, DateTime start, DateTime end) =>
      (start == default || key >= start) && (end == default || key <= end);

    /// <summary>First entry with the lowest value in a time range.</summary>
    /// <param name="list">Values by time.</param>
    /// <param name="start">Range start (inclusive); default = unbounded.</param>
    /// <param name="end">Range end (inclusive); default = unbounded.</param>
    /// <returns>The entry; default if the range is empty.</returns>
    public static KeyValuePair<DateTime, int> FirstMinOrDefault(this Dictionary<DateTime, int> list, DateTime start = default, DateTime end = default) =>
      FirstExtremeOrDefault(list, -1, start, end);
    /// <summary>First entry with the highest value in a time range.</summary>
    /// <param name="list">Values by time.</param>
    /// <param name="start">Range start (inclusive); default = unbounded.</param>
    /// <param name="end">Range end (inclusive); default = unbounded.</param>
    /// <returns>The entry; default if the range is empty.</returns>
    public static KeyValuePair<DateTime, int> FirstMaxOrDefault(this Dictionary<DateTime, int> list, DateTime start = default, DateTime end = default) =>
      FirstExtremeOrDefault(list, 1, start, end);
    /// <summary>First entry with the lowest or highest value in a time range.</summary>
    /// <param name="list">Values by time.</param>
    /// <param name="sign">−1 = lowest, +1 = highest.</param>
    /// <param name="start">Range start (inclusive); default = unbounded.</param>
    /// <param name="end">Range end (inclusive); default = unbounded.</param>
    /// <returns>The entry; default if the range is empty.</returns>
    private static KeyValuePair<DateTime, int> FirstExtremeOrDefault(Dictionary<DateTime, int> list, int sign, DateTime start, DateTime end)
    {
      KeyValuePair<DateTime, int> best = default;
      bool found = false;
      foreach (var kv in list)
        if (InRange(kv.Key, start, end) && (!found || sign * kv.Value > sign * best.Value)) { best = kv; found = true; }
      return best;
    }
    /// <summary>First entry at or below a value in a time range.</summary>
    /// <param name="list">Values by time.</param>
    /// <param name="underValue">Threshold (inclusive).</param>
    /// <param name="start">Range start (inclusive); default = unbounded.</param>
    /// <param name="end">Range end (inclusive); default = unbounded.</param>
    /// <returns>The entry; default if there is none.</returns>
    public static KeyValuePair<DateTime, int> FirstUnderOrDefault(this Dictionary<DateTime, int> list, int underValue, DateTime start = default, DateTime end = default)
    {
      foreach (var kv in list)
        if (InRange(kv.Key, start, end) && kv.Value <= underValue) return kv;
      return default;
    }
    /// <summary>Entry of the quarter-hour a time lies in.</summary>
    /// <param name="list">Values by quarter-hour start.</param>
    /// <param name="time">Any time within the quarter.</param>
    /// <returns>The entry; (default, 0) if missing.</returns>
    public static KeyValuePair<DateTime, int> GetEntryAtTime(this Dictionary<DateTime, int> list, DateTime time)
    {
      if (list.TryGetValue(time.RoundToNearestQuarterHour(), out var entry))
        return new KeyValuePair<DateTime, int>(time.RoundToNearestQuarterHour(), entry);
      else
        return new KeyValuePair<DateTime, int>(default, 0);
    }
    /// <summary>Running sum per day (restarts at each new date).</summary>
    /// <param name="list">Values by time, in time order.</param>
    /// <returns>Cumulative values by time.</returns>
    public static Dictionary<DateTime, int> GetRunningSumsDaily(this Dictionary<DateTime, int> list)
    {
      return list.Aggregate(new Dictionary<DateTime, int>(), (acc, x) =>
      {
        if (acc.Count == 0 || x.Key.Date != acc.Last().Key.Date)
          acc.Add(x.Key, x.Value);
        else
          acc.Add(x.Key, acc.Last().Value + x.Value);
        return acc;
      });
    }
    /// <summary>Sum of the values in a time range.</summary>
    /// <param name="list">Values by time.</param>
    /// <param name="start">Range start (inclusive); default = unbounded.</param>
    /// <param name="end">Range end (inclusive); default = unbounded.</param>
    /// <returns>The sum.</returns>
    public static int GetSum(this Dictionary<DateTime, int> list, DateTime start = default, DateTime end = default) =>
      list.Where(t => InRange(t.Key, start, end)).Sum(s => s.Value);
    /// <summary>Mean of the values in a time range, rounded.</summary>
    /// <param name="list">Values by time.</param>
    /// <param name="start">Range start (inclusive); default = unbounded.</param>
    /// <param name="end">Range end (inclusive); default = unbounded.</param>
    /// <returns>The rounded mean; throws if the range is empty.</returns>
    public static int GetAverage(this Dictionary<DateTime, int> list, DateTime start = default, DateTime end = default) =>
      (int)Math.Round(list.Where(t => InRange(t.Key, start, end)).Average(s => s.Value), 0);
    /// <summary>Start of the quarter-hour a time lies in (floors, despite the name).</summary>
    /// <param name="time">Any time.</param>
    /// <returns>The quarter start; seconds dropped.</returns>
    public static DateTime RoundToNearestQuarterHour(this DateTime time) => time.Floor(TimeRange.SlotMinutes);

    /// <summary>A UTC time as local time; other kinds unchanged (DB timestamps).</summary>
    /// <param name="time">Time of any kind.</param>
    /// <returns>Local time.</returns>
    public static DateTime AsLocal(this DateTime time) => time.Kind == DateTimeKind.Utc ? time.ToLocalTime() : time;

    /// <summary>Next quarter-hour strictly after a time.</summary>
    /// <param name="time">Any time.</param>
    /// <returns>The next quarter start.</returns>
    public static DateTime GetNextQuarterHour(this DateTime time) => time.RoundToNearestQuarterHour() + TimeRange.Slot;
    /// <summary>Fills a prediction window with zeros: today 00:00 → tomorrow 23:45 in 15-min slots.</summary>
    /// <param name="data">Window to clear and fill.</param>
    /// <param name="now">Any time of today.</param>
    public static void ClearAndCreateEmptyPredictionData(this Dictionary<DateTime, int> data, DateTime now)
    {
      data.Clear();
      for (var time = now.Date; time < now.Date.AddDays(2); time = time.AddMinutes(15))
      {
        data.Add(time, 0);
      }
    }
  }
}
