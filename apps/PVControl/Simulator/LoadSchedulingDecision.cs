namespace NetDeamon.apps.PVControl.Simulator;

/// <summary>
/// Pure-function schedulable-load decision logic used by HouseEnergy.FindLoadWindow's real-time
/// SoC safety overlay.
///
/// This is deliberately free of any Home Assistant or live-sensor dependencies so it can be
/// unit-tested in isolation — see LoadSchedulingDecisionTests.
/// </summary>
public static class LoadSchedulingDecision
{
  /// <summary>
  /// Real-time Schmitt-trigger gate on the Priority/PriorityPlus SoC floor, used by
  /// HouseEnergy.FindLoadWindow as a live safety overlay on top of the simulation-oracle window
  /// search (which already owns price/PV window optimization for the session itself).
  ///
  /// The hard floor (currentSoC &gt;= socFloor) always applies first, regardless of PV — a
  /// depleted battery below the floor never allows charging to start or continue.
  ///
  /// Stop (currentlyActive):  currentSoC &lt; socFloor — hard cutoff, no hysteresis.
  /// Start (!currentlyActive): currentSoC &gt;= socFloor + hysteresisMarginPct, i.e. the battery
  ///   must recover a few percent above the floor before restarting. Without this, a session
  ///   that just stopped at the floor restarts on the very next tiny SoC uptick and immediately
  ///   drains back under the floor again (start/stop thrashing).
  ///   Exception: once SoC has climbed at least pvBypassMinSocBufferPct above the hard floor —
  ///   NOT merely reached it — and PV alone already covers the charge rate (netPvW >= chargeRateW),
  ///   starting won't draw the battery down further, so the rest of the hysteresis wait is
  ///   skipped. The buffer matters: without it, this exception fires the instant SoC ticks up to
  ///   EXACTLY socFloor (since PV readings are frequently already above a typical charge rate),
  ///   defeating the hysteresis it's meant to sit inside — SoC hovering right at the floor would
  ///   restart on every uptick and drain straight back under it again within minutes, the exact
  ///   thrashing this function exists to prevent.
  /// </summary>
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
