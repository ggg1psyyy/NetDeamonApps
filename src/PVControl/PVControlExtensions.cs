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
