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
}
