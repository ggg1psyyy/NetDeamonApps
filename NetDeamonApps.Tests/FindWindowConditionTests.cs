using NetDeamon.apps;
using NetDeamon.apps.PVControl;
using NetDeamon.apps.PVControl.Simulator;
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace NetDeamonApps.Tests;

/// <summary>
/// Tests verifying the SIMULATION CONDITIONS that drive FindLoadWindow's binary search
/// for each LoadSchedulingMode.  FindLoadWindow itself is private, so these tests operate
/// directly on EnergySimulator.Simulate() and check the predicates that each binary-search
/// step relies on. This also means FindLoadWindow's own early-exit branches (Off, target-reached,
/// AvailabilityEntity not present, …) aren't covered here — they read live Entity/PVCC_Config
/// state and FindLoadWindow has no public/reflective test seam, matching the HA-integration
/// testing exception documented in CLAUDE.md.
///
/// Live data source: 2026-03-22 morning (~09:30), confirmed via Home Assistant sensors:
///   - Battery: 11 520 Wh capacity, SoC 59 % (solax_battery_capacity = 59)
///   - PV remaining today: ~17 000 Wh (pv_control_estimated_remaining_charge_today = 16 987)
///   - House load remaining today: ~6 400 Wh (pv_control_estimated_remaining_discharge_today = 6 394)
///   - EV charger: BYD ATTO 2, current 60 %, target 100 %, 0.6 kWh/%, 1 800 W → 24 kWh needed
///   - Best import price today: 0.110 €/kWh (at 09:00), MaxPriceForForceCharge: 15 ct/kWh
///
/// Test profile (rounded for stability):
///   PV: 600 Wh/slot (≈ 2 400 W) during 06:00–17:59; 0 at night.
///   House load: 112 Wh/slot (≈ 450 W) flat.
///   Net during EV+PV hours: 600 − 112 − 450 = +38 Wh/slot (battery gains slowly).
///   Net during PV-only: 600 − 112 = +488 Wh/slot.
/// </summary>
public class FindWindowConditionTests : TestBase
{
  // ── Shared constants reflecting live conditions ─────────────────────────────────────────

  const int BatCap    = 11_520; // Wh
  const int StartSoc  = 59;     // %  → 6 797 Wh stored
  const int AbsMin    = 10;     // %  → 1 152 Wh floor
  const int ChargeA   = 30;     // A  → 1 725 Wh/slot max
  const int PvWh      = 600;    // Wh/slot daytime
  const int LoadWh    = 112;    // Wh/slot
  const int EvW       = 1_800;  // W
  const int EvWh      = 450;    // Wh/slot (1 800 W × 15 min / 60)

  /// <summary>
  /// Start of the spring-day scenario: 09:30 on 2026-03-22.
  /// The overnight window used by overnightOk checks: 17:45 today → 06:00 tomorrow.
  /// </summary>
  static readonly DateTime SpringStart       = new(2026, 3, 22, 9, 30, 0);
  static readonly DateTime LastPVToday       = new(2026, 3, 22, 17, 45, 0);   // last slot with PV
  static readonly DateTime FirstPVTomorrow   = new(2026, 3, 23, 6,  0, 0);    // first slot tomorrow

  // ── Input builders ──────────────────────────────────────────────────────────────────────

  /// <summary>
  /// Build a 3-day per-slot PV dictionary with daytime (06:00–17:59) PV only.
  /// </summary>
  static Dictionary<DateTime, int> DaytimePV(DateTime baseDate, int whPerSlot, int days = 3)
  {
    var d = new Dictionary<DateTime, int>();
    for (var t = baseDate; t < baseDate.AddDays(days); t = t.AddMinutes(15))
      d[t] = (t.Hour >= 6 && t.Hour < 18) ? whPerSlot : 0;
    return d;
  }

  /// <summary>Build a flat load dictionary for <paramref name="days"/> days.</summary>
  static Dictionary<DateTime, int> FlatLoad(DateTime baseDate, int whPerSlot, int days = 3)
  {
    var d = new Dictionary<DateTime, int>();
    for (var t = baseDate; t < baseDate.AddDays(days); t = t.AddMinutes(15))
      d[t] = whPerSlot;
    return d;
  }

  /// <summary>
  /// Hourly prices: <paramref name="cheapPrice"/> at <paramref name="cheapHour"/>,
  /// <paramref name="expensivePrice"/> all other hours.
  /// </summary>
  static List<PriceTableEntry> HourlyPrices(
      DateTime baseDate, float cheapPrice, float expensivePrice, int cheapHour, int days = 3)
  {
    var list = new List<PriceTableEntry>();
    for (int h = 0; h < days * 24; h++)
    {
      var t = baseDate.AddHours(h);
      float p = (h % 24) == cheapHour ? cheapPrice : expensivePrice;
      list.Add(new PriceTableEntry(t, t.AddHours(1), p));
    }
    return list;
  }

  /// <summary>
  /// Hourly prices: <paramref name="cheapPrice"/> for hours in [cheapFrom, cheapTo),
  /// <paramref name="expensivePrice"/> otherwise.
  /// </summary>
  static List<PriceTableEntry> BandedPrices(
      DateTime baseDate, float cheapPrice, float expensivePrice,
      int cheapFrom, int cheapTo, int days = 3)
  {
    var list = new List<PriceTableEntry>();
    for (int h = 0; h < days * 24; h++)
    {
      var t = baseDate.AddHours(h);
      int hr = h % 24;
      float p = (hr >= cheapFrom && hr < cheapTo) ? cheapPrice : expensivePrice;
      list.Add(new PriceTableEntry(t, t.AddHours(1), p));
    }
    return list;
  }

