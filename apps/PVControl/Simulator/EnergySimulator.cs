using System;
using System.Collections.Generic;
using System.Linq;
using NetDeamon.apps.PVControl;

namespace NetDeamon.apps.PVControl.Simulator;

/// <summary>
/// Forward-stepping two-day energy simulator.
///
/// The key insight this solves: the old BatterySoCPrediction integrated PV minus load naively,
/// so it had no idea that the system would force-charge at 3 am. That made "what will the SoC
/// be in 8 hours?" unreliable whenever charging decisions were going to happen in between.
///
/// The time window always covers exactly two full calendar days — today 00:00 through tomorrow
/// 23:45 — matching the EPEX Spot price publication cycle (current day always known; next day
/// published around 14:00–15:00 and immediately usable). The simulation starts at the current
/// quarter-hour slot and runs to tomorrow's midnight; past slots of today are back-filled by
/// HouseEnergy.RunSimulation() using the net-energy prediction in reverse.
///
/// This simulator steps through every 15-minute slot from now to end of tomorrow. At each slot it:
///   1. Computes a BASE future SoC (PV – load, no charging) as a look-ahead.
///   2. Asks "would the real algorithm charge now?" using that base look-ahead.
///   3. Decides the inverter mode (same logic as the old CalculateNewInverterMode).
///   4. Computes the resulting energy flows (battery delta, grid import/export).
///   5. Steps the simulated SoC forward.
///
/// Because the mode decision and the SoC update happen together at each step, future slots
/// already reflect charging that was scheduled in earlier slots — giving an accurate SoC
/// timeline that accounts for all system decisions.
///
/// The first slot's InverterState becomes the live inverter command.
/// Extra loads (car charging etc.) can be injected to see their impact on the plan.
/// </summary>
public static class EnergySimulator
{
  private const int SlotMinutes = 15;
  private const int ChargeVoltage = 230; // V — assumed fixed for charge power calculations

  /// <summary>
  /// Runs the full simulation and returns one <see cref="SimulationSlot"/> per 15-minute
  /// interval from <see cref="SimulationInput.StartTime"/> (rounded to the nearest quarter-hour)
  /// through the end of tomorrow — i.e. today + tomorrow as two full calendar days.
  /// This window matches the EPEX Spot price data range and the PredictionContainer layout.
  /// </summary>
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
    int currentEnergyWh = input.StartSocPercent * input.BatteryCapacityWh / 100;
    var currentMode = input.CurrentMode;

    for (var slotTime = startSlot; slotTime < endSlot; slotTime = slotTime.AddMinutes(SlotMinutes))
    {
      // Energy values for this slot (Wh per 15-min period)
      int pvWh = input.PVPredictionWh.GetValueOrDefault(slotTime, 0);
      int loadWh = input.LoadPredictionWh.GetValueOrDefault(slotTime, 0);
      int extraLoadWh = input.ExtraLoads.Sum(e => e.GetWhForSlot(slotTime));
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
      var newMode = ComputeMode(currentMode, needToCharge, input, slotTime, currentSoc,
        baseFutureSoC, pvWh, totalLoadWh);
      currentMode = newMode;

      // --- Step 4: compute energy flows for this slot given the chosen mode ---
      // For NeedToCharge-triggered force_charge, only charge the minimum required to stay
      // above the floor — not all the way to ForceChargeTargetSocPercent (which is meant for
      // user-initiated force charging and would cause massive over-charging here).
      int chargeTargetSocPercent = input.ForceChargeTargetSocPercent;
      if (newMode.Mode == InverterModes.force_charge &&
          (newMode.ModeReason == ForceChargeReasons.GoingUnderAbsoluteMinima ||
           newMode.ModeReason == ForceChargeReasons.GoingUnderPreferredMinima))
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
        var pvPeak = baseFutureSoC.FirstMaxOrDefault();
        // Only use the trough window if PV actually recovers above the floor (avoids edge case
        // where base trajectory is always declining — no sun — where pvPeak.Key equals the first slot).
        var troughWindowEnd = (pvPeak.Key != default && pvPeak.Value > floorSoc + 10)
          ? pvPeak.Key
          : slotTime.AddHours(24);
        var troughEntry = baseFutureSoC.FirstMinOrDefault(end: troughWindowEnd);
        int baseTrough = troughEntry.Key != default ? troughEntry.Value : needToCharge.EstimatedSoc;
        int deficit = Math.Max(0, floorSoc - baseTrough);
        chargeTargetSocPercent = Math.Min(100, currentSoc + deficit + 2);
      }
      var (battChargeWh, battDischargeWh, gridImportWh, gridExportWh) = ComputeEnergyFlows(newMode, pvWh, totalLoadWh, currentEnergyWh, input, chargeTargetSocPercent);

