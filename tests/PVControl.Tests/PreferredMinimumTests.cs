using NetDeamon.apps;
using NetDeamon.apps.PVControl;
using NetDeamon.apps.PVControl.Simulator;
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace NetDeamonApps.Tests;

/// <summary>
/// The preferred minimum SoC (live 2026-10-30): planning strives for it — a grid charge in cheap quarters keeps the
/// battery at it when PV doesn't — while an unforeseen load may drain it down to the absolute minimum (the hard floor,
/// held with grid_only). It is only worth cheap energy: never bought at an expensive time. With EnforcePreferredSoc it
/// is the hard floor itself. That morning the night charge (0.39) had only aimed for the absolute 12 %, a heating run
/// at 07:05 emptied the rest, and the house imported at the morning peak (0.50) from 07:28. The same day the plan let
/// the battery dip to 17 % from 21:15 because every price was above the 0.35 limit: the band is kept independent of the
/// limit — in the cheapest quarters before the dip, never above the coming 24 h's average price.
/// Scenario: 11.52 kWh battery, absolute 12 %, preferred 25 %, ~320 W house load, PV from 08:00, the prices of
/// 2026-10-30 (night 0.39, morning peak 0.50).
/// </summary>
public class PreferredMinimumTests : TestBase
{
  static readonly DateTime Day = new(2026, 10, 30);

  /// <summary>Gross import price of the hour, shaped like 2026-10-30.</summary>
  static float Price(int hour) => hour switch
  {
    >= 0 and < 3 => 0.40f,
    >= 3 and < 5 => 0.389f,
    5 => 0.40f,
    6 => 0.473f,
    7 or 8 => 0.50f,
    >= 9 and < 17 => 0.45f,
    >= 17 and < 21 => 0.55f,
    _ => 0.43f,
  };

  static SimulationInput Input(DateTime start, int soc, bool enforce = false, InverterState? mode = null, bool flatPrices = false, int preferred = 25, int loadWh = 80)
  {
    var prices = new List<PriceTableEntry>();
    var load = new Dictionary<DateTime, int>();
    var pv = new Dictionary<DateTime, int>();
    for (var t = Day.AddDays(-1); t < Day.AddDays(3); t = t.AddMinutes(15))
    {
      if (t < Day.AddDays(2)) prices.Add(new PriceTableEntry(t, t.AddMinutes(15), flatPrices ? 0.40f : Price(t.Hour)));
      load[t] = loadWh;
      pv[t] = t.Hour >= 8 && t.Hour < 16 ? 450 : 0;
    }
    return new SimulationInput
    {
      StartTime = start, StartSocPercent = soc, BatteryCapacityWh = 11_520,
      AbsoluteMinSocPercent = 12, PreferredMinSocPercent = preferred, EnforcePreferredSoc = enforce,
      MaxChargePowerAmps = 30, InverterEfficiency = 0.95f, ImportPrices = prices, ExportPrices = prices,
      LoadPredictionWh = load, PVPredictionWh = pv, ExtraLoads = [],
      EnableCheapForceCharge = false, OpportunisticDischarge = false, ForceChargeMaxPrice = 0.42f,
      ForceChargeTargetSocPercent = 95, CurrentMode = mode ?? new InverterState(InverterModes.normal),
    };
  }

  static string Modes(IEnumerable<SimulationSlot> slots) =>
    string.Join(", ", slots.Select(s => $"{s.Time:HH:mm} {s.State.Mode} {s.SoC}%"));

  [Fact]
  public void SoftBand_IsThePreferredMinimum_OrTheForecastMarginOnAHardFloor()
  {
    Assert.Equal(13, EnergySimulator.SoftBandPct(Input(Day, 50), TimeSpan.FromHours(5)));                 // 25 − 12
    Assert.Equal(EnergySimulator.ForecastMarginPct(TimeSpan.FromHours(5)),
      EnergySimulator.SoftBandPct(Input(Day, 50, enforce: true), TimeSpan.FromHours(5)));                    // 25 is the hard floor
    Assert.Equal(0, EnergySimulator.SoftBandPct(Input(Day, 50, preferred: 10), TimeSpan.FromHours(5)));   // never below the absolute
  }

