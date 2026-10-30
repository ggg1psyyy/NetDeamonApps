using System;
using System.Collections.Generic;
using System.Linq;
using NetDeamon.apps.PVControl;

namespace NetDeamon.apps.PVControl.Simulator;

/// <summary>
/// Simulates the house in 15-min slots from now to the end of tomorrow. Per slot: a look-ahead
/// without charging (base SoC), the charge need from it, the inverter mode, the energy flows, the
/// next SoC. Later slots therefore see the charging decided before them; the first slot's mode is
/// the live inverter command.
/// </summary>
public static class EnergySimulator
{
  private const int SlotMinutes = TimeRange.SlotMinutes;

  /// <summary>Runs the simulation.</summary>
  /// <param name="input">Start state, forecasts, prices, extra loads and settings.</param>
  /// <returns>One slot per 15 min from the running quarter through the end of tomorrow, with derived values.</returns>
  public static SimulationResult Simulate(SimulationInput input)
  {
    var slots = new List<SimulationSlot>();
    var startSlot = input.StartTime.RoundToNearestQuarterHour();
    // Always end at tomorrow's midnight so the window is exactly two full calendar days
    // (today 00:00–23:45 + tomorrow 00:00–23:45), matching ClearAndCreateEmptyPredictionData.
    var endSlot = startSlot.Date.AddDays(2);

    int currentSoc = input.StartSocPercent;
    // Track battery energy in exact Wh to avoid accumulated rounding error from % ↔ Wh conversions.
    // currentSoc (%) is derived from this for mode decisions and slot output only.
    int currentEnergyWh = input.SocToWh(input.StartSocPercent);
    var currentMode = input.CurrentMode;

    for (var slotTime = startSlot; slotTime < endSlot; slotTime = slotTime.AddMinutes(SlotMinutes))
    {
      // Energy values for this slot (Wh per 15-min period; the running slot only its rest)
      var (pvWh, loadWh, extraLoadWh) = input.SlotEnergyWh(slotTime);
      int totalLoadWh = loadWh + extraLoadWh;

      // --- Step 1: base look-ahead SoC (no charging) for this slot onward ---
      // Used as input to NeedToCharge and to some mode conditions (max SoC duration, sell maxima).
      // We recompute it each slot so it reflects the already-updated simulated SoC, not the
      // original starting SoC — otherwise the look-ahead would drift further from reality as
      // we step forward.
      var baseFutureSoC = ComputeBaseFutureSoC(currentSoc, slotTime, endSlot, input);

      // --- Step 2: decide if charging is needed based on base look-ahead ---
      var needToCharge = ComputeNeedToCharge(currentSoc, baseFutureSoC, currentMode, input, slotTime);

      // --- Step 3: pick inverter mode ---
      var newMode = ComputeMode(currentMode, needToCharge, input, slotTime, endSlot, currentSoc,
        baseFutureSoC, pvWh, totalLoadWh);
      // A running NeedToCharge charge never stops where starting one would begin it: the larger
      // margin while charging also moves the latest charge time and with it the price window, and
      // in that other window the running quarter could lose — force_charge and normal then
      // alternated every cycle (live 2026-10-25 11:30, 2026-10-22 09:00).
      if (IsNeedCharge(currentMode) && newMode.Mode != InverterModes.force_charge)
      {
        var idle = new InverterState(InverterModes.normal, ForceChargeReasons.None);
        var startNeed = ComputeNeedToCharge(currentSoc, baseFutureSoC, idle, input, slotTime);
        var start = ComputeMode(idle, startNeed, input, slotTime, endSlot, currentSoc, baseFutureSoC, pvWh, totalLoadWh);
        if (IsNeedCharge(start))
          (newMode, needToCharge) = (start, startNeed);
      }
      // A load that never draws the battery (PriorityPlus EV): hold it while PV doesn't cover all loads.
      if (newMode.Mode == InverterModes.normal && input.BatteryHeldForLoad(slotTime, pvWh, totalLoadWh))
        newMode = new InverterState(InverterModes.grid_only, ForceChargeReasons.LoadFromGrid, newMode.BatteryChargeEnable, newMode.ExportAllowed);
      var previousMode = currentMode;

      // --- Step 4: compute energy flows for this slot given the chosen mode ---
      // For NeedToCharge-triggered force_charge, only charge the minimum required to stay
      // above the floor — not all the way to ForceChargeTargetSocPercent (which is meant for
      // user-initiated force charging and would cause massive over-charging here).
      int chargeTargetSocPercent = input.ForceChargeTargetSocPercent;
      if (IsNeedCharge(newMode))
        chargeTargetSocPercent = !input.EnforcePreferredSoc && newMode.ModeReason == ForceChargeReasons.GoingUnderPreferredMinima
          ? PreferredBandTargetSoc(input, slotTime, currentSoc, baseFutureSoC, needToCharge.LatestChargeTime)
          : DeficitChargeTargetSoc(currentSoc, slotTime, endSlot, baseFutureSoC, needToCharge, input,
              withMargin: BandAllowedAt(input, input.ImportPrices.GetPrice(slotTime)));
      else if (newMode is { Mode: InverterModes.force_charge, ModeReason: ForceChargeReasons.ForcedChargeAtMinimumPrice })
        chargeTargetSocPercent = DailyTargetChargeSoc(input, slotTime, currentSoc, baseFutureSoC) ?? chargeTargetSocPercent;
      // The charge path is chosen when a grid charge starts and kept while it runs.
      if (newMode.Mode == InverterModes.force_charge)
        newMode.Bulk = previousMode.Mode == InverterModes.force_charge
          ? previousMode.Bulk
          : ChargesBulk(input, slotTime, currentSoc, chargeTargetSocPercent);
      currentMode = newMode;
      var (battChargeWh, battDischargeWh, gridImportWh, gridExportWh) = ComputeEnergyFlows(newMode, pvWh, totalLoadWh, currentEnergyWh, input, chargeTargetSocPercent, input.SlotFraction(slotTime));

      slots.Add(new SimulationSlot(slotTime, currentSoc, newMode, pvWh, loadWh, extraLoadWh, battChargeWh, battDischargeWh, gridImportWh, gridExportWh));

      // --- Step 5: advance SoC for the next slot ---
      // Keep Wh exact; derive % only for mode decisions (integer division, no rounding).
      currentEnergyWh = Math.Clamp(currentEnergyWh + battChargeWh - battDischargeWh, 0, input.BatteryCapacityWh);
      currentSoc = input.WhToSoc(currentEnergyWh);
    }

    return new SimulationResult(slots, input);
  }

  /// <summary>
  /// Whether a grid charge starting now needs the full-power (bulk) path: the remote-controlled (VPP)
  /// path can't put the energy to the target into the battery within the quarters that cost the same
  /// as this one. With hourly prices that's up to the rest of the hour (four quarters at a lower
  /// power cost the same as one at full power), with quarter-hourly prices usually one quarter.
  /// </summary>
  /// <param name="input">Simulation input (prices, VPP curve).</param>
  /// <param name="slotTime">Slot in which the charge starts.</param>
  /// <param name="socPercent">SoC at the start, %.</param>
  /// <param name="targetSocPercent">Target SoC of the charge, %.</param>
  /// <returns>True: bulk; false: VPP (or no VPP limit known).</returns>
  /// <remarks>
  /// If VPP doesn't get the energy in, bulk only where it delivers more over the block: both paths as the import
  /// cap leaves them per quarter (house and planned loads), VPP falling with the SoC. A bulk charge follows the cap
  /// with a lower current (since 2026-10-29); where that leaves it no more than VPP manages, it would charge no
  /// faster and only cost the Manual Mode and current EEPROM writes (e.g. EV + heat pump leaving ~4.4 kW at 25 %).
  /// </remarks>
  public static bool ChargesBulk(SimulationInput input, DateTime slotTime, int socPercent, int targetSocPercent)
  {
    if (input.VppChargePowerW is null) return false;
    int neededWh = input.SocToWh(Math.Max(0, Math.Min(targetSocPercent, 100) - socPercent));
    var blockEnd = SamePriceBlockEnd(input, slotTime);
    int vppWh = BlockChargeWh(input, slotTime, blockEnd, socPercent, neededWh, bulk: false);
    return vppWh < neededWh && BlockChargeWh(input, slotTime, blockEnd, socPercent, neededWh, bulk: true) > vppWh;
  }

  /// <summary>Energy a grid charge puts into the battery on one path from a slot until the end of its price block or the
  /// energy needed: per quarter the path's limit at the SoC then (<see cref="SimulationInput.ChargeLimitWh"/>), at most the
  /// grid energy the import cap leaves (<see cref="SimulationInput.GridChargeWhPerSlot"/>) plus the PV surplus.</summary>
  /// <param name="input">Simulation input (prices, loads, VPP curve, cap).</param>
  /// <param name="slotTime">First slot.</param>
  /// <param name="blockEnd">End of the price block.</param>
  /// <param name="socPercent">SoC at the start, %.</param>
  /// <param name="neededWh">Energy needed, Wh (stops there).</param>
  /// <param name="bulk">The full-power path.</param>
  /// <returns>Wh.</returns>
  private static int BlockChargeWh(SimulationInput input, DateTime slotTime, DateTime blockEnd, int socPercent, int neededWh, bool bulk)
  {
    int wh = 0, soc = socPercent;
    for (var t = slotTime; wh < neededWh && t < blockEnd; t = t.AddMinutes(SlotMinutes))
    {
      double fraction = input.SlotFraction(t);
      var (pv, load, extra) = input.SlotEnergyWh(t);
      int pathWh = input.ChargeLimitWh(bulk, soc, fraction);
      int slotWh = Math.Min(pathWh, input.GridChargeWhPerSlot(pv, load + extra, fraction, pathWh) + Math.Max(0, pv - load - extra));
      wh += slotWh;
      soc = Math.Min(100, soc + input.WhToSoc(slotWh));
    }
    return wh;
  }

