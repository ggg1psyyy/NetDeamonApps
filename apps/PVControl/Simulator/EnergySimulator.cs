using System;
using System.Collections.Generic;
using System.Linq;

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
///   1. Computes a NAIVE future SoC (PV – load, no charging) as a look-ahead.
///   2. Asks "would the real algorithm charge now?" using that naive look-ahead.
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
  public static List<SimulationSlot> Simulate(SimulationInput input)
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
    int resetCounter = input.CurrentResetCounter; // counts down to 0; each tick emits reset mode
    int problemCounter = 0; // counts consecutive ticks in the inverter-won't-use-battery condition

    for (var slotTime = startSlot; slotTime < endSlot; slotTime = slotTime.AddMinutes(SlotMinutes))
    {
      // Energy values for this slot (Wh per 15-min period)
      int pvWh = input.PVPredictionWh.GetValueOrDefault(slotTime, 0);
      int loadWh = input.LoadPredictionWh.GetValueOrDefault(slotTime, 0);
      int extraLoadWh = input.ExtraLoads.Sum(e => e.GetWhForSlot(slotTime));
      int totalLoadWh = loadWh + extraLoadWh;

      // --- Step 1: naive look-ahead SoC (no charging) for this slot onward ---
      // Used as input to NeedToCharge and to some mode conditions (max SoC duration, sell maxima).
      // We recompute it each slot so it reflects the already-updated simulated SoC, not the
      // original starting SoC — otherwise the look-ahead would drift further from reality as
      // we step forward.
      var naiveFutureSoC = ComputeNaiveFutureSoC(currentSoc, slotTime, startSlot, endSlot, input);

      // --- Step 2: decide if charging is needed based on naive look-ahead ---
      var needToCharge = ComputeNeedToCharge(currentSoc, naiveFutureSoC, currentMode, input, slotTime);

      // --- Step 3: pick inverter mode ---
      // Only the first slot uses the live grid-power reading for the inverter-bug-fix check;
      // for future slots we have no real grid power reading so that check is skipped.
      bool isFirstSlot = slotTime == startSlot;
      var newMode = ComputeMode(currentMode, needToCharge, input, slotTime, currentSoc,
        naiveFutureSoC, pvWh, totalLoadWh, isFirstSlot, ref problemCounter, ref resetCounter);
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
        int floorSoc = input.EnforcePreferredSoc ? input.PreferredMinSocPercent : input.AbsoluteMinSocPercent;
        // Charge enough to clear the WHOLE overnight trough (minimum before PV recovery),
        // not just the first floor-crossing. Using EstimatedSoc (first-crossing value) leaves
        // the naive still declining past that point, so needCharge keeps firing every few slots
        // at progressively more expensive windows until dawn.
        //
        // Strategy: find the lowest point of the naive between now and the next PV peak.
        // chargeTarget = floorSoc + (currentSoc - naiveTrough) + buffer
        //             = currentSoc + (floorSoc - naiveTrough) + buffer
        // After charging, the naive trough shifts up by the same amount, landing above the floor.
        var pvPeak = naiveFutureSoC.FirstMaxOrDefault();
        // Only use the trough window if PV actually recovers above the floor (avoids edge case
        // where naive is always declining — no sun — where pvPeak.Key equals the first slot).
        var troughWindowEnd = (pvPeak.Key != default && pvPeak.Value > floorSoc + 10)
          ? pvPeak.Key
          : slotTime.AddHours(24);
        var troughEntry = naiveFutureSoC.FirstMinOrDefault(end: troughWindowEnd);
        int naiveTrough = troughEntry.Key != default ? troughEntry.Value : needToCharge.EstimatedSoc;
        int deficit = Math.Max(0, floorSoc - naiveTrough);
        chargeTargetSocPercent = Math.Min(100, currentSoc + deficit + 2);
      }
      var (battChargeWh, battDischargeWh, gridImportWh, gridExportWh) = ComputeEnergyFlows(newMode, pvWh, totalLoadWh, currentEnergyWh, input, chargeTargetSocPercent);

      slots.Add(new SimulationSlot(slotTime, currentSoc, newMode, pvWh, loadWh, extraLoadWh, battChargeWh, battDischargeWh, gridImportWh, gridExportWh));

      // --- Step 5: advance SoC for the next slot ---
      // Keep Wh exact; derive % only for mode decisions (integer division, no rounding).
      currentEnergyWh = Math.Clamp(currentEnergyWh + battChargeWh - battDischargeWh, 0, input.BatteryCapacityWh);
      currentSoc = currentEnergyWh * 100 / input.BatteryCapacityWh;
    }

    return slots;
  }

  // ── Naive future SoC ────────────────────────────────────────────────────────────────────
  // Integrates PV – totalLoad (including extra loads) from fromSlot onward without applying
  // any inverter decisions. This gives the "worst case" trajectory used to judge whether
  // charging must be scheduled: if the naive SoC drops below the minimum before PV can
  // recover it, we need to force-charge from the grid.

  private static Dictionary<DateTime, int> ComputeNaiveFutureSoC(
    int currentSocPercent, DateTime fromSlot, DateTime startSlot, DateTime endSlot, SimulationInput input)
  {
    var result = new Dictionary<DateTime, int>();
    int energy = currentSocPercent * input.BatteryCapacityWh / 100;

    for (var t = fromSlot; t < endSlot; t = t.AddMinutes(SlotMinutes))
    {
      int pv = input.PVPredictionWh.GetValueOrDefault(t, 0);
      int load = input.LoadPredictionWh.GetValueOrDefault(t, 0);
      int extra = input.ExtraLoads.Sum(e => e.GetWhForSlot(t));
      // Store SoC at the START of the slot (before applying energy) so the naive look-ahead
      // values align with SimulationSlot.SoC which is also the start-of-slot value.
      result[t] = energy * 100 / input.BatteryCapacityWh;
      energy = Math.Clamp(energy + pv - load - extra, 0, input.BatteryCapacityWh);
    }

    return result;
  }

  // ── NeedToCharge ────────────────────────────────────────────────────────────────────────
  // Mirrors the live NeedToChargeFromExternal logic.
  // We find when (and at what SoC) the naive trajectory first drops below the minimum,
  // then check whether a PV recovery (reaching 100 %) happens before that point.
  // If no recovery is coming in time we flag NeedToCharge and report the latest safe
  // moment to start charging (10 % earlier than the critical time, rounded to 15-min slots).

  private static NeedToChargeResult ComputeNeedToCharge(
    int currentSoc, Dictionary<DateTime, int> naiveFutureSoC,
    InverterState currentMode, SimulationInput input, DateTime now)
  {
    int minSoC = input.EnforcePreferredSoc ? input.PreferredMinSocPercent : input.AbsoluteMinSocPercent;
    // Add 1 % hysteresis while already charging to prevent thrashing at the boundary
    if (currentMode.Mode == InverterModes.force_charge)
      minSoC++;

    // When do we first drop to or below minSoC?
    // If we're already below it (edge case after a settings change or offline period),
    // treat "now" as the critical moment.
    var minReached = currentSoc < minSoC
      ? new KeyValuePair<DateTime, int>(now, currentSoc)
      : naiveFutureSoC.FirstUnderOrDefault(minSoC, start: now);

    // If we never drop below minimum, just record the lowest point for diagnostics
    if (minReached.Key == default)
      minReached = naiveFutureSoC.FirstMinOrDefault(start: now);

    // When does the naive trajectory peak (PV fully charges the battery)?
    var maxReached = naiveFutureSoC.FirstMaxOrDefault(start: now);

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
  //   reset → override → inverter-bug-fix → negative import → negative export →
  //   opportunistic discharge → user force-charge slot → need-to-charge → normal

  private static InverterState ComputeMode(
    InverterState currentMode, NeedToChargeResult need, SimulationInput input,
    DateTime now, int simulatedSoc, Dictionary<DateTime, int> naiveFutureSoC,
    int pvWhSlot, int totalLoadWhSlot, bool isFirstSlot,
    ref int problemCounter, ref int resetCounter)
  {
    // ── Reset signal ──────────────────────────────────────────────────────────────────────
    // After the inverter returns from a manual/remote mode, HouseEnergy sets resetCounter=2.
    // The simulator emits "reset" mode for that many ticks before resuming normal logic.
    // This gives the inverter hardware time to re-initialise battery control.
    if (currentMode.Mode == InverterModes.reset && resetCounter > 0)
    {
      resetCounter--;
      return new InverterState(InverterModes.reset, ForceChargeReasons.None);
    }

    // ── User override ─────────────────────────────────────────────────────────────────────
    // The HA UI select.pv_control_mode_override lets the user lock a specific mode.
    // All automated logic is bypassed when this is active.
    if (input.OverrideMode != InverterModes.automatic)
      return new InverterState(input.OverrideMode, ForceChargeReasons.UserMode);

    // ── Inverter bug fix ──────────────────────────────────────────────────────────────────
    // Some SMA inverters fail to switch to battery in "normal" mode when house load is
    // very low (50–300 W), instead drawing from the grid. We detect this by watching the
    // grid power for ~1 minute (4 × 15-sec ticks), then briefly force-discharge to kick
    // the inverter into battery mode.
    // Only checked for the current live slot; future slots lack a real grid-power reading.
    if (isFirstSlot && currentMode.Mode == InverterModes.normal
        && input.CurrentAverageGridPowerW is > 50 and < 300
        && simulatedSoc > GetPreferredMinSoC(input))
    {
      if (problemCounter <= 4)
      {
        problemCounter++;
        return currentMode; // keep normal, keep counting
      }
      problemCounter = 0;
      return new InverterState(InverterModes.force_discharge, ForceChargeReasons.None);
    }
    problemCounter = 0;

    float importPriceNow = GetPrice(input.ImportPrices, now);
    float exportPriceNow = GetPrice(input.ExportPrices, now);

    // ── Negative import price ─────────────────────────────────────────────────────────────
    // Grid is paying us to consume electricity → fill battery as fast as possible.
    // Use force_charge_grid_only (PV disconnected) unless battery is already ≥ 95 % or PV
    // is negligible; in that case grid_only keeps PV running for the house without wasting
    // charge cycles on a nearly-full battery.
    if (importPriceNow < 0)
    {
      int pvPowerW = pvWhSlot * 4; // convert Wh/slot → W
      var mode = (simulatedSoc <= 95 || pvPowerW < 100)
        ? InverterModes.force_charge_grid_only
        : InverterModes.grid_only;
      return new InverterState(mode, ForceChargeReasons.ImportPriceNegative);
    }

    // ── Negative export price ─────────────────────────────────────────────────────────────
    // Grid charges us for feeding in → stop exporting, cover house from PV/battery only.
    // If negative import prices are also coming later today, disable battery charging now
    // so we have room to absorb the cheap/free grid energy then.
    if (exportPriceNow < 0)
    {
      bool battChargeEnable = !NegativeImportUpcoming(input.ImportPrices, now);
      return new InverterState(InverterModes.house_only, ForceChargeReasons.ExportPriceNegative, battChargeEnable);
    }

    float exportPriceNextHour = GetPrice(input.ExportPrices, now.AddHours(1));

    // ── Opportunistic discharge ───────────────────────────────────────────────────────────
    // When enabled, the system can earn money by selling battery energy at peak prices,
    // provided we are confident the battery will still reach 100 % later (via PV) and
    // stay above the minimum SoC floor.
    if (input.OpportunisticDischarge)
    {
      // Hysteresis: once in feedin_priority we stay a bit longer before switching back
      double maxSocDuration = currentMode.Mode == InverterModes.feedin_priority ? 1.5 : 2.0;

      // How long does the naive trajectory predict 100 % SoC today?
      // If it stays full for more than maxSocDuration hours we can afford to export
      var todayNaiveSoC = naiveFutureSoC.Where(s => s.Key.Date == now.Date).ToDictionary();
      double maxSocDurationCalc = ComputeMaxSocDuration(todayNaiveSoC);

      bool inPVPeriod = IsInPVPeriod(input, now);
      int pvPowerW = pvWhSlot * 4;
      int loadPowerW = totalLoadWhSlot * 4;

      // Case A: during PV peak, battery will hit 100 % anyway → export the overflow now
      // at today's price (even if not a daily maximum) rather than wasting it
      if (!need.NeedToCharge && inPVPeriod && maxSocDurationCalc > maxSocDuration
          && exportPriceNow >= 1 && exportPriceNow >= exportPriceNextHour
          && simulatedSoc > (input.EnforcePreferredSoc ? input.PreferredMinSocPercent : input.AbsoluteMinSocPercent) + 3)
        return new InverterState(InverterModes.feedin_priority, ForceChargeReasons.OpportunisticDischarge);

      // Case B: we are at one of the two highest daily export price peaks
      // → actively discharge the battery to the grid if the SoC forecast allows it
      var sellMaxima = input.ExportPrices.GetLocalMaxima(end: now.Date.AddDays(1))
        .OrderByDescending(t => t.Price).Select(t => t.StartTime).Take(2);

      if (sellMaxima.Any(t => t.Date == now.Date && t.Hour == now.Hour)
          && (exportPriceNow >= input.ForceChargeMaxPrice || NegativeImportUpcoming(input.ImportPrices, now)))
      {
        // Near solar time we can go lower (absolute minimum), otherwise stay at preferred
        int minAllowedSoc = input.PreferredMinSocPercent;
        var firstPVToday = GetFirstRelevantPVTime(input, now.Date, now);
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
    if (input.ForceCharge)
    {
      var cheapestToday = GetCheapestWindowToday(input.ImportPrices, now);
      if (now > cheapestToday.StartTime.AddHours(-1) && now < cheapestToday.StartTime.AddHours(2))
      {
        // Hysteresis: if already charging in this slot, keep going until target is reached
        if (currentMode.ModeReason == ForceChargeReasons.ForcedChargeAtMinimumPrice
            && currentMode.Mode == InverterModes.force_charge
            && simulatedSoc <= Math.Min(98, input.ForceChargeTargetSocPercent + 2))
          return currentMode;

        // How long will charging take from the naive SoC at the cheapest start time?
        int socAtBestTime = naiveFutureSoC.GetValueOrDefault(cheapestToday.StartTime, simulatedSoc);
        int chargeTime = CalculateChargingDurationA(socAtBestTime, 100, input.MaxChargePowerAmps, input.InverterEfficiency, input.BatteryCapacityWh);
        int rankBefore = GetPriceRank(input.ImportPrices, cheapestToday.StartTime.AddHours(-1));
        int rankAfter = GetPriceRank(input.ImportPrices, cheapestToday.StartTime.AddHours(1));
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
    // If the naive SoC forecast predicts we will go below minimum and PV won't rescue us,
    // we must buy electricity. We pick the cheapest available hour before the critical time.
    // Exception: if the NEXT hour is even cheaper, hold off (importing now would just cost more).
    if (need.NeedToCharge)
    {
      // Use the cheapest window between now and the next PV peak.
      //
      // Two problems with the original deadline-constrained GetBestChargeWindow:
      //   1. LatestChargeTime collapses to "now" when the naive SoC first touches the floor
      //      → bestChargeWindow = current (expensive) hour → premature force_charge.
      //   2. A fully unconstrained search (whole 48 h) finds the global price minimum, which
      //      could be tomorrow afternoon when PV is already generating — the simulation then
      //      waits for that distant minimum and skips the real overnight cheap window.
      //
      // Correct bound: search only up to the next PV recovery peak. After that point, solar
      // will charge the battery; no grid charging is needed before then.
      int floorSoc = input.EnforcePreferredSoc ? input.PreferredMinSocPercent : input.AbsoluteMinSocPercent;
      // Search only for prices before PV first exceeds house load.
      // That is the moment solar takes over from the grid — any grid charging after that
      // point is unnecessary. Using the SoC peak was too late (battery already full by then),
      // causing the search to include cheap afternoon prices that arrive after PV has already
      // charged the battery.
      var pvTakeover = naiveFutureSoC.Keys
        .Where(t => t > now
                    && input.PVPredictionWh.GetValueOrDefault(t, 0) > input.LoadPredictionWh.GetValueOrDefault(t, 0))
        .DefaultIfEmpty(now.AddHours(24))
        .First();

      var bestChargeWindow = input.ImportPrices
        .Where(p => p.StartTime >= now.Date.AddHours(now.Hour) && p.StartTime < pvTakeover)
        .OrderBy(p => p.Price)
        .FirstOrDefault();
      if (bestChargeWindow.StartTime == default)
        bestChargeWindow = GetBestChargeWindow(input.ImportPrices, need, now); // safety fallback

      // Simulation slots are always on exact quarter-hour boundaries, so the ±30-second
      // guard used by the live system (to avoid triggering before the price feed updates)
      // would skip the first slot of every window (02:00 > 02:00:30 is false).
      // Use exact boundary comparison instead.
      bool inWindow = now >= bestChargeWindow.StartTime && now < bestChargeWindow.EndTime;
      // Don't start over 96 % (too slow); allow up to 98 % once already charging
      bool socOk = currentMode.Mode == InverterModes.force_charge ? simulatedSoc <= 98 : simulatedSoc <= 96;

      if (inWindow && socOk)
      {
        float importPriceNextHour = GetPrice(input.ImportPrices, now.AddHours(1));
        // No deadline constraint on deferral — the floor is held by grid_only anyway.
        // (In practice this rarely fires since bestChargeWindow IS the cheapest hour.)
        bool canWaitForNextHour = importPriceNow > importPriceNextHour;
        if (canWaitForNextHour)
        {
          int effectiveFloor = input.EnforcePreferredSoc ? input.PreferredMinSocPercent : input.AbsoluteMinSocPercent;
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
  /// Feed-in priority: PV generation is directed to the grid first (to maximise export earnings).
  /// The battery only discharges to cover any house load that PV cannot satisfy.
  /// Battery charging from PV does NOT happen in this mode — the inverter feeds PV to grid.
  /// </summary>
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

  /// <summary>Effective minimum SoC: always at least AbsoluteMin, at most Preferred.</summary>
  private static int GetPreferredMinSoC(SimulationInput input) =>
    Math.Max(input.PreferredMinSocPercent, input.AbsoluteMinSocPercent);

  /// <summary>Import or export price at the given time (0 if no matching entry).</summary>
  private static float GetPrice(List<PriceTableEntry> prices, DateTime time) =>
    prices.FirstOrDefault(p => p.StartTime <= time && p.EndTime > time).Price;

  /// <summary>
  /// True if any import price entry for today's remaining hours has a negative price.
  /// Used to decide whether to pre-emptively disable battery charging so we have room
  /// to absorb free/paid grid energy later.
  /// </summary>
  private static bool NegativeImportUpcoming(List<PriceTableEntry> importPrices, DateTime now) =>
    importPrices.Any(p => p.StartTime.Date == now.Date && p.Price < 0 && p.StartTime > now);

  /// <summary>The cheapest import hour within today (midnight to midnight).</summary>
  private static PriceTableEntry GetCheapestWindowToday(List<PriceTableEntry> prices, DateTime now) =>
    prices.Where(p => p.StartTime >= now.Date && p.EndTime <= now.Date.AddDays(1))
          .OrderBy(p => p.Price).FirstOrDefault();

  /// <summary>
  /// The cheapest upcoming import window we should use for force-charging.
  /// If NeedToCharge is true the search is limited to hours before LatestChargeTime;
  /// otherwise the globally cheapest upcoming hour is returned.
  /// </summary>
  private static PriceTableEntry GetBestChargeWindow(List<PriceTableEntry> prices, NeedToChargeResult need, DateTime now)
  {
    var upcoming = prices.Where(p => p.StartTime >= now.Date.AddHours(now.Hour)).OrderBy(p => p.StartTime).ToList();
    if (need.NeedToCharge)
      return upcoming.Where(p => p.StartTime <= need.LatestChargeTime).OrderBy(p => p.Price).FirstOrDefault();
    return upcoming.OrderBy(p => p.Price).FirstOrDefault();
  }

  /// <summary>
  /// Rank of the given hour in the day's import price list (1 = cheapest).
  /// Used to decide whether to start charging an hour early or an hour late.
  /// </summary>
  private static int GetPriceRank(List<PriceTableEntry> prices, DateTime time)
  {
    var ordered = prices.OrderBy(p => p.Price).ToList();
    var entry = prices.FirstOrDefault(p => p.StartTime.Date == time.Date && p.StartTime.Hour == time.Hour);
    return ordered.IndexOf(entry) + 1;
  }

  /// <summary>
  /// How many hours the battery stays at or above 99 % SoC in the given naive forecast.
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

  /// <summary>
  /// True if the current time is between the first and last slot where PV net generation
  /// exceeds 50 Wh (i.e. PV minus house load is meaningfully positive).
  /// </summary>
  private static bool IsInPVPeriod(SimulationInput input, DateTime now)
  {
    var firstPV = GetFirstRelevantPVTime(input, now.Date, now);
    var lastPV = GetLastRelevantPVTime(input, now.Date);
    return now >= firstPV && now <= lastPV;
  }

  /// <summary>
  /// First slot on the given date (on or after <paramref name="from"/>) where net PV
  /// (PV minus load) exceeds 50 Wh — the point where solar generation meaningfully
  /// exceeds consumption and the battery starts charging from PV.
  /// Falls back to a far-future date if no such slot exists (treats it as "no PV today").
  /// </summary>
  private static DateTime GetFirstRelevantPVTime(SimulationInput input, DateTime date, DateTime from)
  {
    var fallback = date.AddDays(2).AddMinutes(-1);
    return input.PVPredictionWh
      .Where(k => k.Key.Date == date && k.Key >= from
                  && k.Value - input.LoadPredictionWh.GetValueOrDefault(k.Key, 0) > 50)
      .Select(k => k.Key).DefaultIfEmpty(fallback).First();
  }

  /// <summary>Last slot on the given date where net PV exceeds 50 Wh (solar day end).</summary>
  private static DateTime GetLastRelevantPVTime(SimulationInput input, DateTime date)
  {
    var fallback = date.AddDays(2).AddMinutes(-1);
    return input.PVPredictionWh
      .Where(k => k.Key.Date == date
                  && k.Value - input.LoadPredictionWh.GetValueOrDefault(k.Key, 0) > 50)
      .Select(k => k.Key).DefaultIfEmpty(fallback).Last();
  }

  /// <summary>
  /// Estimates how many minutes it takes to charge from <paramref name="startSoC"/> to
  /// <paramref name="endSoC"/> at the given charge current and inverter efficiency.
  /// Formula: requiredEnergy = (endSoC – startSoC) × capacity × efficiency;
  ///          duration = requiredEnergy / (amps × volts) × 60 min/h.
  /// </summary>
  private static int CalculateChargingDurationA(
    int startSoC, int endSoC, int amps, float efficiency, int battCapacity, int volts = ChargeVoltage)
  {
    float reqEnergy = (float)(endSoC - startSoC) / 100 * battCapacity * efficiency;
    return (int)(reqEnergy / (amps * volts) * 60);
  }
}
