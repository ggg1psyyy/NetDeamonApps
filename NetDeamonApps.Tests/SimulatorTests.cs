using NetDeamon.apps;
using NetDeamon.apps.PVControl;
using NetDeamon.apps.PVControl.Simulator;
using Xunit;

namespace NetDeamonApps.Tests;

/// <summary>
/// End-to-end tests for PVSimulator.Simulate().  All inputs are injected so no HA is needed.
/// Focuses on midnight-rollover window correctness and charging decisions.
/// </summary>
public class SimulatorTests : TestBase
{
  // ── input builder ─────────────────────────────────────────────────────────

  /// <summary>
  /// Returns a minimal SimulationInput anchored to <paramref name="startTime"/>.
  /// The load and PV data cover startTime.Date through startTime.Date + 2 days.
  /// Override any field via the optional parameters.
  /// </summary>
  static SimulationInput BuildInput(
    DateTime startTime,
    int startSocPct = 50,
    int batteryCapWh = 10_000,
    int absMinSocPct = 12,
    int prefMinSocPct = 20,
    bool enforcePreferred = false,
    int loadWhPerSlot = 300,     // 1200 W house load
    int pvWhPerSlot = 0,         // no PV by default
    float cheapPrice = 5f,
    float expensivePrice = 35f,
    int cheapHour = 2,           // 02:00 is cheapest
    bool forceCharge = false)
  {
    var date = startTime.Date;
    // Build 3 days of slots so that after rounding startTime forward the data always covers the window
    var horizonDate = date.AddDays(3);

    var load = new Dictionary<DateTime, int>();
    var pv   = new Dictionary<DateTime, int>();
    for (var t = date; t < horizonDate; t = t.AddMinutes(15))
    {
      load[t] = loadWhPerSlot;
      pv[t]   = pvWhPerSlot;
    }

    // Hourly prices for 3 days; cheapHour is cheapest, hour 18 is expensive
    var importPrices = new List<PriceTableEntry>();
    var exportPrices = new List<PriceTableEntry>();
    for (int h = 0; h < 72; h++)
    {
      float price = (h % 24) == cheapHour ? cheapPrice
                  : (h % 24) == 18        ? expensivePrice
                  : 20f;
      var entry = new PriceTableEntry(date.AddHours(h), date.AddHours(h + 1), price);
      importPrices.Add(entry);
      exportPrices.Add(entry);
    }

    return new SimulationInput
    {
      StartTime                 = startTime,
      StartSocPercent           = startSocPct,
      BatteryCapacityWh         = batteryCapWh,
      AbsoluteMinSocPercent     = absMinSocPct,
      PreferredMinSocPercent    = prefMinSocPct,
      EnforcePreferredSoc       = enforcePreferred,
      MaxChargePowerAmps        = 10,
      InverterEfficiency        = 0.9f,
      ImportPrices              = importPrices,
      ExportPrices              = exportPrices,
      LoadPredictionWh          = load,
      PVPredictionWh            = pv,
      EnableCheapForceCharge               = forceCharge,
      OpportunisticDischarge    = false,
      ForceChargeMaxPrice       = 0.25f,
      ForceChargeTargetSocPercent = 100,
      CurrentMode               = new InverterState(InverterModes.normal),
    };
  }

  // ── window / slot-count tests ─────────────────────────────────────────────

  [Fact]
  public void StartDuringDay_SlotsCoverUntilEndOfTomorrow()
  {
    // endSlot = startSlot.Date.AddDays(2) = 2025-06-17 00:00 (exclusive)
    // Last slot = 2025-06-16 23:45, count = 39h * 4 = 156
    var start = new DateTime(2025, 6, 15, 9, 0, 0);
    var slots = EnergySimulator.Simulate(BuildInput(start));

    Assert.Equal(start, slots.First().Time);
    Assert.Equal(new DateTime(2025, 6, 16, 23, 45, 0), slots.Last().Time);

    int expectedSlots = (int)((new DateTime(2025, 6, 17) - start).TotalMinutes / 15); // 156
    Assert.Equal(expectedSlots, slots.Count);
  }

