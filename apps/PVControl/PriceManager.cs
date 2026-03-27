using System.Linq;
using System.Text.Json;
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

    /// <summary>Maximum import price (€/kWh) at which force-charging is permitted.</summary>
    public float ForceChargeMaxPrice { get; set; }

    /// <summary>Clears the netto cache, forcing a re-fetch on the next access.</summary>
    public void UpdatePriceList() => _priceListCache = new();

    /// <summary>Raw EPEX Spot netto prices (€/kWh), lazily fetched and cached.</summary>
    public PriceList PriceListNetto
    {
      get
      {
        if (_priceListCache.Count == 0)
        {
          if (PVCC_Config.CurrentImportPriceEntity is not null
              && PVCC_Config.CurrentImportPriceEntity.TryGetJsonAttribute("data", out JsonElement data))
          {
            if (data.Deserialize<System.Collections.Generic.List<PriceTableEntry>>()
                    ?.OrderBy(x => x.StartTime).ToList() is System.Collections.Generic.List<PriceTableEntry> priceList)
            {
              _priceListCache = new PriceList(priceList.Select(p => new PriceTableEntry(p.StartTime, p.EndTime, p.Price)));
            }
          }
        }
        return _priceListCache;
      }
    }

    /// <summary>Brutto import prices (netto × multiplier + addition + network) × (1 + tax).</summary>
    public PriceList PriceListImport =>
      new(PriceListNetto.Select(p => new PriceTableEntry(p.StartTime, p.EndTime, CalculateBruttoPriceImport(p.Price, true, p.StartTime))));

    /// <summary>Brutto export prices — either variable (scaled netto) or fixed feed-in tariff.</summary>
    public PriceList PriceListExport
    {
      get
      {
        if (PVCC_Config.ExportPriceIsVariable)
          return new(PriceListNetto.Select(p => new PriceTableEntry(p.StartTime, p.EndTime, CalculateBruttoPriceExport(p.Price, true))));

        return new(PriceListNetto.Select(p => new PriceTableEntry(
          p.StartTime, p.EndTime,
          PVCC_Config.CurrentExportPriceEntity.TryGetStateValue(out float value, numericalGetBaseValue: false) ? value : 0)));
      }
    }

    // ── Brutto price calculations ────────────────────────────────────────────────────────────

    /// <summary>Returns the first period whose month+hour window contains <paramref name="t"/>, or null.</summary>
    private NetworkPricePeriod? ActivePeriodAt(System.DateTime t)
    {
      if (PVCC_Config.ImportPriceNetworkPeriods != null)
        foreach (var p in PVCC_Config.ImportPriceNetworkPeriods)
          if (t.Month >= p.StartMonth && t.Month <= p.EndMonth
              && t.Hour >= p.StartHour && t.Hour < p.EndHour)
            return p;
      return null;
    }

    /// <summary>Returns the effective network import price at the given time,
    /// applying any matching <see cref="PVConfig.ImportPriceNetworkPeriods"/> override.</summary>
    private float NetworkPriceAt(System.DateTime t) =>
      ActivePeriodAt(t)?.Price ?? PVCC_Config.ImportPriceNetwork;

    /// <summary>Name of the currently active network price period, or "Standard" if none.</summary>
    public string ActiveNetworkPricePeriodName =>
      ActivePeriodAt(System.DateTime.Now)?.Name ?? "Standard";

    public float CalculateBruttoPriceImport(float nettoPrice, bool inclNetworkPrice, System.DateTime? at = null) =>
      (nettoPrice * PVCC_Config.ImportPriceMultiplier + PVCC_Config.ImportPriceAddition
        + (inclNetworkPrice ? NetworkPriceAt(at ?? System.DateTime.Now) : 0)) * (1 + PVCC_Config.ImportPriceTax);

    public float CalculateBruttoPriceExport(float nettoPrice, bool inclNetworkPrice) =>
      (nettoPrice * PVCC_Config.ExportPriceMultiplier + PVCC_Config.ExportPriceAddition
        + (inclNetworkPrice ? PVCC_Config.ExportPriceNetwork : 0)) * (1 + PVCC_Config.ExportPriceTax);

    // ── Component price breakdowns (need config, no dedicated PriceList) ────────────────────

    /// <summary>Brutto import price at current time, excluding network fee.</summary>
    public float CurrentEnergyImportPriceEnergyOnly => CalculateBruttoPriceImport(PriceListNetto.GetPrice(System.DateTime.Now), false);

    /// <summary>Network fee component of the current import price (tax-inclusive), with any active period override applied.</summary>
    public float CurrentEnergyImportPriceNetworkOnly => NetworkPriceAt(System.DateTime.Now) * (1 + PVCC_Config.ImportPriceTax);
  }
}
