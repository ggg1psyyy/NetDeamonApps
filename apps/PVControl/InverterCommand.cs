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

  /// <summary>The grid charge the plan runs now, else the next one: contiguous force_charge (or force_charge_grid_only)
  /// slots from the running slot on — its energy, the SoC it reaches and its end.</summary>
  /// <param name="timeline">The plan's slots in time order.</param>
  /// <param name="now">Current time.</param>
  /// <param name="capacityWh">Battery capacity, Wh (the SoC after a charge that ends with the plan).</param>
  /// <returns>The charge; null if none is planned.</returns>
  public static PlannedCharge? NextCharge(IReadOnlyList<SimulationSlot> timeline, DateTime now, int capacityWh)
  {
    var rest = timeline.SkipWhile(s => s.Time.AddMinutes(TimeRange.SlotMinutes) <= now).ToList();
    int first = rest.FindIndex(s => s.State.Mode is InverterModes.force_charge or InverterModes.force_charge_grid_only);
    if (first < 0) return null;
    int end = first;
    while (end < rest.Count && rest[end].State.Mode == rest[first].State.Mode) end++;
    var last = rest[end - 1];
    int targetSoc = end < rest.Count
      ? rest[end].SoC                                                     // SoC at the start of the slot after the charge
      : Math.Min(100, Soc.FromWh(Soc.ToWh(last.SoC, capacityWh) + last.BatteryChargeWh - last.BatteryDischargeWh, capacityWh));
    return new PlannedCharge(rest.Skip(first).Take(end - first).Sum(s => s.BatteryChargeWh), targetSoc,
      rest[first].Time, last.Time.AddMinutes(TimeRange.SlotMinutes));
  }

  /// <summary>
  /// Whether a grid charge runs on the full-power (bulk) path: as the plan chose it
  /// (<see cref="EnergySimulator.ChargesBulk"/>), always for a force_charge the user forces (the plan
  /// doesn't know about the override), never while the import cap throttles it (a bulk path at full
  /// power can't follow the cap; the remote-controlled one can).
  /// </summary>
  /// <param name="mode">Mode now.</param>
  /// <param name="planBulk">Bulk in the plan's running slot.</param>
  /// <param name="forced">The user forces force_charge (mode override).</param>
  /// <param name="throttled">The import cap limits the charge below what the path would charge.</param>
  /// <returns>True: bulk.</returns>
  public static bool BulkFor(InverterModes mode, bool planBulk, bool forced, bool throttled) =>
    mode == InverterModes.force_charge && !throttled && (forced || planBulk);

  /// <summary>Shortest run of a grid charge once started.</summary>
  public static readonly TimeSpan MinChargeRun = TimeSpan.FromMinutes(1);

  /// <summary>
  /// Keeps a grid charge running for <see cref="MinChargeRun"/> after it started, unless the user
  /// changes the mode (override): a plan that flips back a moment later shouldn't stop it.
  /// </summary>
  /// <param name="proposed">Mode the plan proposes now.</param>
  /// <param name="running">Mode published last.</param>
  /// <param name="runningSince">When <paramref name="running"/> started.</param>
  /// <param name="now">Current time.</param>
  /// <returns>The mode to publish.</returns>
  public static InverterState HoldMinimumRun(InverterState proposed, InverterState running, DateTime runningSince, DateTime now) =>
    running.Mode is InverterModes.force_charge or InverterModes.force_charge_grid_only
    && proposed.Mode != running.Mode
    && proposed.ModeReason != ForceChargeReasons.UserMode && running.ModeReason != ForceChargeReasons.UserMode
    && now - runningSince < MinChargeRun
      ? running
      : proposed;
}

/// <summary>A grid charge in the plan (<see cref="InverterCommand.NextCharge"/>).</summary>
/// <param name="EnergyWh">Energy into the battery, Wh.</param>
/// <param name="TargetSocPercent">SoC at its end, %.</param>
/// <param name="Start">Start of its first slot.</param>
/// <param name="End">End of its last slot.</param>
public readonly record struct PlannedCharge(int EnergyWh, int TargetSocPercent, DateTime Start, DateTime End);
