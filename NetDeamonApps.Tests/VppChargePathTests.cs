using NetDeamon.apps;
using NetDeamon.apps.PVControl;
using NetDeamon.apps.PVControl.Managers;
using NetDeamon.apps.PVControl.Simulator;
using Xunit;

namespace NetDeamonApps.Tests;

/// <summary>
/// Remote-controlled (VPP) grid charging is limited by the inverter, falling with the SoC (SolaX G4,
/// 2026-10-26/27: 5.07 kW at 21 %, 4.64 at 33 %, 3.95 at 48 %, 3.33 at 63 %; Manual Force Charge
/// ~7.45 kW up to 89 %, BMS limit 35 A). <see cref="VppChargeCurve"/> learns it; the simulator charges a
/// grid charge on VPP when that path puts the energy in within the quarters of the same price —
/// with hourly prices up to the rest of the hour, with quarter-hourly ones usually one quarter — else
/// at full power (bulk), and models the battery at the path's limit, PV included.
/// </summary>
public class VppChargePathTests
{
  static readonly VppChargeCurve Measured = new(5950, -41);

  [Fact]
  public void Curve_DefaultLine_MatchesTheMeasurements()
  {
    Assert.InRange(Measured.MaxW(21), 5000, 5150);
    Assert.InRange(Measured.MaxW(63), 3300, 3450);
    Assert.False(Measured.Line.Learned);
    Assert.Equal(VppChargeCurve.MinW, new VppChargeCurve(1000, -100).MaxW(90));
  }

  [Fact]
  public void Curve_LearnsALineOnceTheBucketsSpanEnoughSoC()
  {
    var curve = new VppChargeCurve(5950, -41);
    for (int i = 0; i < 20; i++) curve.Observe(31, 4000);   // only one bucket: still the default
    Assert.False(curve.Line.Learned);
    for (int i = 0; i < 20; i++) curve.Observe(61, 2800);
    Assert.True(curve.Line.Learned);
    Assert.InRange(curve.MaxW(46), 3300, 3500);              // between the two learned points
    Assert.InRange(curve.Line.SlopeWPerPct, -45, -35);
  }

  [Fact]
  public void Curve_TooFewReadingsOrTooNarrowASpan_StaysOnTheDefault()
  {
    // Fewer than MinReadings in a bucket, or buckets spanning less than MinSpanPct: no line is fitted.
    var few = new VppChargeCurve(5950, -41);
    for (int i = 0; i < VppChargeCurve.MinReadings - 1; i++) { few.Observe(31, 4000); few.Observe(61, 2800); }
    Assert.False(few.Line.Learned);
    few.Observe(31, 4000); few.Observe(61, 2800);
    Assert.True(few.Line.Learned);

    var narrow = new VppChargeCurve(5950, -41);
    for (int i = 0; i < 20; i++) { narrow.Observe(31, 4000); narrow.Observe(41, 3600); } // buckets 30 and 40: a 10 % span
    Assert.False(narrow.Line.Learned);
    Assert.Equal(5950 - 41 * 46, narrow.MaxW(46));
  }

  [Fact]
  public void Curve_SerializeRestore_RoundTrip()
  {
    var curve = new VppChargeCurve(5950, -41);
    for (int i = 0; i < 10; i++) { curve.Observe(22, 5000); curve.Observe(52, 3800); }
    var restored = new VppChargeCurve(5950, -41);
    restored.Restore(curve.Serialize() + ";garbage;7:x:1");
    Assert.Equal(curve.MaxW(40), restored.MaxW(40));
    Assert.True(restored.Line.Learned);
  }

  static readonly DateTime Night = new(2026, 10, 28, 0, 0, 0);

