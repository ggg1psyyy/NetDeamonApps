using System;
using System.Globalization;
using System.Threading.Tasks;
using NetDaemon.HassModel.Entities;
using static NetDeamon.apps.PVControl.PVControlCommon;
using Math = System.Math;

namespace NetDeamon.apps.PVControl;

public class CostTracker
{
  // Plausible range for battery average cost. Values outside this are floating-point
  // artefacts (underflow → near-zero, bad initial state → sky-high) and are clamped.
  private const float BatteryAvgCostMinEurPerKwh = 0.0001f;
  private const float BatteryAvgCostMaxEurPerKwh = 1.0f;

  private float _batteryAvgCostPerKwh;
  private float _lastBatteryInputEnergyKwh = -1f; // -1 = not yet initialized
  private float _lastImportEnergySum;
  private float _lastExportEnergySum;

  // ── Cost sum entities (HA is the source of truth — no local copy) ─────────────────────
  /// <summary>Set by PVControl after entity registration; CostTracker writes directly to these.</summary>
  public Entity? SumExportEarningsEntity { get; set; }
  public Entity? SumImportCostBruttoEntity { get; set; }
  public Entity? SumImportCostEnergyOnlyEntity { get; set; }
  public Entity? SumImportCostNetworkOnlyEntity { get; set; }
  public Entity? SumImportExportNetCostEntity { get; set; }

  private Entity? _batteryAvgCostEntity;
  public Entity? BatteryAvgCostEntity
  {
    get => _batteryAvgCostEntity;
    set
    {
      _batteryAvgCostEntity = value;
      // Restore persisted value on startup so attribution is correct immediately.
      // Use numericalGetBaseValue:false — the value is stored raw in €/kWh, no unit conversion needed.
      // Reject values outside the plausible range (e.g. underflow artefacts, corrupt state).
      if (value != null && value.TryGetStateValue(out float v, numericalGetBaseValue: false)
          && v >= BatteryAvgCostMinEurPerKwh && v <= BatteryAvgCostMaxEurPerKwh)
        _batteryAvgCostPerKwh = v;
    }
  }

  public float BatteryAvgCostPerKwh => _batteryAvgCostPerKwh;

  public CostTracker()
  {
    if (PVCC_Config.DailyExportEnergyEntity is null || PVCC_Config.DailyImportEnergyEntity is null)
      throw new NullReferenceException("DailyEnergyEntities not available");
    if (PVCC_Config.DailyExportEnergyEntity.TryGetStateValue(out float lastExportEnergySum))
      _lastExportEnergySum = lastExportEnergySum / 1000;
    if (PVCC_Config.DailyImportEnergyEntity.TryGetStateValue(out float lastImportEnergySum))
      _lastImportEnergySum = lastImportEnergySum / 1000;
  }

  private async Task AddToSumEntityAsync(Entity? entity, float deltaEur)
  {
    if (entity is null) return;
    // numericalGetBaseValue:false — value is stored raw in €, no unit-multiplier conversion.
    float current = entity.TryGetStateValue(out float v, numericalGetBaseValue: false) ? v : 0f;
    await PVCC_EntityManager.SetStateAsync(entity.EntityId, (current + deltaEur).ToString(CultureInfo.InvariantCulture));
  }

  private async Task UpdateNetCostEntityAsync()
  {
    if (SumImportExportNetCostEntity is null || SumImportCostBruttoEntity is null || SumExportEarningsEntity is null) return;
    float imp = SumImportCostBruttoEntity.TryGetStateValue(out float i, numericalGetBaseValue: false) ? i : 0f;
    float exp = SumExportEarningsEntity.TryGetStateValue(out float e, numericalGetBaseValue: false) ? e : 0f;
    await PVCC_EntityManager.SetStateAsync(SumImportExportNetCostEntity.EntityId, (imp - exp).ToString(CultureInfo.InvariantCulture));
  }

