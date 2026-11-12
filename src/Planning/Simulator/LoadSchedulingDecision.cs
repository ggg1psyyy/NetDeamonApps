namespace NetDeamon.apps.PVControl.Simulator;

/// <summary>Live decision rules for schedulable loads, free of HA (tested in LoadSchedulingDecisionTests).</summary>
public static class LoadSchedulingDecision
{
  /// <summary>
  /// Live SoC gate of Priority/PriorityPlus with hysteresis: never below the floor; a stopped load
  /// restarts only once the SoC is <paramref name="hysteresisMarginPct"/> above it, or
  /// <paramref name="pvBypassMinSocBufferPct"/> above it while PV alone covers the charge power.
  /// </summary>
  /// <param name="currentlyActive">The load is running.</param>
  /// <param name="currentSoC">Battery SoC now, %.</param>
  /// <param name="socFloor">SoC floor of the mode, %.</param>
  /// <param name="netPvW">PV minus house load now, W.</param>
  /// <param name="chargeRateW">Power of the load, W.</param>
  /// <param name="hysteresisMarginPct">Restart margin above the floor, %.</param>
  /// <param name="pvBypassMinSocBufferPct">Smaller restart margin when PV covers the load, %.</param>
  /// <returns>True if the load may run.</returns>
  public static bool PrioritySoCGateOk(
    bool currentlyActive, int currentSoC, int socFloor, int netPvW, int chargeRateW,
    int hysteresisMarginPct, int pvBypassMinSocBufferPct)
  {
    if (currentSoC < socFloor)
      return false;
    if (currentlyActive)
      return true;
    if (currentSoC >= socFloor + hysteresisMarginPct)
      return true;
    return currentSoC >= socFloor + pvBypassMinSocBufferPct && netPvW >= chargeRateW;
  }

  /// <summary>
  /// The battery hold for a load that never draws it (<see cref="ForceChargeReasons.LoadFromGrid"/>) applies live only
  /// while such a load draws power: the plan holds it for the planned session, but a car that isn't charging (not
  /// plugged in, full, still ramping up) leaves only the house on the grid — the battery covers it in normal mode.
  /// </summary>
  /// <param name="planned">The plan's mode for the running quarter.</param>
  /// <param name="fromGridLoadDrawing">A load whose session runs from the grid now draws power.</param>
  /// <returns>The mode to run.</returns>
  public static InverterState LiveLoadHold(InverterState planned, bool fromGridLoadDrawing) =>
    planned is { Mode: InverterModes.grid_only, ModeReason: ForceChargeReasons.LoadFromGrid } && !fromGridLoadDrawing
      ? new InverterState(InverterModes.normal, ForceChargeReasons.None, planned.BatteryChargeEnable, planned.ExportAllowed)
      : planned;
}

