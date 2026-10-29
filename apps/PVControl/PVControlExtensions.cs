using System;
using NetDaemon.Client;
using NetDaemon.HassModel.Entities;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Web;

namespace NetDeamon.apps
{
  // PVControl's own types and helpers (the generic HA helpers are in Common's Extensions).
  /// <summary>Price of a time range.</summary>
  /// <param name="startTime">Start (inclusive).</param>
  /// <param name="endTime">End (exclusive).</param>
  /// <param name="price">Price in €/kWh.</param>
  public struct PriceTableEntry(DateTime startTime, DateTime endTime, float price)
  {
    [JsonPropertyName("start_time")]
    public DateTime StartTime { get; set; } = startTime;
    [JsonPropertyName("end_time")]
    public DateTime EndTime { get; set; } = endTime;
    [JsonPropertyName("price_per_kwh")]
    public float Price { get; set; } = price;

    /// <summary>Whether a time lies in this entry.</summary>
    /// <param name="t">Time to test.</param>
    /// <returns>True if StartTime ≤ <paramref name="t"/> &lt; EndTime.</returns>
    public readonly bool Covers(DateTime t) => TimeRange.Contains(StartTime, EndTime, t);
  }
  public enum InverterModes
  {
    automatic,
    normal,
    force_charge,
    force_charge_grid_only,
    grid_only,
    force_discharge,
    feedin_priority,
  }
  public enum BatteryStatuses
  {
    idle,
    charging,
    discharging,
    unknown,
  }
  public enum ForceChargeReasons
  {
    None,
    GoingUnderPreferredMinima,
    GoingUnderAbsoluteMinima,
    ForcedChargeAtMinimumPrice,
    ImportPriceNegative,
    ExportPriceNegative,
    OpportunisticDischarge,
    NextHourCheaper,
    BugFixMode,
    UserMode,
    /// <summary>PV surplus fed in now (battery not charged) to keep room for PV in coming negative-export hours.</summary>
    RoomForNegativeExport,
    /// <summary>Battery held while a load that never draws it runs (PriorityPlus EV from the grid).</summary>
    LoadFromGrid,
  }

  public enum PriceResolution
  {
    QuarterHourly,
    Hourly,
  }

  public enum PVPeriods
  {
    BeforePV,
    InPVPeriod,
    AfterPV,
  }
  // Optimal/Priority/PriorityPlus form a numbered ladder, climbed from the bottom in
  // HouseEnergy.FindLoadWindow: each mode's ruleset runs in full, and if it doesn't allow
  // charging, the next-higher-numbered ruleset gets a turn. The gaps between 10/20/30 are
  // deliberate room for a future mode to slot in between without renumbering anything else.
  // Off/Emergency are unrelated, independent gates (handled before the ladder even starts),
  // so their own values don't need to fit the numbering scheme.
  public enum LoadSchedulingMode
  {
    Off = 0,
    Emergency = 1,
    Optimal = 10,
    Priority = 20,
    PriorityPlus = 30,
  }
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

  /// <summary>
  /// An energy counter with coarse steps made continuous: the last counter value plus the power
  /// integrated since it last changed, at most one step.
  /// </summary>
  /// <param name="resolutionWh">Step of the counter in Wh (inverter's PV energy today: 100).</param>
  public sealed class CounterInterpolator(int resolutionWh)
  {
    private int? _counter;
    private double _sinceStepWh;
    private DateTime? _powerAt;
    private int _powerW;

    /// <summary>Adds a power reading.</summary>
    /// <param name="at">Time of the reading.</param>
    /// <param name="watts">Power in W; negative counts as 0.</param>
    public void AddPower(DateTime at, int watts)
    {
      Accumulate(at);
      _powerW = Math.Max(0, watts);
      _powerAt = at;
    }

    /// <summary>The counter made continuous.</summary>
    /// <param name="now">Current time.</param>
    /// <param name="counterWh">Counter as read now, in Wh.</param>
    /// <returns>Counter + energy since its last step (at most one step), in Wh.</returns>
    public int Value(DateTime now, int counterWh)
    {
      if (counterWh != _counter)
      {
        _counter = counterWh;
        _sinceStepWh = 0;
        if (_powerAt is not null) _powerAt = now;
      }
      Accumulate(now);
      return counterWh + (int)Math.Min(_sinceStepWh, resolutionWh);
    }

    /// <summary>Integrates the last power reading up to a time.</summary>
    /// <param name="at">Integrate until here.</param>
    private void Accumulate(DateTime at)
    {
      if (_powerAt is { } p && at > p)
      {
        _sinceStepWh += Energy.Wh(_powerW, at - p);
        _powerAt = at;
      }
    }
  }

  /// <summary>
  /// Average of the values of a time window. Values also expire on read; the latest one is always
  /// kept (sensors that report only on change still have their current state).
  /// </summary>
  /// <param name="window">Window length.</param>
  /// <param name="utcNow">Clock (UTC); null = system clock. For tests.</param>
  public class RunningIntAverage(TimeSpan window, Func<DateTime>? utcNow = null)
  {
    private Queue<(DateTime timestamp, int value)> _Values = [];
    private TimeSpan _Window = window;
    private readonly Func<DateTime> _utcNow = utcNow ?? (() => DateTime.UtcNow);

    /// <summary>Adds a value with the current time.</summary>
    /// <param name="value">Value to add.</param>
    public void AddValue(int value)
    {
      _Values.Enqueue((_utcNow(), value));
      Prune();
    }

    /// <summary>Drops values older than the window, keeping at least the latest one.</summary>
    private void Prune()
    {
      DateTime now = _utcNow();
      while (_Values.Count > 1 && now - _Values.Peek().timestamp > _Window)
        _Values.Dequeue();
    }
    /// <summary>Number of values in the window.</summary>
    public int Count
    {
      get { return _Values.Count; }
    }
    /// <summary>Average of the values in the window.</summary>
    /// <returns>Rounded average; int.MinValue without any value.</returns>
    public int GetAverage()
    {
      if (_Values.Count == 0)
        return int.MinValue;
      Prune();
      return (int)Math.Round(_Values.Average(v => v.value), 0);
    }

    /// <summary>Same as <see cref="GetAverage"/>.</summary>
    public int CurrentAverage
    {
      get => GetAverage();
    }
    /// <summary>Drops all values and starts again with 0.</summary>
    public void Reset()
    {
      _Values = [];
      AddValue(0);
    }
  }
}