  SimulationInput SpringBaseInput(DateTime start)
  {
    var date = start.Date;
    var prices = HourlyPrices(date, 5f, 35f, cheapHour: 2);
    return new SimulationInput
    {
      StartTime                   = start,
      StartSocPercent             = StartSoc,
      BatteryCapacityWh           = BatCap,
      AbsoluteMinSocPercent       = AbsMin,
      PreferredMinSocPercent      = 30,
      EnforcePreferredSoc         = false,
      MaxChargePowerAmps          = ChargeA,
      InverterEfficiency          = 0.9f,
      ImportPrices                = prices,
      ExportPrices                = prices,
      LoadPredictionWh            = FlatLoad(date, LoadWh),
      PVPredictionWh              = DaytimePV(date, PvWh),
      ExtraLoads                  = [],
      EnableCheapForceCharge                 = false,
      OpportunisticDischarge      = false,
      ForceChargeMaxPrice         = 0.25f,
      ForceChargeTargetSocPercent = 95,
      CurrentMode                 = new InverterState(InverterModes.normal),

    };
  }

  static SimulationInput WithEV(SimulationInput src, DateTime start, DateTime end) => new()
  {
    StartTime                   = src.StartTime,
    StartSocPercent             = src.StartSocPercent,
    BatteryCapacityWh           = src.BatteryCapacityWh,
    AbsoluteMinSocPercent       = src.AbsoluteMinSocPercent,
    PreferredMinSocPercent      = src.PreferredMinSocPercent,
    EnforcePreferredSoc         = src.EnforcePreferredSoc,
    MaxChargePowerAmps          = src.MaxChargePowerAmps,
    InverterEfficiency          = src.InverterEfficiency,
    ImportPrices                = src.ImportPrices,
    ExportPrices                = src.ExportPrices,
    LoadPredictionWh            = src.LoadPredictionWh,
    PVPredictionWh              = src.PVPredictionWh,
    ExtraLoads                  = [new ExtraLoad { Name = "EV", Priority = 10, StartTime = start, EndTime = end, PowerW = EvW }],
    EnableCheapForceCharge                 = src.EnableCheapForceCharge,
    OpportunisticDischarge      = src.OpportunisticDischarge,
    ForceChargeMaxPrice         = src.ForceChargeMaxPrice,
    ForceChargeTargetSocPercent = src.ForceChargeTargetSocPercent,
    CurrentMode                 = src.CurrentMode,
  };

  // ── Helpers ──────────────────────────────────────────────────────────────────────────────

  /// <summary>
  /// True if any slot on <paramref name="today"/> shows SoC ≥ 99 %
  /// (mirrors SimWillReachMaxSocToday).
  /// </summary>
  static bool Reaches99Today(SimulationResult sim, DateTime today)
    => sim.Slots.Any(s => s.Time.Date == today.Date && s.SoC >= 99);

  /// <summary>
  /// True if all slots in the overnight window [lastPV, firstPVTomorrow] have SoC ≥ absMinSoc
  /// (mirrors SimOvernightMinSocOk with EnforcePreferredSoc=false).
  /// </summary>
  static bool OvernightOk(SimulationResult sim, DateTime lastPV, DateTime firstPV, int absMin)
  {
    var night = sim.Slots.Where(s => s.Time >= lastPV && s.Time <= firstPV).ToList();
    return night.Count == 0 || night.Min(s => s.SoC) >= absMin;
  }

  /// <summary>
  /// True if the test sim introduced NO new force_charge slots at ANY time
  /// (mirrors !HasNewGrid — checks all slots, not just the overnight window).
  /// </summary>
  static bool NoNewGrid(SimulationResult sim, HashSet<DateTime> baseFCS)
    => !sim.Slots.Any(s =>
      s.State.Mode == InverterModes.force_charge
      && !baseFCS.Contains(s.Time));

  /// <summary>
  /// True if every NEW force_charge slot (at any time) has an import price ≤ maxPrice
  /// (mirrors IsGridCheap — checks all slots, not just the overnight window).
  /// </summary>
  static bool GridCheap(SimulationResult sim, HashSet<DateTime> baseFCS,
      List<PriceTableEntry> prices, float maxPrice)
    => !sim.Slots.Any(s =>
      s.State.Mode == InverterModes.force_charge
      && !baseFCS.Contains(s.Time)
      && !prices.Any(p => p.StartTime <= s.Time && p.EndTime > s.Time && p.Price <= maxPrice));

  // ── Step 1 (Optimal) tests ───────────────────────────────────────────────────────────────

  /// <summary>
  /// Spring/summer day live-data scenario (2026-03-22).
  ///
  /// Optimal requires that the house battery reaches ~100 % even WITH the EV running.
  /// A session of 26 slots (390 min, ending 16:00) leaves 7 post-EV PV slots that push the
  /// battery to 100 % → Optimal daytime condition met.
  ///
  /// Verifies the LOWER boundary: at T=26, SimWillReachMaxSocToday = true.
  /// </summary>
  [Fact]
  public void SpringDay_Optimal_ShortSession_Reaches99()
  {
    var start    = SpringStart;
    var evEnd    = start.AddMinutes(26 * 15); // T=26 → 16:00
    var baseSim  = EnergySimulator.Simulate(SpringBaseInput(start));
    var evSim    = EnergySimulator.Simulate(WithEV(SpringBaseInput(start), start, evEnd));

    // With EV for 390 min, house STILL hits ≥ 99 % today (battery tops up after EV stops).
    Assert.True(Reaches99Today(evSim, start.Date),
      $"T=26 (390 min EV): battery should reach ≥99 % today. Max SoC today = " +
      $"{evSim.Slots.Where(s => s.Time.Date == start.Date).Max(s => s.SoC)} %");

    // The base simulation (no EV) also reaches 100 % — confirms PV is genuinely strong.
    Assert.True(Reaches99Today(baseSim, start.Date),
      "Base (no EV) should reach ≥99 % today on a strong spring day.");
  }

