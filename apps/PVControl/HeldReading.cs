using System;

namespace NetDeamon.apps.PVControl;

/// <summary>Last good reading of a sensor, held through short dropouts; older than the hold it is stale (nothing is decided from it).</summary>
/// <param name="maxHold">How long a reading is held.</param>
public sealed class HeldReading(TimeSpan maxHold)
{
  /// <summary>Last good reading; null if none yet.</summary>
  public int? Value { get; private set; }
  /// <summary>Time of the last good reading.</summary>
  public DateTime? LastGood { get; private set; }

  /// <summary>Takes a new reading.</summary>
  /// <param name="now">Time of the reading.</param>
  /// <param name="reading">The value; null (unknown/unavailable) keeps the last good one.</param>
  public void Update(DateTime now, int? reading)
  {
    if (reading is not { } r) return;
    Value = r;
    LastGood = now;
  }

  /// <summary>Whether the reading is too old.</summary>
  /// <param name="now">Current time.</param>
  /// <returns>True without a good reading or if it is older than the hold.</returns>
  public bool IsStale(DateTime now) => LastGood is not { } t || now - t > maxHold;
}
