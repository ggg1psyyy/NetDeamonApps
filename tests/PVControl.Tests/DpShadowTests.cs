using Microsoft.Data.Sqlite;
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
/// The DP shadow (<see cref="DpShadow"/>): the comparison of the two plans, their charge blocks and the per-quarter log.
/// Publishing the sensor (PVControl) needs HA and is checked live (sensor.pv_control_dp_shadow).
/// </summary>
public class DpShadowTests : TestBase, IDisposable
{
  static readonly DateTime Day = new(2026, 10, 30);
  readonly string _db = Path.Combine(Path.GetTempPath(), "dpshadow-" + Guid.NewGuid().ToString("N") + ".db");

  public void Dispose()
  {
    SqliteConnection.ClearAllPools();
    if (File.Exists(_db)) File.Delete(_db);
  }

  static SimulationInput Input(DateTime start, int soc)
  {
    var prices = new List<PriceTableEntry>();
    var load = new Dictionary<DateTime, int>();
    var pv = new Dictionary<DateTime, int>();
    for (var t = Day.AddDays(-1); t < Day.AddDays(3); t = t.AddMinutes(15))
    {
      prices.Add(new PriceTableEntry(t, t.AddMinutes(15), t.Hour is 23 or < 5 ? 0.39f : t.Hour is >= 16 and < 21 ? 0.51f : 0.44f));
      load[t] = 100;
      pv[t] = t.Hour >= 10 && t.Hour < 15 ? 240 : 0;
    }
    return new SimulationInput
    {
      StartTime = start, StartSocPercent = soc, BatteryCapacityWh = 11_520,
      AbsoluteMinSocPercent = 12, PreferredMinSocPercent = 20, EnforcePreferredSoc = false,
      MaxChargePowerAmps = 30, InverterEfficiency = 0.95f, ImportPrices = prices, ExportPrices = prices,
      LoadPredictionWh = load, PVPredictionWh = pv, ExtraLoads = [],
      EnableCheapForceCharge = false, OpportunisticDischarge = false, ForceChargeMaxPrice = 0.35f,
      ForceChargeTargetSocPercent = 95, CurrentMode = new InverterState(InverterModes.normal),
    };
  }

  [Fact]
  public void Charges_ConsecutiveQuartersAreOneBlock_WithEnergyAndMeanPrice()
  {
    var input = Input(Day.AddHours(22), 30);
    var blocks = DpShadow.Charges(input,
    [
      (Day.AddHours(22).AddMinutes(45), false, 0),
      (Day.AddHours(23), true, 2000),
      (Day.AddHours(23).AddMinutes(15), true, 1000),
      (Day.AddHours(23).AddMinutes(30), false, 0),
      (Day.AddDays(1).AddHours(3), true, 500),
    ]);
    Assert.Equal(2, blocks.Count);
    Assert.Equal(new ChargeBlock(Day.AddHours(23), Day.AddHours(23).AddMinutes(30), 3f, 0.39f), blocks[0]);
    Assert.Equal(Day.AddDays(1).AddHours(3).AddMinutes(15), blocks[1].End);
  }

  [Fact]
  public void Report_ComparesBothPlansUnderTheOneModel()
  {
    var input = Input(Day.AddHours(22), 29);
    var greedy = EnergySimulator.Simulate(input);
    var dp = BatteryDpPlanner.Plan(input);
    var r = DpShadow.Report(greedy, dp, Day.AddHours(22), 42, new DpOptions());
    Assert.Equal(PlanEvaluation.Evaluate(greedy, new DpOptions()).Total, r.Greedy.Total, 4);
    Assert.Equal(dp.Cost.Total, r.Dp.Total, 4);
    Assert.Equal(r.Greedy.Total - r.Dp.Total, r.Saving, 4);
    Assert.Equal(greedy.Slots[0].State.Mode == dp.Slots[0].Mode, r.AgreeNow);
    Assert.Equal(29, r.SocNow);
  }

  [Fact]
  public async Task RunAsync_LogsOneRowPerQuarter_AndRespectsTheInterval()
  {
    var shadow = new DpShadow(new DpShadowConfig { IntervalMinutes = 5 }, _db);
    var sim = EnergySimulator.Simulate(Input(Day.AddHours(22), 29));
    Assert.True(shadow.Due(Day.AddHours(22)));
    Assert.NotNull(await shadow.RunAsync(sim, Day.AddHours(22).AddMinutes(1)));
    Assert.False(shadow.Due(Day.AddHours(22).AddMinutes(4)));
    Assert.True(shadow.Due(Day.AddHours(22).AddMinutes(6)));
    await shadow.RunAsync(sim, Day.AddHours(22).AddMinutes(7));    // same quarter: no second row
    await shadow.RunAsync(sim, Day.AddHours(22).AddMinutes(16));   // next quarter
    await using var con = new SqliteConnection($"Data Source={_db};Pooling=False");
    await con.OpenAsync();
    await using var cmd = con.CreateCommand();
    cmd.CommandText = "SELECT COUNT(*) FROM shadow";
    Assert.Equal(2L, (long)(await cmd.ExecuteScalarAsync())!);
  }

  [Fact]
  public void Disabled_NeverDue() =>
    Assert.False(new DpShadow(new DpShadowConfig { Enabled = false }, _db).Due(Day));
}