  [Fact]
  public void Night_ChargesInTheCheapHours_ToThePreferredMinimumAtTheMorningLow()
  {
    // 01:00 at 20 %: the morning low would be ~12 %. The cheap night hours (03–05, 0.389) lift it to the preferred minimum.
    var slots = EnergySimulator.Simulate(Input(Day.AddHours(1), 20)).Slots;
    var charging = slots.Where(s => s.Time < Day.AddHours(8) && s.State.Mode == InverterModes.force_charge).ToList();
    Assert.True(charging.Count > 0 && charging.All(s => s.Time.Hour is 3 or 4), Modes(slots.Where(s => s.Time < Day.AddHours(10))));
    Assert.All(charging, s => Assert.InRange(s.Time.Hour, 3, 4));
    int morningLow = slots.Where(s => s.Time >= Day.AddHours(5) && s.Time < Day.AddHours(9)).Min(s => s.SoC);
    Assert.InRange(morningLow, 25, 25 + EnergySimulator.ChargeTargetBufferPct + 1);
  }

  [Fact]
  public void BelowPreferred_AboveTheHardFloorUntilPv_NoChargeAtThePeak()
  {
    // 07:30 at 18 % (an unforeseen heating run drained it): the low before PV stays above 12 % — the band is used,
    // nothing is bought at the morning peak (0.50), no hold either.
    var slots = EnergySimulator.Simulate(Input(Day.AddHours(7).AddMinutes(30), 18)).Slots;
    var morning = slots.Where(s => s.Time < Day.AddHours(9)).ToList();
    Assert.True(morning.All(s => s.State.Mode == InverterModes.normal), Modes(morning));
  }

  [Fact]
  public void AtTheHardFloor_BeforePv_HoldsItAtThePeak()
  {
    // 07:00 at 13 %: the battery would drop under 12 % before PV — the hard floor is held (grid_only buys only the
    // load), no top-up charge at 0.50 (the night was cheaper).
    var slots = EnergySimulator.Simulate(Input(Day.AddHours(7), 13)).Slots;
    var morning = slots.Where(s => s.Time < Day.AddHours(8)).ToList();
    Assert.DoesNotContain(morning, s => s.State.Mode == InverterModes.force_charge);
    Assert.Contains(morning, s => s.State.Mode == InverterModes.grid_only);
    Assert.True(morning.All(s => s.SoC >= 11), Modes(morning));
  }

  [Fact]
  public void FlatPrices_TheBandIsKept()
  {
    // The band is a planning constraint, not a price bet: kept also when nothing is cheaper later.
    var slots = EnergySimulator.Simulate(Input(Day.AddHours(1), 20, flatPrices: true)).Slots;
    int morningLow = slots.Where(s => s.Time >= Day.AddHours(5) && s.Time < Day.AddHours(9)).Min(s => s.SoC);
    Assert.InRange(morningLow, 25, 25 + EnergySimulator.ChargeTargetBufferPct + 1);
  }

  /// <summary>Gross import prices of 2026-10-30 from 10:00 (the night after shaped like the night before).</summary>
  static float Live1030(DateTime t) => (t.Date == Day ? t.Hour : t.Hour + 24) switch
  {
    10 => 0.426f, 11 => 0.414f, 12 => 0.401f, 13 => 0.404f, 14 => 0.425f, 15 => 0.469f, 16 => 0.510f, 17 => 0.514f,
    18 => 0.499f, 19 => 0.488f, 20 => 0.468f, 21 => 0.435f, 22 => 0.412f, 23 => 0.394f,
    >= 24 and < 29 => 0.39f, 29 => 0.40f, 30 => 0.47f, 31 or 32 => 0.50f, _ => 0.45f,
  };

