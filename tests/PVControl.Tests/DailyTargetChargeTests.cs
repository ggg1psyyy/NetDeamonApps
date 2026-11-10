using NetDeamon.apps;
using NetDeamon.apps.PVControl;
using NetDeamon.apps.PVControl.Simulator;
using Xunit;

namespace NetDeamonApps.Tests;

/// <summary>
/// The battery top-up in the greedy simulation (switch.pv_control_force_charge_at_cheapest_period, one-shot): reach the
/// target SoC by its deadline — the ready-by time after arming, else the next start of PV — buying only what PV won't
/// deliver by then, in the cheapest quarters under the price limit (EnergySimulator.DailyTargetChargeSoc,
/// SimulationInput.TopUpDeadline, TopUp.Deadline).
/// </summary>
public class DailyTargetChargeTests : TestBase
{
  static readonly DateTime Day = new(2026, 10, 29);
  const int LoadWh = 60;                                   // 240 W

  /// <param name="pvWhSlot">PV per slot 09:00–15:00 on both days.</param>
  /// <param name="cheapFromHour">Start of the two cheap hours (0.25 €/kWh), else 0.40.</param>
  /// <param name="readyBy">The top-up's ready-by time (default 15:00, the end of the PV); armed at <paramref name="start"/>.</param>
  static SimulationInput Input(DateTime start, int soc, int pvWhSlot, int cheapFromHour, float maxPrice = 0.34f, TimeOnly? readyBy = null,
    bool noReadyBy = false, DateTime? armedAt = null)
  {
    var load = new Dictionary<DateTime, int>();
    var pv = new Dictionary<DateTime, int>();
    var prices = new List<PriceTableEntry>();
    for (var t = Day; t < Day.AddDays(3); t = t.AddMinutes(15))
    {
      load[t] = LoadWh;
      pv[t] = t.Hour is >= 9 and < 15 ? pvWhSlot : 0;
      prices.Add(new PriceTableEntry(t, t.AddMinutes(15), t.Hour >= cheapFromHour && t.Hour < cheapFromHour + 2 ? 0.25f : 0.40f));
    }
    return new SimulationInput
    {
      StartTime = start, StartSocPercent = soc, BatteryCapacityWh = 11_520,
      AbsoluteMinSocPercent = 12, PreferredMinSocPercent = 15, EnforcePreferredSoc = false,
      MaxChargePowerAmps = 30, ChargeVoltageV = 240, InverterEfficiency = 0.95f,
      ImportPrices = prices, ExportPrices = new PriceList(prices.Select(p => new PriceTableEntry(p.StartTime, p.EndTime, 0.08f))),
      LoadPredictionWh = load, PVPredictionWh = pv,
      EnableCheapForceCharge = true, OpportunisticDischarge = false,
      TopUpArmedAt = armedAt ?? start, TopUpReadyBy = noReadyBy ? null : readyBy ?? new TimeOnly(15, 0),
      ForceChargeMaxPrice = maxPrice, ForceChargeTargetSocPercent = 95,
      CurrentMode = new InverterState(InverterModes.normal),
    };
  }

  static List<SimulationSlot> DailyCharges(SimulationResult plan) =>
    plan.Slots.Where(s => s.State is { Mode: InverterModes.force_charge, ModeReason: ForceChargeReasons.ForcedChargeAtMinimumPrice }).ToList();

  [Fact]
  public void PvReachesTheTarget_NothingBought()
  {
    // 800 Wh/slot for 6 h: the battery is full by sunset on its own.
    var plan = EnergySimulator.Simulate(Input(Day.AddHours(6), 40, pvWhSlot: 800, cheapFromHour: 11));
    Assert.Empty(DailyCharges(plan).Where(s => s.Time < Day.AddHours(15)));
    Assert.True(plan.Slots.First(s => s.Time == Day.AddHours(15)).SoC >= 95);
  }

  [Fact]
  public void PvFallsShort_TheRestIsBoughtInTheCheapestQuartersBeforeSunset()
  {
    // 200 Wh/slot: PV alone gets to ~63 % by sunset. The missing ~32 % (3 quarters at 1.8 kWh) is bought
    // in the cheap hours 11:00–13:00 — not at 06:00 at 0.40, not after sunset.
    var plan = EnergySimulator.Simulate(Input(Day.AddHours(6), 40, pvWhSlot: 200, cheapFromHour: 11));
    var charges = DailyCharges(plan).Where(s => s.Time < Day.AddHours(15).AddMinutes(15)).ToList();
    Assert.InRange(charges.Count, 2, 4);
    Assert.All(charges, s => Assert.InRange(s.Time, Day.AddHours(11), Day.AddHours(13)));
    Assert.InRange(plan.Slots.First(s => s.Time == Day.AddHours(15).AddMinutes(15)).SoC, 92, 97);
    Assert.DoesNotContain(plan.Slots, s => s.Time < Day.AddHours(11) && s.State.Mode == InverterModes.force_charge);
  }

