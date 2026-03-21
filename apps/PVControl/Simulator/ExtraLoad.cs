using System;

namespace NetDeamon.apps.PVControl.Simulator;

/// <summary>
/// Represents a known extra load that is not captured in the historical load prediction —
/// e.g. car charging, warm-water heat pump, washing machine running on a schedule.
/// The simulator adds this load on top of the base house-load prediction for the relevant slots,
/// so the SoC forecast and mode decisions account for the additional energy draw.
/// </summary>
public class ExtraLoad
{
  /// <summary>Human-readable name shown in diagnostics (e.g. "EV Charger").</summary>
  public string Name { get; init; } = "";

  /// <summary>
  /// Scheduling priority integer (higher = wins in conflict resolution).
  /// Emergency mode always overrides regardless of this value. For future use.
  /// </summary>
  public int Priority { get; init; } = 10;

  /// <summary>Wall-clock time when this load switches on.</summary>
  public DateTime StartTime { get; init; }

  /// <summary>Wall-clock time when this load switches off.</summary>
  public DateTime EndTime { get; init; }

  /// <summary>Constant power draw in Watts while active.</summary>
  public int PowerW { get; init; }

  /// <summary>
  /// Returns the energy consumed by this load within the given 15-minute slot, in Wh.
  /// Handles partial overlap: if the load starts or ends mid-slot the proportional
  /// energy is returned (e.g. a load that starts 7.5 min into the slot contributes half).
  /// Returns 0 if the load is completely outside this slot.
  /// </summary>
  public int GetWhForSlot(DateTime slotStart)
  {
    var slotEnd = slotStart.AddMinutes(15);

    // No overlap with this slot
    if (EndTime <= slotStart || StartTime >= slotEnd)
      return 0;

    // Clamp to slot boundaries and convert overlap duration to hours → Wh
    var overlapTicks = Math.Min(slotEnd.Ticks, EndTime.Ticks) - Math.Max(slotStart.Ticks, StartTime.Ticks);
    return (int)(new TimeSpan(overlapTicks).TotalHours * PowerW);
  }
}
