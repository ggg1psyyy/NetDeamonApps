using System.Linq;
using System.Text.Json;
using NetDaemon.HassModel.Entities;
using static NetDeamon.apps.PVControl.PVControlCommon;
using NetDeamon.apps;

namespace NetDeamon.apps.PVControl
{
  /// <summary>
  /// Builds and caches the three price lists (netto, import brutto, export brutto) from the
  /// Home Assistant EPEX Spot entity and the tariff configuration. All per-list query methods
  /// live on <see cref="PriceList"/> itself.
  /// </summary>
  public class PriceManager
  {
    private PriceList _priceListCache = new();
    private PriceList _independentExportPriceListCache = new();

    /// <summary>Maximum import price (€/kWh) at which force-charging is permitted.</summary>
    public float ForceChargeMaxPrice { get; set; }

    /// <summary>Fetches the netto prices again.</summary>
    /// <returns>True if they differ from the ones held so far (the price entities change state every quarter and with
    /// every fetch; a replan is only worth a real change).</returns>
    public bool UpdatePriceList()
    {
      var import = FetchNettoPriceList(PVCC_Config.CurrentImportPriceEntity);
      var export = FetchNettoPriceList(PVCC_Config.CurrentExportPriceEntity);
      bool changed = !import.SameAs(_priceListCache) || !export.SameAs(_independentExportPriceListCache);
      _priceListCache = import;
      _independentExportPriceListCache = export;
      return changed;
    }

    /// <summary>Netto prices from a price entity's "data" attribute (ha_epex_spot format), quarter-hourly.</summary>
    /// <param name="sourceEntity">Price entity; null gives an empty list.</param>
    /// <returns>The prices in time order; empty without valid data.</returns>
    private static PriceList FetchNettoPriceList(Entity? sourceEntity)
    {
      if (sourceEntity is not null && sourceEntity.TryGetJsonAttribute("data", out JsonElement data)
          && data.Deserialize<System.Collections.Generic.List<PriceTableEntry>>()
              ?.OrderBy(x => x.StartTime).ToList() is System.Collections.Generic.List<PriceTableEntry> priceList)
      {
        return new PriceList(priceList.Select(p => new PriceTableEntry(p.StartTime, p.EndTime, p.Price)))
          .NormalizeToQuarterHourly();
      }
      return new PriceList();
    }

    /// <summary>End of the price data of a source.</summary>
    /// <param name="sourceEntity">Price entity with a "data" attribute.</param>
    /// <returns>Latest EndTime; null without valid data.</returns>
    public static System.DateTime? DataEnd(Entity? sourceEntity) =>
      sourceEntity is not null && sourceEntity.TryGetJsonAttribute("data", out JsonElement data)
        && data.Deserialize<System.Collections.Generic.List<PriceTableEntry>>() is { Count: > 0 } list
        ? list.Max(p => p.EndTime) : null;

    /// <summary>Which price source to use: the one whose data reaches furthest (tie: the earlier one).</summary>
    /// <param name="dataEnds">End of each source's data (<see cref="DataEnd"/>), in configured order.</param>
    /// <returns>Index of the source; null if none has data.</returns>
    public static int? FurthestSource(System.Collections.Generic.IReadOnlyList<System.DateTime?> dataEnds)
    {
      int? best = null;
      for (int i = 0; i < dataEnds.Count; i++)
        if (dataEnds[i] is { } end && (best is null || end > dataEnds[best.Value]!.Value))
          best = i;
      return best;
    }

    /// <summary>Raw EPEX Spot netto import prices (€/kWh), lazily fetched and cached.</summary>
    public PriceList PriceListNetto
    {
      get
      {
        if (_priceListCache.Count == 0)
          _priceListCache = FetchNettoPriceList(PVCC_Config.CurrentImportPriceEntity);
        return _priceListCache;
      }
    }