  [Fact]
  public void StartAtMidnight_SlotsCoverExactlyTwoDays()
  {
    var start = new DateTime(2025, 6, 16, 0, 0, 0);
    var slots = EnergySimulator.Simulate(BuildInput(start));

    Assert.Equal(start, slots.First().Time);
    Assert.Equal(new DateTime(2025, 6, 17, 23, 45, 0), slots.Last().Time);
    Assert.Equal(192, slots.Count); // 2 × 96 slots
  }

  [Fact]
  public void StartJustBeforeMidnight_SlotWindowIsFromCurrentDay()
  {
    // RoundToNearestQuarterHour rounds DOWN (truncates), so 23:45 stays 23:45.
    // endSlot = startSlot.Date.AddDays(2) = 2025-06-17 00:00 (exclusive).
    // Count = (2025-06-17 - 2025-06-15 23:45) / 15 min = 101 slots.
    var start = new DateTime(2025, 6, 15, 23, 45, 0);
    var slots = EnergySimulator.Simulate(BuildInput(start));

    Assert.Equal(start, slots.First().Time);
    Assert.Equal(new DateTime(2025, 6, 16, 23, 45, 0), slots.Last().Time);
    Assert.Equal(97, slots.Count);
  }

  [Fact]
  public void AllSlotsAre15MinutesApart()
  {
    var start = new DateTime(2025, 3, 18, 9, 0, 0);
    var slots = EnergySimulator.Simulate(BuildInput(start));

    for (int i = 1; i < slots.Count; i++)
      Assert.Equal(TimeSpan.FromMinutes(15), slots[i].Time - slots[i - 1].Time);
  }

  // ── charging decision tests ───────────────────────────────────────────────

  [Fact]
  public void HighSoC_NoForceCharge()
  {
    // Battery at 90% with no load and no PV — well above floor, no need to charge
    var start = new DateTime(2025, 6, 15, 9, 0, 0);
    var slots = EnergySimulator.Simulate(BuildInput(start, startSocPct: 90, loadWhPerSlot: 0));

    Assert.DoesNotContain(slots, s => s.State.Mode == InverterModes.force_charge);
  }

  [Fact]
  public void LowSoC_NeedToCharge_ForceChargeScheduledAtCheapestHour()
  {
    // Battery at 15% (just above 12% floor), heavy load, no PV → needs grid charge.
    // Cheap hour is 02:00 — verify force_charge appears at that hour.
    var start = new DateTime(2025, 6, 15, 20, 0, 0); // 20:00, before the 02:00 cheap window
    var slots = EnergySimulator.Simulate(BuildInput(
      start,
      startSocPct: 15,
      loadWhPerSlot: 300,
      pvWhPerSlot: 0,
      cheapHour: 2));

    var chargeSlots = slots.Where(s => s.State.Mode == InverterModes.force_charge).ToList();
    Assert.NotEmpty(chargeSlots);

    // All force_charge slots should fall within the 02:00–03:00 window
    foreach (var s in chargeSlots)
      Assert.Equal(2, s.Time.Hour);
  }

  [Fact]
  public void FloorHoldback_BeforeCheapWindow_UsesGridOnly()
  {
    // SoC at exactly the floor (20%), no PV, cheap hour at 02:00.
    // Between now (20:00) and 02:00 the simulator should hold with grid_only, not force_charge.
    var start = new DateTime(2025, 6, 15, 20, 0, 0);
    var slots = EnergySimulator.Simulate(BuildInput(
      start,
      startSocPct: 20,
      loadWhPerSlot: 300,
      pvWhPerSlot: 0,
      prefMinSocPct: 20,
      enforcePreferred: true,
      cheapHour: 2));

    // Slots between 20:00 and 02:00 that are at-or-below the floor should be grid_only, not force_charge
    var holdbackSlots = slots
      .Where(s => s.Time.Hour >= 20 || s.Time.Hour < 2)
      .Where(s => s.SoC <= 20)
      .ToList();

    Assert.True(holdbackSlots.Count > 0);
    Assert.DoesNotContain(holdbackSlots, s => s.State.Mode == InverterModes.force_charge);
  }

