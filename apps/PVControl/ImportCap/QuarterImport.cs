using System;
using System.Collections.Generic;
using System.Linq;

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
  DateTime _startTime;
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
      // The finished quarter ends with this update's readings: its energy spans from the last update before it
      // to the first after it (up to ~30 s more than the 15 min at 15-s cycles, counted twice with the neighbours).
      // The live guard keeps that overestimate as a margin (ImportedWh); the finished quarter — the month's peak,
      // the over-cap warning — is scaled to exactly 15 min, what the meter bills.
      if (_quarter is { } old && _start is not null && old == quarter - TimeRange.Slot
          && _counter.IncreaseSince(_start) is { } kwh)
      {
        double spanned = (ToUtc(now) - _startTime).TotalSeconds;
        double toQuarter = spanned > TimeRange.Slot.TotalSeconds ? TimeRange.Slot.TotalSeconds / spanned : 1;
        finished = new QuarterResult(old.ToLocalTime(), Energy.SlotW(kwh * toQuarter));
      }
      // Starts from the last update before the quarter — not after a restart or a gap, nor without any reading then.
      bool fromStart = _previousTime is { } t && quarter - t <= MaxStartGap && _previous is { } p && p.Any(v => v is not null);
      _start = fromStart ? _previous : null;
      _startTime = fromStart ? _previousTime!.Value : default;
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
