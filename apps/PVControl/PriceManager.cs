using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using static NetDeamon.apps.PVControl.PVControlCommon;
using NetDeamon.apps;
using Math = System.Math;

namespace NetDeamon.apps.PVControl
{
  /// <summary>
  /// Manages electricity price lists, price calculations, and price-derived queries.
  /// Centralises all import/export price logic previously spread across HouseEnergy.
  /// </summary>
  public class PriceManager
  {
    private List<PriceTableEntry> _priceListCache = [];

    /// <summary>Maximum import price (€/kWh) at which force-charging is permitted.</summary>
    public float ForceChargeMaxPrice { get; set; }

    /// <summary>Clears the price list cache, forcing a re-fetch on the next access.</summary>
    public void UpdatePriceList() => _priceListCache = [];

    public List<PriceTableEntry> PriceListNetto
    {
      get
      {
        if (_priceListCache.Count == 0)
        {
          _priceListCache = [];
          if (PVCC_Config.CurrentImportPriceEntity is not null && PVCC_Config.CurrentImportPriceEntity.TryGetJsonAttribute("data", out JsonElement data))
            if (data.Deserialize<List<PriceTableEntry>>()?.OrderBy(x => x.StartTime).ToList() is List<PriceTableEntry> priceList)
            {
              _priceListCache = priceList.Select(p => new PriceTableEntry(
                p.StartTime,
                p.EndTime,
                p.Price
              )).ToList();
            }
        }
        return _priceListCache;
      }
    }

    public List<PriceTableEntry> PriceListImport
    {
      get
      {
        List<PriceTableEntry> resultList = PriceListNetto.Select(p => new PriceTableEntry(
          p.StartTime,
          p.EndTime,
          CalculateBruttoPriceImport(p.Price, true)
          )).ToList();
        return resultList;
      }
    }

    public List<PriceTableEntry> PriceListExport
    {
      get
      {
        if (PVCC_Config.ExportPriceIsVariable)
        {
          return PriceListNetto.Select(p => new PriceTableEntry(
            p.StartTime,
            p.EndTime,
            CalculateBruttoPriceExport(p.Price, true)
            )).ToList();
        }
        else
        {
          return PriceListNetto.Select(p => new PriceTableEntry(
            p.StartTime,
            p.EndTime,
            PVCC_Config.CurrentExportPriceEntity.TryGetStateValue(out float value, numericalGetBaseValue: false) ? value : 0
            )).ToList();
        }
      }
    }

    public float CalculateBruttoPriceExport(float nettoPrice, bool inclNetworkPrice)
    {
      return (nettoPrice * PVCC_Config.ExportPriceMultiplier + PVCC_Config.ExportPriceAddition + (inclNetworkPrice ? PVCC_Config.ExportPriceNetwork : 0)) * (1 + PVCC_Config.ExportPriceTax);
    }

    public float CalculateBruttoPriceImport(float nettoPrice, bool inclNetworkPrice)
    {
      return (nettoPrice * PVCC_Config.ImportPriceMultiplier + PVCC_Config.ImportPriceAddition + (inclNetworkPrice ? PVCC_Config.ImportPriceNetwork : 0)) * (1 + PVCC_Config.ImportPriceTax);
    }

    public float CurrentEnergyPriceNetto
    {
      get
      {
        var now = DateTime.Now;
        return PriceListNetto.FirstOrDefault(p => p.StartTime <= now && p.EndTime >= now).Price;
      }
    }

    public float CurrentEnergyImportPriceTotal => CalculateBruttoPriceImport(CurrentEnergyPriceNetto, true);

    public float CurrentEnergyImportPriceEnergyOnly => CalculateBruttoPriceImport(CurrentEnergyPriceNetto, false);

    public float CurrentEnergyImportPriceNetworkOnly => PVCC_Config.ImportPriceNetwork * (1 + PVCC_Config.ImportPriceTax);

    public float CurrentEnergyExportPriceTotal => CalculateBruttoPriceExport(CurrentEnergyPriceNetto, true);

    public PriceTableEntry CheapestImportWindowToday
    {
      get
      {
        DateTime now = DateTime.Now;
        return PriceListImport.Where(p => p.StartTime >= now.Date && p.EndTime <= now.Date.AddDays(1)).OrderBy(p => p.Price).FirstOrDefault();
      }
    }

    public PriceTableEntry MostExpensiveImportWindowToday
    {
      get
      {
        DateTime now = DateTime.Now;
        return PriceListImport.Where(p => p.StartTime >= now.Date && p.EndTime <= now.Date.AddDays(1)).OrderBy(p => p.Price).LastOrDefault();
      }
    }

    public PriceTableEntry CheapestImportWindowTotal
    {
      get
      {
        return PriceListImport.OrderBy(p => p.Price).First();
      }
    }

    public bool IsNowCheapestImportWindowToday
    {
      get
      {
        var cheapest = CheapestImportWindowToday;
        var now = DateTime.Now;
        return now > cheapest.StartTime && now < cheapest.EndTime;
      }
    }

    public bool IsNowCheapestImportWindowTotal
    {
      get
      {
        var cheapest = CheapestImportWindowTotal;
        var now = DateTime.Now;
        return now > cheapest.StartTime && now < cheapest.EndTime;
      }
    }

    public PriceTableEntry CheapestExportWindowToday
    {
      get
      {
        DateTime now = DateTime.Now;
        return PriceListExport.Where(p => p.StartTime >= now.Date && p.EndTime <= now.Date.AddDays(1)).OrderBy(p => p.Price).FirstOrDefault();
      }
    }

    public PriceTableEntry MostExpensiveExportWindowToday
    {
      get
      {
        DateTime now = DateTime.Now;
        return PriceListExport.Where(p => p.StartTime >= now.Date && p.EndTime <= now.Date.AddDays(1)).OrderBy(p => p.Price).LastOrDefault();
      }
    }

    private Dictionary<int, PriceTableEntry> PriceListRanked
    {
      get
      {
        Dictionary<int, PriceTableEntry> result = [];
        int rank = 1;
        foreach (var entry in PriceListImport.OrderBy(p => p.Price))
        {
          result.Add(rank, entry);
          rank++;
        }
        return result.OrderBy(r => r.Value.StartTime).ToDictionary();
      }
    }

    private List<Tuple<int, PriceTableEntry>> PriceListPercentage
    {
      get
      {
        List<Tuple<int, PriceTableEntry>> result = [];
        float minPrice = PriceListImport.Min(p => p.Price);
        float maxPrice = PriceListImport.Max(p => p.Price);
        foreach (var entry in PriceListImport)
        {
          result.Add(new Tuple<int, PriceTableEntry>(maxPrice - minPrice == 0 ? 0 : (int)Math.Round((entry.Price - minPrice) / (maxPrice - minPrice) * 100, 0), entry));
        }
        return result.OrderBy(r => r.Item2.StartTime).ToList();
      }
    }

    private int GetPriceRank(DateTime dateTime)
    {
      var priceRankAtTime = PriceListRanked.FirstOrDefault(r => r.Value.StartTime.Date == dateTime.Date && r.Value.StartTime.Hour == dateTime.Hour);
      return priceRankAtTime.Key;
    }

    private int GetPricePercentage(DateTime dateTime)
    {
      var pricePercentageAtTime = PriceListPercentage.FirstOrDefault(r => r.Item2.StartTime.Hour == dateTime.Hour);
      return pricePercentageAtTime?.Item1 ?? -1;
    }

    public int CurrentPriceRank => GetPriceRank(DateTime.Now);

    public int CurrentPricePercentage => GetPricePercentage(DateTime.Now);
  }
}