  [Fact]
  public void PVSurplus_ChargesBattery_NormalMode()
  {
    // Plenty of PV (1000 Wh/slot = 4 kW), low load → battery should charge in normal mode
    var start = new DateTime(2025, 6, 15, 10, 0, 0); // daytime
    var slots = EnergySimulator.Simulate(BuildInput(
      start,
      startSocPct: 30,
      loadWhPerSlot: 100,
      pvWhPerSlot: 1000));

    var daySlots = slots.Where(s => s.Time.Date == start.Date && s.Time.Hour >= 10).ToList();

    // Battery should be charging (BatteryChargeWh > 0) during PV surplus
    Assert.Contains(daySlots, s => s.BatteryChargeWh > 0);
    // No force_charge needed when PV is covering load and charging naturally
    Assert.DoesNotContain(daySlots, s => s.State.Mode == InverterModes.force_charge);
  }

  [Fact]
  public void SoCNeverDropsBelowAbsoluteMin()
  {
    // Even without any charging the simulator must clamp battery discharge at AbsoluteMinSocPercent
    var start = new DateTime(2025, 6, 15, 9, 0, 0);
    var slots = EnergySimulator.Simulate(BuildInput(
      start,
      startSocPct: 50,
      absMinSocPct: 12,
      loadWhPerSlot: 800,  // heavy load to drain battery fast
      pvWhPerSlot: 0));

    Assert.All(slots, s => Assert.True(s.SoC >= 12,
      $"SoC {s.SoC}% at {s.Time} dropped below AbsoluteMinSoc 12%"));
  }

  /// <summary>
  /// Validates the precondition behind the Optimal window-finder fix:
  /// the BASE simulation (no EV) reaches 99% when PV > load, but the same input
  /// WITH a large EV ExtraLoad consumes the surplus and never reaches 99%.
  /// FindLoadWindow uses baseResult for SimWillReachMaxSocToday (not simResult)
  /// so Optimal mode correctly fires when there is genuine excess PV.
  /// </summary>
  [Fact]
  public void OptimalFixPrecondition_BaseSocReaches99_EvLoadedSimDoesNot()
  {
    // 10 kWh battery at 20%, strong PV surplus → base sim charges to 100%
    var start = new DateTime(2025, 6, 15, 9, 0, 0);
    var baseInput = BuildInput(
      start,
      startSocPct: 20,
      batteryCapWh: 10_000,
      loadWhPerSlot: 100,   // 400 W house load
      pvWhPerSlot: 800);    // 3.2 kW PV → 700 Wh net surplus per slot

    var baseSlots = EnergySimulator.Simulate(baseInput);

    // Base simulation must reach ≥ 99 % today
    Assert.Contains(baseSlots.Slots, s => s.Time.Date == start.Date && s.SoC >= 99);

    // Now add a large EV load that absorbs the entire PV surplus from session start to sunset.
    // Sunset proxy: last slot today (23:45).
    var sessionEnd = start.Date.AddHours(23).AddMinutes(45);
    var evLoad = new ExtraLoad
    {
      Name = "EV",
      Priority = 10,
      StartTime = start,
      EndTime   = sessionEnd,
      PowerW    = 3_200   // fully consumes the PV surplus
    };

    var evInput  = new SimulationInput
    {
      StartTime                   = baseInput.StartTime,
      StartSocPercent             = baseInput.StartSocPercent,
      BatteryCapacityWh           = baseInput.BatteryCapacityWh,
      AbsoluteMinSocPercent       = baseInput.AbsoluteMinSocPercent,
      PreferredMinSocPercent      = baseInput.PreferredMinSocPercent,
      EnforcePreferredSoc         = baseInput.EnforcePreferredSoc,
      MaxChargePowerAmps          = baseInput.MaxChargePowerAmps,
      InverterEfficiency          = baseInput.InverterEfficiency,
      ImportPrices                = baseInput.ImportPrices,
      ExportPrices                = baseInput.ExportPrices,
      LoadPredictionWh            = baseInput.LoadPredictionWh,
      PVPredictionWh              = baseInput.PVPredictionWh,
      ExtraLoads                  = [evLoad],
      EnableCheapForceCharge                 = baseInput.EnableCheapForceCharge,
      OpportunisticDischarge      = baseInput.OpportunisticDischarge,
      ForceChargeMaxPrice         = baseInput.ForceChargeMaxPrice,
      ForceChargeTargetSocPercent = baseInput.ForceChargeTargetSocPercent,
      CurrentMode                 = baseInput.CurrentMode,
    };
    var evSlots = EnergySimulator.Simulate(evInput);

    // EV-loaded simulation must NOT reach 99 % today (EV consumes the surplus)
    Assert.DoesNotContain(evSlots.Slots, s => s.Time.Date == start.Date && s.SoC >= 99);
  }

