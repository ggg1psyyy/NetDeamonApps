using System;

namespace NetDeamon.apps.PVControl.Simulator;

/// <summary>A load on top of the base load forecast (EV session, warm-water heating), added to the simulated slots it covers.</summary>
public class ExtraLoad
{
  /// <summary>Name for diagnostics, e.g. "EV Charger".</summary>
  public string Name { get; init; } = "";

  /// <summary>Priority (higher wins); not used by the simulation yet.</summary>
  public int Priority { get; init; } = 10;

  /// <summary>Switch-on time.</summary>
  public DateTime StartTime { get; init; }

  /// <summary>Switch-off time (exclusive).</summary>
  public DateTime EndTime { get; init; }

  /// <summary>Constant power while running, W.</summary>
  public int PowerW { get; init; }

  /// <summary>Not fed from the house battery: PV surplus first, the rest directly from the grid (the
  /// battery is held while it runs) — battery wear and round-trip losses. PriorityPlus's grid sessions.</summary>
  public bool FromGrid { get; init; }

  /// <summary>Energy of the load within a 15-min slot (partial overlap pro rata).</summary>
  /// <param name="slotStart">Start of the slot.</param>
  /// <returns>Wh; 0 outside the slot.</returns>
  public int GetWhForSlot(DateTime slotStart) =>
    (int)Energy.Wh(PowerW, TimeRange.Overlap(slotStart, slotStart + TimeRange.Slot, StartTime, EndTime));

  /// <summary>Energy of the whole session, Wh.</summary>
  public double EnergyWh => EnergyFromWh(null);

  /// <summary>Energy the session still delivers from a time on: a session that began in the running slot only
  /// from now (the simulation counts the rest of that slot only).</summary>
  /// <param name="now">Current time; null = the whole session.</param>
  /// <returns>Wh ≥ 0.</returns>
  public double EnergyFromWh(DateTime? now) =>
    Energy.Wh(PowerW, EndTime - (now is { } n && n > StartTime ? (n < EndTime ? n : EndTime) : StartTime));

  /// <summary>Whether the load runs at a time.</summary>
  /// <param name="t">Time to test.</param>
  /// <returns>True if StartTime ≤ <paramref name="t"/> &lt; EndTime.</returns>
  public bool Covers(DateTime t) => TimeRange.Contains(StartTime, EndTime, t);

  /// <summary>The same load (name, priority, power) in another time range.</summary>
  /// <param name="start">Switch-on time.</param>
  /// <param name="end">Switch-off time.</param>
  /// <returns>A new load.</returns>
  public ExtraLoad Between(DateTime start, DateTime end) =>
    new() { Name = Name, Priority = Priority, StartTime = start, EndTime = end, PowerW = PowerW, FromGrid = FromGrid };

  /// <summary>The same load, fed from PV and the grid only or also from the battery.</summary>
  /// <param name="fromGrid">See <see cref="FromGrid"/>.</param>
  /// <returns>A new load.</returns>
  public ExtraLoad WithFromGrid(bool fromGrid) =>
    new() { Name = Name, Priority = Priority, StartTime = StartTime, EndTime = EndTime, PowerW = PowerW, FromGrid = fromGrid };
}