  /// <summary>
  /// The UPPER boundary for Optimal daytime condition: at T=27 (405 min, ending 16:15),
  /// one extra slot of EV robs the remaining PV of the energy needed to reach 99 %.
  /// Binary search for Optimal would cap the daytime part at T=26, but the session can
  /// still extend overnight via the same Step 1 predicate (SimWillReachMaxSocToday remains
  /// true for shorter daytime + overnight tail once battery reached 100 %).
  /// </summary>
  [Fact]
  public void SpringDay_Optimal_FullSession_DoesNotReach99()
  {
    var start = SpringStart;
    var evEnd = start.AddMinutes(27 * 15); // T=27 → 16:15
    var evSim = EnergySimulator.Simulate(WithEV(SpringBaseInput(start), start, evEnd));

    // With T=27 the battery peaks at ≈97.6 % (just short of 99 %).
    Assert.False(Reaches99Today(evSim, start.Date),
      $"T=27 (405 min EV): battery should NOT reach ≥99 % today. Max SoC today = " +
      $"{evSim.Slots.Where(s => s.Time.Date == start.Date).Max(s => s.SoC)} %");
  }

  /// <summary>
  /// Optimal session extends overnight: battery starts at 90 % so PV+EV daytime net
  /// (+38 Wh/slot) pushes it to 99 % by ~16:00.  Session continues until 20:00; battery
  /// drains from 100 % to ~22 % overnight — above AbsMin (10 %) with no new grid.
  ///
  /// Verifies that Step 1's tomorrowMax allows Optimal to run past LastPVToday.
  /// </summary>
  [Fact]
  public void SpringDay_Optimal_ExtendsOvernightAfterFull()
  {
    var start  = SpringStart;
    var evEnd  = new DateTime(2026, 3, 22, 20, 0, 0); // 2h15min past last PV (17:45)

    // High start SoC so battery hits 99 % during the day even with EV running.
    var src    = SpringBaseInput(start);
    var baseIn = new SimulationInput
    {
      StartTime                   = src.StartTime,
      StartSocPercent             = 90,   // high start SoC so battery hits 99 % with EV running
      BatteryCapacityWh           = src.BatteryCapacityWh,
      AbsoluteMinSocPercent       = src.AbsoluteMinSocPercent,
      PreferredMinSocPercent      = src.PreferredMinSocPercent,
      EnforcePreferredSoc         = src.EnforcePreferredSoc,
      MaxChargePowerAmps          = src.MaxChargePowerAmps,
      InverterEfficiency          = src.InverterEfficiency,
      ImportPrices                = src.ImportPrices,
      ExportPrices                = src.ExportPrices,
      LoadPredictionWh            = src.LoadPredictionWh,
      PVPredictionWh              = src.PVPredictionWh,
      ExtraLoads                  = src.ExtraLoads,
      EnableCheapForceCharge                 = src.EnableCheapForceCharge,
      OpportunisticDischarge      = src.OpportunisticDischarge,
      ForceChargeMaxPrice         = src.ForceChargeMaxPrice,
      ForceChargeTargetSocPercent = src.ForceChargeTargetSocPercent,
      CurrentMode                 = src.CurrentMode,

    };
    var baseSim = EnergySimulator.Simulate(baseIn);
    var evSim   = EnergySimulator.Simulate(WithEV(baseIn, start, evEnd));
    var baseFCS = new HashSet<DateTime>(
      baseSim.Slots.Where(s => s.State.Mode == InverterModes.force_charge).Select(s => s.Time));

    // Battery must still reach 99 % today (PV surplus is strong enough despite EV).
    Assert.True(Reaches99Today(evSim, start.Date),
      "Optimal overnight: battery must reach ≥99 % today despite EV running.");

    // Overnight SoC must remain above AbsMin.
    Assert.True(OvernightOk(evSim, LastPVToday, FirstPVTomorrow, AbsMin),
      "Optimal overnight: SoC must stay ≥ AbsMin through the night.");

    // No new grid required.
    Assert.True(NoNewGrid(evSim, baseFCS),
      "Optimal overnight: no new grid import should be needed.");
  }

  // ── Step 2 (Priority) tests ──────────────────────────────────────────────────────────────

  /// <summary>
  /// Priority does NOT require the house to reach 100 %; it only requires the overnight SoC
  /// to stay above AbsMin and no new grid import to be scheduled overnight.
  ///
  /// With the full T=33 session (495 min, ending at last PV slot 17:45), the battery is at
  /// ~70 % at sunset.  Overnight drain to ~21 % still stays above AbsMin 10 %, and the naive
  /// trajectory never dips below 10 %, so no force_charge is triggered.
  ///
  /// Confirms Priority finds a longer session (33 slots = 495 min) than Optimal (26 slots = 390 min).
  /// </summary>
  [Fact]
  public void SpringDay_Priority_FullSessionSatisfiesOvernightConditions()
  {
    var start   = SpringStart;
    var evEnd   = LastPVToday.AddMinutes(15); // include the 17:45 slot (last PV slot)
    var baseIn  = SpringBaseInput(start);
    var baseSim = EnergySimulator.Simulate(baseIn);
    var evSim   = EnergySimulator.Simulate(WithEV(baseIn, start, evEnd));

    var baseFCS = new HashSet<DateTime>(
      baseSim.Slots.Where(s => s.State.Mode == InverterModes.force_charge).Select(s => s.Time));

    // Priority condition: overnightOk AND no new grid overnight.
    Assert.True(OvernightOk(evSim, LastPVToday, FirstPVTomorrow, AbsMin),
      "Priority full session: overnight SoC should stay above AbsMin.");
    Assert.True(NoNewGrid(evSim, baseFCS),
      "Priority full session: no new force_charge should be scheduled overnight.");

    // Demonstrate that Priority's session is longer than Optimal's:
    // Optimal maxes out at 26 slots (390 min), but Priority can run for 33 slots (495 min).
    int optimalSlots  = 26;
    int prioritySlots = (int)(evEnd - start).TotalMinutes / 15;
    Assert.True(prioritySlots > optimalSlots,
      $"Priority session ({prioritySlots} slots) should be longer than Optimal ({optimalSlots} slots).");
  }