  // ── charge/discharge rate limit tests ───────────────────────────────────────

  [Fact]
  public void PVDeficitExceedsChargeRate_DischargeIsCappedAndRemainderComesFromGrid()
  {
    // MaxChargePowerAmps=10 -> maxChargeWh = 10 * 230V / 4 = 575 Wh/slot (EnergySimulator's
    // hardware rate limit). A 2000 Wh/slot load with no PV creates a 2000 Wh deficit, far
    // beyond what the battery/inverter can physically discharge in one 15-min slot, so the
    // remainder must show up as grid import rather than being modeled as instant battery drain.
    var start = new DateTime(2025, 6, 15, 9, 0, 0);
    var slots = EnergySimulator.Simulate(BuildInput(start, startSocPct: 70, loadWhPerSlot: 2000, pvWhPerSlot: 0));
    var firstSlot = slots[0];

    Assert.True(firstSlot.BatteryDischargeWh <= 575);
    Assert.Equal(2000 - firstSlot.BatteryDischargeWh, firstSlot.GridImportWh);
    Assert.True(firstSlot.GridImportWh > 0);
  }

  [Fact]
  public void HouseOnly_PVSurplusExceedsChargeRate_ChargeIsCappedAndSurplusCurtailed()
  {
    // Negative export price -> house_only (no grid export allowed). PV surplus of 2700 Wh/slot
    // (3000 pv - 300 load) far exceeds the 575 Wh/slot rate limit, so charging must be capped
    // and the excess simply curtailed (house_only never exports), not smuggled in over the limit.
    var start = new DateTime(2025, 6, 15, 12, 0, 0);
    var date = start.Date;
    var horizonDate = date.AddDays(3);

    var load = new Dictionary<DateTime, int>();
    var pv = new Dictionary<DateTime, int>();
    for (var t = date; t < horizonDate; t = t.AddMinutes(15))
    {
      load[t] = 300;
      pv[t] = 3000;
    }

    var importPrices = new List<PriceTableEntry>();
    var exportPrices = new List<PriceTableEntry>();
    for (int h = 0; h < 72; h++)
    {
      importPrices.Add(new PriceTableEntry(date.AddHours(h), date.AddHours(h + 1), 20f)); // never negative
      exportPrices.Add(new PriceTableEntry(date.AddHours(h), date.AddHours(h + 1), -5f)); // always negative
    }

    var input = new SimulationInput
    {
      StartTime                  = start,
      StartSocPercent            = 50,
      BatteryCapacityWh          = 10_000,
      AbsoluteMinSocPercent      = 12,
      PreferredMinSocPercent     = 20,
      EnforcePreferredSoc        = false,
      MaxChargePowerAmps         = 10,
      InverterEfficiency         = 0.9f,
      ImportPrices               = importPrices,
      ExportPrices               = exportPrices,
      LoadPredictionWh           = load,
      PVPredictionWh             = pv,
      EnableCheapForceCharge     = false,
      OpportunisticDischarge     = false,
      ForceChargeMaxPrice        = 0.25f,
      ForceChargeTargetSocPercent = 100,
      CurrentMode                = new InverterState(InverterModes.normal),
    };

    var slots = EnergySimulator.Simulate(input);
    var firstSlot = slots[0];

    Assert.Equal(InverterModes.house_only, firstSlot.State.Mode);
    Assert.Equal(575, firstSlot.BatteryChargeWh);
    Assert.Equal(0, firstSlot.GridExportWh);
  }

  // ── battery charge enable tests ───────────────────────────────────────────

