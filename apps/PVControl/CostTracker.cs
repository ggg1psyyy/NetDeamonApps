using System;
using System.Globalization;
using System.Threading.Tasks;
using NetDaemon.HassModel.Entities;
using static NetDeamon.apps.PVControl.PVControlCommon;
using Math = System.Math;

namespace NetDeamon.apps.PVControl;

/// <summary>Tracks energy costs in HA sensors: import/export sums, the battery's average stored cost.</summary>
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

  /// <summary>Starts from the current daily import/export counters.</summary>
  /// <exception cref="NullReferenceException">The daily energy entities are not configured.</exception>
  public CostTracker()
  {
    if (PVCC_Config.DailyExportEnergyEntity is null || PVCC_Config.DailyImportEnergyEntity is null)
      throw new NullReferenceException("DailyEnergyEntities not available");
    if (PVCC_Config.DailyExportEnergyEntity.TryGetStateValue(out float lastExportEnergySum))
      _lastExportEnergySum = lastExportEnergySum / 1000;
    if (PVCC_Config.DailyImportEnergyEntity.TryGetStateValue(out float lastImportEnergySum))
      _lastImportEnergySum = lastImportEnergySum / 1000;
  }

  /// <summary>Adds an amount to a sum sensor.</summary>
  /// <param name="entity">Sum sensor (€); null = nothing to do.</param>
  /// <param name="deltaEur">Amount to add in €.</param>
  private async Task AddToSumEntityAsync(Entity? entity, float deltaEur)
  {
    if (entity is null) return;
    // numericalGetBaseValue:false — value is stored raw in €, no unit-multiplier conversion.
    float current = entity.TryGetStateValue(out float v, numericalGetBaseValue: false) ? v : 0f;
    await PVCC_EntityManager.SetStateAsync(entity.EntityId, (current + deltaEur).ToString(CultureInfo.InvariantCulture));
  }

  /// <summary>Sets the net cost sensor to import cost − export earnings.</summary>
  private async Task UpdateNetCostEntityAsync()
  {
    if (SumImportExportNetCostEntity is null || SumImportCostBruttoEntity is null || SumExportEarningsEntity is null) return;
    float imp = SumImportCostBruttoEntity.TryGetStateValue(out float i, numericalGetBaseValue: false) ? i : 0f;
    float exp = SumExportEarningsEntity.TryGetStateValue(out float e, numericalGetBaseValue: false) ? e : 0f;
    await PVCC_EntityManager.SetStateAsync(SumImportExportNetCostEntity.EntityId, (imp - exp).ToString(CultureInfo.InvariantCulture));
  }

  /// <summary>Blends the energy charged since the last reading into the battery's average cost.</summary>
  /// <param name="batteryInputKwh">Battery input counter (kWh, daily; resets at midnight).</param>
  /// <param name="sources">Sources of the charge (<see cref="SourceShares.ForBatteryCharge"/>); PV is free.</param>
  /// <param name="importPrices">Gross import prices.</param>
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
    _batteryAvgCostPerKwh = BlendAverageCost(Soc.ToWh(socPct, capWh) / 1000f, _batteryAvgCostPerKwh, deltaKwh, sourcePrice);
    if (_batteryAvgCostEntity != null)
      await PVCC_EntityManager.SetStateAsync(_batteryAvgCostEntity.EntityId,
        _batteryAvgCostPerKwh.ToString(CultureInfo.InvariantCulture));
  }

  /// <summary>New average cost after a charge, clamped to the plausible range.</summary>
  /// <param name="storedKwh">Energy in the battery before the charge, kWh (at least 0.1).</param>
  /// <param name="avgCostPerKwh">Average cost of that energy, €/kWh.</param>
  /// <param name="deltaKwh">Energy charged, kWh.</param>
  /// <param name="pricePerKwh">Cost of the charged energy, €/kWh.</param>
  /// <returns>Weighted mean, €/kWh.</returns>
  public static float BlendAverageCost(float storedKwh, float avgCostPerKwh, float deltaKwh, float pricePerKwh)
  {
    return Math.Clamp((float)Mean.Weighted(avgCostPerKwh, Math.Max(0.1f, storedKwh), pricePerKwh, deltaKwh),
      BatteryAvgCostMinEurPerKwh, BatteryAvgCostMaxEurPerKwh);
  }

  /// <summary>Adds the export since the last reading at the current export price to the earnings sum.</summary>
  /// <param name="export">Daily export counter in Wh.</param>
  /// <param name="exportPrices">Gross export prices.</param>
  public async Task OnExportEnergyChangedAsync(float export, PriceList exportPrices)
  {
    float diff = CounterDeltaKwh(ref _lastExportEnergySum, export);
    if (diff > 0)
    {
      await AddToSumEntityAsync(SumExportEarningsEntity, diff * exportPrices.GetPriceOrMean(DateTime.Now));
      await UpdateNetCostEntityAsync();
    }
  }

  /// <summary>Adds the import since the last reading to the import cost sums (gross, energy only, network only).</summary>
  /// <param name="import">Daily import counter in Wh.</param>
  /// <param name="importPrices">Gross import prices.</param>
  /// <param name="energyOnlyPrice">Current gross price without network fee, €/kWh.</param>
  /// <param name="networkOnlyPrice">Current network fee incl. tax, €/kWh.</param>
  public async Task OnImportEnergyChangedAsync(float import, PriceList importPrices, float energyOnlyPrice, float networkOnlyPrice)
  {
    float diff = CounterDeltaKwh(ref _lastImportEnergySum, import);
    if (diff > 0)
    {
      await AddToSumEntityAsync(SumImportCostBruttoEntity, diff * importPrices.GetPriceOrMean(DateTime.Now));
      await AddToSumEntityAsync(SumImportCostEnergyOnlyEntity, diff * energyOnlyPrice);
      await AddToSumEntityAsync(SumImportCostNetworkOnlyEntity, diff * networkOnlyPrice);
      await UpdateNetCostEntityAsync();
    }
  }

  /// <summary>Energy since the last reading of a daily counter.</summary>
  /// <param name="lastKwh">Last reading in kWh; set to this reading.</param>
  /// <param name="counterWh">This reading in Wh.</param>
  /// <returns>kWh since the last reading; negative at the midnight reset.</returns>
  private static float CounterDeltaKwh(ref float lastKwh, float counterWh)
  {
    float diff = counterWh / 1000 - lastKwh;
    lastKwh = counterWh / 1000;
    return diff;
  }
}