  [Fact]
  public void AfterSunset_TomorrowsDeadline_OnlyWhatTomorrowsPvWontDeliver()
  {
    // 20:00, 50 %: by tomorrow's sunset PV gets to ~39 %; the rest is bought in tonight's cheap hours 02:00–04:00.
    var plan = EnergySimulator.Simulate(Input(Day.AddHours(20), 50, pvWhSlot: 200, cheapFromHour: 2));
    var charges = DailyCharges(plan);
    Assert.NotEmpty(charges);
    Assert.All(charges.Where(s => s.Time < Day.AddDays(1).AddHours(15)), s => Assert.InRange(s.Time, Day.AddDays(1).AddHours(2), Day.AddDays(1).AddHours(4)));
    Assert.DoesNotContain(plan.Slots, s => s.Time < Day.AddDays(1).AddHours(2) && s.State.Mode == InverterModes.force_charge);
    Assert.InRange(plan.Slots.First(s => s.Time == Day.AddDays(1).AddHours(15).AddMinutes(15)).SoC, 90, 97);
  }

  [Fact]
  public void AllQuartersAboveTheLimit_NothingBought()
  {
    var plan = EnergySimulator.Simulate(Input(Day.AddHours(6), 40, pvWhSlot: 200, cheapFromHour: 11, maxPrice: 0.20f));
    Assert.Empty(DailyCharges(plan));
  }

  [Fact]
  public void Deadline_TheReadyByAfterArming()
  {
    var input = Input(Day.AddHours(6), 40, pvWhSlot: 200, cheapFromHour: 11);
    Assert.Equal(Day.AddHours(15), input.TopUpDeadline);                                     // armed 06:00, 15:00: today
    Assert.Equal(Day.AddHours(15), input.DailyTargetDeadline(Day.AddHours(6)));
    Assert.Null(input.DailyTargetDeadline(Day.AddHours(15)));                                // passed: nothing more to buy
    var evening = Input(Day.AddHours(16), 40, pvWhSlot: 200, cheapFromHour: 11, readyBy: new TimeOnly(7, 0));
    Assert.Equal(Day.AddDays(1).AddHours(7), evening.TopUpDeadline);                         // armed 16:00, 07:00: tomorrow
    var odd = Input(Day.AddHours(6), 40, pvWhSlot: 200, cheapFromHour: 11, readyBy: new TimeOnly(7, 10));
    Assert.Equal(Day.AddHours(7), odd.TopUpDeadline);                                        // on the quarter grid
    // Armed at 07:03 for 07:10: its quarter 07:00 has passed — tomorrow's, not an instant expiry.
    Assert.Equal(Day.AddDays(1).AddHours(7), TopUp.Deadline(Day.AddHours(7).AddMinutes(3), new TimeOnly(7, 10), _ => null));
  }

  [Fact]
  public void Deadline_NoReadyBy_TheNextStartOfPv()
  {
    // PV 09:00–15:00 both days: armed at 03:00 that morning's, armed at 13:00 (in the PV) tomorrow's.
    Assert.Equal(Day.AddHours(9), Input(Day.AddHours(3), 40, 200, 11, noReadyBy: true).TopUpDeadline);
    Assert.Equal(Day.AddDays(1).AddHours(9), Input(Day.AddHours(13), 40, 200, 11, noReadyBy: true).TopUpDeadline);
    // Armed last evening, the forecast starting today: today's.
    Assert.Equal(Day.AddHours(9), Input(Day.AddHours(5), 40, 200, 11, noReadyBy: true, armedAt: Day.AddHours(-3)).TopUpDeadline);
  }

  [Fact]
  public void NotArmed_NoDeadline_OneShotExpires()
  {
    var input = Input(Day.AddHours(6), 40, 200, 11);
    var off = new SimulationInput
    {
      StartTime = input.StartTime, StartSocPercent = 40, BatteryCapacityWh = 11_520, AbsoluteMinSocPercent = 12, PreferredMinSocPercent = 15,
      EnforcePreferredSoc = false, MaxChargePowerAmps = 30, InverterEfficiency = 0.95f, ImportPrices = input.ImportPrices,
      ExportPrices = input.ExportPrices, LoadPredictionWh = input.LoadPredictionWh, PVPredictionWh = input.PVPredictionWh,
      EnableCheapForceCharge = false, OpportunisticDischarge = false, ForceChargeMaxPrice = 0.34f, ForceChargeTargetSocPercent = 95,
      CurrentMode = new InverterState(InverterModes.normal), TopUpArmedAt = Day.AddHours(6), TopUpReadyBy = new TimeOnly(15, 0),
    };
    Assert.Null(off.TopUpDeadline);
    Assert.False(TopUp.Expired(input.TopUpDeadline, Day.AddHours(14).AddMinutes(59)));
    Assert.True(TopUp.Expired(input.TopUpDeadline, Day.AddHours(15)));
    Assert.False(TopUp.Expired(null, Day.AddHours(15)));
  }
}
