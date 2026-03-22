using System;
using System.Globalization;
using System.Threading.Tasks;
using NetDaemon.HassModel.Entities;
using static NetDeamon.apps.PVControl.PVControlCommon;
using Math = System.Math;

namespace NetDeamon.apps.PVControl;

public class CostTracker
{
  private float _batteryAvgCostPerKwh;
  private DateTime _lastBatteryPowerTime = DateTime.MinValue;
  private int _lastBatteryPowerW;
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
      // Clamp to a plausible range; corrupt values (e.g. 14 M €/kWh) are silently discarded.
      if (value != null && value.TryGetStateValue(out float v, numericalGetBaseValue: false) && v is > 0f and <= 10f)
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
  /// Called when the battery power sensor changes. Computes the blended cost of newly charged energy
  /// and updates the running average cost per kWh stored in the battery.
  /// </summary>
  public async Task OnBatteryPowerChangedAsync(int bat, int currentSocPct, int battCapacityWh, float pvSurplusW, PriceList importPrices)
  {
    var now = DateTime.Now;
    if (_lastBatteryPowerTime != DateTime.MinValue && _lastBatteryPowerW > 0)
    {
      double deltaHours = Math.Min((now - _lastBatteryPowerTime).TotalHours, 0.25);
      float deltaKwh = (float)(_lastBatteryPowerW * deltaHours / 1000.0);

      float pvFraction = Math.Min(1f, pvSurplusW / _lastBatteryPowerW);
      float sourcePrice = (1f - pvFraction) * importPrices.GetPrice(now);

      // Weighted average: blend existing stored energy cost with new charge cost.
      float currentStoredKwh = Math.Max(0.1f, currentSocPct * battCapacityWh / 100f / 1000f);
      _batteryAvgCostPerKwh = (currentStoredKwh * _batteryAvgCostPerKwh + deltaKwh * sourcePrice)
                              / (currentStoredKwh + deltaKwh);
      if (_batteryAvgCostEntity != null)
        await PVCC_EntityManager.SetStateAsync(_batteryAvgCostEntity.EntityId,
          _batteryAvgCostPerKwh.ToString(CultureInfo.InvariantCulture));
    }
    _lastBatteryPowerW = bat;
    _lastBatteryPowerTime = now;
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