  /// <summary>Night prices: hourly (four equal quarters per hour) or quarter-hourly (each quarter its own price).</summary>
  static SimulationInput Input(bool hourly, DateTime start, int soc, bool vpp = true, int capW = 0, int loadWh = 110)
  {
    var prices = new List<PriceTableEntry>();
    for (var t = Night.AddDays(-1); t < Night.AddDays(2); t = t.AddMinutes(15))
    {
      float price = hourly ? 0.37f + 0.01f * (t.Hour % 6) : 0.37f + 0.002f * ((t.Hour * 4 + t.Minute / 15) % 13);
      prices.Add(new PriceTableEntry(t, t.AddMinutes(15), price));
    }
    var load = new Dictionary<DateTime, int>();
    var pv = new Dictionary<DateTime, int>();
    for (var t = Night.AddDays(-1); t < Night.AddDays(3); t = t.AddMinutes(15))
    {
      load[t] = loadWh;
      pv[t] = t.Hour is >= 10 and < 15 ? 600 : 0;
    }
    return new SimulationInput
    {
      StartTime = start, StartSocPercent = soc, BatteryCapacityWh = 11_520,
      AbsoluteMinSocPercent = 12, PreferredMinSocPercent = 25, EnforcePreferredSoc = false,
      MaxChargePowerAmps = 30, InverterEfficiency = 0.95f, ChargeVoltageV = 245,
      ImportPrices = new PriceList(prices), ExportPrices = new PriceList(prices.Select(p => new PriceTableEntry(p.StartTime, p.EndTime, 0.17f))),
      PublishedPricesUntil = Night.AddDays(2),
      LoadPredictionWh = load, PVPredictionWh = pv,
      EnableCheapForceCharge = false, OpportunisticDischarge = false,
      ForceChargeMaxPrice = 0.35f, ForceChargeTargetSocPercent = 95,
      CurrentMode = new InverterState(InverterModes.normal),
      VppChargePowerW = vpp ? Measured.MaxW : null,
      MaxGridImportW = capW,
    };
  }

  [Fact]
  public void ChargesBulk_HourlyPrices_VppSpreadsOverTheHour()
  {
    // 40 % → 55 % = 1.73 kWh; VPP at 40 % ≈ 4.3 kW → 1.08 kWh per quarter: two quarters of the hour.
    Assert.False(EnergySimulator.ChargesBulk(Input(hourly: true, Night, 40), Night, 40, 55));
  }

  [Fact]
  public void ChargesBulk_QuarterHourlyPrices_OneCheapQuarterNeedsFullPower()
  {
    // The same energy in the one quarter at that price: only bulk (7.35 kW = 1.84 kWh) does it.
    Assert.True(EnergySimulator.ChargesBulk(Input(hourly: false, Night, 40), Night, 40, 55));
    // A small top-up fits into one quarter on VPP too.
    Assert.False(EnergySimulator.ChargesBulk(Input(hourly: false, Night, 40), Night, 40, 47));
  }

  [Fact]
  public void ChargesBulk_LateInTheHour_FewerEqualQuartersLeft()
  {
    Assert.True(EnergySimulator.ChargesBulk(Input(hourly: true, Night.AddMinutes(45), 40), Night.AddMinutes(45), 40, 55));
  }

  [Fact]
  public void ChargesBulk_CapLeavesBulkNoMoreThanVpp_Vpp()
  {
    // One cheap quarter, 25 % → 55 %: VPP (~4.9 kW at 25 %) can't, full bulk could. With the house drawing 5.4 kW
    // (EV + heat pump) the cap leaves ~4.4 kW: bulk would charge no faster than VPP — only the EEPROM writes.
    Assert.True(EnergySimulator.ChargesBulk(Input(hourly: false, Night, 25, loadWh: 1350), Night, 25, 55));
    Assert.False(EnergySimulator.ChargesBulk(Input(hourly: false, Night, 25, capW: 9800, loadWh: 1350), Night, 25, 55));
  }

  [Fact]
  public void ChargesBulk_VppFallsBelowTheCapsRoom_OverTheHour_Bulk()
  {
    // An hour at one price, 40 % → 80 % (4.6 kWh): VPP falls from ~4.3 kW with the SoC and gets ~4.1 kWh in; the cap
    // leaves bulk 5.8 kW (house 4 kW) every quarter, 5.8 kWh — more than VPP, so bulk at the cap's current.
    Assert.True(EnergySimulator.ChargesBulk(Input(hourly: true, Night, 40, capW: 9800, loadWh: 1000), Night, 40, 80));
  }

  [Fact]
  public void ChargesBulk_WithoutVppCurve_NeverBulk() =>
    Assert.False(EnergySimulator.ChargesBulk(Input(hourly: false, Night, 40, vpp: false), Night, 40, 90));

