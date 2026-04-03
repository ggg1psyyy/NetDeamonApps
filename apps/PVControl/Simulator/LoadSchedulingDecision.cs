using NetDeamon.apps;

namespace NetDeamon.apps.PVControl.Simulator;

/// <summary>
/// Pure-function schedulable-load decision logic.
/// Implements a Schmitt-trigger (hysteresis) controller: the start threshold is stricter than
/// the keep threshold, preventing rapid on/off cycling when conditions are borderline.
///
/// Start: all mode conditions must be satisfied at full thresholds.
/// Keep:  the same conditions are checked with relaxed thresholds (HysteresisMarginPct applied).
///
/// This is deliberately free of any Home Assistant or live-sensor dependencies so it can be
/// unit-tested in isolation — see LoadSchedulingDecisionTests.
/// </summary>
public static class LoadSchedulingDecision
{
  /// <summary>
  /// Returns true when the load should be active given the supplied inputs.
  /// </summary>
  public static bool Decide(LoadSchedulingInput input) => Decide(input, out _);

  /// <summary>
  /// Returns true when the load should be active and sets <paramref name="reason"/>
  /// to a human-readable explanation of why charging was started, kept, or suppressed.
  /// </summary>
  public static bool Decide(LoadSchedulingInput input, out string reason)
  {
    // ── Off ───────────────────────────────────────────────────────────────
    if (input.Mode == LoadSchedulingMode.Off)
    {
      reason = "Off";
      return false;
    }

    // ── Target reached ────────────────────────────────────────────────────
    if (input.CurrentLevel >= input.TargetLevel)
    {
      reason = $"Target reached ({input.CurrentLevel} ≥ {input.TargetLevel})";
      return false;
    }

    // ── Emergency ─────────────────────────────────────────────────────────
    // Always charge regardless of PV or battery conditions.
    if (input.Mode == LoadSchedulingMode.Emergency)
    {
      reason = $"Emergency (level={input.CurrentLevel} → {input.TargetLevel})";
      return true;
    }

    // ── PriorityPlus: cheap grid import ───────────────────────────────────────
    // When the import price is below the user's threshold, allow grid import for the load
    // (PV condition bypassed). The battery SoC condition still applies (Priority rules).
    bool priceGood = input.Mode == LoadSchedulingMode.PriorityPlus
                     && input.CurrentImportPriceCt <= input.MaxPriceCt;

    // ── PV / import condition (Schmitt trigger) ────────────────────────────────
    // When NOT active: require enough PV to cover the full load rate.
    // When already active: tolerate up to ImportToleranceW of grid import.
    int pvThreshold = input.CurrentlyActive ? -input.ImportToleranceW : input.ChargeRateW;
    bool pvOk = priceGood || input.NetPvW >= pvThreshold;

    // ── Battery SoC condition ─────────────────────────────────────────────
    // Each mode defines different SoC requirements; hysteresis relaxes the threshold
    // once the load is already active (CurrentlyActive = true).
    // Optimal-start: simulation predicts 100 % today AND current SoC ≥ preferred min
    //   (regardless of EnforcePreferredSoC). Prevents morning activation on a depleted battery.
    // Optimal-keep:  Schmitt-trigger stop floor = max(preferred − 10 %, absolute + 5 %).
    //   The 10 %-wide hysteresis band below the start threshold prevents on/off cycling.
    //   The absolute+5 floor keeps the stop threshold safely above the hardware minimum.
    // Priority/PriorityPlus: classic Schmitt-trigger on preferred-min SoC.
    bool socOk = (input.Mode, input.CurrentlyActive) switch
    {
      (LoadSchedulingMode.Optimal, false) =>
        input.WillReachMaxSocToday && input.BatterySoC >= input.PreferredMinSoC,
      (LoadSchedulingMode.Optimal, true) =>
        input.BatterySoC >= Math.Max(input.PreferredMinSoC - 10, input.AbsoluteMinSoC + 5),

      (LoadSchedulingMode.Priority or LoadSchedulingMode.PriorityPlus, false) =>
        input.BatterySoC >= input.PreferredMinSoC + input.HysteresisMarginPct,
      (LoadSchedulingMode.Priority or LoadSchedulingMode.PriorityPlus, true) =>
        input.BatterySoC >= input.PreferredMinSoC,

      _ => false,
    };

    if (!pvOk)
    {
      string pvDesc = priceGood ? "price ok" : $"net_pv={input.NetPvW}W, need {pvThreshold}W";
      reason = $"PV insufficient ({pvDesc})";
      return false;
    }
    if (!socOk)
    {
      int socThreshold = (input.Mode, input.CurrentlyActive) switch
      {
        (LoadSchedulingMode.Optimal, false) => input.PreferredMinSoC,
        (LoadSchedulingMode.Optimal, true)  => Math.Max(input.PreferredMinSoC - 10, input.AbsoluteMinSoC + 5),
        (LoadSchedulingMode.Priority or LoadSchedulingMode.PriorityPlus, false) =>
          input.PreferredMinSoC + input.HysteresisMarginPct,
        (LoadSchedulingMode.Priority or LoadSchedulingMode.PriorityPlus, true) =>
          input.PreferredMinSoC,
        _ => 101,
      };
      string socDesc = (input.Mode, input.CurrentlyActive) switch
      {
        (LoadSchedulingMode.Optimal, false) when !input.WillReachMaxSocToday =>
          $"won't reach 100% today (bat={input.BatterySoC}%)",
        (LoadSchedulingMode.Optimal, _) =>
          $"bat={input.BatterySoC}% < {socThreshold}% (preferred {input.PreferredMinSoC}% − 10%)",
        _ => $"bat={input.BatterySoC}% < {socThreshold}%",
      };
      reason = $"Battery SoC too low ({socDesc})";
      return false;
    }

    string chargeDesc = priceGood
      ? $"cheap price {input.CurrentImportPriceCt:F1}ct, bat={input.BatterySoC}%"
      : $"net_pv={input.NetPvW}W, bat={input.BatterySoC}%";
    reason = $"Active ({input.Mode}: {chargeDesc})";
    return true;
  }
}