  [Fact]
  public void NegativeImportPrice_WithCheaperWindowLaterToday_CurtailsPVSurplusInsteadOfCharging()
  {
    // 09:00 import price is negative but a cheaper (more negative) window is still coming
    // at 14:00 today — ComputeMode defers grid charging to that window and disables battery
    // charging in the meantime so PV surplus doesn't fill the battery first (HouseEnergy.cs
    // ComputeMode / EnergySimulator.HouseOnly). PV surplus during 09:00 must be curtailed,
    // not captured, and the slot must show zero flow in every direction.
    var start = new DateTime(2025, 6, 15, 9, 0, 0);
    var date = start.Date;
    var horizonDate = date.AddDays(3);

    var load = new Dictionary<DateTime, int>();
    var pv = new Dictionary<DateTime, int>();
    for (var t = date; t < horizonDate; t = t.AddMinutes(15))
    {
      load[t] = 300;
      pv[t] = 1000; // 700 Wh/slot surplus over load
    }

    var importPrices = new List<PriceTableEntry>();
    var exportPrices = new List<PriceTableEntry>();
    for (int h = 0; h < 72; h++)
    {
      float price = (h % 24) == 9 ? -5f
                  : (h % 24) == 14 ? -10f
                  : 20f;
      importPrices.Add(new PriceTableEntry(date.AddHours(h), date.AddHours(h + 1), price));
      exportPrices.Add(new PriceTableEntry(date.AddHours(h), date.AddHours(h + 1), 10f));
    }

    var input = new SimulationInput
    {
      StartTime                  = start,
      StartSocPercent            = 50,
      BatteryCapacityWh          = 10_000,
      AbsoluteMinSocPercent      = 12,
      PreferredMinSocPercent     = 20,
      EnforcePreferredSoc        = false,
      MaxChargePowerAmps         = 10,
      InverterEfficiency         = 0.9f,
      ImportPrices               = importPrices,
      ExportPrices               = exportPrices,
      LoadPredictionWh           = load,
      PVPredictionWh             = pv,
      EnableCheapForceCharge     = false,
      OpportunisticDischarge     = false,
      ForceChargeMaxPrice        = 0.25f,
      ForceChargeTargetSocPercent = 100,
      CurrentMode                = new InverterState(InverterModes.normal),
    };

    var slots = EnergySimulator.Simulate(input);
    var firstSlot = slots[0];

    Assert.Equal(InverterModes.house_only, firstSlot.State.Mode);
    Assert.False(firstSlot.State.BatteryChargeEnable);
    Assert.Equal(0, firstSlot.BatteryChargeWh);
    Assert.Equal(0, firstSlot.BatteryDischargeWh);
    Assert.Equal(0, firstSlot.GridImportWh);
    Assert.Equal(0, firstSlot.GridExportWh);
  }

  // ── overnight window fix tests ─────────────────────────────────────────────

  /// <summary>
  /// Builds an input with PV only during daytime hours, suitable for testing overnight window.
  /// </summary>
  static SimulationInput BuildInputDayPV(
    DateTime startTime,
    int startSocPct,
    int pvWhPerSlot,
    int pvStartHour    = 8,
    int pvEndHour      = 18,
    int loadWhPerSlot  = 300,
    int absMinSocPct   = 12,
    int prefMinSocPct  = 20,
    int batteryCapWh   = 10_000)
  {
    var date        = startTime.Date;
    var horizonDate = date.AddDays(3);
    var load = new Dictionary<DateTime, int>();
    var pv   = new Dictionary<DateTime, int>();
    for (var t = date; t < horizonDate; t = t.AddMinutes(15))
    {
      load[t] = loadWhPerSlot;
      pv[t]   = (t.Hour >= pvStartHour && t.Hour < pvEndHour) ? pvWhPerSlot : 0;
    }
    var prices = new List<PriceTableEntry>();
    for (int h = 0; h < 72; h++)
      prices.Add(new PriceTableEntry(date.AddHours(h), date.AddHours(h + 1), 20f));

    return new SimulationInput
    {
      StartTime                   = startTime,
      StartSocPercent             = startSocPct,
      BatteryCapacityWh           = batteryCapWh,
      AbsoluteMinSocPercent       = absMinSocPct,
      PreferredMinSocPercent      = prefMinSocPct,
      EnforcePreferredSoc         = false,
      MaxChargePowerAmps          = 10,
      InverterEfficiency          = 0.9f,
      ImportPrices                = prices,
      ExportPrices                = prices,
      LoadPredictionWh            = load,
      PVPredictionWh              = pv,
      EnableCheapForceCharge      = false,
      OpportunisticDischarge      = false,
      ForceChargeMaxPrice         = 0.25f,
      ForceChargeTargetSocPercent = 100,
      CurrentMode                 = new InverterState(InverterModes.normal),
    };
  }