    /// <summary>Raw netto prices for the independent-variable export price case (neither
    /// <see cref="PVConfig.ExportPriceSameAsImport"/> nor <see cref="PVConfig.ExportPriceFixed"/>),
    /// lazily fetched and cached from <see cref="PVConfig.CurrentExportPriceEntity"/>.</summary>
    public PriceList PriceListNettoIndependentExport
    {
      get
      {
        if (_independentExportPriceListCache.Count == 0)
          _independentExportPriceListCache = FetchNettoPriceList(PVCC_Config.CurrentExportPriceEntity);
        return _independentExportPriceListCache;
      }
    }

    /// <summary>Netto prices actually used to compute <see cref="PriceListImport"/> — i.e. <see cref="PriceListNetto"/>
    /// after applying <see cref="PVConfig.ImportPriceResolution"/> (so it reconciles with the shown brutto price).</summary>
    public PriceList PriceListNettoImport => PriceListNetto.WithResolution(PVCC_Config.ImportPriceResolution);

    /// <summary>Network fee component of <see cref="PriceListImport"/> (tax-inclusive), per slot,
    /// with any active <see cref="PVConfig.ImportPriceNetworkPeriods"/> override applied.</summary>
    public PriceList PriceListNetworkImport =>
      new(PriceListNettoImport.Select(p => new PriceTableEntry(p.StartTime, p.EndTime, NetworkImportGrossAt(p.StartTime))));

    /// <summary>Brutto import prices (netto × multiplier + addition + network) × (1 + tax).</summary>
    public PriceList PriceListImport =>
      new(PriceListNettoImport
          .Select(p => new PriceTableEntry(p.StartTime, p.EndTime, CalculateBruttoPriceImport(p.Price, true, p.StartTime))));

    /// <summary>Netto prices actually used to compute <see cref="PriceListExport"/> when not
    /// <see cref="PVConfig.ExportPriceFixed"/> — either <see cref="PriceListNetto"/> (reused from
    /// import, when <see cref="PVConfig.ExportPriceSameAsImport"/>) or the independent export
    /// price series. Empty when <see cref="PVConfig.ExportPriceFixed"/> is true (a fixed feed-in
    /// tariff has no netto breakdown).</summary>
    public PriceList PriceListNettoExport =>
      PVCC_Config.ExportPriceFixed ? new PriceList()
      : (PVCC_Config.ExportPriceSameAsImport ? PriceListNetto : PriceListNettoIndependentExport)
          .WithResolution(PVCC_Config.ExportPriceResolution);

    /// <summary>Network fee component of <see cref="PriceListExport"/> (tax-inclusive), per slot.
    /// Empty when <see cref="PVConfig.ExportPriceFixed"/> is true.</summary>
    public PriceList PriceListNetworkExport =>
      new(PriceListNettoExport.Select(p => new PriceTableEntry(p.StartTime, p.EndTime, GrossPrice(0, 0, 0, PVCC_Config.ExportPriceNetwork, PVCC_Config.ExportPriceTax))));

    /// <summary>Brutto export prices — either a fixed feed-in tariff (entity, falling back to a
    /// static value), or scaled netto (reused from import, or an independent export price series).</summary>
    public PriceList PriceListExport
    {
      get
      {
        if (PVCC_Config.ExportPriceFixed)
        {
          float fixedPrice = PVCC_Config.ExportPriceFixedEntity is not null
              && PVCC_Config.ExportPriceFixedEntity.TryGetStateValue(out float v, numericalGetBaseValue: false)
            ? v : PVCC_Config.ExportPriceFixedValue;
          return new(PriceListNetto.Select(p => new PriceTableEntry(p.StartTime, p.EndTime, fixedPrice)));
        }

        return new(PriceListNettoExport.Select(p => new PriceTableEntry(p.StartTime, p.EndTime, CalculateBruttoPriceExport(p.Price, true))));
      }
    }

    // ── Brutto price calculations ────────────────────────────────────────────────────────────

