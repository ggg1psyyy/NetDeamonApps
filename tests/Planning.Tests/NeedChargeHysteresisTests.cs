using NetDeamon.apps;
using NetDeamon.apps.PVControl;
using NetDeamon.apps.PVControl.Simulator;
using Xunit;

namespace NetDeamonApps.Tests;

/// <summary>
/// A running NeedToCharge charge never stops where starting one would begin it. The larger margin
/// while charging moved the latest charge time and with it the price window; in that window the
/// running quarter lost, so each plan flipped the next one: force_charge and normal alternated
/// every 15-s cycle (live 2026-10-25 11:30, 2026-10-22 09:00).
/// </summary>
public class NeedChargeHysteresisTests : TestBase
{
  static readonly DateTime Day = new(2026, 10, 25);
  // Gross import €/kWh 2026-10-25 per hour.
  static readonly float[] Gross = [0.349f, 0.344f, 0.348f, 0.348f, 0.368f, 0.377f, 0.365f, 0.355f, 0.328f, 0.322f, 0.350f, 0.353f,
    0.354f, 0.361f, 0.395f, 0.423f, 0.433f, 0.420f, 0.419f, 0.420f, 0.412f, 0.403f, 0.361f, 0.356f];

  static SimulationInput Input(DateTime start, int soc, int nightWh, int pvWh, bool enforce, InverterState mode)
  {
    var load = new Dictionary<DateTime, int>();
    var pv = new Dictionary<DateTime, int>();
    for (var t = Day.AddDays(-1); t < Day.AddDays(3); t = t.AddMinutes(15))
    {
      load[t] = t.Hour is >= 22 or < 7 ? nightWh : nightWh + 30;
      pv[t] = t.Hour is >= 9 and < 16 ? pvWh : 0;
    }
    var prices = new List<PriceTableEntry>();
    for (var t = Day; t < Day.AddDays(1); t = t.AddHours(1))
      prices.Add(new PriceTableEntry(t, t.AddHours(1), Gross[t.Hour]));
    return new SimulationInput
    {
      StartTime = start, StartSocPercent = soc, BatteryCapacityWh = 11_520,
      AbsoluteMinSocPercent = 12, PreferredMinSocPercent = 30, EnforcePreferredSoc = enforce,
      MaxChargePowerAmps = 30, ChargeVoltageV = 240, InverterEfficiency = 0.95f,
      ImportPrices = new PriceList(prices).WithEstimatedPrices(Day.AddDays(2), 0.05f),
      ExportPrices = new PriceList(prices.Select(p => new PriceTableEntry(p.StartTime, p.EndTime, 0.08f))),
      PublishedPricesUntil = Day.AddDays(1), LoadPredictionWh = load, PVPredictionWh = pv,
      EnableCheapForceCharge = false, OpportunisticDischarge = false,
      ForceChargeMaxPrice = 0.35f, ForceChargeTargetSocPercent = 100, CurrentMode = mode, MaxGridImportW = 9800,
    };
  }

  static readonly InverterState Idle = new(InverterModes.normal, ForceChargeReasons.None);

  /// <summary>Whether the plan started from its own first slot's mode keeps that mode (no flip-flop).</summary>
  static bool Stable(DateTime start, int soc, int night, int pv, bool enforce, out string modes)
  {
    var a = EnergySimulator.Simulate(Input(start, soc, night, pv, enforce, Idle));
    var b = EnergySimulator.Simulate(Input(start, soc, night, pv, enforce, a[0].State));
    modes = $"normal → {a[0].State.Mode}, then {a[0].State.Mode} → {b[0].State.Mode}";
    return a[0].State.Mode != InverterModes.force_charge || b[0].State.Mode == InverterModes.force_charge;
  }

  [Fact]
  public void NoGridChargeBeforeAPvPeakThatFillsTheBattery()
  {
    // Live 2026-10-25 11:45: 95 %, PV fills the battery by 12:15, the low (floor + margin) only
    // tomorrow 07:00 — the plan bought a few % at 35 ct right before PV filled the battery anyway.
    var load = new Dictionary<DateTime, int>();
    var pv = new Dictionary<DateTime, int>();
    for (var t = Day.AddDays(-1); t < Day.AddDays(3); t = t.AddMinutes(15))
    {
      load[t] = 137;                                                     // ~550 W, heating on
      pv[t] = t.Date == Day && t.Hour is >= 11 and < 13 ? 200 : 0;       // ~0.8 kW around noon: the peak stays just under 100 %
    }
    var prices = new List<PriceTableEntry>();
    for (var t = Day; t < Day.AddDays(1); t = t.AddHours(1))
      prices.Add(new PriceTableEntry(t, t.AddHours(1), Gross[t.Hour]));
    var input = new SimulationInput
    {
      StartTime = Day.AddHours(11).AddMinutes(45), StartSocPercent = 95, BatteryCapacityWh = 11_520,
      AbsoluteMinSocPercent = 12, PreferredMinSocPercent = 30, EnforcePreferredSoc = false,
      MaxChargePowerAmps = 30, ChargeVoltageV = 240, InverterEfficiency = 0.95f,
      ImportPrices = new PriceList(prices).WithEstimatedPrices(Day.AddDays(2), 0.05f),
      ExportPrices = new PriceList(prices.Select(p => new PriceTableEntry(p.StartTime, p.EndTime, 0.08f))),
      PublishedPricesUntil = Day.AddDays(1), LoadPredictionWh = load, PVPredictionWh = pv,
      EnableCheapForceCharge = false, OpportunisticDischarge = false,
      ForceChargeMaxPrice = 0.35f, ForceChargeTargetSocPercent = 100, CurrentMode = Idle, MaxGridImportW = 9800,
    };

    var result = EnergySimulator.Simulate(input);

    int peak = result.Where(x => x.Time < Day.AddHours(14)).Max(x => x.SoC);
    var peakTime = result.First(s => s.SoC == peak).Time;
    Assert.DoesNotContain(result, s => s.Time <= peakTime && s.State.Mode == InverterModes.force_charge);
    // The deficit is handled after the peak (charge or floor hold before the low).
    Assert.Contains(result, s => s.Time > peakTime && s.State.Mode is InverterModes.force_charge or InverterModes.grid_only);
  }

  [Fact]
  public void RunningCharge_ContinuesWhereStartingWouldBegin()
  {
    Assert.True(Stable(Day.AddHours(12).AddMinutes(30).AddSeconds(30), 70, 100, 300, true, out string modes), modes);
  }

  [Fact]
  public void NoFlipFlop_AcrossSocLoadsAndPv()
  {
    foreach (int hour in new[] { 9, 11, 12, 13 })
    foreach (int soc in new[] { 20, 40, 60, 70, 90 })
    foreach (int night in new[] { 100, 200, 300 })
    foreach (int pv in new[] { 0, 300, 600 })
    foreach (bool enforce in new[] { false, true })
      Assert.True(Stable(Day.AddHours(hour).AddMinutes(30).AddSeconds(30), soc, night, pv, enforce, out string modes),
        $"{hour}:30 SoC {soc} night {night} pv {pv} enforce {enforce}: {modes}");
  }
}
