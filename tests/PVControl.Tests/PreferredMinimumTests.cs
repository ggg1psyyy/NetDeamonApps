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
/// at 07:05 emptied the rest, and the house imported at the morning peak (0.50) from 07:28.
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

  static SimulationInput Input(DateTime start, int soc, bool enforce = false, InverterState? mode = null, bool flatPrices = false, int preferred = 25)
  {
    var prices = new List<PriceTableEntry>();
    var load = new Dictionary<DateTime, int>();
    var pv = new Dictionary<DateTime, int>();
    for (var t = Day.AddDays(-1); t < Day.AddDays(3); t = t.AddMinutes(15))
    {
      if (t < Day.AddDays(2)) prices.Add(new PriceTableEntry(t, t.AddMinutes(15), flatPrices ? 0.40f : Price(t.Hour)));
      load[t] = 80;
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
    Assert.NotEmpty(charging);
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
  public void FlatPrices_TheBandIsNotBought()
  {
    // Nothing is cheaper than holding through the low: the band isn't worth a charge (the hard floor still is).
    var slots = EnergySimulator.Simulate(Input(Day.AddHours(1), 20, flatPrices: true)).Slots;
    int morningLow = slots.Where(s => s.Time >= Day.AddHours(5) && s.Time < Day.AddHours(9)).Min(s => s.SoC);
    Assert.InRange(morningLow, 11, 24);
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
    // Flat prices (the band isn't bought): the night drains to ~16 % — below the preferred 25 %, so a planned load
    // isn't OK there; with a preferred minimum of 15 % it is.
    Assert.False(EnergySimulator.Simulate(Input(Day.AddHours(18), 30, flatPrices: true)).IsOvernightMinSocOk());
    Assert.True(EnergySimulator.Simulate(Input(Day.AddHours(18), 30, flatPrices: true, preferred: 15)).IsOvernightMinSocOk());
  }
}
