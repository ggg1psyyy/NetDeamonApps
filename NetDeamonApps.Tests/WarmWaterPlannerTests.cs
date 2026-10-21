using NetDeamon.apps;
using NetDeamon.apps.PVControl;
using NetDeamon.apps.PVControl.Simulator;
using NetDeamon.apps.PVControl.WarmWater;
using Xunit;

namespace NetDeamonApps.Tests;

/// <summary>
/// WarmWaterPlanner: the dynamic program that picks the heating slots, and the per-slot heating
/// prices from the house simulation. Tank model = the measured default (17.3 K/h heating,
/// 0.79 K/h loss at 42 °C, 1.56 K/h at 55 °C, ~1.7 kW).
/// </summary>
public class WarmWaterPlannerTests : TestBase
{
  static readonly DateTime Start = new(2026, 10, 20, 10, 0, 0);
  static readonly DateTime Evening = new(2026, 10, 20, 18, 0, 0);
  static readonly List<ComfortWindow> EveningWindow = [new(Evening, Evening.AddHours(1), 43)];

  static PlanRequest Request(double temp, Func<DateTime, float> price, IReadOnlyList<ComfortWindow> comfort,
    DateTime? now = null, double? forced = null, int slotCount = 56)
  {
    var slots = Enumerable.Range(0, slotCount).Select(i => Start.AddMinutes(15 * i)).ToList();
    return new PlanRequest(now ?? Start, temp, slots, slots.Select(price).ToList(), TankModel.Default, comfort, 57, forced);
  }

  static double TempAt(WarmWaterPlan plan, DateTime t) => plan.Temps.First(x => x.Time == t).Temp;

  static void AssertWindowMet(WarmWaterPlan plan, ComfortWindow w)
  {
    Assert.Equal(0, plan.ComfortDeficitK);
    Assert.All(plan.Temps.Where(x => x.Time >= w.Start && x.Time <= w.End), x => Assert.True(x.Temp >= w.MinTemp, $"{x.Time:HH:mm}: {x.Temp:F2} °C"));
  }

  [Fact]
  public void NoComfortWindow_NeverHeats()
  {
    var plan = WarmWaterPlanner.Plan(Request(40, _ => 0.30f, []));
    Assert.Empty(plan.Runs);
    Assert.Equal(0, plan.CostEur);
  }

  [Fact]
  public void FlatPrice_HeatsJustBeforeTheWindow()
  {
    // Heating early only adds standby loss, so the cheapest run ends right at 18:00.
    var plan = WarmWaterPlanner.Plan(Request(40, _ => 0.30f, EveningWindow));

    var run = Assert.Single(plan.Runs);
    Assert.True(run.Start >= Evening.AddHours(-1.5), $"run starts {run.Start:HH:mm}");
    Assert.True(run.End <= Evening);
    AssertWindowMet(plan, EveningWindow[0]);
    // ...and only as warm as needed: the window's minimum plus the loss until it ends.
    Assert.InRange(TempAt(plan, Evening), 43, 44.5);
  }

  [Fact]
  public void CheapMidday_PreheatsHigherInTheCheapSlots()
  {
    static float Price(DateTime t) => t.Hour is 12 or 13 ? 0.10f : 0.40f;
    var plan = WarmWaterPlanner.Plan(Request(40, Price, EveningWindow));

    var run = Assert.Single(plan.Runs);
    Assert.True(run.Start.Hour is 12 or 13 && run.End <= Start.Date.AddHours(14), $"run {run.Start:HH:mm}–{run.End:HH:mm}");
    // It has to cover four hours of loss at a higher temperature on top.
    Assert.True(run.TargetTemp > 47, $"target {run.TargetTemp:F1}");
    AssertWindowMet(plan, EveningWindow[0]);
  }

  [Fact]
  public void SlightlyCheaperMidday_NotWorthTheExtraLoss()
  {
    // 7 % cheaper doesn't pay for heating ~2 K more and keeping it warm for 4 hours.
    static float Price(DateTime t) => t.Hour is 12 or 13 ? 0.28f : 0.30f;
    var plan = WarmWaterPlanner.Plan(Request(40, Price, EveningWindow));

    var run = Assert.Single(plan.Runs);
    Assert.True(run.Start >= Evening.AddHours(-1.5), $"run starts {run.Start:HH:mm}");
  }

  [Fact]
  public void VeryCheapNow_StopsAtMaxTemp()
  {
    // Free now, very expensive later, and the evening needs 50 °C (more than 57 °C at 11:00 still
    // has by then): heat as far as possible now — but never above 57 °C.
    static float Price(DateTime t) => t < Start.AddHours(1) ? 0f : 2f;
    var plan = WarmWaterPlanner.Plan(Request(40, Price, [EveningWindow[0] with { MinTemp = 50 }]));

    // Up to the heating-level step (~0.4 K) below the maximum — the last tenth of a K loses more
    // than it saves.
    Assert.InRange(plan.Runs[0].TargetTemp, 56.5, 57);
    Assert.True(plan.Runs[0].End <= Start.AddHours(1));
    Assert.All(plan.Temps, t => Assert.True(t.Temp <= 57));
    Assert.All(plan.Runs, r => Assert.True(r.TargetTemp <= 57));
  }

