using NetDeamon.apps;
using NetDeamon.apps.PVControl;
using NetDeamon.apps.PVControl.Simulator;
using NetDeamon.apps.PVControl.WarmWater;
using Xunit;

namespace NetDeamonApps.Tests;

/// <summary>
/// The import cap in the plan: grid charging yields to the load (<see cref="SimulationInput.GridChargeWhPerSlot"/>),
/// PriorityPlus never plans over the cap, warm-water starts wait for room, and DataLogger v8 logs the
/// highest 15-min import. Live 2026-10-25 08:00 (same scenario as <see cref="CheapWindowShorterThanSessionTests"/>).
/// </summary>
public class ImportCapSimulationTests : TestBase
{
  static readonly DateTime Day = new(2026, 10, 25);
  static readonly float[] Gross = [0.349f, 0.344f, 0.348f, 0.348f, 0.368f, 0.377f, 0.365f, 0.355f, 0.328f, 0.322f, 0.350f, 0.353f,
    0.354f, 0.361f, 0.395f, 0.423f, 0.433f, 0.420f, 0.419f, 0.420f, 0.412f, 0.403f, 0.361f, 0.356f];

  static SimulationInput Input(int capW, int loadWhSlot = 130)
  {
    var load = new Dictionary<DateTime, int>();
    var pv = new Dictionary<DateTime, int>();
    for (var t = Day.AddDays(-1); t < Day.AddDays(3); t = t.AddMinutes(15))
    {
      load[t] = t.Hour is >= 22 or < 8 ? 100 : loadWhSlot;
      pv[t] = 0;
    }
    var prices = new List<PriceTableEntry>();
    for (var t = Day; t < Day.AddDays(1); t = t.AddHours(1))
      prices.Add(new PriceTableEntry(t, t.AddHours(1), Gross[t.Hour]));
    return new SimulationInput
    {
      StartTime = Day.AddHours(8), StartSocPercent = 18, BatteryCapacityWh = 11_520,
      AbsoluteMinSocPercent = 12, PreferredMinSocPercent = 25, EnforcePreferredSoc = false,
      MaxChargePowerAmps = 30, ChargeVoltageV = 245, InverterEfficiency = 0.95f,
      ImportPrices = new PriceList(prices).WithEstimatedPrices(Day.AddDays(2), 0.05f),
      ExportPrices = new PriceList(prices.Select(p => new PriceTableEntry(p.StartTime, p.EndTime, 0.08f))),
      PublishedPricesUntil = Day.AddDays(1), LoadPredictionWh = load, PVPredictionWh = pv,
      EnableCheapForceCharge = false, OpportunisticDischarge = false,
      ForceChargeMaxPrice = 0.35f, ForceChargeTargetSocPercent = 100,
      CurrentMode = new InverterState(InverterModes.normal),
      MaxGridImportW = capW,
    };
  }

  static ExtraLoad Ev(DateTime start, DateTime end) => new() { Name = "EV", Priority = 10, StartTime = start, EndTime = end, PowerW = 1930 };

  [Theory]
  [InlineData(0, 0, 1000, 1838)]       // no cap: the charge limit (30 A × 245 V)
  [InlineData(9800, 0, 1000, 1450)]    // 2450 Wh cap − 1000 Wh load
  [InlineData(9800, 400, 1000, 1838)]  // PV covers part of the load: room up to the charge limit
  [InlineData(9800, 0, 3000, 0)]       // load alone over the cap: no grid charging
  public void GridChargeWhPerSlot_ChargeLimitLessTheLoadsGridDraw(int capW, int pvWh, int loadWh, int expected) =>
    Assert.Equal(expected, Input(capW).GridChargeWhPerSlot(pvWh, loadWh));

  [Fact]
  public void ForceChargeWithTheEv_BatteryYields_NoSlotOverTheCap()
  {
    var withoutCap = EnergySimulator.Simulate(Input(0, loadWhSlot: 300).WithExtraLoads([Ev(Day.AddHours(8), Day.AddHours(10))]));
    var withCap = EnergySimulator.Simulate(Input(9800, loadWhSlot: 300).WithExtraLoads([Ev(Day.AddHours(8), Day.AddHours(10))]));

    Assert.Contains(withoutCap, s => Energy.SlotW(s.GridImportWh) > 9800); // 7.35 kW charge + 1.93 kW EV + house
    Assert.All(withCap, s => Assert.True(Energy.SlotW(s.GridImportWh) <= 9800 * 1.001, $"{s.Time:HH:mm}: {Energy.SlotW(s.GridImportWh)} W"));
    Assert.Contains(withCap, s => s.State.Mode == InverterModes.force_charge && s.BatteryChargeWh > 0);
  }

