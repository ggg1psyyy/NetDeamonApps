using NetDeamon.apps;
using NetDeamon.apps.PVControl;
using NetDeamon.apps.PVControl.Simulator;
using Xunit;

namespace NetDeamonApps.Tests;

/// <summary>
/// Live 2026-10-22: the 02:00 grid charge (0.32 €/kWh) lifted the forecast morning low to
/// floor + 5 %, the night used ~340 W instead of the forecast ~260 W, and at 07:45 PVControl
/// topped the battery up at 7.4 kW for 0.426 €/kWh — the only quarters left before PV takes over
/// (~09:30). Fixes: the night charge gets a forecast-error margin (+1 % per 2 h until the low,
/// max 4 %), and a charge only runs where it's cheaper than the hours it bridges (round trip
/// included) — otherwise grid_only holds the floor and buys just the load.
/// Inputs: the day's real gross prices, the load forecast from energy_history.db (10-day
/// weighted average without EV and warm water), battery 11.52 kWh, floor 12 % (10 % + 2).
/// </summary>
public class MorningTopUpTests
{
  static readonly DateTime Day = new(2026, 10, 22);

  // EPEX netto €/kWh 2026-10-22 (hourly), reused for the 23rd.
  static readonly float[] Netto = [0.194f, 0.187f, 0.186f, 0.190f, 0.185f, 0.196f, 0.241f, 0.263f, 0.272f, 0.269f, 0.233f, 0.207f,
    0.198f, 0.194f, 0.185f, 0.202f, 0.213f, 0.260f, 0.276f, 0.272f, 0.246f, 0.225f, 0.233f, 0.211f];

  // Load forecast Wh per quarter for each hour (weighted 10-day average from the DB).
  static readonly int[] LoadPerSlot = [71, 65, 65, 65, 66, 67, 69, 64, 76, 105, 104, 104, 101, 98, 102, 100, 96, 94, 98, 114, 109, 111, 104, 83];

  /// <summary>Gross import price as PVControl computes it: (netto + 0.012 + network) × 1.2,
  /// network 0.06882 in WiNAP hours (22–04), else 0.07992 (07:00 → 0.426, 02:00 → 0.320).</summary>
  static float Gross(int hour) =>
    (Netto[hour] + 0.012f + (hour >= 22 || hour < 4 ? 0.06882f : 0.07992f)) * 1.2f;

  /// <summary>Late-October PV forecast: nothing before 08:00, beats the morning load ~09:30,
  /// ~1.5 kW at noon, gone by 18:00.</summary>
  static int Pv(DateTime t)
  {
    double h = t.Hour + t.Minute / 60.0;
    if (h < 8 || h > 18) return 0;
    double s = Math.Sin(Math.PI * (h - 8) / 10);
    return (int)(375 * s * s);
  }

  static SimulationInput Input(DateTime start, int soc, InverterModes mode = InverterModes.normal)
  {
    var load = new Dictionary<DateTime, int>();
    var pv = new Dictionary<DateTime, int>();
    var prices = new List<PriceTableEntry>();
    for (var t = Day.AddDays(-1); t < Day.AddDays(3); t = t.AddMinutes(15))
    {
      load[t] = LoadPerSlot[t.Hour];
      pv[t] = Pv(t);
    }
    for (var t = Day.AddDays(-1); t < Day.AddDays(3); t = t.AddHours(1))
      prices.Add(new PriceTableEntry(t, t.AddHours(1), Gross(t.Hour)));
    return new SimulationInput
    {
      StartTime = start,
      StartSocPercent = soc,
      BatteryCapacityWh = 11_520,
      AbsoluteMinSocPercent = 12,
      PreferredMinSocPercent = 12,
      EnforcePreferredSoc = false,
      MaxChargePowerAmps = 30,
      InverterEfficiency = 0.95f,
      ImportPrices = prices,
      ExportPrices = prices,
      LoadPredictionWh = load,
      PVPredictionWh = pv,
      EnableCheapForceCharge = false,
      OpportunisticDischarge = false,
      ForceChargeMaxPrice = 0.25f,
      ForceChargeTargetSocPercent = 100,
      CurrentMode = new InverterState(mode),
    };
  }

  static string Modes(IEnumerable<SimulationSlot> slots) =>
    string.Join(", ", slots.Select(s => $"{s.Time:HH:mm} {s.State.Mode} {s.SoC}%"));

  [Fact]
  public void Morning0720_OnlyPeakQuartersLeft_NoTopUpCharge()
  {
    var slots = EnergySimulator.Simulate(Input(Day.AddHours(7).AddMinutes(20), 17));
    var beforePv = slots.Where(s => s.Time < Day.AddHours(10)).ToList();
    Assert.True(beforePv.All(s => s.State.Mode != InverterModes.force_charge), Modes(beforePv));
    // ...but the floor still holds (grid_only buys the load instead).
    Assert.True(beforePv.All(s => s.SoC >= 11), Modes(beforePv));
  }

  [Fact]
  public void Night0130_ChargesInTheCheapNightHours_WithMarginForTheForecast()
  {
    var slots = EnergySimulator.Simulate(Input(Day.AddHours(1).AddMinutes(30), 20));
    var night = slots.Where(s => s.Time < Day.AddHours(6)).ToList();
    Assert.Contains(night, s => s.State.Mode == InverterModes.force_charge);
    Assert.True(night.Where(s => s.State.Mode == InverterModes.force_charge).All(s => Gross(s.Time.Hour) < 0.33f), Modes(night));
    // Morning low: floor 12 + hysteresis 5 + forecast margin (low ~09:15, charge ~02:00–04:00 → +3).
    int morningLow = slots.Where(s => s.Time >= Day.AddHours(6) && s.Time < Day.AddHours(10)).Min(s => s.SoC);
    Assert.True(morningLow >= 19, $"morning low {morningLow}%: {Modes(slots.Take(40))}");
  }

  [Theory]
  [InlineData(0, 0)]
  [InlineData(0.9, 0)]
  [InlineData(1, 1)]   // +1 % per 2 h, rounded
  [InlineData(3, 2)]
  [InlineData(7, 4)]   // 02:00 → 09:00
  [InlineData(24, 4)]  // capped
  public void ForecastMargin_OnePercentPerTwoHours_Max4(double hours, int expected) =>
    Assert.Equal(expected, EnergySimulator.ForecastMarginPct(TimeSpan.FromHours(hours)));
}
