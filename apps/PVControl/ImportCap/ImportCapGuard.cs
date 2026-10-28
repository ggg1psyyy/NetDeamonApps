using System;

namespace NetDeamon.apps.PVControl.ImportCap;

/// <summary>Live inputs of one import cap decision.</summary>
/// <param name="CapW">Highest mean import of a 15-min block, W.</param>
/// <param name="ElapsedSeconds">Seconds of the running quarter that have passed.</param>
/// <param name="ImportedWh">Energy imported in the running quarter so far; null = unknown (not tracked from its start).</param>
/// <param name="GridImportW">Grid import now, W ≥ 0.</param>
/// <param name="HouseLoadW">House load now, W — everything except the battery, extra loads included.</param>
/// <param name="PvW">PV power now, W.</param>
/// <param name="ExtraLoadsW">Measured power of the extra loads now (EV), W — also one that runs outside PVControl's switch.</param>
/// <param name="ExtraLoadsOn">Extra loads asked to run (charge_now), whether or not they draw yet.</param>
/// <param name="ExtraLoadsNominalW">Their usual power, W (counted while they ramp up, and to decide a resume).</param>
/// <param name="ExtraLoadsPaused">The guard paused them last cycle.</param>
/// <param name="MaxChargeA">Hardware charge current limit, A.</param>
/// <param name="VoltageV">Battery voltage, V (W per A).</param>
/// <param name="ReactionSeconds">How long a requested change takes to show at the meter, s.</param>
public readonly record struct GuardInput(
  int CapW, double ElapsedSeconds, double? ImportedWh, int GridImportW, int HouseLoadW, int PvW,
  int ExtraLoadsW, bool ExtraLoadsOn, int ExtraLoadsNominalW, bool ExtraLoadsPaused,
  double MaxChargeA, int VoltageV, int ReactionSeconds);

/// <summary>Result of <see cref="ImportCapGuard.Decide"/>.</summary>
/// <param name="ChargeLimitA">Battery charge current limit, A.</param>
/// <param name="PauseExtraLoads">Pause the extra loads (the battery alone can't keep the cap).</param>
/// <param name="HeadroomW">Import left for a new load (e.g. a warm-water heating) with the battery yielding, W.</param>
/// <param name="AllowedW">Mean import allowed for the rest of the quarter after the reaction time, W.</param>
/// <param name="ProjectedKw">The quarter's mean import if the current power continues, kW.</param>
/// <param name="Reason">Explanation for the sensor.</param>
public readonly record struct GuardDecision(double ChargeLimitA, bool PauseExtraLoads, int HeadroomW, int AllowedW, double ProjectedKw, string Reason);

/// <summary>
/// Keeps the mean grid import of every 15-min block under the cap: throttles the battery's grid
/// charging first, then pauses the extra loads. Nothing reacts instantly (automation delays, ramps),
/// so the current power counts as continuing for the reaction time; a limit set late in a quarter
/// takes effect in the next one, which starts with the full cap.
/// </summary>
public static class ImportCapGuard
{
  /// <summary>Length of a block, s.</summary>
  const double QuarterSeconds = TimeRange.SlotMinutes * 60;

  /// <summary>Margin before a paused extra load resumes, W (no flapping at the edge).</summary>
  public const int ResumeMarginW = 500;

  /// <summary>More than this mean import is impossible for a house connection, W: a counter reading above it is wrong
  /// (e.g. a unit error), and the quarter is estimated from the power instead.</summary>
  public const int MaxPlausibleW = 45_000;

  /// <summary>Remaining time after the reaction below which a change only matters for the next quarter, s.</summary>
  public const double MinEffectSeconds = 30;