  [Fact]
  public void Live20261030_MiddayCharge_KeepsThePreferredMinimumThroughTheEvening()
  {
    // 10:30 at 27 %: PV lifts the battery to ~49 % by 15:00, the evening drains it to ~20 % by 23:15. All prices are
    // above the 0.35 limit; the plan used to dip under 25 % from ~21:30 and refill at 23:15 (0.394). Now the band is
    // charged before the dip, in the day's cheapest quarter (12:00, 0.401 — under the 24-h average), and the evening
    // stays at the preferred minimum.
    var b = Input(Day.AddHours(10).AddMinutes(30), 27, loadWh: 100);
    var prices = new List<PriceTableEntry>();
    var pv = new Dictionary<DateTime, int>();
    for (var t = Day.AddDays(-1); t < Day.AddDays(2); t = t.AddMinutes(15))
    {
      prices.Add(new PriceTableEntry(t, t.AddMinutes(15), Live1030(t)));
      pv[t] = t.Hour >= 10 && t.Hour < 15 ? 240 : 0;
    }
    var input = new SimulationInput
    {
      StartTime = b.StartTime, StartSocPercent = b.StartSocPercent, BatteryCapacityWh = b.BatteryCapacityWh,
      AbsoluteMinSocPercent = 12, PreferredMinSocPercent = 25, EnforcePreferredSoc = false,
      MaxChargePowerAmps = b.MaxChargePowerAmps, InverterEfficiency = b.InverterEfficiency, ImportPrices = prices, ExportPrices = prices,
      LoadPredictionWh = b.LoadPredictionWh, PVPredictionWh = pv, ExtraLoads = [],
      EnableCheapForceCharge = false, OpportunisticDischarge = false, ForceChargeMaxPrice = 0.35f,
      ForceChargeTargetSocPercent = 95, CurrentMode = b.CurrentMode,
    };
    var slots = EnergySimulator.Simulate(input).Slots;
    var today = slots.Where(s => s.Time < Day.AddHours(23)).ToList();
    var charging = today.Where(s => s.State.Mode == InverterModes.force_charge).ToList();
    Assert.True(charging.Count > 0, Modes(today));
    Assert.All(charging, s => Assert.Equal(12, s.Time.Hour));
    Assert.True(today.Where(s => s.Time >= Day.AddHours(15)).All(s => s.SoC >= 24), Modes(today));
  }

  [Fact]
  public void LateDip_OnlyPeakQuartersBefore_NoChargeAtThePeak()
  {
    // 18:00 at 26 %, the evening peak (0.55) until 21:00 — all above the 24-h average: the band is not bought there.
    // The plan accepts the dip and refills in the cheap night.
    var slots = EnergySimulator.Simulate(Input(Day.AddHours(18), 26, loadWh: 40)).Slots;
    var peak = slots.Where(s => s.Time < Day.AddHours(21)).ToList();
    Assert.DoesNotContain(peak, s => s.State.Mode == InverterModes.force_charge);
    Assert.Contains(slots.Where(s => s.Time >= Day.AddHours(21) && s.Time < Day.AddDays(1).AddHours(8)), s => s.State.Mode == InverterModes.force_charge);
  }

  [Fact]
  public void Enforced_ThePreferredMinimumIsTheHardFloor()
  {
    // 07:00 at 26 %, enforced: the battery is held at 25 % (grid_only), never drained into the band.
    var slots = EnergySimulator.Simulate(Input(Day.AddHours(7), 26, enforce: true)).Slots;
    var morning = slots.Where(s => s.Time < Day.AddHours(8)).ToList();
    Assert.True(morning.All(s => s.SoC >= 24), Modes(morning));
    Assert.Contains(morning, s => s.State.Mode is InverterModes.grid_only or InverterModes.force_charge);
  }

  [Theory]
  [InlineData(13, InverterModes.grid_only)]   // 1 % above the floor: still held
  [InlineData(14, InverterModes.grid_only)]   // the release margin itself
  [InlineData(15, InverterModes.normal)]      // above floor + HoldReleasePct: released
  public void Hold_ReleasedOnlyAboveFloorPlusMargin(int soc, InverterModes expected)
  {
    // Live 2026-10-30 07:44: at the bare floor the 1-% SoC steps switched normal/grid_only every 15 s.
    // Enforced 12 % floor (no band) at 07:30, PV from 08:00: only the hold decides.
    var holding = new InverterState(InverterModes.grid_only, ForceChargeReasons.GoingUnderAbsoluteMinima);
    var input = Input(Day.AddHours(7).AddMinutes(30), soc, enforce: true, mode: holding, preferred: 12);
    Assert.Equal(expected, EnergySimulator.Simulate(input).Slots[0].State.Mode);
  }

  [Fact]
  public void PlannedLoads_KeepThePreferredMinimumOvernight()
  {
    // The late dip above: the night goes under the preferred 25 % before the refill, so a planned load isn't OK there;
    // without a band (preferred = absolute) the same night is.
    Assert.False(EnergySimulator.Simulate(Input(Day.AddHours(18), 26, loadWh: 40)).IsOvernightMinSocOk());
    Assert.True(EnergySimulator.Simulate(Input(Day.AddHours(18), 26, loadWh: 40, preferred: 12)).IsOvernightMinSocOk());
  }
}
