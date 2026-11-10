using NetDeamon.apps.PVControl;
using NetDeamon.apps.PVControl.Managers;
using Xunit;

namespace NetDeamonApps.Tests;

/// <summary>
/// A consumer's € total from its energy counter (<see cref="CostMeter"/>: the heat pump's total, heating and warm water)
/// and its price: the house's source mix (<see cref="SourceShares.ForLoad"/> with the whole house load).
/// Subscribing to the counters and writing sensor.pv_control_&lt;slug&gt;_total_cost (HouseEnergy, PVControl) needs live HA.
/// </summary>
public class CostMeterTests
{
  static CostMeter Meter() => new(new CostMeterConfig { Name = "Heat pump", Slug = "heatpump", MaxPowerW = 4000 });

  [Fact]
  public void FirstReading_IsTheBase_LaterIncreasesCostThePriceOfTheirMoment()
  {
    var m = Meter();
    Assert.Equal((0f, 0f), m.Book(123_700, 0.50f));      // after a start (or a counter unavailable then)
    m.Book(124_200, 0.50f);                               // 0.5 kWh at 0.50
    m.Book(124_700, 0.20f);                               // 0.5 kWh at 0.20 (e.g. half PV)
    Assert.Equal(0.35f, m.TotalCostEur, 4);
  }

  [Fact]
  public void CounterBlipToZeroAndBack_BooksNotTheWholeTotal()
  {
    var m = Meter();
    m.Book(123_700, 0.5f);
    Assert.Equal(0f, m.Book(0, 0.5f).BookedKwh);          // reads as a reset: books the 0 since it
    var (booked, skipped) = m.Book(123_800, 0.5f);        // the whole total as one step: a glitch
    Assert.Equal(0f, booked);
    Assert.Equal(123.8f, skipped, 3);
    Assert.Equal(0.1f, m.Book(123_900, 0.5f).BookedKwh, 3);
    Assert.Equal(0.05f, m.TotalCostEur, 4);
  }

  [Fact]
  public void DailyCounterReset_BooksTheEnergyAfterMidnight()
  {
    var m = Meter();
    m.Book(5_000, 0.4f);
    Assert.Equal(0.2f, m.Book(200, 0.4f).BookedKwh, 3);
    Assert.Equal(0.08f, m.TotalCostEur, 4);
  }

  [Fact]
  public void PlausibleStep_IsTwoHoursAtTheHighestPower()
  {
    Assert.Equal(8f, CostMeter.MaxStepKwh(4000), 3);
    var m = Meter();
    m.Book(0, 0.5f);
    Assert.Equal(7.9f, m.Book(7_900, 0.5f).BookedKwh, 3);  // a counter unavailable for a while: still booked
  }

  [Theory]
  // house W, battery W (+ charging), grid W (+ import) → PV, grid, battery share of every consumer in the house
  [InlineData(2000, 0, 1000, 0.5f, 0.5f, 0f)]        // half PV, half grid
  [InlineData(1000, -600, 400, 0f, 0.4f, 0.6f)]      // night: battery and grid
  [InlineData(1000, 3000, 4000, 0f, 1f, 0f)]         // grid charge, no PV: the house from the grid
  [InlineData(1000, 5000, 4000, 1f, 0f, 0f)]         // grid charge at 2 kW PV: PV covers the house first
  [InlineData(1000, 2000, -500, 1f, 0f, 0f)]         // PV surplus into battery and export
  public void Proportional_EveryConsumerGetsTheHousesMix(int houseW, int batteryW, int gridW, float pv, float grid, float battery)
  {
    var s = SourceShares.ForLoad(houseW, batteryW, gridW);
    Assert.Equal(pv, s.Pv, 3);
    Assert.Equal(grid, s.Grid, 3);
    Assert.Equal(battery, s.Battery, 3);
  }

  [Fact]
  public void Restore_KeepsWhatWasBookedDuringTheStart_Once()
  {
    // The counter subscribes in the constructor: a step during the start books before HA's total is restored.
    var m = Meter();
    m.Book(10_000, 0.30f);       // the base
    m.Book(11_000, 0.30f);       // 1 kWh during the start: 0.30 €
    m.Restore(12.5f);
    Assert.Equal(12.8f, m.TotalCostEur, 3);
    m.Restore(12.5f);            // a re-registration doesn't add it again
    Assert.Equal(12.8f, m.TotalCostEur, 3);
  }
}
