using System;
using System.Collections.Generic;

namespace NetDeamon.apps.PVControl.ImportCap;

/// <summary>A finished 15-min block: its start and mean import power.</summary>
/// <param name="Start">Start of the quarter.</param>
/// <param name="AvgKw">Mean import, kW.</param>
public readonly record struct QuarterResult(DateTime Start, double AvgKw);

/// <summary>
/// Grid import of the running 15-min block (clock quarters, as the meter bills them) from the
/// import counters (<see cref="PriorityCounter"/>). Each quarter starts from the readings of the last
/// update before it began, so the energy is rather over- than under-counted.
/// </summary>
/// <param name="sources">Number of counter sources.</param>
public sealed class QuarterImport(int sources)
{
  /// <summary>An update this long before a quarter began still counts as its start reading.</summary>
  public static readonly TimeSpan MaxStartGap = TimeSpan.FromMinutes(1);

  readonly PriorityCounter _counter = new(sources);
  double?[]? _start;
  double?[]? _previous;
  DateTime? _previousTime;
  DateTime? _quarter;

  /// <summary>The counters.</summary>
  public PriorityCounter Counter => _counter;

  /// <summary>Start of the running quarter, local time; null before the first update.</summary>
  public DateTime? QuarterStart => _quarter?.ToLocalTime();

  /// <summary>Takes the counters' readings.</summary>
  /// <param name="now">Time of the readings (local, or UTC-kind).</param>
  /// <param name="readingsKwh">kWh per source in priority order; null = unavailable.</param>
  /// <returns>The quarter that just ended, if this update started a new one and the old one was tracked from its start.</returns>
  public QuarterResult? Update(DateTime now, IReadOnlyList<double?> readingsKwh)
  {
    // Quarters as UTC instants: across a clock change the local quarters aren't consecutive (the
    // repeated hour in October, the skipped one in March), and the quarter at the change would be lost.
    var quarter = DateTime.SpecifyKind(ToUtc(now).Floor(TimeRange.SlotMinutes), DateTimeKind.Utc);
    _counter.Update(readingsKwh);
    QuarterResult? finished = null;
    if (_quarter != quarter)
    {
      // The finished quarter ends with this update's readings (a few seconds of the new one count to it).
      if (_quarter is { } old && _start is not null && old == quarter - TimeRange.Slot
          && _counter.IncreaseSince(_start) is { } kwh)
        finished = new QuarterResult(old.ToLocalTime(), kwh * 60 / TimeRange.SlotMinutes);
      // Starts from the last update before the quarter — not after a restart or a gap.
      _start = _previousTime is { } t && quarter - t <= MaxStartGap ? _previous : null;
      _quarter = quarter;
    }
    _previous = _counter.Snapshot();
    _previousTime = ToUtc(now);
    return finished;
  }

  /// <summary>A time as a UTC instant (a local time of the ambiguous October hour as DateTime.Now gives it).</summary>
  /// <param name="time">Local or UTC-kind time.</param>
  static DateTime ToUtc(DateTime time) => time.Kind == DateTimeKind.Utc ? time : time.ToUniversalTime();

  /// <summary>Whether the running quarter is tracked from its start (not the first one after a restart).</summary>
  public bool Complete => _start is not null;

  /// <summary>Energy imported in the running quarter so far, Wh; null if not tracked from its start or no source works.</summary>
  public double? ImportedWh => _start is null ? null : _counter.IncreaseSince(_start) * 1000;

  /// <summary>Seconds of the running quarter that have passed.</summary>
  /// <param name="now">Current time.</param>
  public double ElapsedSeconds(DateTime now) => _quarter is { } q ? Math.Clamp((ToUtc(now) - q).TotalSeconds, 0, TimeRange.SlotMinutes * 60) : 0;
}