  /// <summary>
  /// Whether a grid charge should start now on VPP rather than later in this price block at full power:
  /// the deficit by the block's end makes this block charge (the quarters-needed rule), the VPP path
  /// still gets the energy in from now, but no longer from the next quarter. Applied quarter by quarter
  /// this gives the latest start that stays on VPP; a later start that VPP still manages, or one that
  /// needs full power anyway, is left alone. The deficit grows with every quarter of discharge, so the
  /// quarters-needed rule could start a charge mid-hour with too few quarters left for VPP.
  /// </summary>
  /// <param name="input">Simulation input (prices, VPP curve).</param>
  /// <param name="now">Current slot.</param>
  /// <param name="soc">Simulated SoC now, %.</param>
  /// <param name="targetSoc">Target SoC of the charge, %.</param>
  /// <param name="baseFutureSoC">SoC look-ahead without charging (the discharge until the block's end).</param>
  /// <param name="cheaperUnderMax">Cheaper quarters under the price limit (the quarters-needed rule).</param>
  /// <param name="maxChargeWh">Charge per quarter at full power, Wh.</param>
  /// <returns>True: start now (VPP).</returns>
  public static bool VppNowBeatsBulkLater(SimulationInput input, DateTime now, int soc, int targetSoc,
    Dictionary<DateTime, int> baseFutureSoC, int cheaperUnderMax, int maxChargeWh)
  {
    if (input.VppChargePowerW is null) return false;
    var next = now.AddMinutes(SlotMinutes);
    var blockEnd = SamePriceBlockEnd(input, now);
    if (next >= blockEnd || !baseFutureSoC.TryGetValue(next, out int socNext)) return false;
    // Does this block charge at all? The deficit at its end decides it as the rule would there.
    int socAtEnd = baseFutureSoC.GetValueOrDefault(blockEnd, socNext);
    int neededAtEndWh = input.SocToWh(Math.Max(0, targetSoc - Math.Min(soc, socAtEnd)));
    if (cheaperUnderMax >= Math.Max(1, (neededAtEndWh + maxChargeWh - 1) / maxChargeWh)) return false;
    return !ChargesBulk(input, now, soc, targetSoc) && ChargesBulk(input, next, socNext, targetSoc);
  }

  /// <summary>End of the run of quarters from a slot on that cost the same (hourly prices: the end of the hour).</summary>
  /// <param name="input">Simulation input (import prices).</param>
  /// <param name="slotTime">First quarter.</param>
  /// <returns>Start of the first quarter with another price (or without one).</returns>
  public static DateTime SamePriceBlockEnd(SimulationInput input, DateTime slotTime)
  {
    var t = slotTime.AddMinutes(SlotMinutes);
    if (!input.ImportPrices.TryGetPrice(slotTime, out float price)) return t;
    while (input.ImportPrices.TryGetPrice(t, out float p) && Math.Abs(p - price) < 1e-6f)
      t = t.AddMinutes(SlotMinutes);
    return t;
  }

  /// <summary>Whether a mode is a NeedToCharge grid charge (force_charge for a minimum).</summary>
  /// <param name="mode">The mode.</param>
  private static bool IsNeedCharge(InverterState mode) => mode.Mode == InverterModes.force_charge
    && mode.ModeReason is ForceChargeReasons.GoingUnderAbsoluteMinima or ForceChargeReasons.GoingUnderPreferredMinima;

  /// <summary>Whether the SoC allows a grid charge: not over 96 % to start (too slow), up to 98 % once charging.</summary>
  /// <param name="currentMode">Mode of the previous slot.</param>
  /// <param name="simulatedSoc">Simulated SoC now, %.</param>
  private static bool SocOkToCharge(InverterState currentMode, int simulatedSoc) =>
    currentMode.Mode == InverterModes.force_charge ? simulatedSoc <= 98 : simulatedSoc <= 96;

  // ── Cheapest quarters ────────────────────────────────────────────────────────────────────
  /// <summary>Price rank of the current quarter in a window: how many quarters are cheaper (a tie goes to the earlier one) and how many of those are under the price limit.</summary>
  /// <param name="window">Quarter-hourly price entries the charge may use.</param>
  /// <param name="now">Current slot.</param>
  /// <param name="maxPrice">Price limit.</param>
  /// <returns>The current entry and the counts; null if the window doesn't cover now.</returns>
  private static (PriceTableEntry Now, int Cheaper, int CheaperUnderMax)? RankNow(List<PriceTableEntry> window, DateTime now, float maxPrice)
  {
    // Exact boundary comparison: simulation slots are always on quarter-hour boundaries. Runs every
    // slot of every simulation, so it ranks `now` in one pass instead of sorting.
    int nowIndex = window.FindIndex(p => p.Covers(now));
    if (nowIndex < 0) return null;
    var nowEntry = window[nowIndex];
    int cheaper = 0, cheaperUnderMax = 0;
    for (int i = 0; i < window.Count; i++)
      if (window[i].Price < nowEntry.Price || (window[i].Price == nowEntry.Price && i < nowIndex))
      {
        cheaper++;
        if (window[i].Price <= maxPrice) cheaperUnderMax++;
      }
    return (nowEntry, cheaper, cheaperUnderMax);
  }

  /// <summary>
  /// Whether a grid charge to a target charges in the current quarter: it is one of the N cheapest quarters
  /// under the price limit, N = quarters the deficit needs at the charge rate the load leaves under the
  /// import cap — or a VPP start now beats a later full-power one (<see cref="VppNowBeatsBulkLater"/>).
  /// Only those N: an over-threshold quarter ahead of a cheaper one isn't worth it. Price ties go to the
  /// earlier quarter.
  /// </summary>
  /// <param name="input">Simulation input.</param>
  /// <param name="now">Current slot.</param>
  /// <param name="cheaperUnderMax">Cheaper quarters under the limit (<see cref="RankNow"/>).</param>
  /// <param name="simulatedSoc">Simulated SoC now, %.</param>
  /// <param name="targetSoc">Target SoC of the charge, %.</param>
  /// <param name="baseFutureSoC">SoC look-ahead without charging.</param>
  /// <param name="pvWhSlot">PV of the slot, Wh.</param>
  /// <param name="totalLoadWhSlot">Load incl. extra loads of the slot, Wh.</param>
  private static bool ChargesInCheapestQuarters(SimulationInput input, DateTime now, int cheaperUnderMax, int simulatedSoc, int targetSoc,
    Dictionary<DateTime, int> baseFutureSoC, int pvWhSlot, int totalLoadWhSlot)
  {
    int maxChargeWh = Math.Max(1, input.GridChargeWhPerSlot(pvWhSlot, totalLoadWhSlot));
    int neededWh = input.SocToWh(Math.Max(0, targetSoc - simulatedSoc));
    int quartersNeeded = Math.Max(1, (neededWh + maxChargeWh - 1) / maxChargeWh);
    return cheaperUnderMax < quartersNeeded
      || VppNowBeatsBulkLater(input, now, simulatedSoc, targetSoc, baseFutureSoC, cheaperUnderMax, maxChargeWh);
  }

  // ── Daily target charge (user switch) ────────────────────────────────────────────────────
  /// <summary>
  /// SoC the daily target charge charges to now: <see cref="SimulationInput.ForceChargeTargetSocPercent"/> by
  /// the end of the day's PV (<see cref="SimulationInput.DailyTargetDeadline"/>) — headroom for the evening and
  /// the night — buying only what PV won't deliver by then (the look-ahead's SoC at the deadline), and at most
  /// the room a PV peak before it leaves (<see cref="PvRoomBeforeLow"/>).
  /// </summary>
  /// <param name="input">Simulation input.</param>
  /// <param name="now">Current slot.</param>
  /// <param name="simulatedSoc">Simulated SoC now, %.</param>
  /// <param name="baseFutureSoC">SoC look-ahead without charging.</param>
  /// <returns>The SoC to charge to, %; null if there's nothing worth buying (at most <see cref="MinPvRoomPct"/>).</returns>
  public static int? DailyTargetChargeSoc(SimulationInput input, DateTime now, int simulatedSoc, Dictionary<DateTime, int> baseFutureSoC)
  {
    if (input.DailyTargetDeadline(now) is not { } deadline) return null;
    int target = Math.Min(100, input.ForceChargeTargetSocPercent);
    int socAtDeadline = baseFutureSoC.TryGetValue(deadline, out int at) ? at
      : baseFutureSoC.Where(s => s.Key < deadline).Select(s => (int?)s.Value).LastOrDefault() ?? simulatedSoc;
    int deficit = Math.Max(0, target - socAtDeadline);
    int charge = Math.Min(deficit, PvRoomBeforeLow(simulatedSoc, now, deadline, baseFutureSoC));
    return charge > MinPvRoomPct ? Math.Min(target, simulatedSoc + charge) : null;
  }

