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
/// <param name="RoomA">Battery charge current the cap leaves, A — not clamped to the hardware maximum, so a raise can
/// see how much headroom there is (<see cref="ImportCapGuard.NextLimit"/>).</param>
public readonly record struct GuardDecision(double ChargeLimitA, bool PauseExtraLoads, int HeadroomW, int AllowedW, double ProjectedKw, string Reason,
  double RoomA = double.MaxValue);

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
    double projectedKw = Energy.SlotW(imported + Energy.Wh(g.GridImportW, TimeSpan.FromSeconds(remaining))) / 1000;

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
    // Battery power → charge current, 0.1 A steps rounded down; the room unclamped, the limit within the hardware maximum.
    double RoomFor(double w) => Math.Max(0, Math.Floor(w / Math.Max(1, g.VoltageV) * 10) / 10);
    double roomA = RoomFor(batteryW);

    bool pause = g.ExtraLoadsOn && (g.ExtraLoadsPaused
      ? allowedW - baseW < g.ExtraLoadsNominalW + ResumeMarginW   // resumes only with room to spare
      : batteryW < 0);                                            // battery at 0 A and still over
    if (pause) roomA = RoomFor(allowedW - baseW);
    double limitA = Math.Min(roomA, g.MaxChargeA);

    int headroom = (int)(Math.Min(allowedW, g.CapW) - baseW - (pause ? 0 : extraW));
    string reason = pause ? $"extra loads paused: {allowedW / 1000:F1} kW allowed, house {baseW / 1000.0:F1} kW"
      : limitA < g.MaxChargeA ? $"charge limited to {limitA:F1} A: {allowedW / 1000:F1} kW allowed for the rest of the quarter"
      : "no limit needed";
    if (g.ImportedWh is not null && !plausible) reason += $" (counter implausible: {g.ImportedWh / 1000:F1} kWh, estimated)";
    return new GuardDecision(limitA, pause, headroom, (int)allowedW, Math.Round(projectedKw, 2), reason, roomA);
  }

  /// <summary>
  /// Charge current the plan's room leaves a grid charge from its start (<see cref="Simulator.SimulationResult.GridChargeRoomW"/>),
  /// less <see cref="ResumeMarginW"/>: the plan is optimistic — the "30 A" setting drew 7.52 kW, not 30 × 245 V = 7.35 kW,
  /// and the live house load runs above its hourly-average forecast. Without it the 2026-10-29 night charge
  /// (9800 − house 450 − EV 1930 W = 30.3 A) would have started at 30 A and still been cut a moment later.
  /// </summary>
  /// <param name="roomW">Battery power the plan's cap leaves, W.</param>
  /// <param name="voltageV">Battery voltage, V.</param>
  /// <returns>A, ≥ 0.</returns>
  public static double PlanRoomA(int roomW, int voltageV) => Math.Max(0, roomW - ResumeMarginW) / (double)Math.Max(1, voltageV);

  /// <summary>Smallest raise of the published limit below the maximum, A (a lower limit follows in whole amps).</summary>
  public const double RaiseStepA = 2;

  /// <summary>
  /// The charge current to publish, in whole amps: each change is an EEPROM write on the inverter (the
  /// SolaX charge current register a bulk charge runs at), so it is lowered at once, by whole amps, as far
  /// as the room needs — the cap is the cap — but raised only with twice the headroom: from L to R only
  /// while the room is at least R + (R − L), at least by <see cref="RaiseStepA"/> or straight to the maximum.
  /// A load that brought the import near the cap (the EV) usually keeps running, and a raise into the same
  /// edge was lowered again a moment later. No grid charging: the maximum (the battery then only charges
  /// from PV surplus, which never imports).
  /// </summary>
  /// <param name="previousA">Limit published last, A; null = none yet.</param>
  /// <param name="roomA">Charge current the cap leaves (<see cref="GuardDecision.RoomA"/>, or the plan's room if lower), A.</param>
  /// <param name="maxA">Hardware maximum, A.</param>
  /// <param name="gridCharging">The battery charges from the grid (force_charge, force_charge_grid_only).</param>
  /// <returns>The limit, A.</returns>
  public static double NextLimit(double? previousA, double roomA, double maxA, bool gridCharging)
  {
    if (!gridCharging) return maxA;
    double level = Math.Clamp(Math.Floor(roomA), 0, maxA);
    if (previousA is not { } p || level < p) return level;
    double raise = Math.Min(maxA, Math.Floor((roomA + p) / 2));    // the highest R with room ≥ R + (R − p)
    return raise >= maxA || raise >= p + RaiseStepA ? raise : p;
  }
}
