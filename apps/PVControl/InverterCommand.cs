using System;
using System.Collections.Generic;
using System.Linq;
using NetDeamon.apps.PVControl.Simulator;

namespace NetDeamon.apps.PVControl;

/// <summary>
/// What PVControl asks the inverter to do, versioned: the HA script that applies it stores the
/// <see cref="Seq"/> it applied, and the reconciler automation compares desired, applied and the
/// inverter's actual state. Manufacturer-independent — the register writes live in HA.
/// </summary>
/// <param name="Mode">Inverter mode.</param>
/// <param name="BatteryPowerW">Battery power, W: positive charges, negative discharges, 0 holds (or the inverter's own control in normal).</param>
/// <param name="Bulk">A large grid charge at full power (HA may use a faster path than for small top-ups).</param>
/// <param name="Seq">Version; increases with every change of mode, power or <see cref="Bulk"/>.</param>
public readonly record struct InverterCommand(InverterModes Mode, int BatteryPowerW, bool Bulk, int Seq)
{
  /// <summary>The command of this cycle: the previous one if nothing changed, else a new version.</summary>
  /// <param name="previous">Command published last; null = none yet.</param>
  /// <param name="mode">Mode now.</param>
  /// <param name="batteryPowerW">Battery power now, W.</param>
  /// <param name="bulk">Bulk charge now.</param>
  /// <param name="restoredSeq">Last version before a restart (continues counting from it).</param>
  /// <returns>The command.</returns>
  public static InverterCommand Next(InverterCommand? previous, InverterModes mode, int batteryPowerW, bool bulk, int restoredSeq = 0)
  {
    if (previous is { } p && p.Mode == mode && p.BatteryPowerW == batteryPowerW && p.Bulk == bulk) return p;
    return new InverterCommand(mode, batteryPowerW, bulk, (previous?.Seq ?? restoredSeq) + 1);
  }

  /// <summary>
  /// Battery power of a mode: charging from the grid at the import cap's limit (whole amps, so it
  /// changes rarely), discharging at the maximum, otherwise 0.
  /// </summary>
  /// <param name="mode">Inverter mode.</param>
  /// <param name="chargeLimitA">Charge current the import cap allows (<see cref="ImportCap.ImportCapGuard.NextLimit"/>), A.</param>
  /// <param name="maxA">Hardware maximum, A.</param>
  /// <param name="voltageV">Battery voltage, V.</param>
  /// <returns>W: positive charges, negative discharges.</returns>
  public static int PowerFor(InverterModes mode, double chargeLimitA, double maxA, int voltageV) => mode switch
  {
    InverterModes.force_charge or InverterModes.force_charge_grid_only => (int)Math.Round(Math.Min(chargeLimitA, maxA) * voltageV),
    InverterModes.force_discharge => -(int)Math.Round(maxA * voltageV),
    _ => 0,
  };

  /// <summary>Energy of the grid charge planned from now: the contiguous force_charge slots from the running one.</summary>
  /// <param name="timeline">The plan's slots in time order.</param>
  /// <param name="now">Current time.</param>
  /// <returns>Wh; 0 if the running slot isn't a force_charge.</returns>
  public static int PlannedChargeWh(IReadOnlyList<SimulationSlot> timeline, DateTime now) =>
    timeline.SkipWhile(s => s.Time.AddMinutes(TimeRange.SlotMinutes) <= now)
      .TakeWhile(s => s.State.Mode == InverterModes.force_charge)
      .Sum(s => s.BatteryChargeWh);

  /// <summary>
  /// Whether a grid charge counts as bulk: decided when it starts (planned energy at least
  /// <paramref name="minPercent"/> of the battery) and kept for the session; off while the import cap
  /// throttles it (a bulk path at full power can't follow the cap).
  /// </summary>
  /// <param name="mode">Mode now.</param>
  /// <param name="previousMode">Mode published last cycle.</param>
  /// <param name="sessionBulk">Bulk decision of the running session (kept while it lasts).</param>
  /// <param name="plannedChargeWh">Energy of the planned charge (<see cref="PlannedChargeWh"/>).</param>
  /// <param name="capacityWh">Battery capacity, Wh.</param>
  /// <param name="minPercent">Share of the battery from which a charge counts as bulk, %; 0 = never.</param>
  /// <param name="throttled">The import cap limits the charge below the maximum.</param>
  /// <param name="forced">The user forces force_charge (mode override): always bulk, the plan doesn't know about it.</param>
  /// <returns>(bulk now, session decision to keep).</returns>
  public static (bool Bulk, bool SessionBulk) BulkFor(InverterModes mode, InverterModes previousMode, bool sessionBulk,
    int plannedChargeWh, int capacityWh, int minPercent, bool throttled, bool forced = false)
  {
    if (mode != InverterModes.force_charge || minPercent <= 0) return (false, false);
    bool session = forced
      || (previousMode == InverterModes.force_charge ? sessionBulk : plannedChargeWh >= capacityWh * minPercent / 100);
    return (session && !throttled, session);
  }
}