  /// <summary>
  /// Called when the battery input energy sensor changes. Uses the actual energy delta
  /// to update the running average cost per kWh stored in the battery. The delta is priced by
  /// <paramref name="sources"/> (<see cref="SourceShares.ForBatteryCharge"/>: PV free, the grid
  /// share at the import price) — the same calculation as the extra loads' costs.
  /// </summary>
  public async Task OnBatteryInputEnergyChangedAsync(float batteryInputKwh, SourceShares sources, PriceList importPrices)
  {
    float currentKwh = batteryInputKwh; // sensor reports in kWh

    // First call or midnight reset (sensor went backwards) — just store the baseline.
    if (_lastBatteryInputEnergyKwh < 0 || currentKwh < _lastBatteryInputEnergyKwh)
    {
      _lastBatteryInputEnergyKwh = currentKwh;
      return;
    }

    float deltaKwh = currentKwh - _lastBatteryInputEnergyKwh;
    _lastBatteryInputEnergyKwh = currentKwh;

    if (deltaKwh <= 0) return;

    // A share the split can't place counts at the battery's own cost (leaves the average as is).
    float sourcePrice = sources.Cost(importPrices.GetPriceOrMean(DateTime.Now), _batteryAvgCostPerKwh);

    // currentStoredKwh from the battery SoC entity is the authoritative stored energy.
    if (!PVCC_Config.BatterySoCEntity.TryGetStateValue(out int socPct) ||
        !PVCC_Config.BatteryCapacityEntity.TryGetStateValue(out int capWh))
      return;
    _batteryAvgCostPerKwh = BlendAverageCost(socPct * capWh / 100f / 1000f, _batteryAvgCostPerKwh, deltaKwh, sourcePrice);
    if (_batteryAvgCostEntity != null)
      await PVCC_EntityManager.SetStateAsync(_batteryAvgCostEntity.EntityId,
        _batteryAvgCostPerKwh.ToString(CultureInfo.InvariantCulture));
  }

  /// <summary>Weighted average: the stored energy at its average cost blended with the new charge at its price, clamped to the plausible range.</summary>
  public static float BlendAverageCost(float storedKwh, float avgCostPerKwh, float deltaKwh, float pricePerKwh)
  {
    storedKwh = Math.Max(0.1f, storedKwh);
    return Math.Clamp((storedKwh * avgCostPerKwh + deltaKwh * pricePerKwh) / (storedKwh + deltaKwh),
      BatteryAvgCostMinEurPerKwh, BatteryAvgCostMaxEurPerKwh);
  }

  /// <summary>Called when the daily export energy sensor changes. Accumulates export earnings.</summary>
  public async Task OnExportEnergyChangedAsync(float export, PriceList exportPrices)
  {
    float diff = (export / 1000) - _lastExportEnergySum;
    if (diff > 0)
    {
      await AddToSumEntityAsync(SumExportEarningsEntity, diff * exportPrices.GetPrice(DateTime.Now));
      await UpdateNetCostEntityAsync();
    }
    _lastExportEnergySum = export / 1000;
  }

  /// <summary>Called when the daily import energy sensor changes. Accumulates import costs.</summary>
  public async Task OnImportEnergyChangedAsync(float import, PriceList importPrices, float energyOnlyPrice, float networkOnlyPrice)
  {
    float diff = (import / 1000) - _lastImportEnergySum;
    if (diff > 0)
    {
      await AddToSumEntityAsync(SumImportCostBruttoEntity, diff * importPrices.GetPrice(DateTime.Now));
      await AddToSumEntityAsync(SumImportCostEnergyOnlyEntity, diff * energyOnlyPrice);
      await AddToSumEntityAsync(SumImportCostNetworkOnlyEntity, diff * networkOnlyPrice);
      await UpdateNetCostEntityAsync();
    }
    _lastImportEnergySum = import / 1000;
  }
}
