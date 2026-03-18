namespace NetDeamon.apps.PVControl.Simulator;

/// <summary>
/// Immutable snapshot of one 15-minute slot produced by <see cref="PVSimulator.Simulate"/>.
/// Together the slots form a two-day timeline that shows exactly what the system is expected
/// to do: which inverter mode will be active, how the battery SoC evolves, and where energy
/// flows (PV generation, house load, grid import/export, battery charge/discharge).
///
/// The SoC field reflects the state at the START of the slot; after applying
/// BatteryChargeWh − BatteryDischargeWh you get the SoC at the start of the next slot.
/// </summary>
public record SimulationSlot(
  /// <summary>Start of this 15-minute slot (quarter-hour aligned).</summary>
  DateTime Time,

  /// <summary>Battery state of charge at the beginning of this slot, in %.</summary>
  int SoC,

  /// <summary>
  /// Inverter mode the simulator decided for this slot, together with the reason.
  /// For the current (first) slot this becomes the live command sent to the inverter.
  /// </summary>
  InverterState State,

  /// <summary>Predicted PV generation during this slot, in Wh (from OpenMeteo forecast).</summary>
  int PVWh,

  /// <summary>Predicted house load during this slot, in Wh (from historical weighted average).</summary>
  int LoadWh,

  /// <summary>
  /// Energy consumed by <see cref="ExtraLoad"/> entries during this slot, in Wh.
  /// This is additive on top of <see cref="LoadWh"/>.
  /// </summary>
  int ExtraLoadWh,

  /// <summary>Energy charged into the battery during this slot, in Wh. Always ≥ 0.</summary>
  int BatteryChargeWh,

  /// <summary>Energy discharged from the battery during this slot, in Wh. Always ≥ 0.</summary>
  int BatteryDischargeWh,

  /// <summary>Energy imported from the grid during this slot, in Wh. Always ≥ 0.</summary>
  int GridImportWh,

  /// <summary>Energy exported to the grid during this slot, in Wh. Always ≥ 0.</summary>
  int GridExportWh
);