  /// <summary>A night that needs a charge (20 % at midnight, floor 12 %, PV from 10:00) with one cheap hour
  /// (hourly prices) or one cheap quarter (quarter-hourly), the morning expensive.</summary>
  static SimulationInput NightCharge(bool hourly)
  {
    var i = Input(hourly, Night, 20);
    var prices = i.ImportPrices.Select(p => new PriceTableEntry(p.StartTime, p.EndTime,
      p.StartTime >= Night && p.StartTime < Night.AddMinutes(hourly ? 60 : 15) ? 0.30f
      : p.StartTime >= Night && p.StartTime < Night.AddHours(1) ? 0.33f + 0.01f * (p.StartTime.Minute / 15) : 0.45f)).ToList();
    return new SimulationInput
    {
      StartTime = i.StartTime, StartSocPercent = i.StartSocPercent, BatteryCapacityWh = i.BatteryCapacityWh,
      AbsoluteMinSocPercent = i.AbsoluteMinSocPercent, PreferredMinSocPercent = i.PreferredMinSocPercent, EnforcePreferredSoc = i.EnforcePreferredSoc,
      MaxChargePowerAmps = i.MaxChargePowerAmps, InverterEfficiency = i.InverterEfficiency, ChargeVoltageV = i.ChargeVoltageV,
      ImportPrices = new PriceList(prices), ExportPrices = i.ExportPrices, PublishedPricesUntil = i.PublishedPricesUntil,
      LoadPredictionWh = i.LoadPredictionWh, PVPredictionWh = i.PVPredictionWh,
      EnableCheapForceCharge = false, OpportunisticDischarge = false,
      ForceChargeMaxPrice = i.ForceChargeMaxPrice, ForceChargeTargetSocPercent = i.ForceChargeTargetSocPercent,
      CurrentMode = i.CurrentMode, VppChargePowerW = i.VppChargePowerW,
    };
  }

  [Fact]
  public void Simulate_HourlyPrices_VppChargeOverTheHour_AtTheCurve()
  {
    var result = EnergySimulator.Simulate(NightCharge(hourly: true));
    var charge = result.Slots.Where(s => s.State.Mode == InverterModes.force_charge).ToList();
    Assert.NotEmpty(charge);
    Assert.All(charge, s =>
    {
      Assert.False(s.State.Bulk);
      Assert.True(s.BatteryChargeWh <= Measured.MaxW(s.SoC) / 4 + 1, $"{s.Time:HH:mm} {s.BatteryChargeWh} Wh at {s.SoC} %");
    });
    Assert.True(charge.Count >= 2, "spread over the cheap hour");
  }

  [Fact]
  public void Simulate_QuarterHourlyPrices_OneCheapQuarter_BulkAtFullPower()
  {
    var first = EnergySimulator.Simulate(NightCharge(hourly: false)).Slots[0];
    Assert.Equal(InverterModes.force_charge, first.State.Mode);
    Assert.True(first.State.Bulk);
    Assert.InRange(first.BatteryChargeWh, 1700, 1900);   // 30 A × 245 V for a quarter
  }
}

public class ChargeStartAlignmentTests
{
  static readonly DateTime Night = new(2026, 10, 28, 0, 0, 0);

  /// <summary>Planned from 22:00: 00:00–01:00 at 0.34 (under the limit), 01:00–02:00 at 0.33, the rest 0.45 — hourly prices.</summary>
  public static SimulationInput Input(int loadWhPerSlot, int soc)
  {
    var prices = new List<PriceTableEntry>();
    for (var t = Night.AddDays(-1); t < Night.AddDays(2); t = t.AddHours(1))
      prices.Add(new PriceTableEntry(t, t.AddHours(1), t == Night ? 0.34f : t == Night.AddHours(1) ? 0.33f : 0.45f));
    var load = new Dictionary<DateTime, int>();
    var pv = new Dictionary<DateTime, int>();
    for (var t = Night.AddDays(-1); t < Night.AddDays(3); t = t.AddMinutes(15))
    {
      load[t] = loadWhPerSlot;
      pv[t] = t.Hour is >= 10 and < 15 ? 1500 : 0;
    }
    return new SimulationInput
    {
      StartTime = Night.AddHours(-2), StartSocPercent = soc, BatteryCapacityWh = 11_520,
      AbsoluteMinSocPercent = 12, PreferredMinSocPercent = 25, EnforcePreferredSoc = false,
      MaxChargePowerAmps = 30, InverterEfficiency = 0.95f, ChargeVoltageV = 245,
      ImportPrices = new PriceList(prices), ExportPrices = new PriceList(prices.Select(p => new PriceTableEntry(p.StartTime, p.EndTime, 0.17f))),
      PublishedPricesUntil = Night.AddDays(2),
      LoadPredictionWh = load, PVPredictionWh = pv,
      EnableCheapForceCharge = false, OpportunisticDischarge = false,
      ForceChargeMaxPrice = 0.35f, ForceChargeTargetSocPercent = 95,
      CurrentMode = new InverterState(InverterModes.normal),
    };
  }

