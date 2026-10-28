using System;
using System.Collections.Generic;
using System.Linq;

namespace NetDeamon.apps.PVControl.ImportCap;

/// <summary>
/// One energy quantity counted by several sources (kWh counters) in priority order: the first source
/// is used while it works, the next ones only while it fails. A source fails while it has no reading,
/// or while it stays frozen although a later source keeps counting (<see cref="FrozenKwh"/>) — a
/// counter that doesn't change is valid (no import), so "no update" alone isn't a failure.
/// </summary>
/// <param name="sources">Number of sources.</param>
public sealed class PriorityCounter(int sources)
{
  /// <summary>A source counts as frozen once a later source counted this much since its last change, kWh.</summary>
  public const double FrozenKwh = 0.2;

  readonly double?[] _values = new double?[sources];
  // Values of all sources when source i last changed (its frozen check compares against these).
  readonly double?[][] _atChange = Enumerable.Range(0, sources).Select(_ => new double?[sources]).ToArray();

  /// <summary>Current readings, kWh; null where a source has none.</summary>
  public IReadOnlyList<double?> Values => _values;

  /// <summary>Takes the sources' readings.</summary>
  /// <param name="readings">kWh per source in priority order; null = unavailable/unusable.</param>
  public void Update(IReadOnlyList<double?> readings)
  {
    var changed = new bool[_values.Length];
    for (int i = 0; i < _values.Length; i++)
    {
      var r = i < readings.Count ? readings[i] : null;
      changed[i] = r is { } v && _values[i] != v;
      _values[i] = r;
    }
    for (int i = 0; i < _values.Length; i++)
      for (int j = 0; j < _values.Length; j++)
        if (changed[i] || _atChange[i][j] is null) // a source first seen later starts its comparison now
          _atChange[i][j] = _values[j];
  }

  /// <summary>Whether a source fails: no reading, or frozen while a later source counts.</summary>
  /// <param name="i">Source index.</param>
  public bool Failed(int i)
  {
    if (_values[i] is null) return true;
    for (int j = i + 1; j < _values.Length; j++)
      if (_values[j] is { } now && _atChange[i][j] is { } then && now - then >= FrozenKwh)
        return true;
    return false;
  }

  /// <summary>
  /// The source to use for a closed period from each source's increase in it: the first with an increase,
  /// unless a later source counted at least <see cref="FrozenKwh"/> more — then it was (partly) frozen,
  /// e.g. a wM-Bus receiver online but without telegrams keeps its last value.
  /// </summary>
  /// <param name="increases">kWh per source in priority order; null = no readings.</param>
  /// <returns>Index; -1 if none has readings.</returns>
  public static int FirstWorking(IReadOnlyList<double?> increases)
  {
    for (int i = 0; i < increases.Count; i++)
    {
      if (increases[i] is not { } mine) continue;
      bool frozen = false;
      for (int j = i + 1; j < increases.Count; j++)
        if (increases[j] is { } later && later - mine >= FrozenKwh) frozen = true;
      if (!frozen) return i;
    }
    return -1;
  }

  /// <summary>Index of the source in use: the first that doesn't fail; -1 if all fail.</summary>
  public int ActiveIndex => Enumerable.Range(0, _values.Length).FirstOrDefault(i => !Failed(i), -1);

  /// <summary>Reading of the source in use, kWh; null if all fail.</summary>
  public double? ActiveValue => ActiveIndex is >= 0 and var i ? _values[i] : null;

  /// <summary>
  /// Energy counted since a snapshot of the readings, by the first working source that has a snapshot
  /// value (a source switched to mid-interval counts from its own start value). Counter resets and
  /// glitches as in <see cref="global::PVControl.DataLogger.CounterIncrease"/>.
  /// </summary>
  /// <param name="since">Readings at the start, per source (<see cref="Values"/> then).</param>
  /// <returns>kWh; null if no working source has a start value.</returns>
  public double? IncreaseSince(IReadOnlyList<double?> since)
  {
    for (int i = 0; i < _values.Length; i++)
      if (!Failed(i) && i < since.Count && since[i] is { } start && _values[i] is { } now)
        return global::PVControl.DataLogger.CounterIncrease([(float)start, (float)now]);
    return null;
  }

  /// <summary>Snapshot of the readings (start of an interval for <see cref="IncreaseSince"/>).</summary>
  public double?[] Snapshot() => (double?[])_values.Clone();

  /// <summary>
  /// Start snapshot from one stored total after a restart: it belongs to the source whose reading is
  /// at or just above it (the sources' totals differ widely); the others start from their reading now.
  /// </summary>
  /// <param name="storedTotal">Total stored at the last booking, kWh.</param>
  /// <param name="maxGapKwh">Most energy plausible since then.</param>
  /// <returns>The snapshot.</returns>
  public double?[] SnapshotFrom(double storedTotal, double maxGapKwh = 100)
  {
    var snapshot = Snapshot();
    int owner = Enumerable.Range(0, _values.Length)
      .Where(i => _values[i] is { } v && v - storedTotal >= 0 && v - storedTotal <= maxGapKwh)
      .OrderBy(i => _values[i]!.Value - storedTotal).FirstOrDefault(-1);
    if (owner >= 0) snapshot[owner] = storedTotal;
    return snapshot;
  }
}