  /// <summary>
  /// The daily target charge's mode for a slot: force_charge in one of the cheapest quarters under the price
  /// limit before the deadline (<see cref="ChargesInCheapestQuarters"/>), else nothing.
  /// </summary>
  /// <param name="currentMode">Mode of the previous slot (hysteresis).</param>
  /// <param name="input">Simulation input.</param>
  /// <param name="now">Current slot.</param>
  /// <param name="simulatedSoc">Simulated SoC now, %.</param>
  /// <param name="baseFutureSoC">SoC look-ahead without charging.</param>
  /// <param name="pvWhSlot">PV of the slot, Wh.</param>
  /// <param name="totalLoadWhSlot">Load incl. extra loads of the slot, Wh.</param>
  /// <returns>The mode; null if the charge doesn't run in this slot.</returns>
  private static InverterState? DailyTargetChargeMode(InverterState currentMode, SimulationInput input, DateTime now, int simulatedSoc,
    Dictionary<DateTime, int> baseFutureSoC, int pvWhSlot, int totalLoadWhSlot)
  {
    if (!input.EnableCheapForceCharge || !SocOkToCharge(currentMode, simulatedSoc)
        || DailyTargetChargeSoc(input, now, simulatedSoc, baseFutureSoC) is not { } chargeTo
        || input.DailyTargetDeadline(now) is not { } deadline)
      return null;
    var window = input.QuarterHourlyImportPrices.Where(p => p.EndTime > now && p.StartTime < deadline).ToList();
    return RankNow(window, now, input.ForceChargeMaxPrice) is { } rank && rank.Now.Price <= input.ForceChargeMaxPrice
           && ChargesInCheapestQuarters(input, now, rank.CheaperUnderMax, simulatedSoc, chargeTo, baseFutureSoC, pvWhSlot, totalLoadWhSlot)
      ? new InverterState(InverterModes.force_charge, ForceChargeReasons.ForcedChargeAtMinimumPrice)
      : null;
  }

  // ── NeedToCharge charge target ──────────────────────────────────────────────────────────
  /// <summary>Target SoC of a NeedToCharge grid charge: lifts the unclamped trough before the next PV peak to floor + forecast margin + <see cref="ChargeTargetBufferPct"/>.</summary>
  /// <param name="currentSoc">Simulated SoC now, %.</param>
  /// <param name="slotTime">Current slot.</param>
  /// <param name="endSlot">End of the simulation.</param>
  /// <param name="baseFutureSoC">SoC look-ahead without charging.</param>
  /// <param name="need">Charge need (fallback for the trough).</param>
  /// <param name="input">Simulation input.</param>
  /// <param name="withMargin">False: only up to the floor + buffer, without the forecast margin (charging above the price limit).</param>
  /// <returns>Target SoC, % (at most 100).</returns>
  private static int DeficitChargeTargetSoc(
    int currentSoc, DateTime slotTime, DateTime endSlot,
    Dictionary<DateTime, int> baseFutureSoC, NeedToChargeResult need, SimulationInput input, bool withMargin = true)
  {
    int floorSoc = input.GetEffectiveMinSoC();
    // Charge enough to clear the WHOLE overnight trough (minimum before PV recovery),
    // not just the first floor-crossing. Using EstimatedSoc (first-crossing value) leaves
    // the base trajectory still declining past that point, so needCharge keeps firing every
    // few slots at progressively more expensive windows until dawn.
    //
    // Strategy: find the lowest point of the base trajectory between now and the next PV peak.
    // chargeTarget = floorSoc + (currentSoc - baseTrough) + buffer
    //             = currentSoc + (floorSoc - baseTrough) + buffer
    // After charging, the base trough shifts up by the same amount, landing above the floor.
    // The PV peak AFTER the critical point: the first maximum of the whole look-ahead can be a
    // small bump before it (2026-10-24: afternoon bump, fog the next day — the trough search
    // ended that afternoon, the deficit came out 0). Without a real recovery above the floor
    // (no sun) the window is 24 h.
    var pvPeak = baseFutureSoC.FirstMaxOrDefault(start: need.LatestChargeTime);
    var troughWindowEnd = (pvPeak.Key != default && pvPeak.Value > floorSoc + 10)
      ? pvPeak.Key
      : slotTime.AddHours(24);
    // baseFutureSoC clamps at 0 % (ComputeBaseFutureSoC), so once the no-charging trajectory
    // reaches empty it just sits there instead of showing how much FURTHER it would have
    // dropped — silently hiding the true depth of the deficit. Sizing off that clamped value
    // charges only enough to lift the visible (already-floored) trough to floorSoc + buffer,
    // which under-corrects whenever the real, unclamped deficit is larger — so the very next
    // slot finds the same kind of clamped-at-zero trough again and charges a bit more, and
    // again, instead of covering the whole remaining deficit in one shot. Recompute the same
    // window WITHOUT the lower clamp to find the true minimum.
    var cappedTroughWindowEnd = troughWindowEnd < endSlot ? troughWindowEnd : endSlot;
    var unclampedTrough = ComputeBaseFutureSoC(currentSoc, slotTime, cappedTroughWindowEnd, input, clampAtZero: false)
      .FirstMinOrDefault();
    int baseTrough = unclampedTrough.Key != default ? unclampedTrough.Value : need.EstimatedSoc;
    var troughTime = unclampedTrough.Key != default ? unclampedTrough.Key : need.LatestChargeTime;
    // Lift the trough to floor + forecast margin + ChargeTargetBufferPct: the level at which
    // ComputeNeedToCharge stops an ongoing charge, so the simulated charge and the live stop
    // condition agree.
    int margin = withMargin ? SoftBandPct(input, troughTime - slotTime) : 0;
    int deficit = Math.Max(0, floorSoc + margin + ChargeTargetBufferPct - baseTrough);
    // Grid energy charged before a PV peak on the way to the low only counts up to the room PV
    // leaves there: more is clipped at 100 % and pushes PV out to the grid.
    return Math.Min(100, currentSoc + Math.Min(deficit, PvRoomBeforeLow(currentSoc, slotTime, troughTime, baseFutureSoC)));
  }

  /// <summary>Before a coming PV peak a grid charge of this much or less doesn't start, %: PV fills the battery anyway (the charge is capped to the room PV leaves).</summary>
  public const int MinPvRoomPct = 2;

  /// <summary>Whether PV lifts the look-ahead above the SoC now before the low (a PV peak still ahead).</summary>
  /// <param name="currentSoc">Simulated SoC now, %.</param>
  /// <param name="slotTime">Current slot.</param>
  /// <param name="lowTime">Time of the low.</param>
  /// <param name="baseFutureSoC">SoC look-ahead without charging.</param>
  private static bool PvRisesBeforeLow(int currentSoc, DateTime slotTime, DateTime lowTime, Dictionary<DateTime, int> baseFutureSoC) =>
    baseFutureSoC.Any(s => s.Key > slotTime && s.Key <= lowTime && s.Value > currentSoc);

  /// <summary>Room a grid charge now keeps until a low: 100 % less the highest point of the look-ahead before it (PV fills the battery that far anyway).</summary>
  /// <param name="currentSoc">Simulated SoC now, %.</param>
  /// <param name="slotTime">Current slot.</param>
  /// <param name="lowTime">Time of the low the charge has to cover.</param>
  /// <param name="baseFutureSoC">SoC look-ahead without charging.</param>
  /// <returns>%; 100 − SoC now if the look-ahead doesn't rise before the low.</returns>
  private static int PvRoomBeforeLow(int currentSoc, DateTime slotTime, DateTime lowTime, Dictionary<DateTime, int> baseFutureSoC)
  {
    int peak = baseFutureSoC.Where(s => s.Key >= slotTime && s.Key <= lowTime).Select(s => s.Value).DefaultIfEmpty(currentSoc).Max();
    return Math.Max(0, 100 - Math.Max(peak, currentSoc));
  }

  /// <summary>Whether the band above the hard floor (<see cref="SoftBandPct"/>) may be bought at a price: the preferred
  /// minimum (not enforced) at any price — <see cref="PreferredBandChargesNow"/> picks its quarters —, the forecast
  /// margin of an enforced floor only under ForceChargeMaxPrice.</summary>
  /// <param name="input">Simulation input.</param>
  /// <param name="price">Import price of the quarter, €/kWh.</param>
  public static bool BandAllowedAt(SimulationInput input, float price) =>
    !input.EnforcePreferredSoc || price <= input.ForceChargeMaxPrice;