      slots.Add(new SimulationSlot(slotTime, currentSoc, newMode, pvWh, loadWh, extraLoadWh, battChargeWh, battDischargeWh, gridImportWh, gridExportWh));

      // --- Step 5: advance SoC for the next slot ---
      // Keep Wh exact; derive % only for mode decisions (integer division, no rounding).
      currentEnergyWh = Math.Clamp(currentEnergyWh + battChargeWh - battDischargeWh, 0, input.BatteryCapacityWh);
      currentSoc = currentEnergyWh * 100 / input.BatteryCapacityWh;
    }

    return new SimulationResult(slots, input);
  }

  // ── Base future SoC ─────────────────────────────────────────────────────────────────────
  // Integrates PV – totalLoad (including extra loads) from fromSlot onward without applying
  // any inverter decisions. This gives the "worst case" trajectory used to judge whether
  // charging must be scheduled: if the base SoC drops below the minimum before PV can
  // recover it, we need to force-charge from the grid.

  private static Dictionary<DateTime, int> ComputeBaseFutureSoC(
    int currentSocPercent, DateTime startSlot, DateTime endSlot, SimulationInput input)
  {
    var result = new Dictionary<DateTime, int>();
    int energy = currentSocPercent * input.BatteryCapacityWh / 100;

    for (var t = startSlot; t < endSlot; t = t.AddMinutes(SlotMinutes))
    {
      int pv = input.PVPredictionWh.GetValueOrDefault(t, 0);
      int load = input.LoadPredictionWh.GetValueOrDefault(t, 0);
      int extra = input.ExtraLoads.Sum(e => e.GetWhForSlot(t));
      // Store SoC at the START of the slot (before applying energy) so the base look-ahead
      // values align with SimulationSlot.SoC which is also the start-of-slot value.
      result[t] = energy * 100 / input.BatteryCapacityWh;
      energy = Math.Clamp(energy + pv - load - extra, 0, input.BatteryCapacityWh);
    }

    return result;
  }

  // ── NeedToCharge ────────────────────────────────────────────────────────────────────────
  // Mirrors the live NeedToChargeFromExternal logic.
  // We find when (and at what SoC) the base trajectory first drops below the minimum,
  // then check whether a PV recovery (reaching 100 %) happens before that point.
  // If no recovery is coming in time we flag NeedToCharge and report the latest safe
  // moment to start charging (10 % earlier than the critical time, rounded to 15-min slots).

  private static NeedToChargeResult ComputeNeedToCharge(
    int currentSoc, Dictionary<DateTime, int> baseFutureSoC,
    InverterState currentMode, SimulationInput input, DateTime now)
  {
    int minSoC = input.GetEffectiveMinSoC();
    // Add 1 % hysteresis while already charging to prevent thrashing at the boundary
    if (currentMode.Mode == InverterModes.force_charge)
      minSoC++;

    // When do we first drop to or below minSoC?
    // If we're already below it (edge case after a settings change or offline period),
    // treat "now" as the critical moment.
    var minReached = currentSoc < minSoC
      ? new KeyValuePair<DateTime, int>(now, currentSoc)
      : baseFutureSoC.FirstUnderOrDefault(minSoC, start: now);

    // If we never drop below minimum, just record the lowest point for diagnostics
    if (minReached.Key == default)
      minReached = baseFutureSoC.FirstMinOrDefault(start: now);

    // When does the base trajectory peak (PV fully charges the battery)?
    var maxReached = baseFutureSoC.FirstMaxOrDefault(start: now);

    // We need to charge if: SoC will reach or breach the minimum AND that happens before a
    // full PV recovery, OR PV will never get us back to 100 % at all.
    // Note: FirstUnderOrDefault uses <= so minReached.Value can equal minSoC (just touching
    // the floor counts as needing a charge — one more slot of discharge would breach it).
    bool needCharge = minReached.Value <= minSoC
      && (minReached.Key < maxReached.Key || maxReached.Value < 100);

    // Build in 10 % of lead time so we start charging slightly before the critical point
    int quartersTilCharge = (int)(((minReached.Key - now).TotalMinutes * 0.1) / SlotMinutes);
    return new NeedToChargeResult(
      estimatedSoc: minReached.Value,
      latestChargeTime: minReached.Key.AddMinutes(-quartersTilCharge * SlotMinutes),
      needToCharge: needCharge);
  }

  // ── Mode decision ────────────────────────────────────────────────────────────────────────
  // Pure-function equivalent of HouseEnergy.CalculateNewInverterMode.
  // All inputs are passed explicitly so this can run without any live HA state.
  // The logic is evaluated top-to-bottom; the first matching condition wins:
  //   negative import → negative export →
  //   opportunistic discharge → user force-charge slot → need-to-charge → normal

  private static InverterState ComputeMode(
    InverterState currentMode, NeedToChargeResult need, SimulationInput input,
    DateTime now, int simulatedSoc, Dictionary<DateTime, int> baseFutureSoC,
    int pvWhSlot, int totalLoadWhSlot)
  {
    float importPriceNow = input.ImportPrices.GetPrice(now);
    float exportPriceNow = input.ExportPrices.GetPrice(now);

    // ── Negative import price ─────────────────────────────────────────────────────────────
    // Grid is paying us to consume electricity → fill battery as fast as possible.
    // But if a cheaper (more negative) import window is still coming today, defer grid
    // charging to that window. Stay in house_only with battery charging disabled so PV
    // surplus is curtailed rather than exported (export price is also typically negative),
    // preserving battery room for the cheapest grid energy.
    // Once at or past the cheapest window: use force_charge_grid_only to fill the battery.
    // If the battery is already ≥ 95 %: use house_only (not grid_only) to avoid exporting
    // PV surplus at negative export prices.
    if (importPriceNow < 0)
    {
      bool cheaperImportComingToday = input.ImportPrices
        .Any(p => p.StartTime.Date == now.Date && p.StartTime > now && p.Price < importPriceNow);
      if (cheaperImportComingToday)
        return new InverterState(InverterModes.house_only, ForceChargeReasons.ImportPriceNegative, batteryChargeEnable: false);

      int pvPowerW = pvWhSlot * 4; // convert Wh/slot → W
      var mode = (simulatedSoc <= 95 || pvPowerW < 100)
        ? InverterModes.force_charge_grid_only
        : InverterModes.house_only; // avoid exporting PV at negative export prices
      return new InverterState(mode, ForceChargeReasons.ImportPriceNegative);
    }

    // ── Negative export price ─────────────────────────────────────────────────────────────
    // Grid charges us for feeding in → stop exporting, cover house from PV/battery only.
    // If negative import prices are also coming later today, disable battery charging now
    // so we have room to absorb the cheap/free grid energy then.
    if (exportPriceNow < 0)
    {
      bool battChargeEnable = !input.ImportPrices.NegativeImportUpcoming(now);
      return new InverterState(InverterModes.house_only, ForceChargeReasons.ExportPriceNegative, battChargeEnable);
    }

    float exportPriceNextHour = input.ExportPrices.GetPrice(now.AddHours(1));

    // ── Opportunistic discharge ───────────────────────────────────────────────────────────
    // When enabled, the system can earn money by selling battery energy at peak prices,
    // provided we are confident the battery will still reach 100 % later (via PV) and
    // stay above the minimum SoC floor.
    if (input.OpportunisticDischarge)
    {
      // Hysteresis: once in feedin_priority we stay a bit longer before switching back
      double maxSocDuration = currentMode.Mode == InverterModes.feedin_priority ? 1.5 : 2.0;

      // How long does the base trajectory predict 100 % SoC today?
      // If it stays full for more than maxSocDuration hours we can afford to export
      var todayBaseSoC = baseFutureSoC.Where(s => s.Key.Date == now.Date).ToDictionary();
      double maxSocDurationCalc = ComputeMaxSocDuration(todayBaseSoC);

      bool inPVPeriod = input.IsInPVPeriod(now);
      int pvPowerW = pvWhSlot * 4;
      int loadPowerW = totalLoadWhSlot * 4;

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

      if (sellMaxima.Any(t => t.StartTime <= now && t.EndTime > now)
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

    // ── User-initiated force charge at cheapest window ────────────────────────────────────
    // The ForceCharge switch tells us to fill the battery to ForceChargeTargetSoC % at the
    // cheapest hour of the day. We look 1 h before to 2 h after the cheapest slot.
    // If charging takes more than 60 min and the hour BEFORE cheapest is still cheap,
    // we start early so we finish exactly at the cheapest moment.
    if (input.EnableCheapForceCharge)
    {
      var cheapestToday = input.ImportPrices.GetCheapestWindowToday(now);
      if (cheapestToday.StartTime != default &&
          cheapestToday.Price <= input.ForceChargeMaxPrice &&
          now > cheapestToday.StartTime.AddHours(-1) && now < cheapestToday.StartTime.AddHours(2))
      {
        // Hysteresis: if already charging in this slot, keep going until target is reached
        if (currentMode.ModeReason == ForceChargeReasons.ForcedChargeAtMinimumPrice
            && currentMode.Mode == InverterModes.force_charge
            && simulatedSoc <= Math.Min(98, input.ForceChargeTargetSocPercent + 2))
          return currentMode;

        // How long will charging take from the base SoC at the cheapest start time?
        int socAtBestTime = baseFutureSoC.GetValueOrDefault(cheapestToday.StartTime, simulatedSoc);
        int chargeTime = input.CalculateChargingDuration(socAtBestTime, 100);
        int rankBefore = input.ImportPrices.GetPriceRank(cheapestToday.StartTime.AddHours(-1));
        int rankAfter = input.ImportPrices.GetPriceRank(cheapestToday.StartTime.AddHours(1));
        DateTime chargeStart = cheapestToday.StartTime;

        // If charging takes >60 min and the preceding hour is cheaper than the following,
        // start before the cheapest slot so we end at the cheapest hour (cheapest-end charging)
        if (chargeTime > 60 && rankBefore < rankAfter)
        {
          var priceHourBefore = input.ImportPrices.FirstOrDefault(p => p.StartTime == chargeStart.AddHours(-1));
          if (priceHourBefore.Price < input.ForceChargeMaxPrice)
            chargeStart = cheapestToday.StartTime.AddMinutes(-(chargeTime - 50));
        }

        if (now > chargeStart && now < chargeStart.AddMinutes(chargeTime + 10)
            && simulatedSoc < Math.Min(96, input.ForceChargeTargetSocPercent))
          return new InverterState(InverterModes.force_charge, ForceChargeReasons.ForcedChargeAtMinimumPrice);
      }
    }

    // ── NeedToCharge → force charge at best available price window ────────────────────────
    // If the base SoC forecast predicts we will go below minimum and PV won't rescue us,
    // we must buy electricity. We pick the cheapest available hour before the critical time.
    // Exception: if the NEXT hour is even cheaper, hold off (importing now would just cost more).
    if (need.NeedToCharge)
    {
      // Choose the search window for the cheapest charge slot.
      //
      // Two problems with always using deadline-constrained GetBestChargeWindow:
      //   1. LatestChargeTime collapses to "now" when the base SoC first touches the floor
      //      → bestChargeWindow = current (expensive) hour → premature force_charge.
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
      var pvTakeover = baseFutureSoC.Keys
        .Where(t => t > now
                    && input.PVPredictionWh.GetValueOrDefault(t, 0) > input.LoadPredictionWh.GetValueOrDefault(t, 0))
        .DefaultIfEmpty(now.AddHours(24))
        .First();

      PriceTableEntry bestChargeWindow;
      if (need.LatestChargeTime <= pvTakeover)
      {
        // Battery will hit minimum before today's PV recovery → must charge before solar arrives.
        bestChargeWindow = input.ImportPrices
          .Where(p => p.StartTime >= now.Date.AddHours(now.Hour) && p.StartTime < pvTakeover)
          .OrderBy(p => p.Price)
          .FirstOrDefault();
        if (bestChargeWindow.StartTime == default)
          bestChargeWindow = input.ImportPrices.GetBestChargeWindow(need, now); // safety fallback
      }
      else
      {
        // Battery hits minimum after today's PV window (e.g. tomorrow) → broader search up to
        // LatestChargeTime naturally includes cheap afternoon hours without reaching tomorrow PV.
        bestChargeWindow = input.ImportPrices.GetBestChargeWindow(need, now);
      }

      // Simulation slots are always on exact quarter-hour boundaries, so the ±30-second
      // guard used by the live system (to avoid triggering before the price feed updates)
      // would skip the first slot of every window (02:00 > 02:00:30 is false).
      // Use exact boundary comparison instead.
      bool inWindow = now >= bestChargeWindow.StartTime && now < bestChargeWindow.EndTime;
      // Don't start over 96 % (too slow); allow up to 98 % once already charging
      bool socOk = currentMode.Mode == InverterModes.force_charge ? simulatedSoc <= 98 : simulatedSoc <= 96;

      if (inWindow && socOk)
      {
        float importPriceNextHour = input.ImportPrices.GetPrice(now.AddHours(1));
        // No deadline constraint on deferral — the floor is held by grid_only anyway.
        // (In practice this rarely fires since bestChargeWindow IS the cheapest hour.)
        bool canWaitForNextHour = importPriceNow > importPriceNextHour;
        if (canWaitForNextHour)
        {
          int effectiveFloor = input.GetEffectiveMinSoC();
          var mode = simulatedSoc <= effectiveFloor ? InverterModes.grid_only : InverterModes.normal;
          return new InverterState(mode, ForceChargeReasons.NextHourCheaper);
        }
        var reason = need.EstimatedSoc <= input.AbsoluteMinSocPercent + 2
          ? ForceChargeReasons.GoingUnderAbsoluteMinima
          : ForceChargeReasons.GoingUnderPreferredMinima;
        return new InverterState(InverterModes.force_charge, reason);
      }

      // Not yet at the cheapest window — hold the battery at the effective floor.
      // Only use grid_only when PV cannot cover house load (pvWh < loadWh): in that case
      // normal mode would discharge the battery below the floor, so grid must cover instead.
      // When there IS a PV surplus (pvWh >= loadWh), fall through to normal so the battery
      // can charge from PV — grid_only would block all battery charging and cause PV to be
      // exported while the battery sits idle until the next grid charge window.
      if (simulatedSoc <= floorSoc && pvWhSlot < totalLoadWhSlot)
      {
        var floorReason = input.EnforcePreferredSoc
          ? ForceChargeReasons.GoingUnderPreferredMinima
          : ForceChargeReasons.GoingUnderAbsoluteMinima;
        return new InverterState(InverterModes.grid_only, floorReason);
      }
    }

    // ── Default ───────────────────────────────────────────────────────────────────────────
    // None of the special conditions apply → let the inverter manage PV/battery normally.
    return new InverterState(InverterModes.normal, ForceChargeReasons.None);
  }

  // ── Energy flow calculation ───────────────────────────────────────────────────────────────
  // Given the chosen mode and the predicted PV/load for the slot, compute:
  //   battChargeWh    : Wh flowing into the battery (≥ 0)
  //   battDischargeWh : Wh flowing out of the battery (≥ 0)
  //   gridImportWh    : Wh pulled from the grid (≥ 0)
  //   gridExportWh    : Wh pushed to the grid (≥ 0)
  // These drive the SoC update and are stored in the SimulationSlot for diagnostics.

  private static (int battChargeWh, int battDischargeWh, int gridImportWh, int gridExportWh) ComputeEnergyFlows(
    InverterState state, int pvWh, int totalLoadWh, int currentEnergyWh, SimulationInput input, int chargeTargetSocPercent)
  {
    int availEnergy = currentEnergyWh;                              // Wh currently in battery (exact)
    int maxCapacity = input.BatteryCapacityWh;
    int minEnergy = input.AbsoluteMinSocPercent * maxCapacity / 100; // Wh at absolute minimum
    // Maximum charge or discharge energy per 15-min slot: amps × 240 V / 4 slots per hour
    int maxChargeWh = input.MaxChargePowerAmps * ChargeVoltage / 4;

    return state.Mode switch
    {
      // force_charge_grid_only must be checked before force_charge in the pattern
      // because C# switch arms are matched in order and both modes share the same name prefix
      InverterModes.force_charge or InverterModes.force_charge_grid_only
        when state.Mode == InverterModes.force_charge_grid_only
        => ForceChargeGridOnly(totalLoadWh, availEnergy, maxCapacity, maxChargeWh, chargeTargetSocPercent),

      InverterModes.force_charge
        => ForceCharge(pvWh, totalLoadWh, availEnergy, maxCapacity, maxChargeWh, chargeTargetSocPercent),

      InverterModes.force_discharge
        => ForceDischarge(pvWh, totalLoadWh, availEnergy, minEnergy, maxChargeWh),

      // Battery idle; PV covers load, grid covers any shortfall, surplus PV exported
      InverterModes.grid_only
        => (0, 0, Math.Max(0, totalLoadWh - pvWh), Math.Max(0, pvWh - totalLoadWh)),

      // PV → house only, no grid export; battery charges from PV surplus or discharges for deficit.
      // Grid import is still allowed as a fallback if the battery is depleted.
      InverterModes.house_only
        => HouseOnly(pvWh, totalLoadWh, availEnergy, maxCapacity, minEnergy),

      InverterModes.feedin_priority
        => FeedinPriority(pvWh, totalLoadWh, availEnergy, minEnergy, maxChargeWh),

      _ // normal, automatic, reset — all follow the standard PV-first flow
        => Normal(pvWh, totalLoadWh, availEnergy, maxCapacity, maxChargeWh, minEnergy),
    };
  }

  /// <summary>
  /// Standard mode: PV covers house load first.
  /// Any surplus charges the battery (up to max charge rate and capacity).
  /// Any remaining surplus is exported to the grid.
  /// If PV is insufficient, the battery discharges to cover the deficit (never below
  /// <paramref name="minEnergy"/>, matching the inverter's own minimum SoC setting);
  /// any remaining deficit is imported from the grid.
  /// </summary>
  private static (int battChargeWh, int battDischargeWh, int gridImportWh, int gridExportWh) Normal(
    int pvWh, int totalLoadWh, int availEnergy, int maxCapacity, int maxChargeWh, int minEnergy)
  {
    int net = pvWh - totalLoadWh;
    if (net >= 0)
    {
      // PV surplus: charge battery up to the rate limit and available capacity, export the rest
      int battCharge = Math.Min(net, Math.Min(maxChargeWh, maxCapacity - availEnergy));
      return (battCharge, 0, 0, net - battCharge);
    }
    // PV deficit: discharge battery down to the absolute minimum; grid covers the rest
    int deficit = -net;
    int battDischarge = Math.Min(deficit, Math.Max(0, availEnergy - minEnergy));
    return (0, battDischarge, deficit - battDischarge, 0);
  }

  /// <summary>
  /// Force-charge from grid at maximum rate.
  /// PV still covers house load and any PV surplus also goes into the battery,
  /// reducing the grid draw needed to hit the charge rate target.
  /// Charging stops when the battery reaches ForceChargeTargetSoC.
  /// </summary>
  private static (int battChargeWh, int battDischargeWh, int gridImportWh, int gridExportWh) ForceCharge(
    int pvWh, int totalLoadWh, int availEnergy, int maxCapacity, int maxChargeWh, int targetSocPercent)
  {
    int targetEnergy = Math.Min(targetSocPercent, 100) * maxCapacity / 100;
    int pvSurplus = Math.Max(0, pvWh - totalLoadWh);
    // How much grid charging is needed to hit the rate limit (limited by how far we are from target)
    int gridChargeNeeded = Math.Min(maxChargeWh, Math.Max(0, targetEnergy - availEnergy));
    // PV surplus also charges the battery on top of grid charging (capped by capacity)
    int battCharge = Math.Min(gridChargeNeeded + pvSurplus, maxCapacity - availEnergy);
    // Grid import covers the portion of battCharge not covered by PV, plus any load not covered by PV
    int gridImportWh = Math.Max(0, battCharge - pvSurplus) + Math.Max(0, totalLoadWh - pvWh);
    return (battCharge, 0, gridImportWh, 0);
  }

  /// <summary>
  /// Force-charge with PV disconnected (used when import price is negative).
  /// The inverter isolates the PV strings, so no solar energy is available.
  /// Grid covers both house load and battery charging at maximum rate.
  /// </summary>
  private static (int battChargeWh, int battDischargeWh, int gridImportWh, int gridExportWh) ForceChargeGridOnly(
    int totalLoadWh, int availEnergy, int maxCapacity, int maxChargeWh, int targetSocPercent)
  {
    int targetEnergy = Math.Min(targetSocPercent, 100) * maxCapacity / 100;
    int battCharge = Math.Min(maxChargeWh, Math.Max(0, targetEnergy - availEnergy));
    // Everything comes from the grid: house load + battery charge (PV is disconnected)
    return (battCharge, 0, battCharge + totalLoadWh, 0);
  }

  /// <summary>
  /// Force-discharge battery to the grid at maximum rate (opportunistic export at price peak).
  /// PV generation also goes to the grid.
  /// House load is covered from the battery discharge; any remaining deficit from the grid.
  /// Discharge is limited by the absolute minimum SoC floor.
  /// </summary>
  private static (int battChargeWh, int battDischargeWh, int gridImportWh, int gridExportWh) ForceDischarge(
    int pvWh, int totalLoadWh, int availEnergy, int minEnergy, int maxChargeWh)
  {
    // Discharge at max rate, but never below the absolute minimum
    int battDischarge = Math.Min(maxChargeWh, availEnergy - minEnergy);
    int gridExportWh = Math.Max(0, battDischarge + pvWh - totalLoadWh);
    int gridImportWh = Math.Max(0, totalLoadWh - pvWh - battDischarge);
    return (0, battDischarge, gridImportWh, gridExportWh);
  }

  /// <summary>
  /// House-only mode: no grid export. PV covers house load first; any PV surplus charges
  /// the battery (up to capacity). If PV is insufficient the battery discharges for the
  /// deficit; if the battery is depleted the grid covers the remainder as a fallback.
  /// This mode is active when the export price is negative — we avoid feeding in but still
  /// need to power the house and can use the battery normally.
  /// </summary>
  private static (int battChargeWh, int battDischargeWh, int gridImportWh, int gridExportWh) HouseOnly(
    int pvWh, int totalLoadWh, int availEnergy, int maxCapacity, int minEnergy)
  {
    int net = pvWh - totalLoadWh;
    if (net >= 0)
    {
      // PV surplus: charge battery, no export
      int battCharge = Math.Min(net, maxCapacity - availEnergy);
      return (battCharge, 0, 0, 0);
    }
    // PV deficit: discharge battery first, grid as fallback — never export
    int deficit = -net;
    int battDischarge = Math.Min(deficit, availEnergy - minEnergy);
    return (0, battDischarge, deficit - battDischarge, 0);
  }

  /// <summary>
  /// Feed-in priority: PV generation is directed to the grid first (to maximise export earnings).
  /// The battery only discharges to cover any house load that PV cannot satisfy.
  /// Battery charging from PV does NOT happen in this mode — the inverter feeds PV to grid.
  /// </summary>
  private static (int battChargeWh, int battDischargeWh, int gridImportWh, int gridExportWh) FeedinPriority(
    int pvWh, int totalLoadWh, int availEnergy, int minEnergy, int maxChargeWh)
  {
    // PV surplus goes straight to grid
    int gridExportWh = Math.Max(0, pvWh - totalLoadWh);
    // Any deficit is covered by the battery first, then grid
    int deficit = Math.Max(0, totalLoadWh - pvWh);
    int battDischarge = Math.Min(deficit, Math.Min(maxChargeWh, availEnergy - minEnergy));
    return (0, battDischarge, deficit - battDischarge, gridExportWh);
  }

  // ── Helpers ─────────────────────────────────────────────────────────────────────────────

  /// <summary>
  /// Finds the start of the cheapest contiguous export-price window today during which PV is
  /// available, wide enough for natural charging to bring the battery to full.
  ///
  /// Window width (whole hours) is derived from <paramref name="baseFutureSoC"/>: how long
  /// normal PV charging takes from the current simulated SoC to 99 %. The cheapest contiguous
  /// block of that many price hours, within today's remaining PV window, is the "valley" —
  /// the optimal moment to pause feedin_priority so free PV energy refills the battery at the
  /// lowest opportunity cost. The fully-charged battery can then be discharged at peak prices.
  ///
  /// Returns null when the battery will not reach 99 % today or there are insufficient PV
  /// price hours to form a valid window.
  /// </summary>
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
    var nowHour = new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0);
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

  /// <summary>
  /// How many hours the battery stays at or above 99 % SoC in the given base forecast.
  /// A long plateau means we can afford to discharge opportunistically without risking
  /// running short — PV will refill the battery before it matters.
  /// Returns 0 if the SoC never reaches 99 %.
  /// </summary>
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