  /// <summary>
  /// Confirms that the T=27 EV session (which fails Optimal's 99 % test) still satisfies
  /// Priority's conditions — i.e. Priority can run exactly where Optimal would stop.
  /// </summary>
  [Fact]
  public void SpringDay_Priority_RunsWhereOptimalWouldStop()
  {
    var start   = SpringStart;
    var evEnd   = start.AddMinutes(27 * 15); // T=27 → 16:15 (one slot past Optimal max)
    var baseIn  = SpringBaseInput(start);
    var baseSim = EnergySimulator.Simulate(baseIn);
    var evSim   = EnergySimulator.Simulate(WithEV(baseIn, start, evEnd));
    var baseFCS = new HashSet<DateTime>(
      baseSim.Slots.Where(s => s.State.Mode == InverterModes.force_charge).Select(s => s.Time));

    // T=27 fails Optimal (battery doesn't hit 99 %)
    Assert.False(Reaches99Today(evSim, start.Date),
      "T=27 should not satisfy Optimal's 99 % condition.");

    // … but satisfies Priority (overnight OK, no new grid)
    Assert.True(OvernightOk(evSim, LastPVToday, FirstPVTomorrow, AbsMin),
      "T=27 should still satisfy Priority's overnight condition.");
    Assert.True(NoNewGrid(evSim, baseFCS),
      "T=27 should require no new grid import overnight.");
  }

  // ── Step 3 (PriorityPlus) tests ──────────────────────────────────────────────────────────

  /// <summary>
  /// PriorityPlus scenario: EV charging starts at 22:00 (no PV available).
  /// The combined house + EV drain (550 Wh/slot) would deplete the battery overnight,
  /// triggering force_charge — but ONLY during the cheap grid window (00:00–05:59).
  ///
  /// Verifies:
  ///   1. Base case (no EV): battery survives overnight → baseOvernightOk = true.
  ///   2. With EV: force_charge fires but only at cheap hours → gridOnlyCheap = true.
  ///   3. Priority's !needsGrid condition FAILS (force_charge IS required) — showing why
  ///      PriorityPlus is needed.
  /// </summary>
  [Fact]
  public void PriorityPlus_OvernightEV_UsesOnlyCheapGrid()
  {
    // 22:00 start, battery at 85 % — enough to last until the cheap window (00:00–06:00).
    const float CheapPrice     = 0.05f;  // cheap: 00:00–05:59
    const float ExpensivePrice = 0.30f;  // other hours
    const float MaxPrice       = 0.15f;  // ForceChargeMaxPrice; cheap ≤ max ✓

    var start    = new DateTime(2026, 3, 22, 22, 0, 0);
    var baseDate = start.Date;
    var prices   = BandedPrices(baseDate, CheapPrice, ExpensivePrice, cheapFrom: 0, cheapTo: 6);

    var baseIn = new SimulationInput
    {
      StartTime                   = start,
      StartSocPercent             = 85,  // 9 792 Wh — enough for the cheap window
      BatteryCapacityWh           = BatCap,
      AbsoluteMinSocPercent       = AbsMin,
      PreferredMinSocPercent      = 30,
      EnforcePreferredSoc         = false,
      MaxChargePowerAmps          = ChargeA,
      InverterEfficiency          = 0.9f,
      ImportPrices                = prices,
      ExportPrices                = prices,
      LoadPredictionWh            = FlatLoad(baseDate, 100),  // 100 Wh/slot overnight
      PVPredictionWh              = DaytimePV(baseDate, PvWh),
      ExtraLoads                  = [],
      EnableCheapForceCharge                 = false,
      OpportunisticDischarge      = false,
      ForceChargeMaxPrice         = MaxPrice,
      ForceChargeTargetSocPercent = 95,
      CurrentMode                 = new InverterState(InverterModes.normal),

    };

    var baseSim = EnergySimulator.Simulate(baseIn);
    var baseFCS = new HashSet<DateTime>(
      baseSim.Slots.Where(s => s.State.Mode == InverterModes.force_charge).Select(s => s.Time));

    // ── 1. Base overnight OK ─────────────────────────────────────────────────────────────
    // With house load (100 Wh/slot) only, battery (85 %) should comfortably last the night.
    var lastPVToday     = new DateTime(2026, 3, 22, 17, 45, 0);
    var firstPVTomorrow = new DateTime(2026, 3, 23,  6,  0, 0);
    Assert.True(OvernightOk(baseSim, lastPVToday, firstPVTomorrow, AbsMin),
      "Base (no EV): battery should survive overnight above AbsMin.");

    // ── 2. With EV: force_charge fires, but only at cheap hours ──────────────────────────
    var sessionEnd = new DateTime(2026, 3, 23, 6, 15, 0); // EV until next sunrise
    var evSim = EnergySimulator.Simulate(WithEV(baseIn, start, sessionEnd));

    var newForceSlots = evSim.Slots
      .Where(s => s.State.Mode == InverterModes.force_charge
                  && !baseFCS.Contains(s.Time)
                  && s.Time >= lastPVToday && s.Time <= firstPVTomorrow)
      .ToList();

    // force_charge MUST fire overnight (house + EV drain triggers it)
    Assert.NotEmpty(newForceSlots);

    // ALL new force_charge slots must be at cheap hours (price ≤ MaxPrice)
    Assert.All(newForceSlots, s =>
    {
      float price = prices.First(p => p.StartTime <= s.Time && p.EndTime > s.Time).Price;
      Assert.True(price <= MaxPrice,
        $"force_charge at {s.Time:HH:mm} has price {price} > MaxPrice {MaxPrice}");
    });

    // gridOnlyCheap = true for the EV sim
    Assert.True(GridCheap(evSim, baseFCS, prices, MaxPrice),
      "With cheap overnight grid, gridOnlyCheap should be true.");

    // ── 3. Priority's !needsGrid fails (this is WHY PriorityPlus is needed) ─────────────
    Assert.False(NoNewGrid(evSim, baseFCS),
      "Priority should fail here: EV overnight causes new grid import (force_charge).");
  }