  [Fact]
  public void OvernightMin_BeforePV_UsesPreDawnWindow()
  {
    // Regression: before the fix, OvernightMinSocReached used LastPVToday→FirstPVTomorrow.
    // At 03:00 AM that window is ~15 h in the future — battery will have recharged by then via PV.
    // So IsOvernightMinSocOk always returned true even while the current overnight drained the battery.
    //
    // After the fix: overnight window = now → FirstRelevantPVEnergyToday (this morning's sunrise).
    // Battery at 25% drains to AbsMin (12%) well before sunrise at 08:00.
    // OvernightMinSocReached must reflect that drain, not the post-PV-recharge level.
    var start  = new DateTime(2025, 6, 15, 3, 0, 0); // 03:00 — BeforePV
    var result = EnergySimulator.Simulate(BuildInputDayPV(
      start,
      startSocPct:   25,
      pvWhPerSlot:   2000,  // strong daytime PV — battery fully recharges during day
      loadWhPerSlot: 300)); // 1200 W load; (25%−12%) × 10000 Wh / 1200 W ≈ 65 min → hits AbsMin before 08:00

    Assert.Equal(PVPeriods.BeforePV, result.CurrentPVPeriod);

    // Pre-dawn drain must be captured: OvernightMinSocReached must be below PreferredMinSoC (20 %).
    // The exact floor depends on the simulator's discharge accounting; what matters is that the
    // pre-dawn drain brings SoC well below the preferred threshold before 08:00 sunrise.
    Assert.True(result.OvernightMinSocReached < 20,
      $"Expected OvernightMinSocReached < PreferredMinSoC (20 %), got {result.OvernightMinSocReached} %");

    // Priority mode (alwaysEnforcePreferred=true) must reject a session that drains below PreferredMinSoC (20 %).
    Assert.False(result.IsOvernightMinSocOk(alwaysEnforcePreferred: true),
      "IsOvernightMinSocOk should be false: pre-dawn drain drops battery below PreferredMinSoC (20 %)");
  }

  [Fact]
  public void OvernightMin_BeforePV_SufficientBattery_IsOk()
  {
    // Battery at 80 % at 03:00 AM, modest load = 800 W.
    // Pre-dawn drain over 5 h: 5 × 3600 s × 800 W / 3600 = 4000 Wh → battery drops to 40 %.
    // 40 % is well above PreferredMinSoC (20 %), so IsOvernightMinSocOk should be true.
    var start  = new DateTime(2025, 6, 15, 3, 0, 0);
    var result = EnergySimulator.Simulate(BuildInputDayPV(
      start,
      startSocPct:   80,
      pvWhPerSlot:   2000,
      loadWhPerSlot: 200)); // 800 W; 5 h × 200 Wh/slot × 20 slots = 4000 Wh → 40 % remaining

    Assert.Equal(PVPeriods.BeforePV, result.CurrentPVPeriod);
    Assert.True(result.OvernightMinSocReached >= 20,
      $"Expected OvernightMinSocReached ≥ 20 % (battery survives pre-dawn), got {result.OvernightMinSocReached} %");
    Assert.True(result.IsOvernightMinSocOk(alwaysEnforcePreferred: true));
  }

  [Fact]
  public void OvernightMin_AfterPV_UsesTonightWindow_Regression()
  {
    // Regression: after PV (AfterPV), overnight window must still be LastPVToday→FirstPVTomorrow.
    // Battery at 80 % at 20:00 with modest load — should survive the night above PreferredMinSoC.
    var start  = new DateTime(2025, 6, 15, 20, 0, 0); // 20:00 — AfterPV
    var result = EnergySimulator.Simulate(BuildInputDayPV(
      start,
      startSocPct:   80,
      pvWhPerSlot:   2000,
      loadWhPerSlot: 100));

    Assert.Equal(PVPeriods.AfterPV, result.CurrentPVPeriod);
    // 80 % → 400 W drain; (80%−20%) × 10000 = 6000 Wh / 400 W = 15 h → survives past 11:00 AM
    Assert.True(result.IsOvernightMinSocOk(alwaysEnforcePreferred: true),
      "Evening session with high battery should have overnight min above preferred (20 %)");
  }
}