  /// <summary>
  /// Whether the preferred minimum is charged in the current quarter: it is one of the cheapest quarters before the
  /// dip that the deficit needs (<see cref="ChargesInCheapestQuarters"/> without a price limit) — once below it, of the
  /// coming 24 h —, and not above the average price of the coming 24 h: the band is never bought at an expensive time.
  /// </summary>
  /// <param name="input">Simulation input.</param>
  /// <param name="window">Quarters before the dip.</param>
  /// <param name="upcoming">All upcoming quarters.</param>
  /// <param name="now">Current slot.</param>
  /// <param name="simulatedSoc">Simulated SoC now, %.</param>
  /// <param name="targetSoc">Target of the charge, %.</param>
  /// <param name="baseFutureSoC">SoC look-ahead without charging.</param>
  /// <param name="pvWhSlot">PV of the slot, Wh.</param>
  /// <param name="totalLoadWhSlot">Load incl. extra loads of the slot, Wh.</param>
  private static bool PreferredBandChargesNow(SimulationInput input, List<PriceTableEntry> window, List<PriceTableEntry> upcoming,
    DateTime now, int simulatedSoc, int targetSoc, Dictionary<DateTime, int> baseFutureSoC, int pvWhSlot, int totalLoadWhSlot)
  {
    if (targetSoc <= simulatedSoc) return false;   // nothing to keep: no charge that only lets the grid carry the load
    var day = upcoming.Where(p => p.StartTime < now.AddHours(24)).ToList();
    // Already below it (an unforeseen load, a dropout): no dip ahead to keep — refilled in the cheapest quarters of the
    // coming 24 h, and not while PV charges the battery anyway.
    if (simulatedSoc < input.GetPreferredMinSoC())
    {
      if (pvWhSlot > totalLoadWhSlot) return false;
      window = day;
    }
    if (RankNow(window, now, float.MaxValue) is not { } rank) return false;
    if (day.Count == 0 || rank.Now.Price > day.Average(p => p.Price)) return false;
    return ChargesInCheapestQuarters(input, now, rank.Cheaper, simulatedSoc, targetSoc, baseFutureSoC, pvWhSlot, totalLoadWhSlot);
  }

  /// <summary>
  /// Target SoC of a charge for the preferred minimum: the look-ahead stays at it (+ <see cref="ChargeTargetBufferPct"/>)
  /// until PV lifts the battery back to it after the dip, or the first quarter after the dip that is at most as expensive
  /// as now, whichever comes first — from there PV or an as cheap refill take over, so buying more now only moves energy
  /// to a dearer quarter. At most the room a PV peak before then leaves.
  /// </summary>
  /// <param name="input">Simulation input.</param>
  /// <param name="now">Current slot.</param>
  /// <param name="currentSoc">Simulated SoC now, %.</param>
  /// <param name="baseFutureSoC">SoC look-ahead without charging.</param>
  /// <param name="dip">When the look-ahead goes under the preferred minimum (the need's latest charge time).</param>
  /// <returns>Target SoC, % (at most 100).</returns>
  private static int PreferredBandTargetSoc(SimulationInput input, DateTime now, int currentSoc, Dictionary<DateTime, int> baseFutureSoC, DateTime dip)
  {
    float priceNow = input.ImportPrices.GetPrice(now);
    int preferred = input.GetPreferredMinSoC();
    var cheapAgain = input.QuarterHourlyImportPrices.Where(p => p.StartTime > dip && p.Price <= priceNow)
      .Select(p => (DateTime?)p.StartTime).FirstOrDefault() ?? now.AddHours(24);
    // PV refills: back at the preferred minimum after the look-ahead first went under it (not after the need's latest
    // charge time — that has a lead, the look-ahead is still above the minimum there).
    var under = baseFutureSoC.Where(x => x.Key >= now && x.Value < preferred).Select(x => (DateTime?)x.Key).FirstOrDefault();
    var pvRefills = under is { } u
      ? baseFutureSoC.Where(x => x.Key > u && x.Value >= preferred).Select(x => (DateTime?)x.Key).FirstOrDefault() ?? cheapAgain
      : cheapAgain;
    var until = cheapAgain < pvRefills ? cheapAgain : pvRefills;
    int low = baseFutureSoC.Where(x => x.Key >= now && x.Key <= until).Select(x => x.Value).DefaultIfEmpty(currentSoc).Min();
    int deficit = Math.Max(0, preferred + ChargeTargetBufferPct - low);
    return Math.Min(100, currentSoc + Math.Min(deficit, PvRoomBeforeLow(currentSoc, now, until, baseFutureSoC)));
  }

  /// <summary>A hold at the hard floor (grid_only) ends only this far above it, %.</summary>
  public const int HoldReleasePct = 2;

  /// <summary>A NeedToCharge charge lifts the trough this far above floor + forecast margin, %.</summary>
  public const int ChargeTargetBufferPct = 1;

  /// <summary>A NeedToCharge charge starts only this far below floor + forecast margin (never below the floor), %; with <see cref="ChargeTargetBufferPct"/> a 2 % band between start and stop.</summary>
  public const int StartBelowPct = 1;

  /// <summary>
  /// The band a NeedToCharge charge keeps above the hard floor (<see cref="SimulationInput.GetEffectiveMinSoC"/>), %:
  /// without EnforcePreferredSoc the preferred minimum — what planning strives for, while an unforeseen load may
  /// drain it down to the absolute minimum; with it (the preferred minimum is then the hard floor) the forecast
  /// margin (<see cref="ForecastMarginPct"/>). Only charged in cheap quarters: under ForceChargeMaxPrice and cheaper
  /// than holding through the low incl. losses — never bought at an expensive time; the hold (grid_only) acts at the
  /// hard floor only.
  /// </summary>
  /// <param name="input">Simulation input (floors).</param>
  /// <param name="untilLow">Time from now to the low the charge has to cover.</param>
  /// <returns>Band, %.</returns>
  public static int SoftBandPct(SimulationInput input, TimeSpan untilLow) =>
    input.EnforcePreferredSoc ? ForecastMarginPct(untilLow) : Math.Max(0, input.GetPreferredMinSoC() - input.AbsoluteMinSocPercent);

  /// <summary>Extra SoC for forecast errors: +1 % per 2 h until the low, at most <see cref="MaxForecastMarginPct"/>; on top of a hard preferred floor (<see cref="SoftBandPct"/>).</summary>
  /// <param name="untilLow">Time from now to the low the charge has to cover.</param>
  /// <returns>Margin, %.</returns>
  public static int ForecastMarginPct(TimeSpan untilLow) =>
    Math.Clamp((int)Math.Round(untilLow.TotalHours / 2, MidpointRounding.AwayFromZero), 0, MaxForecastMarginPct);

  /// <summary>Upper limit of <see cref="ForecastMarginPct"/>, %.</summary>
  public const int MaxForecastMarginPct = 4;

  /// <summary>Whether a grid charge pays off against holding the floor with grid_only: the window's cheapest price beats the mean price of the hours to bridge, round-trip losses included.</summary>
  /// <param name="window">Price entries the charge may use.</param>
  /// <param name="upcoming">All upcoming price entries.</param>
  /// <param name="need">Charge need (latest charge time).</param>
  /// <param name="bridgeEnd">When PV takes over after the low (<see cref="PvTakeoverAfter"/>): holding the floor bridges the hours until then.</param>
  /// <param name="input">Simulation input (efficiency).</param>
  /// <returns>True if charging is cheaper, or nothing to compare.</returns>
  private static bool ChargingBeatsHolding(List<PriceTableEntry> window, List<PriceTableEntry> upcoming,
    NeedToChargeResult need, DateTime bridgeEnd, SimulationInput input)
  {
    var bridged = upcoming.Where(p => p.StartTime >= need.LatestChargeTime && p.StartTime < bridgeEnd).ToList();
    if (window.Count == 0 || bridged.Count == 0)
      return true;
    float holdPrice = bridged.Average(p => p.Price);
    float roundTrip = input.RoundTripEfficiency;
    return window.Min(p => p.Price) < holdPrice - Math.Abs(holdPrice) * (1 - roundTrip);
  }

  /// <summary>First slot after a time in which PV covers the load incl. extra loads (solar takes over from the battery/grid).</summary>
  /// <param name="after">Search after this time.</param>
  /// <param name="slots">Slots to search (the look-ahead's keys).</param>
  /// <param name="input">Simulation input (the slot energies the simulation uses).</param>
  /// <returns>The slot; <paramref name="after"/> + 24 h if none.</returns>
  private static DateTime PvTakeoverAfter(DateTime after, IEnumerable<DateTime> slots, SimulationInput input) =>
    slots.Where(t => t > after && input.SlotEnergyWh(t) is var (pv, load, extra) && pv > load + extra)
      .DefaultIfEmpty(after.AddHours(24))
      .First();

  // ── Base future SoC ─────────────────────────────────────────────────────────────────────
  /// <summary>SoC look-ahead without inverter decisions: PV − load − extra loads per slot (the worst case for the charge need).</summary>
  /// <param name="currentSocPercent">Start SoC, %.</param>
  /// <param name="startSlot">First slot.</param>
  /// <param name="endSlot">End (exclusive).</param>
  /// <param name="input">Simulation input.</param>
  /// <param name="clampAtZero">False: let the energy go negative to see the true depth of a deficit.</param>
  /// <returns>SoC at the start of each slot, %.</returns>
  private static Dictionary<DateTime, int> ComputeBaseFutureSoC(
    int currentSocPercent, DateTime startSlot, DateTime endSlot, SimulationInput input,
    bool clampAtZero = true)
  {
    var result = new Dictionary<DateTime, int>();
    int energy = input.SocToWh(currentSocPercent);
    int minEnergy = clampAtZero ? 0 : int.MinValue / 2; // avoid overflow when adding pv-load-extra

    for (var t = startSlot; t < endSlot; t = t.AddMinutes(SlotMinutes))
    {
      var (pv, load, extra) = input.SlotEnergyWh(t); // same slot energies as the simulation itself
      // Store SoC at the START of the slot (before applying energy) so the base look-ahead
      // values align with SimulationSlot.SoC which is also the start-of-slot value.
      // % can go negative here when clampAtZero is false — that's intentional, see call site.
      result[t] = input.WhToSoc(energy);
      // The battery is held while a load that never draws it runs without enough PV (grid_only).
      if (!input.BatteryHeldForLoad(t, pv, load + extra))
        energy = Math.Clamp(energy + pv - load - extra, minEnergy, input.BatteryCapacityWh);
    }

    return result;
  }