  /// <summary>
  /// Regression scenario for the FindLoadWindow Step 2/Step 3 ordering bug: house battery at
  /// its daytime trough (19 %, well above the 10 % absolute floor but with little headroom),
  /// import price flat and cheap (10 ct, under ForceChargeMaxPrice = 15 ct) for the whole
  /// simulated period, EV session running from mid-afternoon through to next sunrise.
  ///
  /// This proves the predicate-level premise behind the Step 2/Step 3 fix:
  ///   - Step 3's condition (IsGridCheapVs) STAYS TRUE for this long candidate — Step 3 would
  ///     accept the full overnight session.
  ///   - Step 2's condition (!HasNewGridVs) FLIPS FALSE for the very same candidate — Step 2
  ///     alone would incorrectly reject it, even though the new grid import is exactly the kind
  ///     of cheap self-correction PriorityPlus is designed to tolerate.
  ///   - Step 3's own precondition (base case survives overnight unassisted) holds, confirming
  ///     it is not what blocks PriorityPlus here — matching the live production scenario.
  /// </summary>
  [Fact]
  public void PriorityPlus_DaytimeTroughEV_SelfCorrectingForceChargeStaysCheap()
  {
    const float FlatPrice = 0.10f; // flat cheap price all day/night, as in the live scenario
    const float MaxPrice  = 0.15f; // ForceChargeMaxPrice; flat <= max
    const int TroughSoc   = 19;    // % — daytime trough, well above AbsMin (10 %) but little headroom

    var start    = new DateTime(2026, 3, 22, 14, 0, 0); // mid-afternoon, still in PV window
    var baseDate = start.Date;
    var prices   = HourlyPrices(baseDate, FlatPrice, FlatPrice, cheapHour: 0);

    var baseIn = new SimulationInput
    {
      StartTime                   = start,
      StartSocPercent             = TroughSoc,
      BatteryCapacityWh           = BatCap,
      AbsoluteMinSocPercent       = AbsMin,
      PreferredMinSocPercent      = 30,
      EnforcePreferredSoc         = false,
      MaxChargePowerAmps          = ChargeA,
      InverterEfficiency          = 0.9f,
      ImportPrices                = prices,
      ExportPrices                = prices,
      LoadPredictionWh            = FlatLoad(baseDate, LoadWh),
      PVPredictionWh              = DaytimePV(baseDate, PvWh),
      ExtraLoads                  = [],
      EnableCheapForceCharge      = false,
      OpportunisticDischarge      = false,
      ForceChargeMaxPrice         = MaxPrice,
      ForceChargeTargetSocPercent = 95,
      CurrentMode                 = new InverterState(InverterModes.normal),
    };

    var baseSim = EnergySimulator.Simulate(baseIn);
    var baseFCS = new HashSet<DateTime>(
      baseSim.Slots.Where(s => s.State.Mode == InverterModes.force_charge).Select(s => s.Time));

    // Base case survives overnight comfortably — Step 3's precondition holds.
    Assert.True(OvernightOk(baseSim, LastPVToday, FirstPVTomorrow, AbsMin),
      "Base (no EV): battery should survive overnight above AbsMin — Step 3's precondition holds.");

    // With EV overnight: NeedToCharge self-corrects with a NEW, but cheap, force_charge.
    var evSim = EnergySimulator.Simulate(WithEV(baseIn, start, FirstPVTomorrow));

    var newForceSlots = evSim.Slots
      .Where(s => s.State.Mode == InverterModes.force_charge && !baseFCS.Contains(s.Time))
      .ToList();
    Assert.NotEmpty(newForceSlots);

    // Step 3 would accept: every new force_charge slot is at/under ForceChargeMaxPrice.
    Assert.True(GridCheap(evSim, baseFCS, prices, MaxPrice),
      "Flat cheap price means the self-correcting force_charge stays under ForceChargeMaxPrice.");

    // Step 2 would incorrectly reject: it demands ZERO new grid, at any price.
    Assert.False(NoNewGrid(evSim, baseFCS),
      "Step 2 alone rejects this session solely because SOME new grid was used — " +
      "even though it's exactly the cheap self-correction PriorityPlus should tolerate.");
  }