  static SimulationInput WithVpp(SimulationInput b) => new()
  {
    StartTime = b.StartTime, StartSocPercent = b.StartSocPercent, BatteryCapacityWh = b.BatteryCapacityWh,
    AbsoluteMinSocPercent = b.AbsoluteMinSocPercent, PreferredMinSocPercent = b.PreferredMinSocPercent, EnforcePreferredSoc = b.EnforcePreferredSoc,
    MaxChargePowerAmps = b.MaxChargePowerAmps, InverterEfficiency = b.InverterEfficiency, ChargeVoltageV = b.ChargeVoltageV,
    ImportPrices = b.ImportPrices, ExportPrices = b.ExportPrices, PublishedPricesUntil = b.PublishedPricesUntil,
    LoadPredictionWh = b.LoadPredictionWh, PVPredictionWh = b.PVPredictionWh,
    EnableCheapForceCharge = false, OpportunisticDischarge = false, ForceChargeMaxPrice = b.ForceChargeMaxPrice,
    ForceChargeTargetSocPercent = b.ForceChargeTargetSocPercent, CurrentMode = b.CurrentMode,
    VppChargePowerW = new VppChargeCurve(5950, -41).MaxW,
  };

  static IEnumerable<SimulationSlot> Starts(SimulationResult r) =>
    r.Slots.Where((s, i) => s.State.Mode == InverterModes.force_charge && i > 0 && r.Slots[i - 1].State.Mode != InverterModes.force_charge);

  [Fact]
  public void WithoutVppCurve_TheNaturalStartStays()
  {
    // ~690 W all night from 25 %: the quarters-needed rule starts at 00:15 — later in the block is fine.
    Assert.Equal(Night.AddMinutes(15), Starts(EnergySimulator.Simulate(Input(172, 25))).First().Time);
  }

  // 00:00–01:00 at one price, 38 % at 00:00 losing 2 % per quarter. VPP from 00:00 reaches up to
  // ~68 %, from 00:15 up to ~60 % (EnergySimulator.ChargesBulk with the measured curve).
  static readonly Dictionary<DateTime, int> Drain = new()
  {
    [Night] = 38, [Night.AddMinutes(15)] = 36, [Night.AddMinutes(30)] = 34, [Night.AddMinutes(45)] = 32, [Night.AddHours(1)] = 30,
  };

  [Theory]
  [InlineData(60, 0, false)]   // VPP still manages it from 00:15: a later start is fine
  [InlineData(66, 0, true)]    // from 00:15 only bulk, from 00:00 VPP: start now on VPP
  [InlineData(76, 0, false)]   // bulk either way: the natural (later) start stays
  [InlineData(66, 4, false)]   // four cheaper quarters cover the deficit: this block doesn't charge at all
  public void VppNowBeatsBulkLater_EarlierVppOverLaterBulk(int targetSoc, int cheaperUnderMax, bool expected) =>
    Assert.Equal(expected, EnergySimulator.VppNowBeatsBulkLater(WithVpp(Input(200, 38)), Night, 38, targetSoc, Drain, cheaperUnderMax, 1838));

  [Fact]
  public void VppNowBeatsBulkLater_LastQuarterOfTheBlockOrNoCurve_Never()
  {
    Assert.False(EnergySimulator.VppNowBeatsBulkLater(WithVpp(Input(200, 38)), Night.AddMinutes(45), 32, 66, Drain, 0, 1838));
    Assert.False(EnergySimulator.VppNowBeatsBulkLater(Input(200, 38), Night, 38, 66, Drain, 0, 1838));
  }

}