    /// <summary>The network price period active at a time.</summary>
    /// <param name="t">Time to check.</param>
    /// <returns>The first period whose month/hour window contains <paramref name="t"/>; null if none.</returns>
    private NetworkPricePeriod? ActivePeriodAt(System.DateTime t) =>
      PVCC_Config.ImportPriceNetworkPeriods?.FirstOrDefault(p => p.IsActiveAt(t));

    /// <summary>Network import price (netto) at a time.</summary>
    /// <param name="t">Time to check.</param>
    /// <returns>The active period's price, else <see cref="PVConfig.ImportPriceNetwork"/>; €/kWh.</returns>
    private float NetworkPriceAt(System.DateTime t) =>
      ActivePeriodAt(t)?.Price ?? PVCC_Config.ImportPriceNetwork;

    /// <summary>Name of the active network price period; "Standard" if none.</summary>
    public string ActiveNetworkPricePeriodName =>
      ActivePeriodAt(System.DateTime.Now)?.Name ?? "Standard";

    /// <summary>The one gross price formula, for import and export: (netto × multiplier + addition + network) × (1 + tax).</summary>
    /// <param name="netto">Netto market price in €/kWh.</param>
    /// <param name="multiplier">Factor on the market price.</param>
    /// <param name="addition">Supplier addition in €/kWh.</param>
    /// <param name="network">Network fee in €/kWh; 0 to exclude it.</param>
    /// <param name="tax">Tax rate, e.g. 0.2.</param>
    /// <returns>Gross price in €/kWh.</returns>
    public static float GrossPrice(float netto, float multiplier, float addition, float network, float tax) =>
      (netto * multiplier + addition + network) * (1 + tax);

    /// <summary>Gross import price (<see cref="GrossPrice"/> with the import tariff).</summary>
    /// <param name="nettoPrice">Netto market price in €/kWh.</param>
    /// <param name="inclNetworkPrice">Include the network fee.</param>
    /// <param name="at">Time for the network price period; null = now.</param>
    /// <returns>Gross price in €/kWh.</returns>
    public float CalculateBruttoPriceImport(float nettoPrice, bool inclNetworkPrice, System.DateTime? at = null) =>
      GrossPrice(nettoPrice, PVCC_Config.ImportPriceMultiplier, PVCC_Config.ImportPriceAddition,
        inclNetworkPrice ? NetworkPriceAt(at ?? System.DateTime.Now) : 0, PVCC_Config.ImportPriceTax);

    /// <summary>Gross export price (<see cref="GrossPrice"/> with the export tariff).</summary>
    /// <param name="nettoPrice">Netto market price in €/kWh.</param>
    /// <param name="inclNetworkPrice">Include the export network fee.</param>
    /// <returns>Gross price in €/kWh.</returns>
    public float CalculateBruttoPriceExport(float nettoPrice, bool inclNetworkPrice) =>
      GrossPrice(nettoPrice, PVCC_Config.ExportPriceMultiplier, PVCC_Config.ExportPriceAddition,
        inclNetworkPrice ? PVCC_Config.ExportPriceNetwork : 0, PVCC_Config.ExportPriceTax);

    /// <summary>Network fee of the import price incl. tax.</summary>
    /// <param name="t">Time for the network price period.</param>
    /// <returns>€/kWh.</returns>
    private float NetworkImportGrossAt(System.DateTime t) => GrossPrice(0, 0, 0, NetworkPriceAt(t), PVCC_Config.ImportPriceTax);

    // ── Component price breakdowns (need config, no dedicated PriceList) ────────────────────

    /// <summary>Gross import price now without the network fee, €/kWh.</summary>
    public float CurrentEnergyImportPriceEnergyOnly => CalculateBruttoPriceImport(PriceListNettoImport.GetPrice(System.DateTime.Now), false);

    /// <summary>Network fee of the import price now incl. tax, €/kWh.</summary>
    public float CurrentEnergyImportPriceNetworkOnly => NetworkImportGrossAt(System.DateTime.Now);
  }
}