  // ── PriorityPlus's bonus ruleset ────────────────────────────────────────────────────────────
  // FindLoadWindow's PriorityPlus bonus ruleset scans forward from "now," advancing the EV's own
  // simulated start, for the earliest candidate where it needs no new grid at all (vs. baseline
  // — the same comparison Priority's own rule already uses) or needs new grid at a price at/
  // under the configured threshold. These tests exercise that premise directly via
  // SimulationResult's own public methods (no seam into FindLoadWindow itself, per this file's
  // documented HA-integration exception).

  /// <summary>
  /// Grounds "advance until accepted" empirically: an EV candidate starting right now (T0), in
  /// the evening with a tight battery margin and a flat, never-cheap price, needs new grid
  /// import at a price over threshold — rejected. A later candidate (T1), reached only by
  /// advancing the EV's own simulated start into tomorrow's PV window, is accepted because by
  /// then PV has already recovered the battery and covers the EV's draw directly — no new grid
  /// needed at all. This is exactly what justifies re-simulating at each advancing candidate
  /// (NeedToCharge's own computed deadline/need shifts with where the EV's load actually falls)
  /// rather than checking once at "now."
  /// </summary>
  [Fact]
  public void PriorityPlus_BonusRuleset_AdvancingPastPVRecoveryGetsAccepted()
  {
    const float FlatExpensivePrice = 0.30f; // never cheap anywhere in the horizon
    const float MaxPrice           = 0.15f;

    var t0       = new DateTime(2026, 3, 22, 22, 0, 0); // evening, no PV, tight margin
    var baseDate = t0.Date;
    var prices   = HourlyPrices(baseDate, FlatExpensivePrice, FlatExpensivePrice, cheapHour: 0);
    var t1       = baseDate.AddDays(1).AddHours(10); // tomorrow mid-morning, well inside PV hours

    var baseIn = new SimulationInput
    {
      StartTime                   = t0,
      StartSocPercent             = 60, // comfortable for the house alone; a long EV draw at T0
                                         // still needs grid help, but PV fully recovers it by T1
      BatteryCapacityWh           = BatCap,
      AbsoluteMinSocPercent       = AbsMin,
      PreferredMinSocPercent      = 30,
      EnforcePreferredSoc         = false,
      MaxChargePowerAmps          = ChargeA,
      InverterEfficiency          = 0.9f,
      ImportPrices                = prices,
      ExportPrices                = prices,
      LoadPredictionWh            = FlatLoad(baseDate, LoadWh),
      PVPredictionWh              = DaytimePV(baseDate, PvWh),
      ExtraLoads                  = [],
      EnableCheapForceCharge      = false,
      OpportunisticDischarge      = false,
      ForceChargeMaxPrice         = MaxPrice,
      ForceChargeTargetSocPercent = 95,
      CurrentMode                 = new InverterState(InverterModes.normal),
    };

    var baseSim = EnergySimulator.Simulate(baseIn);
    Assert.Empty(baseSim.Slots.Where(s => s.State.Mode == InverterModes.force_charge).Select(s => s.Time));

    const int durationHours = 4;

    var evSimT0 = EnergySimulator.Simulate(WithEV(baseIn, t0, t0.AddHours(durationHours)));
    Assert.True(evSimT0.HasNewGridVs(baseSim) && !evSimT0.IsGridCheapVs(baseSim),
      "T0 (evening, flat expensive price) should need new grid at a price over threshold — rejected.");

    var evSimT1 = EnergySimulator.Simulate(WithEV(baseIn, t1, t1.AddHours(durationHours)));
    Assert.False(evSimT1.HasNewGridVs(baseSim),
      "T1 (reached only by advancing past tomorrow's PV recovery) should need no new grid at all — accepted.");
  }

  /// <summary>
  /// "PriorityPlus may piggy-back on an active cheapest-period force charge" needs no
  /// special-case code: an EV running inside that window draws its power from the grid at the
  /// window's (under-threshold) price, so it is new grid (Priority rejects it) that passes the
  /// price check (PriorityPlus accepts it).
  /// </summary>
  [Fact]
  public void PriorityPlus_CheapestPeriodPiggyback_IsCheapNewGrid()
  {
    const float CheapPrice     = 0.05f; // cheapest hour: 02:00–02:59
    const float ExpensivePrice = 0.30f;
    const float MaxPrice       = 0.15f;

    var start    = new DateTime(2026, 3, 22, 1, 30, 0); // within 1h before the cheapest hour
    var baseDate = start.Date;
    var prices   = HourlyPrices(baseDate, CheapPrice, ExpensivePrice, cheapHour: 2);

    var baseIn = new SimulationInput
    {
      StartTime                   = start,
      StartSocPercent             = 20,
      BatteryCapacityWh           = BatCap,
      AbsoluteMinSocPercent       = AbsMin,
      PreferredMinSocPercent      = 30,
      EnforcePreferredSoc         = false,
      MaxChargePowerAmps          = ChargeA,
      InverterEfficiency          = 0.9f,
      ImportPrices                = prices,
      ExportPrices                = prices,
      LoadPredictionWh            = FlatLoad(baseDate, LoadWh),
      PVPredictionWh              = DaytimePV(baseDate, PvWh),
      ExtraLoads                  = [],
      EnableCheapForceCharge      = true, // switch.pv_control_force_charge_at_cheapest_period
      OpportunisticDischarge      = false,
      ForceChargeMaxPrice         = MaxPrice,
      ForceChargeTargetSocPercent = 60,
      CurrentMode                 = new InverterState(InverterModes.normal),
    };

    var baseSim = EnergySimulator.Simulate(baseIn);
    Assert.Contains(baseSim.Slots, s => s.State.Mode == InverterModes.force_charge
                                      && s.State.ModeReason == ForceChargeReasons.ForcedChargeAtMinimumPrice);

    // EV runs only inside the cheap hour while the cheapest-period charge is active, drawing
    // heavily (3 700 W) — the grid covers it at the cheap price.
    var cheapHour = baseDate.AddHours(2);
    var evInput = baseIn.WithExtraLoads([new ExtraLoad { Name = "EV", Priority = 10, StartTime = cheapHour, EndTime = cheapHour.AddHours(1), PowerW = 3_700 }]);
    var evSim   = EnergySimulator.Simulate(evInput);

    Assert.True(evSim.HasNewGridVs(baseSim), "The EV's power comes from the grid — Priority must reject it.");
    Assert.True(evSim.IsGridCheapVs(baseSim), "…but at the cheap-window price — PriorityPlus may piggy-back.");
  }