  // ── NeedToCharge ────────────────────────────────────────────────────────────────────────
  /// <summary>
  /// Whether the battery needs a grid charge: the look-ahead reaches floor + forecast margin
  /// (− <see cref="StartBelowPct"/> unless already charging) before PV refills it.
  /// </summary>
  /// <param name="currentSoc">Simulated SoC now, %.</param>
  /// <param name="baseFutureSoC">SoC look-ahead without charging.</param>
  /// <param name="currentMode">Mode of the previous slot (charging continues to the target).</param>
  /// <param name="input">Simulation input.</param>
  /// <param name="now">Current slot.</param>
  /// <param name="withMargin">False: only the floor itself counts, without the forecast margin.</param>
  /// <returns>Need, the SoC at the critical point and the latest charge time (10 % lead).</returns>
  private static NeedToChargeResult ComputeNeedToCharge(
    int currentSoc, Dictionary<DateTime, int> baseFutureSoC,
    InverterState currentMode, SimulationInput input, DateTime now, bool withMargin = true)
  {
    int minSoC = input.GetEffectiveMinSoC();
    // Start when the trough is at most floor + forecast margin − StartBelowPct (not below the
    // floor); while charging continue until it's floor + margin + ChargeTargetBufferPct, the
    // target DeficitChargeTargetSoc lifts it to.
    bool charging = currentMode.Mode == InverterModes.force_charge;
    int Margin(DateTime t)
    {
      int margin = withMargin ? SoftBandPct(input, t - now) : 0;
      return charging ? margin : Math.Max(0, margin - StartBelowPct);
    }

    // When do we first drop to or below minSoC (+ the forecast margin for that point in time)?
    // If we're already below it (edge case after a settings change or offline period),
    // treat "now" as the critical moment.
    var minReached = currentSoc < minSoC
      ? new KeyValuePair<DateTime, int>(now, currentSoc)
      : baseFutureSoC.Where(s => s.Key >= now).OrderBy(s => s.Key)
          .FirstOrDefault(s => s.Value <= minSoC + Margin(s.Key));

    // If we never drop below minimum, just record the lowest point for diagnostics
    if (minReached.Key == default)
      minReached = baseFutureSoC.FirstMinOrDefault(start: now);

    // When does the base trajectory peak (PV fully charges the battery)?
    var maxReached = baseFutureSoC.FirstMaxOrDefault(start: now);

    // We need to charge if: SoC will reach or breach the minimum AND that happens before a
    // full PV recovery, OR PV will never get us back to 100 % at all.
    // Note: FirstUnderOrDefault uses <= so minReached.Value can equal minSoC (just touching
    // the floor counts as needing a charge — one more slot of discharge would breach it).
    bool needCharge = minReached.Value <= minSoC + Margin(minReached.Key)
      && (minReached.Key < maxReached.Key || maxReached.Value < 100);

    // Build in 10 % of lead time so we start charging slightly before the critical point
    int quartersTilCharge = (int)(((minReached.Key - now).TotalMinutes * 0.1) / SlotMinutes);
    return new NeedToChargeResult(
      estimatedSoc: minReached.Value,
      latestChargeTime: minReached.Key.AddMinutes(-quartersTilCharge * SlotMinutes),
      needToCharge: needCharge);
  }

  // ── Mode decision ────────────────────────────────────────────────────────────────────────
  /// <summary>
  /// Inverter mode of a slot. Order: negative import price (<see cref="NegativeImportPriceMode"/>) →
  /// opportunistic discharge (<see cref="OpportunisticDischargeMode"/>) → daily target charge
  /// (<see cref="DailyTargetChargeMode"/>) → NeedToCharge (<see cref="NeedToChargeMode"/>) → normal;
  /// a negative export price only disables export.
  /// </summary>
  /// <param name="currentMode">Mode of the previous slot (hysteresis).</param>
  /// <param name="need">Charge need of this slot.</param>
  /// <param name="input">Simulation input.</param>
  /// <param name="now">Current slot.</param>
  /// <param name="endSlot">End of the simulation.</param>
  /// <param name="simulatedSoc">Simulated SoC now, %.</param>
  /// <param name="baseFutureSoC">SoC look-ahead without charging.</param>
  /// <param name="pvWhSlot">PV of the slot, Wh.</param>
  /// <param name="totalLoadWhSlot">Load incl. extra loads of the slot, Wh.</param>
  /// <returns>The mode with its reason.</returns>
  private static InverterState ComputeMode(
    InverterState currentMode, NeedToChargeResult need, SimulationInput input,
    DateTime now, DateTime endSlot, int simulatedSoc, Dictionary<DateTime, int> baseFutureSoC,
    int pvWhSlot, int totalLoadWhSlot)
  {
    float importPriceNow = input.ImportPrices.GetPrice(now);
    float exportPriceNow = input.ExportPrices.GetPrice(now);

    if (NegativeImportPriceMode(input, now, simulatedSoc, pvWhSlot, importPriceNow) is { } negativePrice)
      return negativePrice;

    // ── Negative export price ─────────────────────────────────────────────────────────────
    // Grid charges us for feeding in → suppress export only; the charge/discharge decision
    // below still runs on its own merits (see comment above the function).
    bool exportAllowed = exportPriceNow >= 0;

    // The charge/discharge decision, independent of whether export is currently allowed: the first
    // rule that has something to say wins, else the inverter manages PV and battery itself.
    var decided = OpportunisticDischargeMode(currentMode, need, input, now, simulatedSoc, baseFutureSoC, pvWhSlot, totalLoadWhSlot, exportPriceNow)
      ?? DailyTargetChargeMode(currentMode, input, now, simulatedSoc, baseFutureSoC, pvWhSlot, totalLoadWhSlot)
      ?? NeedToChargeMode(currentMode, need, input, now, endSlot, simulatedSoc, baseFutureSoC, pvWhSlot, totalLoadWhSlot, importPriceNow)
      ?? new InverterState(InverterModes.normal, ForceChargeReasons.None);
    if (!exportAllowed)
      decided.ExportAllowed = false;
    // If negative import prices are coming later today, disable battery charging now so we
    // have room to absorb the cheap/free grid energy then. Only has an effect when the mode
    // above resolved to `normal` — every other mode's energy-flow function ignores this flag.
    if (!exportAllowed && input.ImportPrices.NegativeImportUpcoming(now))
      decided.BatteryChargeEnable = false;
    // Negative export prices later today and PV there would fill the battery anyway: feed the
    // surplus in now while it still pays, so the battery has room for the PV that would otherwise
    // be curtailed then. Only in normal mode (feed-in priority/force modes decide on their own).
    if (exportAllowed && decided.Mode == InverterModes.normal && decided.BatteryChargeEnable
        && pvWhSlot > totalLoadWhSlot && NegativeExportFillsBattery(input, now, simulatedSoc))
    {
      decided.BatteryChargeEnable = false;
      decided.ModeReason = ForceChargeReasons.RoomForNegativeExport;
    }
    return decided;
  }

  /// <summary>
  /// Negative import price: the grid pays for consuming → fill the battery as fast as possible
  /// (force_charge_grid_only, PV disconnected). If a cheaper (more negative) window is still coming
  /// today, wait for it in normal mode with battery charging and export disabled (PV surplus is
  /// curtailed rather than exported at the typically negative export price, keeping room for the
  /// cheapest grid energy). A battery over 95 % with PV stays normal with export disabled.
  /// </summary>
  /// <param name="input">Simulation input.</param>
  /// <param name="now">Current slot.</param>
  /// <param name="simulatedSoc">Simulated SoC now, %.</param>
  /// <param name="pvWhSlot">PV of the slot, Wh.</param>
  /// <param name="importPriceNow">Import price now, €/kWh.</param>
  /// <returns>The mode; null unless the import price is negative.</returns>
  private static InverterState? NegativeImportPriceMode(SimulationInput input, DateTime now, int simulatedSoc, int pvWhSlot, float importPriceNow)
  {
    if (importPriceNow >= 0) return null;
    bool cheaperImportComingToday = input.ImportPrices
      .Any(p => p.StartTime.Date == now.Date && p.StartTime > now && p.Price < importPriceNow);
    if (cheaperImportComingToday)
      return new InverterState(InverterModes.normal, ForceChargeReasons.ImportPriceNegative, batteryChargeEnable: false, exportAllowed: false);

    int pvPowerW = Energy.SlotW(pvWhSlot);
    var mode = (simulatedSoc <= 95 || pvPowerW < 100)
      ? InverterModes.force_charge_grid_only
      : InverterModes.normal; // avoid exporting PV at negative export prices
    return new InverterState(mode, ForceChargeReasons.ImportPriceNegative, exportAllowed: false);
  }