  [Fact]
  public void RunInProgress_ContinuesToItsTarget()
  {
    var plan = WarmWaterPlanner.Plan(Request(41, _ => 0.30f, [], forced: 50));

    var run = Assert.Single(plan.Runs);
    Assert.Equal(Start, run.Start);
    Assert.True(run.TargetTemp >= 50 - WarmWaterPlanner.Resolution);
  }

  [Fact]
  public void RunningHeating_ExpensiveNow_StillFinishedFirst()
  {
    // Live 2026-10-20 16:26: a running heating was stopped after 3 min because a replan found
    // later hours cheaper. A running heating is forced to its setpoint, whatever the prices.
    static float Price(DateTime t) => t.Hour is 16 or 17 ? 0.10f : 0.40f;
    var plan = WarmWaterPlanner.Plan(Request(41, Price, EveningWindow, forced: 47.5));

    Assert.Equal(Start, plan.Runs[0].Start);
    Assert.InRange(plan.Runs[0].TargetTemp, 47.4, 47.5);
  }

  [Fact]
  public void WindowAlreadyOpenAndTankCold_HeatsNowAndReportsTheDeficit()
  {
    var now = Start.AddMinutes(7);
    var openWindow = new ComfortWindow(Start.AddMinutes(-30), Start.AddMinutes(30), 43);
    var plan = WarmWaterPlanner.Plan(Request(35, _ => 0.30f, [openWindow], now: now));

    Assert.Equal(now, plan.Runs[0].Start);
    Assert.Equal(now, plan.Temps[0].Time);
    Assert.True(plan.ComfortDeficitK >= 8 - 0.01);
  }

  [Fact]
  public void Automatic_HeatsFromSetpointMinusHysteresisToSetpoint()
  {
    var plan = WarmWaterPlanner.PredictAutomatic(Request(42, _ => 0.30f, []), 45, 5, heatingNow: false);

    Assert.NotEmpty(plan.Runs);
    Assert.All(plan.Runs, r =>
    {
      Assert.InRange(r.StartTemp, 39.5, 40.01);
      Assert.Equal(45, r.TargetTemp, 1);
    });
    Assert.True(plan.CostEur > 0);
  }

  // ── Heating prices from the house simulation ───────────────────────────────────────────────

  static SimulationInput House(DateTime start, int soc, Func<DateTime, int> pvWh)
  {
    var date = start.Date;
    var import = new List<PriceTableEntry>();
    var export = new List<PriceTableEntry>();
    var load = new Dictionary<DateTime, int>();
    var pv = new Dictionary<DateTime, int>();
    for (var t = date; t < date.AddDays(3); t = t.AddMinutes(15))
    {
      if (t < date.AddDays(2))
      {
        import.Add(new PriceTableEntry(t, t.AddMinutes(15), 0.30f));
        export.Add(new PriceTableEntry(t, t.AddMinutes(15), 0.08f));
      }
      load[t] = 100;
      pv[t] = pvWh(t);
    }
    return new SimulationInput
    {
      StartTime = start, StartSocPercent = soc, BatteryCapacityWh = 11_520,
      AbsoluteMinSocPercent = 12, PreferredMinSocPercent = 15, EnforcePreferredSoc = false,
      MaxChargePowerAmps = 30, InverterEfficiency = 0.95f, ImportPrices = import, ExportPrices = export,
      LoadPredictionWh = load, PVPredictionWh = pv, ExtraLoads = [],
      EnableCheapForceCharge = false, OpportunisticDischarge = false, ForceChargeMaxPrice = 0.34f,
      ForceChargeTargetSocPercent = 95, CurrentMode = new InverterState(InverterModes.normal),
    };
  }

  static float PriceAt(SimulationInput input, DateTime slot)
  {
    var baseline = EnergySimulator.Simulate(input);
    var slots = baseline.Slots.Select(s => s.Time).ToList();
    var prices = WarmWaterPlanner.HeatingPricesPerKwh(input, baseline, slots, input.StartTime, 1720);
    return prices[slots.IndexOf(slot)];
  }

  [Fact]
  public void HeatingPrice_PvSurplusWithFullBattery_CostsTheLostExport()
  {
    // Full battery and 3 kW PV at noon: the heating only takes energy that would be exported.
    var input = House(new DateTime(2026, 10, 20, 10, 0, 0), 100, t => t.Hour is >= 9 and < 16 ? 750 : 0);
    float price = PriceAt(input, new DateTime(2026, 10, 20, 12, 0, 0));
    Assert.InRange(price, 0.05f, 0.12f);
  }

  [Fact]
  public void HeatingPrice_EmptyBatteryAtNight_CostsTheImport()
  {
    // Battery at its floor in the evening, no PV until tomorrow: every kWh is bought.
    var input = House(new DateTime(2026, 10, 20, 20, 0, 0), 13, t => t.Hour is >= 9 and < 16 ? 750 : 0);
    float price = PriceAt(input, new DateTime(2026, 10, 20, 21, 0, 0));
    Assert.InRange(price, 0.25f, 0.40f);
  }
}