  /// <summary>The decision of this cycle.</summary>
  /// <param name="g">Live inputs.</param>
  /// <returns>Charge limit, pause, headroom and reason.</returns>
  public static GuardDecision Decide(GuardInput g)
  {
    double remaining = Math.Max(0, QuarterSeconds - g.ElapsedSeconds);
    double estimate = Energy.Wh(g.GridImportW, TimeSpan.FromSeconds(g.ElapsedSeconds));
    bool plausible = g.ImportedWh is { } measured && measured <= Energy.Wh(MaxPlausibleW, TimeSpan.FromSeconds(g.ElapsedSeconds + 60));
    double imported = plausible ? g.ImportedWh!.Value : estimate;
    double projectedKw = (imported + Energy.Wh(g.GridImportW, TimeSpan.FromSeconds(remaining))) * 60 / TimeRange.SlotMinutes / 1000;

    // Mean import allowed once a change takes effect.
    double reaction = Math.Min(g.ReactionSeconds, remaining);
    double effective = remaining - reaction;
    double budgetWh = Energy.Wh(g.CapW, TimeSpan.FromMinutes(TimeRange.SlotMinutes)) - imported - Energy.Wh(g.GridImportW, TimeSpan.FromSeconds(reaction));
    double allowedW = effective >= MinEffectSeconds ? Math.Min(g.CapW, budgetWh * 3600 / effective) : g.CapW;

    // What the battery may take: the cap less the house's own net draw and the extra loads. A load
    // asked to run counts at least its usual power (it may still ramp up); one drawing without being
    // asked (the charger switched on outside PVControl) counts what it draws — it's import all the same.
    int baseW = g.HouseLoadW - g.ExtraLoadsW - g.PvW;
    int extraW = g.ExtraLoadsOn && !g.ExtraLoadsPaused ? Math.Max(g.ExtraLoadsW, g.ExtraLoadsNominalW) : g.ExtraLoadsW;
    double batteryW = allowedW - baseW - extraW;
    double limitA = Math.Clamp(Math.Floor(batteryW / Math.Max(1, g.VoltageV) * 10) / 10, 0, g.MaxChargeA);

    bool pause = g.ExtraLoadsOn && (g.ExtraLoadsPaused
      ? allowedW - baseW < g.ExtraLoadsNominalW + ResumeMarginW   // resumes only with room to spare
      : batteryW < 0);                                            // battery at 0 A and still over
    if (pause) limitA = Math.Clamp(Math.Floor((allowedW - baseW) / Math.Max(1, g.VoltageV) * 10) / 10, 0, g.MaxChargeA);

    int headroom = (int)(Math.Min(allowedW, g.CapW) - baseW - (pause ? 0 : extraW));
    string reason = pause ? $"extra loads paused: {allowedW / 1000:F1} kW allowed, house {baseW / 1000.0:F1} kW"
      : limitA < g.MaxChargeA ? $"charge limited to {limitA:F1} A: {allowedW / 1000:F1} kW allowed for the rest of the quarter"
      : "no limit needed";
    if (g.ImportedWh is not null && !plausible) reason += $" (counter implausible: {g.ImportedWh / 1000:F1} kWh, estimated)";
    return new GuardDecision(limitA, pause, headroom, (int)allowedW, Math.Round(projectedKw, 2), reason);
  }

  /// <summary>Smallest raise of the published limit, A (a lower limit follows in whole amps).</summary>
  public const double RaiseStepA = 2;

  /// <summary>
  /// The limit to publish, in whole amps: each change is an EEPROM write on the inverter (the Solax
  /// charge current register), so it moves only by a step worth it — lower by whole amps at once,
  /// higher only by <see cref="RaiseStepA"/> or back to the maximum. No grid charging: the maximum
  /// (the battery then only charges from PV surplus, which never imports).
  /// </summary>
  /// <param name="previousA">Limit published last, A; null = none yet.</param>
  /// <param name="decidedA">Limit of this decision, A.</param>
  /// <param name="maxA">Hardware maximum, A.</param>
  /// <param name="gridCharging">The battery charges from the grid (force_charge, force_charge_grid_only).</param>
  /// <returns>The limit, A.</returns>
  public static double NextLimit(double? previousA, double decidedA, double maxA, bool gridCharging)
  {
    double wanted = gridCharging && decidedA < maxA ? Math.Floor(decidedA) : maxA;
    if (previousA is not { } p || wanted >= maxA || wanted <= p - 1 || wanted >= p + RaiseStepA) return wanted;
    return p;
  }
}