  /// <summary>
  /// Opportunistic discharge (user switch): sell battery energy at the day's export price peaks when
  /// the battery still reaches 100 % later via PV and stays above its floor; feed PV surplus in
  /// while the battery would sit full anyway, leaving feed-in priority for the cheapest PV window
  /// that refills it.
  /// </summary>
  /// <param name="currentMode">Mode of the previous slot (hysteresis).</param>
  /// <param name="need">Charge need of this slot.</param>
  /// <param name="input">Simulation input.</param>
  /// <param name="now">Current slot.</param>
  /// <param name="simulatedSoc">Simulated SoC now, %.</param>
  /// <param name="baseFutureSoC">SoC look-ahead without charging.</param>
  /// <param name="pvWhSlot">PV of the slot, Wh.</param>
  /// <param name="totalLoadWhSlot">Load incl. extra loads of the slot, Wh.</param>
  /// <param name="exportPriceNow">Export price now, €/kWh.</param>
  /// <returns>The mode; null if the rule has nothing to say (off, or no case applies).</returns>
  private static InverterState? OpportunisticDischargeMode(InverterState currentMode, NeedToChargeResult need, SimulationInput input,
    DateTime now, int simulatedSoc, Dictionary<DateTime, int> baseFutureSoC, int pvWhSlot, int totalLoadWhSlot, float exportPriceNow)
  {
    float exportPriceNextHour = input.ExportPrices.GetPrice(now.AddHours(1));

    if (input.OpportunisticDischarge)
    {
      // Hysteresis: once in feedin_priority we stay a bit longer before switching back
      double maxSocDuration = currentMode.Mode == InverterModes.feedin_priority ? 1.5 : 2.0;

      // How long does the base trajectory predict 100 % SoC today?
      // If it stays full for more than maxSocDuration hours we can afford to export
      var todayBaseSoC = baseFutureSoC.Where(s => s.Key.Date == now.Date).ToDictionary();
      double maxSocDurationCalc = ComputeMaxSocDuration(todayBaseSoC);

      bool inPVPeriod = input.IsInPVPeriod(now);
      int pvPowerW = Energy.SlotW(pvWhSlot);
      int loadPowerW = Energy.SlotW(totalLoadWhSlot);

      // Case A: during PV peak, battery will hit 100 % anyway → export the overflow now
      // at today's price (even if not a daily maximum) rather than wasting it.
      // But: exit feedin_priority during the cheapest window where PV can refill the battery,
      // so we charge from free PV at the lowest opportunity cost instead of exporting at
      // midday low prices — the full battery can then be discharged at expensive evening peaks.
      if (!need.NeedToCharge && inPVPeriod && maxSocDurationCalc > maxSocDuration
          && exportPriceNow >= 0.01f && exportPriceNow >= exportPriceNextHour
          && simulatedSoc > (input.GetEffectiveMinSoC()) + 3)
      {
        // Battery near-full: nothing to gain from valley optimisation — export overflow directly.
        // Battery has room: find the cheapest window to refill; stay in feedin_priority until then.
        var valleyStart = simulatedSoc < 98
          ? FindChargingValleyStart(now, baseFutureSoC, input)
          : null;
        if (valleyStart == null || now < valleyStart.Value)
          return new InverterState(InverterModes.feedin_priority, ForceChargeReasons.OpportunisticDischarge);
        // Reached the valley: fall through so normal mode lets PV fill the battery cheaply.
      }

      // Case B: we are at one of the two highest daily export price peaks
      // → actively discharge the battery to the grid if the SoC forecast allows it
      var sellMaxima = input.ExportPrices.GetLocalMaxima(end: now.Date.AddDays(1))
        .OrderByDescending(t => t.Price).Take(2).ToList();

      if (sellMaxima.Any(t => t.Covers(now))
          && (exportPriceNow >= input.ForceChargeMaxPrice || input.ImportPrices.NegativeImportUpcoming(now)))
      {
        // Near solar time we can go lower (absolute minimum), otherwise stay at preferred
        int minAllowedSoc = input.PreferredMinSocPercent;
        var firstPVToday = input.GetFirstRelevantPVTime(now.Date, now);
        if (inPVPeriod || (now < firstPVToday && (firstPVToday - now).TotalHours is > 0 and < 4))
          minAllowedSoc = input.AbsoluteMinSocPercent + 2;

        // Discharge aggressively as long as SoC stays comfortably above minimum (+4 % buffer)
        if (!need.NeedToCharge && need.EstimatedSoc >= minAllowedSoc + 4)
          return new InverterState(InverterModes.force_discharge, ForceChargeReasons.OpportunisticDischarge);

        // Battery low but PV surplus available → prioritise feed-in over charging
        if (!need.NeedToCharge && pvPowerW > loadPowerW + 200)
          return new InverterState(InverterModes.feedin_priority, ForceChargeReasons.OpportunisticDischarge);

        // We were force-discharging but now SoC is too low or charging is needed → stop
        if (currentMode.ModeReason == ForceChargeReasons.OpportunisticDischarge
            && currentMode.Mode == InverterModes.force_discharge
            && (need.EstimatedSoc <= minAllowedSoc + 2 || need.NeedToCharge))
          return new InverterState(InverterModes.normal, ForceChargeReasons.None);
      }
    }
    return null;
  }