  // ── NeedToCharge slot selection: use the whole cheap band, not just from its cheapest slot ─

  // Live shape, 2026-10-18 ~10:45: cloudy day (no PV today), battery 27 %, cheap band
  // 13:00–14:59 under the 34 ct threshold whose single cheapest quarter is 14:00, expensive
  // everywhere else. The deficit to cover until tomorrow's PV needs more quarters than remain
  // in the band from 14:00 onward, but fits in the whole band.
  const float BandPrice     = 0.32f;
  const float CheapestPrice = 0.31f;
  const float OutsidePrice  = 0.40f;
  const float BandMaxPrice  = 0.34f;
  static readonly DateTime AutumnStart = new(2026, 10, 18, 10, 45, 0);

  static SimulationInput AutumnBandInput()
  {
    var date   = AutumnStart.Date;
    var prices = new List<PriceTableEntry>();
    for (var t = date; t < date.AddDays(2); t = t.AddMinutes(15))
    {
      float p = t == date.AddHours(14) ? CheapestPrice
              : t >= date.AddHours(13) && t < date.AddHours(15) ? BandPrice
              : OutsidePrice;
      prices.Add(new PriceTableEntry(t, t.AddMinutes(15), p));
    }
    var pv = new Dictionary<DateTime, int>();
    for (var t = date; t < date.AddDays(3); t = t.AddMinutes(15))
      pv[t] = t.Date > date && t.Hour >= 6 && t.Hour < 18 ? PvWh : 0;

    return new SimulationInput
    {
      StartTime                   = AutumnStart,
      StartSocPercent             = 27,
      BatteryCapacityWh           = BatCap,
      AbsoluteMinSocPercent       = 12,
      PreferredMinSocPercent      = 15,
      EnforcePreferredSoc         = false,
      MaxChargePowerAmps          = ChargeA,
      InverterEfficiency          = 0.9f,
      ImportPrices                = prices,
      ExportPrices                = prices,
      LoadPredictionWh            = FlatLoad(date, LoadWh),
      PVPredictionWh              = pv,
      ExtraLoads                  = [],
      EnableCheapForceCharge      = false,
      OpportunisticDischarge      = false,
      ForceChargeMaxPrice         = BandMaxPrice,
      ForceChargeTargetSocPercent = 95,
      CurrentMode                 = new InverterState(InverterModes.normal),
    };
  }

  static List<DateTime> ExpensiveForceChargeSlots(SimulationResult sim, SimulationInput input) =>
    sim.Slots
      .Where(s => s.State.Mode == InverterModes.force_charge
                  && input.ImportPrices.GetPrice(s.Time) > input.ForceChargeMaxPrice)
      .Select(s => s.Time).ToList();

  /// <summary>
  /// The NeedToCharge grid charge must be sized into the cheapest quarters of the whole search
  /// window. Previously it waited for the single cheapest quarter (14:00) and charged forward
  /// from there, so a deficit larger than "14:00 → end of band" spilled into the expensive
  /// quarters after the band while the equally-cheap 13:00–13:45 quarters went unused.
  /// </summary>
  [Fact]
  public void NeedToCharge_DeficitLargerThanRestOfBand_ChargesEarlierInBandInsteadOfSpilling()
  {
    var input = AutumnBandInput();
    var sim   = EnergySimulator.Simulate(input);

    Assert.Contains(sim.Slots, s => s.State.Mode == InverterModes.force_charge);
    var expensive = ExpensiveForceChargeSlots(sim, input);
    Assert.True(expensive.Count == 0,
      $"Grid charge spilled into quarters over threshold: {string.Join(", ", expensive.Select(t => t.ToString("HH:mm")))}");
  }

  /// <summary>
  /// The PriorityPlus case from 2026-10-18: an EV start at the beginning of the cheap band makes
  /// the house's grid charge larger. That larger charge must use the earlier quarters of the
  /// band (all under threshold) instead of spilling past it, so the EV start is accepted by
  /// IsGridCheapVs — the scan should not have to wait until after the band.
  /// </summary>
  [Fact]
  public void PriorityPlus_EvStartInBand_GridChargeMovesEarlierInBand_Accepted()
  {
    var baseIn  = AutumnBandInput();
    var baseSim = EnergySimulator.Simulate(baseIn);

    var evStart = AutumnStart.Date.AddHours(13);
    var evIn    = baseIn.WithExtraLoads([new ExtraLoad { Name = "EV", Priority = 10, StartTime = evStart, EndTime = evStart.AddHours(2), PowerW = 1_856 }]);
    var evSim   = EnergySimulator.Simulate(evIn);

    var expensive = ExpensiveForceChargeSlots(evSim, evIn);
    Assert.True(expensive.Count == 0,
      $"Grid charge spilled into quarters over threshold: {string.Join(", ", expensive.Select(t => t.ToString("HH:mm")))}");
    Assert.True(!evSim.HasNewGridVs(baseSim) || evSim.IsGridCheapVs(baseSim),
      "An EV start whose extra grid need fits in the cheap band must pass PriorityPlus's price check.");
  }

