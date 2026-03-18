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
      ForceCharge               = forceCharge,
      OpportunisticDischarge    = false,
      ForceChargeMaxPriceCt     = 25,
      ForceChargeTargetSocPercent = 100,
      OverrideMode              = InverterModes.automatic,
      CurrentMode               = new InverterState(InverterModes.normal),
      CurrentResetCounter       = 0,
      CurrentAverageGridPowerW  = 0,
    };
  }

  // ── window / slot-count tests ─────────────────────────────────────────────

  [Fact]
  public void StartDuringDay_SlotsCoverUntilEndOfTomorrow()
  {
    // endSlot = startSlot.Date.AddDays(2) = 2025-06-17 00:00 (exclusive)
    // Last slot = 2025-06-16 23:45, count = 39h * 4 = 156
    var start = new DateTime(2025, 6, 15, 9, 0, 0);
    var slots = PVSimulator.Simulate(BuildInput(start));

    Assert.Equal(start, slots.First().Time);
    Assert.Equal(new DateTime(2025, 6, 16, 23, 45, 0), slots.Last().Time);

    int expectedSlots = (int)((new DateTime(2025, 6, 17) - start).TotalMinutes / 15); // 156
    Assert.Equal(expectedSlots, slots.Count);
  }

  [Fact]
  public void StartAtMidnight_SlotsCoverExactlyTwoDays()
  {
    var start = new DateTime(2025, 6, 16, 0, 0, 0);
    var slots = PVSimulator.Simulate(BuildInput(start));

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
    var slots = PVSimulator.Simulate(BuildInput(start));

    Assert.Equal(start, slots.First().Time);
    Assert.Equal(new DateTime(2025, 6, 16, 23, 45, 0), slots.Last().Time);
    Assert.Equal(97, slots.Count);
  }

  [Fact]
  public void AllSlotsAre15MinutesApart()
  {
    var start = new DateTime(2025, 3, 18, 9, 0, 0);
    var slots = PVSimulator.Simulate(BuildInput(start));

    for (int i = 1; i < slots.Count; i++)
      Assert.Equal(TimeSpan.FromMinutes(15), slots[i].Time - slots[i - 1].Time);
  }

  // ── charging decision tests ───────────────────────────────────────────────

  [Fact]
  public void HighSoC_NoForceCharge()
  {
    // Battery at 90% with no load and no PV — well above floor, no need to charge
    var start = new DateTime(2025, 6, 15, 9, 0, 0);
    var slots = PVSimulator.Simulate(BuildInput(start, startSocPct: 90, loadWhPerSlot: 0));

    Assert.DoesNotContain(slots, s => s.State.Mode == InverterModes.force_charge);
  }

  [Fact]
  public void LowSoC_NeedToCharge_ForceChargeScheduledAtCheapestHour()
  {
    // Battery at 15% (just above 12% floor), heavy load, no PV → needs grid charge.
    // Cheap hour is 02:00 — verify force_charge appears at that hour.
    var start = new DateTime(2025, 6, 15, 20, 0, 0); // 20:00, before the 02:00 cheap window
    var slots = PVSimulator.Simulate(BuildInput(
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
    var slots = PVSimulator.Simulate(BuildInput(
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
    var slots = PVSimulator.Simulate(BuildInput(
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
    var slots = PVSimulator.Simulate(BuildInput(
      start,
      startSocPct: 50,
      absMinSocPct: 12,
      loadWhPerSlot: 800,  // heavy load to drain battery fast
      pvWhPerSlot: 0));

    Assert.All(slots, s => Assert.True(s.SoC >= 12,
      $"SoC {s.SoC}% at {s.Time} dropped below AbsoluteMinSoc 12%"));
  }
}