  /// <summary>
  /// NeedToCharge: the base SoC forecast reaches the floor before PV rescues it, so electricity has
  /// to be bought. The charge is sized to the deficit (<see cref="DeficitChargeTargetSoc"/>) and placed
  /// in the N cheapest quarters of the search window, N = quarters needed at the charge rate the load
  /// leaves (waiting for the single cheapest quarter and charging forward from it spilled a larger
  /// deficit into the expensive quarters after a cheap band, while equally cheap quarters before it
  /// went unused). Until the window the battery is held at the floor with grid_only while PV doesn't
  /// cover the load.
  /// </summary>
  /// <param name="currentMode">Mode of the previous slot (hysteresis).</param>
  /// <param name="need">Charge need of this slot.</param>
  /// <param name="input">Simulation input.</param>
  /// <param name="now">Current slot.</param>
  /// <param name="endSlot">End of the simulation.</param>
  /// <param name="simulatedSoc">Simulated SoC now, %.</param>
  /// <param name="baseFutureSoC">SoC look-ahead without charging.</param>
  /// <param name="pvWhSlot">PV of the slot, Wh.</param>
  /// <param name="totalLoadWhSlot">Load incl. extra loads of the slot, Wh.</param>
  /// <param name="importPriceNow">Import price now, €/kWh.</param>
  /// <returns>force_charge or grid_only; null if no charge is needed or nothing applies in this slot.</returns>
  private static InverterState? NeedToChargeMode(InverterState currentMode, NeedToChargeResult need, SimulationInput input,
    DateTime now, DateTime endSlot, int simulatedSoc, Dictionary<DateTime, int> baseFutureSoC, int pvWhSlot, int totalLoadWhSlot, float importPriceNow)
  {
    if (need.NeedToCharge)
    {
      // Choose the search window for the cheapest charge quarters.
      //
      // Two problems with always using deadline-constrained GetBestChargeWindow:
      //   1. LatestChargeTime collapses to "now" when the base SoC first touches the floor
      //      → only the current (expensive) quarter is eligible → premature force_charge.
      //   2. A fully unconstrained search finds a global price minimum that could be tomorrow
      //      afternoon when PV is already generating — the simulation waits for that distant
      //      minimum and skips the real overnight cheap window.
      //
      // pvTakeover = first slot where PV > load (solar takes over from the grid).
      // When the critical minimum is BEFORE pvTakeover (battery will drain below floor
      // tonight before solar arrives), we must charge before pvTakeover and that bound
      // prevents problem #2.
      // When the critical minimum is AFTER pvTakeover (e.g. tomorrow night), using pvTakeover
      // as the bound is too narrow: it excludes cheap afternoon solar-surplus hours like 14:00
      // that arrive while PV is already running today. In that case LatestChargeTime safely
      // extends to tomorrow early morning — it won't reach tomorrow afternoon (where PV would
      // handle it for free), so GetBestChargeWindow is the right bound.
      int floorSoc = input.GetEffectiveMinSoC();
      var pvTakeover = PvTakeoverAfter(now, baseFutureSoC.Keys, input);

      // Counting entries as quarters requires quarter-hourly entries; live prices already are
      // (PriceManager normalizes them), but SimulationInput also accepts hourly lists.
      var upcoming = input.QuarterHourlyImportPrices.Where(p => p.EndTime > now).ToList();
      var window = need.LatestChargeTime <= pvTakeover
        // Battery will hit minimum before today's PV recovery → must charge before solar arrives.
        ? upcoming.Where(p => p.StartTime < pvTakeover).ToList()
        // Battery hits minimum after today's PV window (e.g. tomorrow) → broader search up to
        // LatestChargeTime naturally includes cheap afternoon hours without reaching tomorrow PV.
        : upcoming.Where(p => p.StartTime <= need.LatestChargeTime).ToList();
      if (window.Count == 0)
        window = upcoming.Where(p => p.StartTime <= need.LatestChargeTime).ToList(); // safety fallback

      // Always charge in the single cheapest quarter (re-picked every slot, so the charge runs
      // forward from it while it stays the cheapest) if that beats holding the floor. On top of
      // that, charge in the N cheapest quarters under ForceChargeMaxPrice, N = quarters needed for
      // the deficit (ChargesInCheapestQuarters): an over-threshold quarter ahead of the cheapest one
      // isn't worth it, since holding the floor with grid_only buys the load at that same price
      // anyway without filling the battery before the cheap quarter.
      bool inWindow = false;
      if (RankNow(window, now, input.ForceChargeMaxPrice) is { } rank)
      {
        // Holding the floor bridges the low until PV takes over after it — not today's PV that is
        // already running at midday (live 2026-10-26 13:30: that left one cheap night hour to compare
        // with, so 0.3649 now lost and the night charged at 0.3727; at 00:30 the span reached
        // tomorrow's PV and the morning peak).
        inWindow = rank.Cheaper == 0
          && ChargingBeatsHolding(window, upcoming, need, PvTakeoverAfter(need.LatestChargeTime, baseFutureSoC.Keys, input), input);
        if (!inWindow && rank.Now.Price <= input.ForceChargeMaxPrice)
          inWindow = ChargesInCheapestQuarters(input, now, rank.CheaperUnderMax, simulatedSoc,
            DeficitChargeTargetSoc(simulatedSoc, now, endSlot, baseFutureSoC, need, input), baseFutureSoC, pvWhSlot, totalLoadWhSlot);
      }
      // Above the price limit only for the floor itself, never for the forecast margin alone (live
      // 2026-10-25 12:15: 35.4 ct for a margin 19 h ahead; an EV drawing the battery triggered it again).
      bool marginAllowed = BandAllowedAt(input, importPriceNow);
      bool hardNeed = ComputeNeedToCharge(simulatedSoc, baseFutureSoC, currentMode, input, now, withMargin: false).NeedToCharge;
      if (inWindow && !marginAllowed && !hardNeed)
        inWindow = false;
      // The preferred minimum (not enforced) is kept by the plan: charged in the cheapest quarters before the dip, also
      // above the price limit and also when refilling after the dip would be a little cheaper — but never in a quarter
      // above the coming 24 h's average price (live 2026-10-30: all prices above the 0.35 limit, the plan let the
      // battery dip to 17 % from 21:15 and refilled at 23:15 instead of charging at midday's 0.40).
      // With a hard need too (the floor itself would be reached), the floor's own triggers stay and the band's quarters add
      // to them; without one only the band's rule decides.
      bool floorTriggered = hardNeed && inWindow;
      if (!input.EnforcePreferredSoc)
      {
        bool band = PreferredBandChargesNow(input, window, upcoming, now, simulatedSoc,
          PreferredBandTargetSoc(input, now, simulatedSoc, baseFutureSoC, need.LatestChargeTime), baseFutureSoC, pvWhSlot, totalLoadWhSlot);
        inWindow = floorTriggered || band;
      }
      bool socOk = SocOkToCharge(currentMode, simulatedSoc);
      // Not before a PV peak that fills the battery anyway (live 2026-10-25 11:45: 95 %, PV to 100 %
      // by 12:15, the low only tomorrow 07:00 — the charge bought at 35 ct what PV then exported).
      // The rest of the deficit is bought after the peak.
      bool bandOnly = !input.EnforcePreferredSoc && !floorTriggered;
      if (inWindow && PvRisesBeforeLow(simulatedSoc, now, need.LatestChargeTime, baseFutureSoC)
          && (bandOnly ? PreferredBandTargetSoc(input, now, simulatedSoc, baseFutureSoC, need.LatestChargeTime)
                       : DeficitChargeTargetSoc(simulatedSoc, now, endSlot, baseFutureSoC, need, input, marginAllowed)) - simulatedSoc <= MinPvRoomPct)
        inWindow = false;

      // No "next hour is cheaper, wait" deferral here: a cheaper next hour is already among
      // the chosen quarters, and deferring a chosen quarter just pushes the charge past them.
      if (inWindow && socOk)
      {
        // Without enforcing, the reason tells the charge's target apart: GoingUnderPreferredMinima = only the band
        // (PreferredBandTargetSoc), GoingUnderAbsoluteMinima = the floor (DeficitChargeTargetSoc).
        var reason = input.EnforcePreferredSoc
          ? need.EstimatedSoc <= input.AbsoluteMinSocPercent + 2 ? ForceChargeReasons.GoingUnderAbsoluteMinima : ForceChargeReasons.GoingUnderPreferredMinima
          : bandOnly ? ForceChargeReasons.GoingUnderPreferredMinima : ForceChargeReasons.GoingUnderAbsoluteMinima;
        return new InverterState(InverterModes.force_charge, reason);
      }

      // Not yet at the cheapest window — hold the battery at the effective floor.
      // Only use grid_only when PV cannot cover house load (pvWh < loadWh): in that case
      // normal mode would discharge the battery below the floor, so grid must cover instead.
      // When there IS a PV surplus (pvWh >= loadWh), fall through to normal so the battery
      // can charge from PV — grid_only would block all battery charging and cause PV to be
      // exported while the battery sits idle until the next grid charge window.
      // Once holding, release only HoldReleasePct above the floor: at the bare floor the live SoC's 1-% steps
      // switched normal/grid_only every cycle (live 2026-10-30 07:44).
      bool holding = currentMode.Mode == InverterModes.grid_only
        && currentMode.ModeReason is ForceChargeReasons.GoingUnderAbsoluteMinima or ForceChargeReasons.GoingUnderPreferredMinima;
      if (simulatedSoc <= floorSoc + (holding ? HoldReleasePct : 0) && pvWhSlot < totalLoadWhSlot)
      {
        var floorReason = input.EnforcePreferredSoc
          ? ForceChargeReasons.GoingUnderPreferredMinima
          : ForceChargeReasons.GoingUnderAbsoluteMinima;
        return new InverterState(InverterModes.grid_only, floorReason);
      }
    }
    return null;
  }

  // ── Energy flow calculation ───────────────────────────────────────────────────────────────
  /// <summary>Share of the forecast PV surplus in negative-export slots counted on when keeping battery room for it (forecast margin).</summary>
  public const double NegativeExportSurplusShare = 0.7;

  /// <summary>Whether the PV surplus in today's coming negative-export slots would fill the battery's remaining room on its own.</summary>
  /// <param name="input">Simulation input.</param>
  /// <param name="now">Current slot.</param>
  /// <param name="simulatedSoc">Simulated SoC now, %.</param>
  /// <returns>True if <see cref="NegativeExportSurplusShare"/> of that surplus (at most the charge rate per slot) covers the room up to 100 %.</returns>
  private static bool NegativeExportFillsBattery(SimulationInput input, DateTime now, int simulatedSoc)
  {
    double surplusWh = 0;
    for (var t = now.AddMinutes(SlotMinutes); t < now.Date.AddDays(1); t = t.AddMinutes(SlotMinutes))
    {
      if (!input.ExportPrices.TryGetPrice(t, out float exportPrice) || exportPrice >= 0) continue;
      var (pvWh, loadWh, extraWh) = input.SlotEnergyWh(t);
      surplusWh += Math.Min(Math.Max(0, pvWh - loadWh - extraWh), input.MaxChargeWhPerSlot());
    }
    return surplusWh > 0 && surplusWh * NegativeExportSurplusShare >= input.SocToWh(100 - simulatedSoc);
  }

  /// <summary>Energy flows of a slot in a mode.</summary>
  /// <param name="state">Inverter mode.</param>
  /// <param name="pvWh">PV of the slot, Wh.</param>
  /// <param name="totalLoadWh">Load incl. extra loads, Wh.</param>
  /// <param name="currentEnergyWh">Energy in the battery, Wh.</param>
  /// <param name="input">Simulation input.</param>
  /// <param name="chargeTargetSocPercent">Target of a force charge, %.</param>
  /// <param name="slotFraction">Simulated share of the slot (limits the charge rate).</param>
  /// <returns>Battery charge, battery discharge, grid import, grid export; each Wh ≥ 0.</returns>
  internal static (int battChargeWh, int battDischargeWh, int gridImportWh, int gridExportWh) ComputeEnergyFlows(
    InverterState state, int pvWh, int totalLoadWh, int currentEnergyWh, SimulationInput input, int chargeTargetSocPercent,
    double slotFraction = 1.0)
  {
    int availEnergy = currentEnergyWh;                              // Wh currently in battery (exact)
    int maxCapacity = input.BatteryCapacityWh;
    // Battery limit of a grid charge on its path (VPP by the SoC, bulk at the maximum)
    int chargeLimitWh = input.ChargeLimitWh(state.Bulk, input.WhToSoc(currentEnergyWh), slotFraction);
    int minEnergy = input.SocToWh(input.AbsoluteMinSocPercent); // Wh at absolute minimum
    // Maximum charge or discharge energy per 15-min slot (the running slot only for its rest,
    // SimulationInput.SlotFraction)
    int maxChargeWh = input.MaxChargeWhPerSlot(slotFraction);

    return state.Mode switch
    {
      // force_charge_grid_only must be checked before force_charge in the pattern
      // because C# switch arms are matched in order and both modes share the same name prefix
      // PV disconnected (negative import price): the grid covers house load and battery charging
      InverterModes.force_charge_grid_only
        => ForceCharge(0, totalLoadWh, availEnergy, maxCapacity, input.GridChargeWhPerSlot(0, totalLoadWh, slotFraction, chargeLimitWh), chargeLimitWh, chargeTargetSocPercent),

      InverterModes.force_charge
        => ForceCharge(pvWh, totalLoadWh, availEnergy, maxCapacity, input.GridChargeWhPerSlot(pvWh, totalLoadWh, slotFraction, chargeLimitWh), chargeLimitWh, chargeTargetSocPercent),

      InverterModes.force_discharge
        => ForceDischarge(pvWh, totalLoadWh, availEnergy, minEnergy, maxChargeWh),

      // Battery idle (no charge/discharge rate): PV covers load, grid any shortfall, surplus exported
      InverterModes.grid_only
        => Normal(pvWh, totalLoadWh, availEnergy, maxCapacity, maxChargeWh: 0, minEnergy),

      // PV surplus straight to the grid instead of the battery; the battery still covers a deficit
      InverterModes.feedin_priority
        => Normal(pvWh, totalLoadWh, availEnergy, maxCapacity, maxChargeWh, minEnergy, batteryChargeEnable: false),

      _ // normal, automatic — the standard PV-first flow
        => Normal(pvWh, totalLoadWh, availEnergy, maxCapacity, maxChargeWh, minEnergy, state.BatteryChargeEnable, state.ExportAllowed),
    };
  }

