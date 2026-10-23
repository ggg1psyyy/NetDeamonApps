using System;
using NetDeamon.apps;
using NetDeamon.apps.PVControl;
using Xunit;

namespace NetDeamonApps.Tests;

/// <summary>
/// What a kWh costs by where it comes from (<see cref="SourceShares"/>) — shared by
/// sensor.pv_control_battery_avg_cost_per_kwh and the extra loads' cost totals — and the one price
/// lookup (<see cref="PriceList.GetPriceOrMean"/>) they share with the simulation's grid cost.
/// Reading the meters and writing the sensors (CostTracker, HouseEnergy) needs live HA.
/// </summary>
public class SourceSharesTests
{
  static void AssertShares(SourceShares s, float pv, float grid, float battery)
  {
    Assert.Equal(pv, s.Pv, 3);
    Assert.Equal(grid, s.Grid, 3);
    Assert.Equal(battery, s.Battery, 3);
  }

  // ── Battery charge ───────────────────────────────────────────────────────────────────

  [Fact]
  public void BatteryCharge_WhileExporting_IsPV() =>
    AssertShares(SourceShares.ForBatteryCharge(2000, gridImportW: -500), 1, 0, 0);

  [Fact]
  public void BatteryCharge_GridCharge_IsGrid() =>
    // Grid charge at dusk: the meters' balance says grid, whatever a lagging PV average says.
    AssertShares(SourceShares.ForBatteryCharge(3000, gridImportW: 3300), 0, 1, 0);

  [Fact]
  public void BatteryCharge_PartlyFromGrid_PricedByShare()
  {
    var s = SourceShares.ForBatteryCharge(3000, gridImportW: 1000);
    AssertShares(s, 2f / 3, 1f / 3, 0);
    Assert.Equal(0.10f, s.Cost(importPrice: 0.30f, batteryPrice: 0.20f), 4);
  }

  [Fact]
  public void NoMeasuredDraw_CountsAsBattery_KeepsTheAverage()
  {
    var s = SourceShares.ForBatteryCharge(0, gridImportW: 500);
    AssertShares(s, 0, 0, 1);
    Assert.Equal(0.12f, s.Cost(0.30f, batteryPrice: 0.12f), 4);
  }

  // ── Extra load (EV charger) ──────────────────────────────────────────────────────────

  [Fact]
  public void Load_PVThenGridThenBattery()
  {
    // EV 2 kW, battery discharging 1.5 kW, grid 0.3 kW: PV beyond the rest of the house = 0.2 kW.
    var s = SourceShares.ForLoad(2000, batteryChargeW: -1500, gridImportW: 300);
    AssertShares(s, 0.10f, 0.15f, 0.75f);
    Assert.Equal(0.15f * 0.30f + 0.75f * 0.08f, s.Cost(0.30f, 0.08f), 4);
  }

  [Fact]
  public void Load_PVCoversItAndCharges_IsPV() =>
    AssertShares(SourceShares.ForLoad(2000, batteryChargeW: 500, gridImportW: -300), 1, 0, 0);

  [Fact]
  public void Cost_OneCalculation_PVPriceIsTheOnlyDifference()
  {
    var s = SourceShares.ForBatteryCharge(3000, gridImportW: 1000); // ⅔ PV, ⅓ grid
    Assert.Equal(0.10f, s.Cost(0.30f, 0.20f), 4);                 // money paid: PV free
    Assert.Equal(0.10f + 2f / 3 * 0.09f, s.Cost(0.30f, 0.20f, pvPrice: 0.09f), 4); // + lost export
    // Simulation slot (amounts in kWh): 1 kWh imported, 2 kWh exported (= −2 kWh PV not used) earns.
    Assert.Equal(0.30f - 2 * 0.09f, new SourceShares(Pv: -2, Grid: 1, Battery: 0).Cost(0.30f, 0, pvPrice: 0.09f), 4);
  }

  // ── Average cost ─────────────────────────────────────────────────────────────────────

  [Fact]
  public void BlendAverageCost_WeightsByEnergy()
  {
    Assert.Equal(0.15f, CostTracker.BlendAverageCost(storedKwh: 5, avgCostPerKwh: 0.10f, deltaKwh: 1, pricePerKwh: 0.40f), 4);
    Assert.Equal(1.0f, CostTracker.BlendAverageCost(0, 0.5f, 1, 5f));         // clamped to the plausible range
    Assert.Equal(0.0001f, CostTracker.BlendAverageCost(5, 0.0001f, 1, 0f), 6); // free PV never reaches 0
  }

  // ── Price lookup ─────────────────────────────────────────────────────────────────────

  [Fact]
  public void GetPriceOrMean_OutsideThePrices_IsTheMean()
  {
    var t = new DateTime(2026, 10, 23, 14, 0, 0);
    var prices = new PriceList([new PriceTableEntry(t, t.AddHours(1), 0.20f), new PriceTableEntry(t.AddHours(1), t.AddHours(2), 0.30f)]);
    Assert.Equal(0.20f, prices.GetPriceOrMean(t.AddMinutes(20)));
    Assert.Equal(0.30f, prices.GetPriceOrMean(t.AddMinutes(75)));
    Assert.Equal(0.25f, prices.GetPriceOrMean(t.AddHours(3)), 4);
    Assert.Equal(0f, new PriceList().GetPriceOrMean(t));
  }
}
