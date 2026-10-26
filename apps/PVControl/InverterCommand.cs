using System;

namespace NetDeamon.apps.PVControl;

/// <summary>
/// What PVControl asks the inverter to do, versioned: the HA script that applies it stores the
/// <see cref="Seq"/> it applied, and the reconciler automation compares desired, applied and the
/// inverter's actual state. Manufacturer-independent — the register writes live in HA.
/// </summary>
/// <param name="Mode">Inverter mode.</param>
/// <param name="BatteryPowerW">Battery power, W: positive charges, negative discharges, 0 holds (or the inverter's own control in normal).</param>
/// <param name="Seq">Version; increases with every change of mode or power.</param>
public readonly record struct InverterCommand(InverterModes Mode, int BatteryPowerW, int Seq)
{
  /// <summary>The command of this cycle: the previous one if nothing changed, else a new version.</summary>
  /// <param name="previous">Command published last; null = none yet.</param>
  /// <param name="mode">Mode now.</param>
  /// <param name="batteryPowerW">Battery power now, W.</param>
  /// <param name="restoredSeq">Last version before a restart (continues counting from it).</param>
  /// <returns>The command.</returns>
  public static InverterCommand Next(InverterCommand? previous, InverterModes mode, int batteryPowerW, int restoredSeq = 0)
  {
    if (previous is { } p && p.Mode == mode && p.BatteryPowerW == batteryPowerW) return p;
    return new InverterCommand(mode, batteryPowerW, (previous?.Seq ?? restoredSeq) + 1);
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
}