  /// <summary>PV covers the load; surplus charges the battery, then is exported; a deficit comes from the battery down to the floor, then the grid.</summary>
  /// <param name="pvWh">PV, Wh.</param>
  /// <param name="totalLoadWh">Load, Wh.</param>
  /// <param name="availEnergy">Energy in the battery, Wh.</param>
  /// <param name="maxCapacity">Battery capacity, Wh.</param>
  /// <param name="maxChargeWh">Charge/discharge limit of the slot, Wh; 0 = battery idle (grid_only).</param>
  /// <param name="minEnergy">Floor, Wh.</param>
  /// <param name="batteryChargeEnable">False: surplus is not charged (feedin_priority, waiting for a negative price).</param>
  /// <param name="exportAllowed">False: surplus is curtailed (negative export price).</param>
  /// <returns>Battery charge, battery discharge, grid import, grid export, Wh.</returns>
  private static (int battChargeWh, int battDischargeWh, int gridImportWh, int gridExportWh) Normal(
    int pvWh, int totalLoadWh, int availEnergy, int maxCapacity, int maxChargeWh, int minEnergy,
    bool batteryChargeEnable = true, bool exportAllowed = true)
  {
    int net = pvWh - totalLoadWh;
    if (net >= 0)
    {
      // PV surplus: charge battery up to the rate limit and available capacity (or curtail if
      // battery charging is disabled), export what's left over (or curtail if export is disallowed)
      int battCharge = batteryChargeEnable ? Math.Min(net, Math.Min(maxChargeWh, maxCapacity - availEnergy)) : 0;
      int gridExportWh = exportAllowed ? net - battCharge : 0;
      return (battCharge, 0, 0, gridExportWh);
    }
    // PV deficit: discharge battery down to the absolute minimum, up to the rate limit; grid covers the rest
    int deficit = -net;
    int battDischarge = Math.Min(deficit, Math.Min(maxChargeWh, Math.Max(0, availEnergy - minEnergy)));
    return (0, battDischarge, deficit - battDischarge, 0);
  }

  /// <summary>Charges at the maximum rate up to the target; PV covers the load and adds to the charge, the grid the rest.</summary>
  /// <param name="pvWh">PV, Wh; 0 for force_charge_grid_only.</param>
  /// <param name="totalLoadWh">Load, Wh.</param>
  /// <param name="availEnergy">Energy in the battery, Wh.</param>
  /// <param name="maxCapacity">Battery capacity, Wh.</param>
  /// <param name="maxChargeWh">Grid charge limit of the slot, Wh (<see cref="SimulationInput.GridChargeWhPerSlot"/>).</param>
  /// <param name="batteryLimitWh">Battery charge limit of the path, PV included (<see cref="SimulationInput.ChargeLimitWh"/>), Wh.</param>
  /// <param name="targetSocPercent">Target SoC, %.</param>
  /// <returns>Battery charge, 0, grid import, 0, Wh.</returns>
  private static (int battChargeWh, int battDischargeWh, int gridImportWh, int gridExportWh) ForceCharge(
    int pvWh, int totalLoadWh, int availEnergy, int maxCapacity, int maxChargeWh, int batteryLimitWh, int targetSocPercent)
  {
    int targetEnergy = Soc.ToWh(Math.Min(targetSocPercent, 100), maxCapacity);
    int pvSurplus = Math.Max(0, pvWh - totalLoadWh);
    // How much grid charging is needed to hit the rate limit (limited by how far we are from target)
    int gridChargeNeeded = Math.Min(maxChargeWh, Math.Max(0, targetEnergy - availEnergy));
    // PV surplus also charges the battery on top of grid charging (capped by capacity and the path's
    // battery limit: in VPP the inverter limits the battery power, PV's share included)
    int battCharge = Math.Min(Math.Min(gridChargeNeeded + pvSurplus, batteryLimitWh), maxCapacity - availEnergy);
    // Grid import covers the portion of battCharge not covered by PV, plus any load not covered by PV
    int gridImportWh = Math.Max(0, battCharge - pvSurplus) + Math.Max(0, totalLoadWh - pvWh);
    return (battCharge, 0, gridImportWh, 0);
  }

  /// <summary>Discharges at the maximum rate down to the floor; battery and PV cover the load, the rest is exported.</summary>
  /// <param name="pvWh">PV, Wh.</param>
  /// <param name="totalLoadWh">Load, Wh.</param>
  /// <param name="availEnergy">Energy in the battery, Wh.</param>
  /// <param name="minEnergy">Floor, Wh.</param>
  /// <param name="maxChargeWh">Discharge limit of the slot, Wh.</param>
  /// <returns>0, battery discharge, grid import, grid export, Wh.</returns>
  private static (int battChargeWh, int battDischargeWh, int gridImportWh, int gridExportWh) ForceDischarge(
    int pvWh, int totalLoadWh, int availEnergy, int minEnergy, int maxChargeWh)
  {
    // Discharge at max rate, but never below the absolute minimum
    int battDischarge = Math.Min(maxChargeWh, availEnergy - minEnergy);
    int gridExportWh = Math.Max(0, battDischarge + pvWh - totalLoadWh);
    int gridImportWh = Math.Max(0, totalLoadWh - pvWh - battDischarge);
    return (0, battDischarge, gridImportWh, gridExportWh);
  }

  // ── Helpers ─────────────────────────────────────────────────────────────────────────────

  /// <summary>Start of today's cheapest export-price block within the PV window that is long enough for PV to fill the battery (when to leave feedin_priority).</summary>
  /// <param name="now">Current slot.</param>
  /// <param name="baseFutureSoC">SoC look-ahead; its first 99 % sets the block length.</param>
  /// <param name="input">Simulation input.</param>
  /// <returns>Block start; null if the battery doesn't get full today or too few PV price hours remain.</returns>
  private static DateTime? FindChargingValleyStart(
    DateTime now, Dictionary<DateTime, int> baseFutureSoC, SimulationInput input)
  {
    // When does natural charging (base trajectory) first bring the battery to 99 %?
    var chargeComplete = baseFutureSoC
      .Where(kv => kv.Key > now && kv.Value >= 99)
      .OrderBy(kv => kv.Key)
      .Select(kv => (DateTime?)kv.Key)
      .FirstOrDefault();
    if (chargeComplete is null) return null;

    // Window width: hours needed for natural charging, minimum 1
    int durationHours = Math.Max(1, (int)Math.Ceiling((chargeComplete.Value - now).TotalHours));

    // Cap the valley search to today's PV window so charging stays solar-powered.
    var lastPVToday = input.GetLastRelevantPVTime(now.Date);

    // Candidate hours: today's remaining export price entries that fall within the PV window.
    // Only include entries whose full hour ends before PV stops.
    // Floor to the current hour so a valley that started earlier in the same price-hour stays
    // valid for all 15-min slots within it — without this, StartTime < now at 13:15 would
    // exclude the 13:00 entry and push valleyStart to 14:00, re-engaging feedin_priority for
    // the remaining 45 min of the already-started charging window.
    var nowHour = now.Floor(60);
    var pvHours = input.ExportPrices
      .Where(p => p.StartTime >= nowHour
               && p.StartTime.Date == now.Date
               && p.EndTime <= lastPVToday.AddHours(1))
      .OrderBy(p => p.StartTime)
      .ToList();

    if (pvHours.Count < durationHours) return null;

    // Sliding window of durationHours: find the contiguous block with the lowest total price.
    DateTime? best = null;
    float bestTotal = float.MaxValue;
    for (int i = 0; i <= pvHours.Count - durationHours; i++)
    {
      float windowTotal = 0;
      for (int j = 0; j < durationHours; j++) windowTotal += pvHours[i + j].Price;
      if (windowTotal < bestTotal) { bestTotal = windowTotal; best = pvHours[i].StartTime; }
    }
    return best;
  }

  /// <summary>How long the SoC stays at ≥ 99 % from its first maximum on.</summary>
  /// <param name="socDict">SoC by slot.</param>
  /// <returns>Hours; 0 if 99 % is never reached.</returns>
  private static double ComputeMaxSocDuration(Dictionary<DateTime, int> socDict)
  {
    var maxEntry = socDict.FirstMaxOrDefault();
    if (maxEntry.Value < 99) return 0;
    var firstUnder = socDict.FirstUnderOrDefault(99, maxEntry.Key);
    // If it never drops below 99 % again, use the last slot as the end of the plateau
    var endTime = firstUnder.Key == default ? socDict.Keys.Max() : firstUnder.Key;
    return (endTime - maxEntry.Key).TotalHours;
  }

}
