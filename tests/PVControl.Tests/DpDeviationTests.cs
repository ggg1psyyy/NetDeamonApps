using NetDeamon.apps;
using NetDeamon.apps.PVControl;
using NetDeamon.apps.PVControl.Optimizer;
using NetDeamon.apps.PVControl.Simulator;
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace NetDeamonApps.Tests;

/// <summary>
/// A deviation from normal must save at least <see cref="DpOptions.DeviationEurPerKwh"/> per kWh it moves (the user,
/// 2026-11-09). Live that evening: 21:00 at 0.506, 22:00 at 0.480, 23:00 at 0.415 (one hour, charged at full power),
/// 00:00–06:00 at 0.461 and an expensive day after (0.60–0.70): the battery is filled anyway, its last kWh bought at
/// 0.461 — worth 0.461 / 0.95² = 0.511 delivered. A hold at 21:00 saved 0.5 ct/kWh, at 22:00 3 ct/kWh.
/// </summary>
public class DpDeviationTests
{
  static readonly DateTime Day = new(2026, 11, 9);

  static float Price(DateTime t) => (t - Day).TotalHours switch
  {
    < 21 => 0.57f, < 22 => 0.506f, < 23 => 0.480f, < 24 => 0.415f, < 30 => 0.461f, < 45 => 0.65f, _ => 0.45f,
  };

  static SimulationInput Input()
  {
    var prices = new List<PriceTableEntry>();
    var export = new List<PriceTableEntry>();
    var load = new Dictionary<DateTime, int>();
    var pv = new Dictionary<DateTime, int>();
    for (var t = Day.AddDays(-1); t < Day.AddDays(3); t = t.AddMinutes(15))
    {
      prices.Add(new PriceTableEntry(t, t.AddMinutes(15), Price(t)));
      export.Add(new PriceTableEntry(t, t.AddMinutes(15), Price(t) / 2));
      load[t] = 150;   // 600 W
      pv[t] = 0;       // a dull November day
    }
    return new SimulationInput
    {
      StartTime = Day.AddHours(21), StartSocPercent = 31, BatteryCapacityWh = 11_520,
      AbsoluteMinSocPercent = 12, PreferredMinSocPercent = 20, EnforcePreferredSoc = false,
      MaxChargePowerAmps = 30, InverterEfficiency = 0.95f, ImportPrices = prices, ExportPrices = export,
      LoadPredictionWh = load, PVPredictionWh = pv, ExtraLoads = [],
      EnableCheapForceCharge = false, OpportunisticDischarge = false, ForceChargeMaxPrice = 0.35f,
      ForceChargeTargetSocPercent = 95, CurrentMode = new InverterState(InverterModes.normal),
    };
  }

  static string Show(DpPlan p) => string.Join(", ", p.Slots.Take(16).Select(s => $"{s.Time:HH:mm} {s.Mode} {s.SocPct}% {s.BatteryValue}"));

  static InverterModes[] Hour(DpPlan p, int hour) => p.Slots.Where(s => s.Time.Date == Day && s.Time.Hour == hour).Select(s => s.Mode).ToArray();

  [Fact]
  public void HoldSavingUnderOneCentPerKwh_StaysNormal_ClearSavingStillHolds()
  {
    var plan = BatteryDpPlanner.Plan(Input(), new DpOptions());
    Assert.True(Hour(plan, 21).All(m => m == InverterModes.normal), Show(plan));
    Assert.True(Hour(plan, 22).All(m => m == InverterModes.grid_only), Show(plan));
  }

  [Fact]
  public void WithoutTheRule_TheHalfCentHoldIsTaken()
  {
    var plan = BatteryDpPlanner.Plan(Input(), new DpOptions(DeviationEurPerKwh: 0));
    // 21:00 itself stays normal by the running mode's 0.1-ct hysteresis (KeepModeEur); the rest of the hour holds
    Assert.True(Hour(plan, 21).Skip(1).All(m => m == InverterModes.grid_only), Show(plan));
  }

  [Theory]
  // the action's charge, discharge; normal's charge, discharge (Wh) → € at 1 ct/kWh
  [InlineData(0, 0, 0, 600, 0.006f)]       // a hold: the 600 Wh normal took from the battery bought from the grid
  [InlineData(1800, 0, 0, 150, 0.0015f)]   // a grid charge: only the house's draw it holds, not what it stores
  [InlineData(0, 0, 500, 0, 0.005f)]       // feed-in first: the surplus exported instead of stored
  [InlineData(0, 600, 0, 600, 0f)]         // normal itself (or grid_only where a load holds the battery anyway)
  public void Deviation_IsWhatTheBatteryDoesntDeliverOrStore(int ch, int dis, int normalCh, int normalDis, float eur)
  {
    Assert.Equal(eur, BatteryDpPlanner.DeviationEur(new DpOptions(), ch, dis, (normalCh, normalDis, 0, 0)), 5);
  }
}

/// <summary>
/// <see cref="DpOptions.PruneEqualCharges"/>: a charge target only caps the quarter's charge, so above the first target the
/// quarter's limit caps, every target has the same flows (and loses the tie to the lower one) — skipping them gives the
/// same plan. Checked on live snapshots (all 13 of Data/ were identical quarter by quarter, 3–4× faster, 2026-11-09).
/// </summary>
public class DpPruneEqualChargesTests
{
  [Theory]
  [InlineData("forecast_snapshot_2026-11-02_0600.json.gz")]   // night charge, morning, a sunny day
  [InlineData("forecast_snapshot_2026-11-07_1100.json.gz")]   // midday charges near-tied (the charge start cost)
  [InlineData("forecast_snapshot_2026-11-07_2300.json.gz")]   // night at a low SoC
  public void Pruned_GivesTheSamePlan(string snapshot)
  {
    var input = ForecastSnapshot.FromBytes(System.IO.File.ReadAllBytes(
      System.IO.Path.Combine(AppContext.BaseDirectory, "Data", snapshot))).ToInput();
    var full = BatteryDpPlanner.Plan(input, new DpOptions(PruneEqualCharges: false));
    var pruned = BatteryDpPlanner.Plan(input, new DpOptions());
    Assert.Equal(full.Cost, pruned.Cost);
    Assert.Equal(full.EndSocPct, pruned.EndSocPct);
    Assert.Equal(full.Slots, pruned.Slots);
  }
}
