using NetDeamon.apps;
using NetDeamon.apps.PVControl;
using NetDeamon.apps.PVControl.Optimizer;
using NetDeamon.apps.PVControl.Simulator;
using Xunit;

namespace NetDeamonApps.Tests;

/// <summary>
/// The battery top-up in the DP (PlanEvaluation.TopUpGoalOf / TopUpShortfall): the target by the deadline, bought only in
/// quarters at or under the price limit — a rule-strength penalty on a target capped at what those quarters can deliver.
/// A winter night without PV: 0.30 until 02:00, the cheap block 02:00–04:00, 0.30 until 07:00, 0.10 after the deadline
/// (so the DP has no reason of its own to charge before it).
/// </summary>
public class TopUpDpTests : TestBase
{
  static readonly DateTime Day = new(2026, 11, 10);
  static readonly DateTime Deadline = Day.AddDays(1).AddHours(7);

  static SimulationInput Input(bool armed, int target = 90, float limit = 0.25f, int cheapQuarters = 8, int soc = 60)
  {
    var load = new Dictionary<DateTime, int>();
    var pv = new Dictionary<DateTime, int>();
    var prices = new List<PriceTableEntry>();
    var cheapFrom = Day.AddDays(1).AddHours(2);
    for (var t = Day; t < Day.AddDays(3); t = t.AddMinutes(15))
    {
      load[t] = 75;   // 300 W
      pv[t] = 0;
      float p = t >= cheapFrom && t < cheapFrom.AddMinutes(15 * cheapQuarters) ? 0.20f : t >= Deadline ? 0.10f : 0.30f;
      prices.Add(new PriceTableEntry(t, t.AddMinutes(15), p));
    }
    return new SimulationInput
    {
      StartTime = Day.AddHours(20), StartSocPercent = soc, BatteryCapacityWh = 11_520,
      AbsoluteMinSocPercent = 12, PreferredMinSocPercent = 20, EnforcePreferredSoc = false,
      MaxChargePowerAmps = 30, ChargeVoltageV = 240, InverterEfficiency = 0.95f,
      ImportPrices = prices, ExportPrices = new PriceList(prices.Select(p => new PriceTableEntry(p.StartTime, p.EndTime, 0.05f))),
      LoadPredictionWh = load, PVPredictionWh = pv,
      EnableCheapForceCharge = armed, OpportunisticDischarge = false,
      ForceChargeMaxPrice = limit, ForceChargeTargetSocPercent = target,
      TopUpArmedAt = Day.AddHours(19), TopUpReadyBy = new TimeOnly(7, 0),
      CurrentMode = new InverterState(InverterModes.normal),
    };
  }

  static IEnumerable<DpSlot> ChargesBefore(DpPlan p) => p.Slots.Where(s => s.Time < Deadline && s.Mode == InverterModes.force_charge);
  static double SocAt(DpPlan p, SimulationInput i) => Soc.FromWhExact(p.Slots.First(s => s.Time == Deadline).EnergyWh, i.BatteryCapacityWh);

  [Fact]
  public void NotArmed_NoChargeBeforeTheDeadline()
  {
    var plan = BatteryDpPlanner.Plan(Input(armed: false));
    Assert.Empty(ChargesBefore(plan));   // the cheap 0.10 after 07:00 wins
  }

  [Fact]
  public void Armed_TheTargetByTheDeadline_InTheCheapQuarters()
  {
    var input = Input(armed: true);
    var plan = BatteryDpPlanner.Plan(input);
    var charges = ChargesBefore(plan).ToList();
    Assert.NotEmpty(charges);
    Assert.All(charges, s => Assert.InRange(s.Time, Day.AddDays(1).AddHours(2), Day.AddDays(1).AddHours(4).AddMinutes(-15)));
    Assert.InRange(SocAt(plan, input), 89, 92);
    Assert.True(plan.Cost.TopUpShortfall < 0.01f, $"{plan.Cost.TopUpShortfall}");
    Assert.All(charges, s => Assert.Equal(ForceChargeReasons.TopUp, DpSteering.ReasonOf(plan, plan.Slots.ToList().IndexOf(s), input.WithBatteryLosses(), new Dictionary<DateTime, SimulationSlot>())));
  }

  [Fact]
  public void TheLimitIsHard_AllQuartersAboveIt_NothingBought()
  {
    var input = Input(armed: true, limit: 0.15f);
    var plan = BatteryDpPlanner.Plan(input);
    Assert.Empty(ChargesBefore(plan));
    // The goal is capped at the SoC without any charge: the shortfall accepted costs nothing.
    var goal = PlanEvaluation.TopUpGoalOf(input.WithBatteryLosses(), new DpOptions())!.Value;
    Assert.True(Soc.FromWhExact(goal.TargetWh, input.BatteryCapacityWh) < SocAt(plan, input));
    Assert.Equal(0, plan.Cost.TopUpShortfall);
  }

  [Fact]
  public void OnlyTwoCheapQuarters_TheirChargeAndNoMore()
  {
    // Two quarters at 0.20 give ~2 × 6.8 kWh·¼ h ≈ 30 % — 90 % from ~32 % at 02:00 isn't reachable under the limit: the
    // target is capped there, no 0.30 quarter is bought for the rest.
    var input = Input(armed: true, cheapQuarters: 2);
    var plan = BatteryDpPlanner.Plan(input);
    var charges = ChargesBefore(plan).ToList();
    Assert.InRange(charges.Count, 1, 2);
    Assert.All(charges, s => Assert.InRange(s.Time, Day.AddDays(1).AddHours(2), Day.AddDays(1).AddHours(2).AddMinutes(15)));
    var goal = PlanEvaluation.TopUpGoalOf(input.WithBatteryLosses(), new DpOptions())!.Value;
    Assert.True(Soc.FromWhExact(goal.TargetWh, input.BatteryCapacityWh) < 90);
  }

  [Fact]
  public void TargetAlreadyMet_ThePlanAsWithout()
  {
    var without = BatteryDpPlanner.Plan(Input(armed: false));
    var with = BatteryDpPlanner.Plan(Input(armed: true, target: 20));
    Assert.Equal(without.Slots.Select(s => (s.Mode, s.EnergyWh)), with.Slots.Select(s => (s.Mode, s.EnergyWh)));
    Assert.Equal(0, with.Cost.TopUpShortfall);
  }

  [Fact]
  public void TheEvaluation_PricesTheShortfallLikeTheDp()
  {
    // A plan that ignores the top-up (planned unarmed) judged armed: the missing % at the deadline cost at the floor's rate.
    var armed = Input(armed: true).WithBatteryLosses();
    var unarmedPlan = BatteryDpPlanner.Plan(Input(armed: false));
    var o = new DpOptions();
    var cost = PlanEvaluation.Evaluate(armed, unarmedPlan.Slots.Select(s => (s.Time, (double)s.SocPct, s.GridImportWh, s.GridExportWh, s.Mode)),
      unarmedPlan.Slots[^1].EnergyWh, o, t => unarmedPlan.Slots.FirstOrDefault(s => s.Time == t)?.EnergyWh);
    var goal = PlanEvaluation.TopUpGoalOf(armed, o)!.Value;
    double missing = Soc.FromWhExact(goal.TargetWh, armed.BatteryCapacityWh) - SocAt(unarmedPlan, armed);
    Assert.Equal(missing * o.FloorPenaltyPerPctQuarter, cost.TopUpShortfall, 3);
  }
}