  [Fact]
  public void NeedToCharge_TakesMoreCheapQuarters_WhenTheCapLimitsTheChargeRate()
  {
    int Quarters(SimulationResult r) => r.Count(s => s.Time < Day.AddHours(10) && s.State.Mode == InverterModes.force_charge);
    // House at 4 kW: under a 6 kW cap the battery gets 2 kW instead of 7.35.
    var free = EnergySimulator.Simulate(Input(0, loadWhSlot: 1000));
    var capped = EnergySimulator.Simulate(Input(6000, loadWhSlot: 1000));
    Assert.True(Quarters(capped) >= Quarters(free), $"capped {Quarters(capped)} vs free {Quarters(free)} quarters");
    Assert.All(capped, s => Assert.True(Energy.SlotW(s.GridImportWh) <= 6000 * 1.001 || s.State.Mode != InverterModes.force_charge));
  }

  [Fact]
  public void PriorityPlus_LoadThatWouldExceedTheCap_FailsThePriceCheck()
  {
    // House at 8.4 kW: the EV (1.93 kW) on top is over 9.8 kW whatever the battery does.
    var input = Input(9800, loadWhSlot: 2100);
    var baseline = EnergySimulator.Simulate(input);
    var sim = EnergySimulator.Simulate(input.WithExtraLoads([Ev(Day.AddHours(8), Day.AddHours(9))]));
    Assert.True(sim.ExceedsImportCapVs(baseline));

    SimulationResult Run(DateTime s, DateTime e) => EnergySimulator.Simulate(input.WithExtraLoads([Ev(s, e)]));
    var planned = SessionPlanner.FindBestPriceAwareStart(input.StartTime, Day.AddHours(10), (int)Math.Ceiling(1930 * 60 / 60.0), 1930, Run, baseline);
    Assert.True(planned is null || planned.Value.Start >= Day.AddHours(22), $"planned {planned?.Start:HH:mm}"); // only once the house is back at 400 W
  }

  [Fact]
  public void WarmWater_NoRoomUnderTheCap_StartWaits()
  {
    var now = Day.AddHours(14);
    PlannedRun[] runs = [new(now.AddMinutes(-5), now.AddMinutes(55), 40, 50, 2000)];
    ControlInput Input(int? headroom) => new(now, 40, false, false, 50, null, null, WarmWaterMode.Auto, runs, 55,
      false, 0, null, false, 60, null, headroom, 2000);

    Assert.Equal(ControlCommand.Start, WarmWaterControl.Decide(Input(null)).Command);
    Assert.Equal(ControlCommand.Start, WarmWaterControl.Decide(Input(2500)).Command);
    var wait = WarmWaterControl.Decide(Input(1200));
    Assert.Equal(ControlCommand.None, wait.Command);
    Assert.Contains("Import cap", wait.Reason);
  }

  [Fact]
  public void DataLogger_MaxQuarterKw_HighestBlockFromTheCounter()
  {
    var h = new DateTime(2026, 10, 25, 8, 0, 0, DateTimeKind.Utc);
    (DateTime, double)[] readings = [(h.AddMinutes(-1), 100.0), (h.AddMinutes(5), 100.5), (h.AddMinutes(14), 101.0),
      (h.AddMinutes(20), 101.2), (h.AddMinutes(44), 101.4), (h.AddMinutes(61), 101.9)];
    Assert.Equal(4.0, PVControl.DataLogger.MaxQuarterKw(readings, h, h.AddHours(1))!.Value, 2);
    Assert.Null(PVControl.DataLogger.MaxQuarterKw([], h, h.AddHours(1)));
  }

  [Fact]
  public void DataLogger_MigrationV8_AddsTheImportPeakColumn()
  {
    using var con = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
    con.Open();
    var c = con.CreateCommand();
    c.CommandText = "CREATE TABLE hourly (timestamp TEXT, houseenergy INTEGER)";
    c.ExecuteNonQuery();
    c.CommandText = PVControl.DataLogger.AddColumnsSql("hourly", PVControl.DataLogger.ImportPeakColumns, new HashSet<string> { "timestamp", "houseenergy" });
    c.ExecuteNonQuery();
    c.CommandText = "SELECT group_concat(name) FROM pragma_table_info('hourly')";
    Assert.Contains("maxquarterimport", (string)c.ExecuteScalar()!);
  }
}
