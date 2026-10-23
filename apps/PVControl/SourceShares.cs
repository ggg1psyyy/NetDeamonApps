using System;

namespace NetDeamon.apps.PVControl;

/// <summary>
/// Where the power a consumer draws right now comes from (fractions 0…1), and what one kWh of it
/// costs — the one calculation behind the battery's stored-energy cost
/// (<c>sensor.pv_control_battery_avg_cost_per_kwh</c>) and the extra loads' cost totals.
/// PV is free (money actually paid, not the export it displaces), grid import costs the import
/// price (<see cref="PriceList.GetPriceOrMean"/>), battery energy what it cost to store.
/// </summary>
public readonly record struct SourceShares(float Pv, float Grid, float Battery)
{
  /// <summary>
  /// PV surplus first, then grid import, the rest from the battery. Without a measured draw
  /// (<paramref name="powerW"/> ≤ 0) all of it counts as battery — for the battery itself that
  /// keeps its average cost unchanged.
  /// </summary>
  public static SourceShares Of(float powerW, float pvSurplusW, float gridImportW)
  {
    if (powerW <= 0) return new(0, 0, 1);
    float pv = Math.Clamp(pvSurplusW / powerW, 0f, 1f);
    float grid = Math.Min(1f - pv, Math.Max(0f, gridImportW) / powerW);
    return new(pv, grid, 1f - pv - grid);
  }

  // The PV surplus comes from the meters' balance PV − load = battery charge − grid import, so
  // battery and grid (both 1-min averages) are all it needs: the PV average is 15 min and lags the
  // steep evening ramp — a grid charge at dusk would have counted as PV.

  /// <summary>A load that is part of the house load (EV charger): the PV beyond the rest of the house goes to it first.</summary>
  public static SourceShares ForLoad(float loadW, float batteryChargeW, float gridImportW) =>
    Of(loadW, batteryChargeW - gridImportW + loadW, gridImportW);

  /// <summary>The battery charging: PV beyond the whole house load first, then grid import.</summary>
  public static SourceShares ForBatteryCharge(float batteryChargeW, float gridImportW) =>
    Of(batteryChargeW, batteryChargeW - gridImportW, gridImportW);

  /// <summary>
  /// The one cost calculation: grid at <paramref name="importPrice"/>, battery at
  /// <paramref name="batteryPrice"/>, PV at <paramref name="pvPrice"/>. With shares (fractions) it's
  /// €/kWh, with amounts (kWh) €. The parameter is the only difference between its users:
  /// <list type="bullet">
  /// <item>money paid (battery average cost, loads' cost totals): PV free, <paramref name="pvPrice"/> = 0;</item>
  /// <item>what using energy costs (<see cref="Simulator.SimulationResult.NetGridCostVs"/> → slot
  /// prices): <paramref name="pvPrice"/> = the export price — exported energy is PV not used,
  /// a negative <see cref="Pv"/> amount, so it earns.</item>
  /// </list>
  /// </summary>
  public float Cost(float importPrice, float batteryPrice, float pvPrice = 0) =>
    Grid * importPrice + Battery * batteryPrice + Pv * pvPrice;
}
