using System;

namespace NetDeamon.apps.PVControl;

/// <summary>
/// Energy by source — shares (fractions 0…1) or amounts (kWh) — and its cost. The one € calculation
/// for energy: battery average cost, extra loads' cost totals and the simulation's grid cost.
/// </summary>
/// <param name="Pv">PV share/amount; negative amount = exported (PV not used).</param>
/// <param name="Grid">Grid import share/amount.</param>
/// <param name="Battery">Battery share/amount.</param>
public readonly record struct SourceShares(float Pv, float Grid, float Battery)
{
  /// <summary>Splits a draw: PV surplus first, then grid import, the rest battery.</summary>
  /// <param name="powerW">Power drawn in W; ≤ 0 (not measured) counts all as battery.</param>
  /// <param name="pvSurplusW">PV available for this consumer in W.</param>
  /// <param name="gridImportW">Grid import in W (negative = export).</param>
  /// <returns>Shares summing to 1.</returns>
  public static SourceShares Of(float powerW, float pvSurplusW, float gridImportW)
  {
    if (powerW <= 0) return new(0, 0, 1);
    float pv = Math.Clamp(pvSurplusW / powerW, 0f, 1f);
    float grid = Math.Min(1f - pv, Math.Max(0f, gridImportW) / powerW);
    return new(pv, grid, 1f - pv - grid);
  }

  // The PV surplus comes from the meters' balance PV − load = battery charge − grid import, so the
  // 1-min battery and grid averages are all it needs (the 15-min PV average lags, see ARCHITECTURE).

  /// <summary>Sources of a load that is part of the house load (EV charger); PV beyond the rest of the house goes to it first.</summary>
  /// <param name="loadW">Power the load draws in W.</param>
  /// <param name="batteryChargeW">Battery power in W (positive = charging).</param>
  /// <param name="gridImportW">Grid power in W (positive = import).</param>
  /// <returns>Shares of the load's power.</returns>
  public static SourceShares ForLoad(float loadW, float batteryChargeW, float gridImportW) =>
    Of(loadW, batteryChargeW - gridImportW + loadW, gridImportW);

  /// <summary>Sources of the battery charge: PV beyond the whole house load first, then grid import.</summary>
  /// <param name="batteryChargeW">Battery power in W (positive = charging).</param>
  /// <param name="gridImportW">Grid power in W (positive = import).</param>
  /// <returns>Shares of the charge power.</returns>
  public static SourceShares ForBatteryCharge(float batteryChargeW, float gridImportW) =>
    Of(batteryChargeW, batteryChargeW - gridImportW, gridImportW);

  /// <summary>Cost: Grid × import price + Battery × battery price + Pv × PV price.</summary>
  /// <param name="importPrice">Import price in €/kWh.</param>
  /// <param name="batteryPrice">Cost of battery energy in €/kWh.</param>
  /// <param name="pvPrice">Value of PV in €/kWh: 0 = money paid, the export price = what using it costs (lost export).</param>
  /// <returns>€/kWh for shares, € for amounts.</returns>
  public float Cost(float importPrice, float batteryPrice, float pvPrice = 0) =>
    Grid * importPrice + Battery * batteryPrice + Pv * pvPrice;
}
