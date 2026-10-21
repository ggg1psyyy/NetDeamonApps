using System;

namespace NetDeamon.apps.PVControl;

/// <summary>
/// Last good reading of a sensor, held through short dropouts. Live 2026-10-21 13:28:02–13:28:17 the
/// SolaX sensors were unknown; the SoC read as 0 %, the simulation saw an empty battery and the
/// inverter charged from the grid at 6.4 kW. A reading older than <c>maxHold</c> is stale: then
/// nothing should be decided from it (PVControl falls back to the inverter's normal mode).
/// </summary>
public sealed class HeldReading(TimeSpan maxHold)
{
  public int? Value { get; private set; }
  public DateTime? LastGood { get; private set; }

  /// <summary>Takes a new reading; <c>null</c> (unknown/unavailable) keeps the last good one.</summary>
  public void Update(DateTime now, int? reading)
  {
    if (reading is not { } r) return;
    Value = r;
    LastGood = now;
  }

  /// <summary>No good reading yet, or the last one is older than <c>maxHold</c>.</summary>
  public bool IsStale(DateTime now) => LastGood is not { } t || now - t > maxHold;
}