  // ── HasNewGrid daytime regression ────────────────────────────────────────────────────────

  /// <summary>
  /// Regression test for the bug where HasNewGrid only checked the overnight window
  /// [LastPVToday, FirstPVTomorrow] and missed force_charge scheduled BEFORE sunset.
  ///
  /// Scenario: 15:00 start, battery at 40%, high EV drain (3700 W = 925 Wh/slot).
  /// Net balance with PV: 600 − 112 − 925 = −437 Wh/slot → battery hits AbsMin ~16:48.
  /// The simulation schedules force_charge at the current slot (15:00, before sunset 17:45)
  /// to prevent the battery from going below AbsMin.
  ///
  /// The old code checked HasNewGrid overnight-only, which missed this daytime force_charge.
  /// After the fix, HasNewGrid checks all slots and correctly blocks the EV session.
  /// </summary>
  [Fact]
  public void Priority_HighDrainEV_HasNewGrid_CatchesDaytimeGridImport()
  {
    var start = new DateTime(2026, 3, 22, 15, 0, 0);
    const int HighEvW = 3_700;  // W → 925 Wh/slot; net = 600 - 112 - 925 = -437 Wh/slot during PV

    var date   = start.Date;
    var prices = HourlyPrices(date, cheapPrice: 0.05f, expensivePrice: 0.30f, cheapHour: 2);

    var baseIn = new SimulationInput
    {
      StartTime                   = start,
      StartSocPercent             = 40,   // 40 % = 4 608 Wh → hits AbsMin mid-afternoon with EV
      BatteryCapacityWh           = BatCap,
      AbsoluteMinSocPercent       = AbsMin,
      PreferredMinSocPercent      = 30,
      EnforcePreferredSoc         = false,
      MaxChargePowerAmps          = ChargeA,
      InverterEfficiency          = 0.9f,
      ImportPrices                = prices,
      ExportPrices                = prices,
      LoadPredictionWh            = FlatLoad(date, LoadWh),
      PVPredictionWh              = DaytimePV(date, PvWh),
      ExtraLoads                  = [],
      EnableCheapForceCharge                 = false,
      OpportunisticDischarge      = false,
      ForceChargeMaxPrice         = 0.25f,
      ForceChargeTargetSocPercent = 95,
      CurrentMode                 = new InverterState(InverterModes.normal),

    };

    var evIn = new SimulationInput
    {
      StartTime                   = baseIn.StartTime,
      StartSocPercent             = baseIn.StartSocPercent,
      BatteryCapacityWh           = baseIn.BatteryCapacityWh,
      AbsoluteMinSocPercent       = baseIn.AbsoluteMinSocPercent,
      PreferredMinSocPercent      = baseIn.PreferredMinSocPercent,
      EnforcePreferredSoc         = baseIn.EnforcePreferredSoc,
      MaxChargePowerAmps          = baseIn.MaxChargePowerAmps,
      InverterEfficiency          = baseIn.InverterEfficiency,
      ImportPrices                = baseIn.ImportPrices,
      ExportPrices                = baseIn.ExportPrices,
      LoadPredictionWh            = baseIn.LoadPredictionWh,
      PVPredictionWh              = baseIn.PVPredictionWh,
      ExtraLoads                  = [new ExtraLoad { Name = "EV", Priority = 10, StartTime = start, EndTime = FirstPVTomorrow, PowerW = HighEvW }],
      EnableCheapForceCharge                 = baseIn.EnableCheapForceCharge,
      OpportunisticDischarge      = baseIn.OpportunisticDischarge,
      ForceChargeMaxPrice         = baseIn.ForceChargeMaxPrice,
      ForceChargeTargetSocPercent = baseIn.ForceChargeTargetSocPercent,
      CurrentMode                 = baseIn.CurrentMode,
    };

    var baseSim = EnergySimulator.Simulate(baseIn);
    var evSim   = EnergySimulator.Simulate(evIn);
    var baseFCS = new HashSet<DateTime>(
      baseSim.Slots.Where(s => s.State.Mode == InverterModes.force_charge).Select(s => s.Time));

    // The EV causes new grid import BEFORE sunset. Since 2026-10-26 that's the floor held with
    // grid_only from ~17:00 (house + EV at 0.30 directly) and the charge in the 0.05 hour: a force
    // charge at 0.30 in the afternoon cost the same plus the round-trip loss (charging vs holding
    // compares with the hours until PV after the low, not just one hour).
    var baseGrid = baseSim.Slots.ToDictionary(s => s.Time, s => s.GridImportWh);
    var newGridSlots = evSim.Slots.Where(s => s.GridImportWh > baseGrid.GetValueOrDefault(s.Time)).ToList();
    Assert.Contains(newGridSlots, s => s.Time < LastPVToday);
    Assert.Contains(evSim.Slots, s => s.Time < LastPVToday && s.State.Mode == InverterModes.grid_only);
    Assert.False(NoNewGrid(evSim, baseFCS), "The EV's deficit is charged later (cheap hour): a new force_charge.");

    // HasNewGrid (all slots) must detect the daytime import and block the EV.
    Assert.True(evSim.HasNewGridVs(baseSim), "HasNewGrid (all slots) must detect the new grid import caused by the EV.");
  }
}
