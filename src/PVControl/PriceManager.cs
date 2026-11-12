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

    /// <summary>Raw netto prices of the export tariff <see cref="ExportTariff.Market"/>, lazily fetched and cached
    /// from <see cref="PVConfig.CurrentExportPriceEntity"/>.</summary>
    public PriceList PriceListNettoIndependentExport
    {
      get
      {
        if (_independentExportPriceListCache.Count == 0)
          _independentExportPriceListCache = FetchNettoPriceList(PVCC_Config.CurrentExportPriceEntity);
        return _independentExportPriceListCache;
      }
    }

    // ── Tariffs ──────────────────────────────────────────────────────────────────────────────

    /// <summary>The import tariff over time (select.pv_control_import_tariff; set at start).</summary>
    public TariffSchedule<ImportTariff> ImportTariffs { get; set; } = new(ImportTariff.Market);
    /// <summary>The export tariff over time (select.pv_control_export_tariff; set at start).</summary>
    public TariffSchedule<ExportTariff> ExportTariffs { get; set; } = new(ExportTariff.SameAsImport);

    /// <summary>The fixed energy price of the import tariff <see cref="ImportTariff.Fixed"/>, netto, €/kWh: the entity's
    /// value, else the configured one.</summary>
    private static float FixedImportNetto =>
      PVCC_Config.ImportPriceFixedEntity is not null && PVCC_Config.ImportPriceFixedEntity.TryGetStateValue(out float v, numericalGetBaseValue: false)
        ? v : PVCC_Config.ImportPriceFixedValue;

    /// <summary>The fixed feed-in price of the export tariff <see cref="ExportTariff.Fixed"/>, €/kWh as paid (no
    /// netto breakdown): the entity's value, else the configured one.</summary>
    private static float FixedExportPrice =>
      PVCC_Config.ExportPriceFixedEntity is not null && PVCC_Config.ExportPriceFixedEntity.TryGetStateValue(out float v, numericalGetBaseValue: false)
        ? v : PVCC_Config.ExportPriceFixedValue;

    /// <summary>The fixed energy price in force at a time, netto, €/kWh; null while the import tariff is the market.</summary>
    /// <param name="t">The time (a quarter's start).</param>
    public float? ImportPriceFixedNettoAt(System.DateTime t) => ImportTariffs.At(t) == ImportTariff.Fixed ? FixedImportNetto : null;

    /// <summary>Netto import prices of one tariff, everywhere: the market price after <see cref="PVConfig.ImportPriceResolution"/>,
    /// or the fixed energy price in the market list's slots (so it reconciles with the shown brutto price).</summary>
    /// <param name="tariff">The tariff.</param>
    private PriceList NettoImportFor(ImportTariff tariff)
    {
      if (tariff != ImportTariff.Fixed) return PriceListNetto.WithResolution(PVCC_Config.ImportPriceResolution);
      float price = FixedImportNetto;   // read once per list
      return new(PriceListNetto.Select(p => new PriceTableEntry(p.StartTime, p.EndTime, price)));
    }

    /// <summary>Brutto import prices of one tariff, everywhere.</summary>
    /// <param name="tariff">The tariff.</param>
    private PriceList ImportFor(ImportTariff tariff) =>
      new(NettoImportFor(tariff).Select(p => new PriceTableEntry(p.StartTime, p.EndTime,
        ImportPrice(p.Price, tariff == ImportTariff.Fixed ? p.Price : null, true, p.StartTime))));

    /// <summary>Netto export prices of one tariff, everywhere: the import market price
    /// (<see cref="ExportTariff.SameAsImport"/>) or the export's own (<see cref="ExportTariff.Market"/>), after
    /// <see cref="PVConfig.ExportPriceResolution"/>; empty for <see cref="ExportTariff.Fixed"/> (no netto breakdown).</summary>
    /// <param name="tariff">The tariff.</param>
    private PriceList NettoExportFor(ExportTariff tariff) => tariff switch
    {
      ExportTariff.Fixed => new PriceList(),
      ExportTariff.Market => PriceListNettoIndependentExport.WithResolution(PVCC_Config.ExportPriceResolution),
      _ => PriceListNetto.WithResolution(PVCC_Config.ExportPriceResolution),
    };

    /// <summary>Brutto export prices of one tariff, everywhere: the fixed price in the market list's slots, else the
    /// scaled netto price.</summary>
    /// <param name="tariff">The tariff.</param>
    private PriceList ExportFor(ExportTariff tariff)
    {
      if (tariff != ExportTariff.Fixed)
        return new(NettoExportFor(tariff).Select(p => new PriceTableEntry(p.StartTime, p.EndTime, ExportPrice(p.Price, null, true))));
      float price = FixedExportPrice;   // read once per list
      return new(PriceListNetto.Select(p => new PriceTableEntry(p.StartTime, p.EndTime, price)));
    }

    /// <summary>Netto prices actually used to compute <see cref="PriceListImport"/>, each quarter by its tariff.</summary>
    public PriceList PriceListNettoImport => ImportTariffs.Prices(NettoImportFor);

    /// <summary>Network fee component of <see cref="PriceListImport"/> (tax-inclusive), per slot,
    /// with any active <see cref="PVConfig.ImportPriceNetworkPeriods"/> override applied.</summary>
    public PriceList PriceListNetworkImport =>
      new(PriceListNettoImport.Select(p => new PriceTableEntry(p.StartTime, p.EndTime, NetworkImportGrossAt(p.StartTime))));

    /// <summary>Brutto import prices, each quarter by its tariff: (netto × multiplier + addition + network) × (1 + tax),
    /// or (fixed + network) × (1 + tax).</summary>
    public PriceList PriceListImport => ImportTariffs.Prices(ImportFor);

    /// <summary>Netto prices behind <see cref="PriceListExport"/>, each quarter by its tariff (none in fixed quarters).</summary>
    public PriceList PriceListNettoExport => ExportTariffs.Prices(NettoExportFor);

    /// <summary>Network fee component of <see cref="PriceListExport"/> (tax-inclusive), per slot (none in fixed quarters).</summary>
    public PriceList PriceListNetworkExport =>
      new(PriceListNettoExport.Select(p => new PriceTableEntry(p.StartTime, p.EndTime, GrossPrice(0, 0, 0, PVCC_Config.ExportPriceNetwork, PVCC_Config.ExportPriceTax))));

    /// <summary>Brutto export prices, each quarter by its tariff: a fixed feed-in price, or the scaled netto price
    /// (the import market price, or the export's own price series).</summary>
    public PriceList PriceListExport => ExportTariffs.Prices(ExportFor);

    /// <summary>The plan's import prices: <see cref="PriceListImport"/> with estimates for the unpublished quarters
    /// (<see cref="PriceList.WithEstimatedPrices"/>). Each tariff is estimated from its own prices before the quarters
    /// are spliced — a market quarter tomorrow is never estimated from today's fixed price; a fixed price is known,
    /// no risk premium.</summary>
    /// <param name="until">Extend up to here.</param>
    /// <param name="premium">Risk premium on estimated market prices.</param>
    public PriceList ImportPricesForPlanning(System.DateTime until, float premium) =>
      ImportTariffs.Prices(t => ImportFor(t).WithEstimatedPrices(until, t == ImportTariff.Fixed ? 0 : premium));

    /// <summary>The plan's export prices, as <see cref="ImportPricesForPlanning"/>.</summary>
    /// <param name="until">Extend up to here.</param>
    /// <param name="premium">Risk premium on estimated market prices (negative: an estimate is worth less).</param>
    public PriceList ExportPricesForPlanning(System.DateTime until, float premium) =>
      ExportTariffs.Prices(t => ExportFor(t).WithEstimatedPrices(until, t == ExportTariff.Fixed ? 0 : premium));

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

    /// <summary>Gross import price of a tariff: the market price × multiplier + addition, or a fixed contract's energy
    /// price as it is (the supplier's margin is in it); network fee and tax on both (<see cref="GrossPrice"/>).</summary>
    /// <param name="marketNetto">Netto market price, €/kWh (ignored with a fixed price).</param>
    /// <param name="fixedNetto">Fixed energy price, netto, €/kWh; null = market price.</param>
    /// <param name="multiplier">Factor on the market price.</param>
    /// <param name="addition">Supplier addition on the market price, €/kWh.</param>
    /// <param name="network">Network fee, €/kWh; 0 to exclude it.</param>
    /// <param name="tax">Tax rate, e.g. 0.2.</param>
    /// <returns>Gross price in €/kWh.</returns>
    public static float GrossImportPrice(float marketNetto, float? fixedNetto, float multiplier, float addition, float network, float tax) =>
      fixedNetto is { } f ? GrossPrice(f, 1, 0, network, tax) : GrossPrice(marketNetto, multiplier, addition, network, tax);

    /// <summary>Gross import price of one quarter (<see cref="GrossImportPrice"/> with the import settings).</summary>
    /// <param name="marketNetto">Netto market price, €/kWh (ignored with a fixed price).</param>
    /// <param name="fixedNetto">The fixed energy price; null = market price.</param>
    /// <param name="inclNetworkPrice">Include the network fee.</param>
    /// <param name="at">The quarter (network price period).</param>
    private float ImportPrice(float marketNetto, float? fixedNetto, bool inclNetworkPrice, System.DateTime at) =>
      GrossImportPrice(marketNetto, fixedNetto, PVCC_Config.ImportPriceMultiplier, PVCC_Config.ImportPriceAddition,
        inclNetworkPrice ? NetworkPriceAt(at) : 0, PVCC_Config.ImportPriceTax);

    /// <summary>Gross export price of one quarter: a fixed feed-in price as it is, else (netto × multiplier + addition
    /// + network) × (1 + tax) with the export settings.</summary>
    /// <param name="marketNetto">Netto market price, €/kWh (ignored with a fixed price).</param>
    /// <param name="fixedPrice">The fixed feed-in price; null = market price.</param>
    /// <param name="inclNetworkPrice">Include the export network fee.</param>
    private static float ExportPrice(float marketNetto, float? fixedPrice, bool inclNetworkPrice) =>
      fixedPrice ?? GrossPrice(marketNetto, PVCC_Config.ExportPriceMultiplier, PVCC_Config.ExportPriceAddition,
        inclNetworkPrice ? PVCC_Config.ExportPriceNetwork : 0, PVCC_Config.ExportPriceTax);

    /// <summary>Gross import price of a quarter by the import tariff in force then (the cost log).</summary>
    /// <param name="nettoPrice">Netto market price in €/kWh (ignored with a fixed price).</param>
    /// <param name="inclNetworkPrice">Include the network fee.</param>
    /// <param name="at">The quarter's start: its tariff and network price period.</param>
    /// <returns>Gross price in €/kWh.</returns>
    public float CalculateBruttoPriceImport(float nettoPrice, bool inclNetworkPrice, System.DateTime at) =>
      ImportPrice(nettoPrice, ImportPriceFixedNettoAt(at), inclNetworkPrice, at);

    /// <summary>Gross export price of a quarter by the export tariff in force then (the cost log).</summary>
    /// <param name="nettoPrice">Netto market price in €/kWh (ignored with a fixed price).</param>
    /// <param name="inclNetworkPrice">Include the export network fee.</param>
    /// <param name="at">The quarter's start: its tariff.</param>
    /// <returns>Gross price in €/kWh.</returns>
    public float CalculateBruttoPriceExport(float nettoPrice, bool inclNetworkPrice, System.DateTime at) =>
      ExportPrice(nettoPrice, ExportTariffs.At(at) == ExportTariff.Fixed ? FixedExportPrice : null, inclNetworkPrice);

    /// <summary>Network fee of the import price incl. tax.</summary>
    /// <param name="t">Time for the network price period.</param>
    /// <returns>€/kWh.</returns>
    private float NetworkImportGrossAt(System.DateTime t) => GrossPrice(0, 0, 0, NetworkPriceAt(t), PVCC_Config.ImportPriceTax);

    // ── Component price breakdowns (need config, no dedicated PriceList) ────────────────────

    /// <summary>Gross import price now without the network fee, €/kWh.</summary>
    public float CurrentEnergyImportPriceEnergyOnly
    {
      get
      {
        var now = System.DateTime.Now;
        return CalculateBruttoPriceImport(PriceListNettoImport.GetPrice(now), false, now);
      }
    }

    /// <summary>Network fee of the import price now incl. tax, €/kWh.</summary>
    public float CurrentEnergyImportPriceNetworkOnly => NetworkImportGrossAt(System.DateTime.Now);
  }
}
