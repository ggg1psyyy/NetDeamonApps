using LinqToDB;
using NetDaemon.AppModel;
using NetDaemon.HassModel.Entities;
using PVControl;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using System.Collections;
using LinqToDB.Mapping;

namespace PVControl
{
  public class HouseEnergy
  {
    public struct EpexPriceTableEntry(DateTime startTime, DateTime endTime, float price)
    {
      [JsonPropertyName("start_time")]
      public DateTime StartTime { get; set; } = startTime;
      [JsonPropertyName("end_time")]
      public DateTime EndTime { get; set; } = endTime;
      [JsonPropertyName("price_ct_per_kwh")]
      public float Price { get; set; } = price;
    }
    public enum InverterModes
    {
      normal = 0,
      force_charge = 1,
      grid_only = 2,
    }
    public enum BatteryStatuses
    {
      idle,
      charging,
      discharging,
      unknown,
    }
    private readonly PVConfig _config;
    private readonly FixedSizeQueue<int> _battChargeFIFO;
    /// <summary>
    /// Default Efficiency if not set in config
    /// </summary>
    private readonly float _defaultInverterEfficiency = 0.9f;

    public HouseEnergy(PVConfig config)
    { 
      _config = config;
      _battChargeFIFO = new FixedSizeQueue<int>(12);
      _config.CurrentImportPriceEntity?.StateAllChanges().SubscribeAsync(async _ => await UserStateChanged(_config.CurrentImportPriceEntity));
      _config.CurrentBatteryPowerEntity?.StateChanges().SubscribeAsync(async _ => await UserStateChanged(_config.CurrentBatteryPowerEntity));
      PreferredMinBatterySoC = 30;
      EnforcePreferredSoC = false;
      _energyUsagePerHourCache = [];
      _energyUsagePerWeekDayCache = [];
      _energyUsagePerDayOfYearCache = [];
      _priceListCache = [];
    }
    /// <summary>
    /// Enforce the set preferred minimal Soc, if not enforced it's allowed to go down to AbsoluteMinimalSoC to reach cheaper prices or PV charge
    /// </summary>
    public bool EnforcePreferredSoC {  get; set; }
    public int PreferredMinBatterySoC { get; set; }
#pragma warning disable CS1998 // Async method lacks 'await' operators and will run synchronously
    private async Task UserStateChanged(Entity entity)
#pragma warning restore CS1998 // Async method lacks 'await' operators and will run synchronously
    {
      if (entity.EntityId == _config.CurrentImportPriceEntity?.EntityId)
      {
        UpdatePriceList();
      }
      if (entity.EntityId == _config.CurrentBatteryPowerEntity?.EntityId)
      {
        UpdateBatteryPowerQueue();
      }
    }
    private InverterModes _currentMode;
    public InverterModes ProposedMode
    {
      get
      {
        if (CurrentEnergyImportPrice < CurrentEnergyExportPrice * -1)
        {
          _currentMode = InverterModes.grid_only;
          return InverterModes.grid_only;
        }

        else if (
          NeedToChargeFromExternal.Item1
          // stay 2 minutes away from extremes, to make sure we have the same time as the provider
          && DateTime.Now > BestChargeTime.StartTime.AddMinutes(2) && DateTime.Now < BestChargeTime.EndTime.AddMinutes(-2)
          // don't charge over 98% SoC as it get's really slow and inefficient
          && BatterySoc <= 98
          )
        {
          _currentMode = InverterModes.force_charge;
          return InverterModes.force_charge;
        }

        else
        {
          _currentMode = InverterModes.normal;
          return InverterModes.normal;
        }
      }
    }
    /// <summary>
    /// returns:
    /// * Do we need to charge until NextRelevantPVEnergy
    /// * When do we reach minimal Soc
    /// * what's the estimated SoC at this time
    /// </summary>
    public Tuple<bool, DateTime, int> NeedToChargeFromExternal
    {
      get
      {
        var estSoC = EstimatedBatterySoCTodayAndTomorrow;
        int setMinCharge = PreferredMinimalSoC;
        DateTime now = DateTime.Now;

        if (!EnforcePreferredSoC && PreferredMinimalSoC > AbsoluteMinimalSoC)
        {
          var prefMinSoC = estSoC.Where(e => e.Key >= now && e.Key < PriceList.Last().EndTime && e.Value <= PreferredMinimalSoC).FirstOrDefault();
          // if we reach PreferredMinimalSoC we need to search for cheaper price windows, but never go under AbsoluteMinimalSoC
          if (prefMinSoC.Key != default)
          {
            // get the first time we reach a minimum > AbsoluteMinimum
            var socAfterPrefMinSoc = estSoC.Where(x => x.Key > prefMinSoC.Key && x.Key < PriceList.Last().EndTime);
            var minSocAfterPref = prefMinSoC;
            foreach (var s in socAfterPrefMinSoc)
            {
              if (s.Value <= minSocAfterPref.Value && s.Value < PreferredMinimalSoC && s.Value > AbsoluteMinimalSoC)
                minSocAfterPref = s;
              else
                break;
            }
            var maxAfter = estSoC.FirstMaxOrDefault(start: minSocAfterPref.Key);
            var minAfter = estSoC.FirstMinOrDefault(start: minSocAfterPref.Key);
            // if we reach at least 95% SoC via PV charge and never go down to AbsoluteMinimalSoC we don't need to charge
            bool noNeedToCharge = maxAfter.Value > 95 && minAfter.Value > AbsoluteMinimalSoC && minAfter.Key > maxAfter.Key;
            // now see if there is a cheaper price window between Preferred and Absolute MinSoC
            if (minSocAfterPref.Key != prefMinSoC.Key && !noNeedToCharge)
            {
              var cheapestPref = SortPriceListByCheapestPeriod(now, prefMinSoC.Key).Select(p => p.Price).First();
              var cheapestAbs = SortPriceListByCheapestPeriod(prefMinSoC.Key, minSocAfterPref.Key).Select(p => p.Price).First();
              // if there is a cheaper window we will allow to go down to the estimated minima
              if (cheapestAbs < cheapestPref)
                setMinCharge = Math.Max(AbsoluteMinimalSoC, minSocAfterPref.Value);
            }
          }
        }
        // while charging increase minCharge and maxCharge to prevent hysteresis
        int minCharge = _currentMode == InverterModes.force_charge ? setMinCharge + 1 : setMinCharge;
        // we don't want to charge more if we reach 100% SoC with PV
        int maxCharge = _currentMode == InverterModes.force_charge ? 100 : 98;
        // we need to make it at least to the next PV charge without going under minCharge
        DateTime relevantTime = PriceList.Last().EndTime > FirstRelevantPVEnergyTomorrow ? PriceList.Last().EndTime : FirstRelevantPVEnergyTomorrow;
        int min = estSoC.Where(n => n.Key > now && n.Key <= relevantTime).Min(n => n.Value);
        // if we still have PV yield today, check what SoC we will reach, otherwise max can never be greater than current SoC
        int max = now < LastRelevantPVEnergyToday ? estSoC.Where(n => n.Key > now && n.Key <= LastRelevantPVEnergyToday).Max(n => n.Value) : BatterySoc;
        // charge to Max if in absolut cheapest period when we never reach 100% SoC today or tomorrow with PV only and if we have at least 12h price preview
        if (PriceList.Where(p => p.StartTime > now).Count() > 12)
          if (CurrentEnergyImportPrice < PriceList.Where(p => p.StartTime > now).Min(p => p.Price) && max < 99 && estSoC.Where(e => e.Key.Date == now.Date.AddDays(1)).Max(e => e.Value) < 99)
            minCharge = 100;
        // We need to force charge if we estimate to fall below minCharge and can't reach 100% before that
        bool needCharge = min < minCharge && max < maxCharge;
        return new Tuple<bool, DateTime, int>(needCharge, estSoC.Where(n => n.Key > now && n.Key <= relevantTime && n.Value == min).First().Key, min);
      }
    }
    /// <summary>
    /// Current State of Charge of the house battery in %
    /// </summary>
    public int BatterySoc
    {
      get
      {
        return (_config.BatterySoCEntity is not null && _config.BatterySoCEntity.TryGetStateValue<int>(out int soc)) ? soc : 0;
      }
    }
    /// <summary>
    /// Minimal SoC of battery which may not be used normally
    /// if override is active, we try not to go below, but allow if it's cheaper to wait, but we can never go under AbsoluteMinimalSoC (Inverter set limit)
    /// </summary>
    public int PreferredMinimalSoC
    {
      get
      {
        // Preferred can never be lower than AbsoluteMinimalSoC
        return Math.Max(PreferredMinBatterySoC, AbsoluteMinimalSoC);
      }
    }
    public int AbsoluteMinimalSoC
    {
      get
      {
        int minAllowedSoC = _config.MinBatterySoCValue ?? 0;
        if (_config.MinBatterySoCEntity is not null && _config.MinBatterySoCEntity.TryGetStateValue<int>(out int minSoc))
          minAllowedSoC = minSoc;
        return minAllowedSoC;
      }
    }
    private float InverterEfficiency
    {
      get
      {
        return _config.InverterEfficiency is not null ? (float)_config.InverterEfficiency : _defaultInverterEfficiency;
      }
    }
    /// <summary>
    /// BatteryCapacity in Wh
    /// </summary>
    public int BatteryCapacity
    {
      get
      {
        float batteryCapacity = _config.BatteryCapacityValue ?? 0;
        if (_config.BatteryCapacityEntity is not null && _config.BatteryCapacityEntity.TryGetStateValue<float>(out float battCapacity))
          batteryCapacity = battCapacity;
        return (int)batteryCapacity;
      }
    }
    /// <summary>
    /// return the batterystatus acoording to the current average charge power
    /// </summary>
    public BatteryStatuses BatteryStatus
    {
      get
      {
        if (AverageBatteryChargeDischargePower > -10 && AverageBatteryChargeDischargePower < 10)
          return BatteryStatuses.idle;
        else if (AverageBatteryChargeDischargePower > 0)
          return BatteryStatuses.charging;
        else if (AverageBatteryChargeDischargePower < 0)
          return BatteryStatuses.discharging;
        else
          return BatteryStatuses.unknown;

      }
    }
    public int MaxBatteryChargePower
    {
      get
      {
        int maxPower = _config.MaxBatteryChargeCurrrentValue != null ? (int)_config.MaxBatteryChargeCurrrentValue : 10;
        if (_config.MaxBatteryChargeCurrrentEntity is not null && _config.MaxBatteryChargeCurrrentEntity.TryGetStateValue<int>(out int max))
          maxPower = max;

        return maxPower;
      }
    }
    /// <summary>
    /// remaining PV yield forecast for today in WH
    /// </summary>
    public int PVEnergyForecastRemainingToday
    {
      get
      {
        return GetPVForecastForPeriod(DateTime.Now, DateTime.Now.Date.AddHours(23).AddMinutes(59).AddSeconds(59));
      }
    }
    public float CurrentEnergyImportPrice
    {
      get
      {
        return (_config.CurrentImportPriceEntity is not null && _config.CurrentImportPriceEntity.TryGetStateValue<float>(out float value)) ? value : 0;
      }
    }
    public float CurrentEnergyExportPrice
    {
      get
      {
        return (_config.CurrentExportPriceEntity is not null && _config.CurrentExportPriceEntity.TryGetStateValue<float>(out float value)) ? value : 0;
      }
    }
    /// <summary>
    /// Currently usable energy in battery (only down to <see cref="HouseEnergy.PreferredMinimalSoC"/>) in Wh
    /// </summary>
    public int UsableBatteryEnergy
    {
      get
      {
        return CalculateBatteryEnergyAtSoC(BatterySoc);
      }
    }
    public int ReserveBatteryEnergy
    {
      get
      {
        return CalculateBatteryEnergyAtSoC(PreferredMinimalSoC,0);
      }
    }
    public int EstimateBatteryEnergyAtTime(DateTime time)
    {      
      return EstimatedBatteryEnergyTodayAndTomorrow.OrderBy(k => Math.Abs((k.Key - time).Ticks)).First().Value;
    }
    public DateTime FirstRelevantPVEnergyToday
    {
      get
      {
        var result = EstimatedNetEnergyTodayAndTomorrow.Where(f => f.Key.Date == DateTime.Now.Date && f.Value > 50).Select(f => f.Key).FirstOrDefault();
        return result != default ? result : DateTime.Now.Date.AddDays(2).AddMinutes(-1);
      }
    }
    public DateTime FirstRelevantPVEnergyTomorrow
    {
      get
      {
        var result = EstimatedNetEnergyTodayAndTomorrow.Where(f => f.Key.Date == DateTime.Now.Date.AddDays(1) && f.Value > 50).Select(f => f.Key).FirstOrDefault();
        return result != default ? result : DateTime.Now.Date.AddDays(2).AddMinutes(-1);
      }
    }
    public DateTime LastRelevantPVEnergyToday
    {
      get
      {
        var result = EstimatedNetEnergyTodayAndTomorrow.Where(f => f.Key.Date == DateTime.Now.Date && f.Value > 50).Select(f => f.Key).LastOrDefault();
        return result != default ? result : DateTime.Now.Date.AddDays(2).AddMinutes(-1);
      }
    }
    public DateTime LastRelevantPVEnergyTomorrow
    {
      get
      {
        var result = EstimatedNetEnergyTodayAndTomorrow.Where(f => f.Key.Date == DateTime.Now.Date.AddDays(1) && f.Value > 50).Select(f => f.Key).LastOrDefault();
        return result != default ? result : DateTime.Now.Date.AddDays(2).AddMinutes(-1);
      }
    }
    public int EstimatedTimeToBatteryFullOrEmpty
    {
      get
      {
        if (AverageBatteryChargeDischargePower > -10 && AverageBatteryChargeDischargePower < 10)
          return 0;
        else if (AverageBatteryChargeDischargePower > 0)
          return (int)CalculateChargingDurationWh(BatterySoc, 100, AverageBatteryChargeDischargePower);
        else if (AverageBatteryChargeDischargePower < 0)
          return (int)CalculateChargingDurationWh(BatterySoc, PreferredMinimalSoC, AverageBatteryChargeDischargePower);
        else
          return 0;
      }
    }
    public int EstimatedChargeTimeAtMinima
    {
      get
      {
        return CalculateChargingDurationA(NeedToChargeFromExternal.Item3, 100, MaxBatteryChargePower);
      }
    }
    public int AverageBatteryChargeDischargePower
    {
      get
      {
        if (_battChargeFIFO.Count < 2)
          UpdateBatteryPowerQueue();

        return (int)_battChargeFIFO.Average();
      }
    }
    public EpexPriceTableEntry BestChargeTime
    {
      get
      {
        var need = NeedToChargeFromExternal;
        int maxChargeTime = 1;
        if (need.Item1)
        {
          maxChargeTime = CalculateChargingDurationA(need.Item3, 100, MaxBatteryChargePower);
          maxChargeTime = Math.Max((int)Math.Round((float)maxChargeTime / 60.0f, 0), 1);
          var sortedPriceList = PriceList.Where(p => p.EndTime > DateTime.Now && p.StartTime < need.Item2).OrderBy(p => p.Price);
          if (maxChargeTime < 2)
            return sortedPriceList.First();
          else
          {
            var sortetPricesByPeriod = SortPriceListByCheapestPeriod(DateTime.Now, need.Item2, maxChargeTime);
            float sumSingleHours = 0.0f;
            for (int i = 0; i < maxChargeTime; i++)
              sumSingleHours += sortedPriceList.ElementAt(i).Price;
            if (sumSingleHours / maxChargeTime > sortedPriceList.First().Price)
              return sortedPriceList.First();
            else
              return sortetPricesByPeriod.First();
          }
        }
       return PriceList.Where(p => p.EndTime > DateTime.Now).OrderBy(p => p.Price).First();
      }
    }
    public int GetPVForecastForPeriod(DateTime start, DateTime end)
    {
      return PVForecastTodayAndTomorrow.Where(fc => fc.Key >= start && fc.Key <= end).Select(fc => fc.Value).Sum();
    }
    public int GetEnergyUsageForPeriod(DateTime start, DateTime end)
    {
      return EstimatedEnergyUsageTodayAndTomorrow.Where(fc => fc.Key >= start && fc.Key <= end).Select(fc => fc.Value).Sum();
    }
    public int GetNetEnergyForPeriod(DateTime start, DateTime end)
    {
      return EstimatedNetEnergyTodayAndTomorrow.Where(fc => fc.Key >= start && fc.Key <= end).Select(fc => fc.Value).Sum();
    }
    private Dictionary<int, int> HourlyAverageEnergyUsage
    {
      get
      {
        UpdateCaches();
        return _energyUsagePerHourCache;
      }
    }
    private Dictionary<int, int> DayOfWeekAverageEnergyUsage
    {
      get
      {
        UpdateCaches();
        return _energyUsagePerWeekDayCache;
      }
    }
    private Dictionary<int, int> DayOfYearAverageEnergyUsage
    {
      get
      {
        UpdateCaches();
        return _energyUsagePerDayOfYearCache;
      }
    }
    #region Caches for DB Access
    public void UpdateCaches()
    {
      if (_energyUsagePerHourCache == null || _energyUsagePerHourCache.Count == 0 || _lastUsagePerHourCacheUpdate < DateTime.Now.AddDays(-1))
      {
        UpdateHourlyEnergyUsageCache();
      }
      if (_energyUsagePerWeekDayCache == null || _energyUsagePerWeekDayCache.Count == 0 || _lastUsagePerWeekDayCacheUpdate < DateTime.Now.AddDays(-7))
      {
        UpdateWeekDayEnergyUsageCache();
      }
      if (_energyUsagePerDayOfYearCache == null || _energyUsagePerDayOfYearCache.Count == 0 || _lastUsagePerDayOfYearCacheUpdate < DateTime.Now.AddDays(-7))
      {
        UpdateDayOfYearEnergyUsageCache();
      }
    }
    private Dictionary<int, int> _energyUsagePerHourCache;
    private DateTime _lastUsagePerHourCacheUpdate = DateTime.Now;
    private void UpdateHourlyEnergyUsageCache()
    {
      _energyUsagePerHourCache = [];
      for (int i = 0; i < 24; i++)
        _energyUsagePerHourCache.Add(i, GetHourlyHouseEnergyUsageHistory(i));
      _lastUsagePerHourCacheUpdate = DateTime.Now;
    }

    private Dictionary<int, int> _energyUsagePerWeekDayCache;
    private DateTime _lastUsagePerWeekDayCacheUpdate = DateTime.Now;
    private void UpdateWeekDayEnergyUsageCache()
    {
      _energyUsagePerWeekDayCache = [];
      for (int i = 0; i < 7; i++)
        _energyUsagePerWeekDayCache.Add(i, GetWeekDayyHouseEnergyUsageHistory((DayOfWeek)i));
      _lastUsagePerWeekDayCacheUpdate = DateTime.Now;
    }

    private Dictionary<int, int> _energyUsagePerDayOfYearCache;
    private DateTime _lastUsagePerDayOfYearCacheUpdate = DateTime.Now;
    private void UpdateDayOfYearEnergyUsageCache()
    {
      _energyUsagePerDayOfYearCache = [];
      for (int i = 0; i < 366; i++)
        _energyUsagePerDayOfYearCache.Add(i, GetDayOfYearyHouseEnergyUsageHistory(i));
      _lastUsagePerDayOfYearCacheUpdate = DateTime.Now;
    }
    #endregion
    private int CalculateChargingDurationWh(int startSoC, int endSoC, int pow)
    {
      float sS = (float)startSoC / 100;
      float eS = (float)endSoC / 100;

      float reqEnergy = ((eS - sS) * BatteryCapacity) * InverterEfficiency;
      float duration = reqEnergy / pow;

      return (int) (duration * 60);
    }
    private int CalculateChargingDurationA(int startSoC, int endSoC, int amps, int volts = 240)
    {
      int pow = amps * volts;
      return CalculateChargingDurationWh(startSoC, endSoC, pow);
    }
    public int CalculateBatteryEnergyAtSoC(int soc, int minSoC = -1)
    {
      float s = (float)soc / 100;
      float ms = minSoC < 0 ? (float)PreferredMinimalSoC / 100 : (float)minSoC / 100;
      float e = ((float)BatteryCapacity * s - (float)BatteryCapacity * ms);// * InverterEfficiency;
      return (int) e;
    }
    public int CalculateBatterySoCAtEnergy(int energy)
    {
      return (int)(energy * 100 / BatteryCapacity);
    }
    private int CalculateSoCNeededForEnergy(int energy, int minSoC = -1)
    {
      float ms = minSoC < 0 ? (float)PreferredMinimalSoC / 100 : (float)minSoC / 100;
      int socNeeded = (int)(((float)energy / ((float)BatteryCapacity * InverterEfficiency) + ms) * 100);
      return socNeeded;
    }
    private List<EpexPriceTableEntry> SortPriceListByCheapestPeriod(DateTime start, DateTime end, int hours=1)
    {
      if (hours == 0)
        hours = 1;

      if (start.Hour == end.Hour)
        return PriceList.Where(p => p.StartTime.Date == start.Date && p.StartTime.Hour == start.Hour).ToList();

      var prices = PriceList.Where(p => p.EndTime >= start &&  p.EndTime <= end);
      List<EpexPriceTableEntry> result = [];

      if (hours <= 0 || hours > prices.Count())
        result.Add(new EpexPriceTableEntry(DateTime.MaxValue, DateTime.MaxValue, float.NaN));
      else
      {
        var windows = Enumerable.Range(0, prices.Count() - hours + 1)
          .Select(i => new { Sum = prices.Skip(i).Take(hours).Sum(s => s.Price), Index = i }).OrderBy(s => s.Sum);
        foreach (var window in windows)
        {
          result.Add(new EpexPriceTableEntry(prices.ElementAt(window.Index).StartTime, prices.ElementAt(window.Index + hours-1).EndTime, window.Sum / hours));
        }
      }
      return result;
    }
    private List<EpexPriceTableEntry> _priceListCache;
    private void UpdatePriceList()
    {
      _priceListCache = [];
    }
    private void UpdateBatteryPowerQueue()
    {
      _battChargeFIFO.Enqueue((_config.CurrentBatteryPowerEntity is not null && _config.CurrentBatteryPowerEntity.TryGetStateValue<int>(out int value)) ? value : 0);
    }
    private List<EpexPriceTableEntry> PriceList
    {
      get 
      {
        if (_priceListCache is null || _priceListCache.Count == 0)
        {
          _priceListCache = [];
          if (_config.CurrentImportPriceEntity != null && _config.CurrentImportPriceEntity.EntityState?.AttributesJson?.GetProperty("data") is JsonElement data)
            if (data.Deserialize<List<EpexPriceTableEntry>>()?.OrderBy(x => x.StartTime).ToList() is List<EpexPriceTableEntry> priceList)
              _priceListCache = priceList;
        }
        return _priceListCache;
      }
    }
    public Dictionary<DateTime, int> PVForecastTodayAndTomorrow
    {
      get
      {
        Dictionary<DateTime, int> completeForecast = [];
        completeForecast = completeForecast.CombineForecastLists(GetPVForecastFromEntities(_config.ForecastPVEnergyTodayEntities));
        completeForecast = completeForecast.CombineForecastLists(GetPVForecastFromEntities(_config.ForecastPVEnergyTomorrowEntities));
        return completeForecast;
      }
    }
    public Dictionary<DateTime, int> EstimatedEnergyUsageTodayAndTomorrow
    {
      get
      {
        Dictionary<DateTime, int> result = [];
        foreach (var kvp in PVForecastTodayAndTomorrow)
        {
          result.Add(kvp.Key, HourlyAverageEnergyUsage[kvp.Key.Hour] / 4);
        }
        return result;
      }
    }
    public Dictionary<DateTime, int> EstimatedNetEnergyTodayAndTomorrow
    {
      get
      {
        Dictionary<DateTime, int> result = [];
        var estUsage = EstimatedEnergyUsageTodayAndTomorrow;
        foreach (var kvp in PVForecastTodayAndTomorrow)
        {
          if (estUsage.TryGetValue(kvp.Key, out int value))
            result.Add(kvp.Key, kvp.Value - value);
        }
        return result;
      }
    }
    public Dictionary<DateTime, int> EstimatedBatteryEnergyTodayAndTomorrow
    {
      get
      {
        Dictionary<DateTime, int> result = EstimatedNetEnergyTodayAndTomorrow;

        int curEnergy = CalculateBatteryEnergyAtSoC(BatterySoc, 0);
        int curIndex = EstimatedNetEnergyTodayAndTomorrow.Keys.ToList().IndexOf(EstimatedNetEnergyTodayAndTomorrow.Keys.FirstOrDefault(k => k >= DateTime.Now));
        
        for (int i=curIndex; i<result.Count; i++)
        {
          result[result.ElementAt(i).Key] = Math.Min(curEnergy + result[result.ElementAt(i).Key], BatteryCapacity);
          curEnergy = result[result.ElementAt(i).Key];
        }
        curEnergy = CalculateBatteryEnergyAtSoC(BatterySoc, 0);
        for (int i = curIndex-1; i >= 0; i--)
        {
          result[result.ElementAt(i).Key] = Math.Min(curEnergy - result[result.ElementAt(i).Key], BatteryCapacity);
          curEnergy = result[result.ElementAt(i).Key];
        }
        return result;
      }
    }
    public Dictionary<DateTime, int> EstimatedBatterySoCTodayAndTomorrow
    {
      get
      {
        Dictionary<DateTime, int> result = [];
        foreach (var kvp in EstimatedBatteryEnergyTodayAndTomorrow)
        {
          result.Add(kvp.Key, CalculateBatterySoCAtEnergy(kvp.Value));
        }
        return result;
      }
    }
    private static Dictionary<DateTime, int> GetPVForecastFromEntities(List<Entity>? entities)
    {
      Dictionary<DateTime, int> completeForecast = [];
      if (entities != null && entities.Count > 0)
      {
        foreach (Entity entity in entities)
        {
          completeForecast = completeForecast.CombineForecastLists(GetForecastDetailsFromPower(entity));
        }
      }
      return completeForecast;
    }
    private static Dictionary<DateTime, int> GetForecastDetailsFromPower(Entity entity)
    {
      try
      {
        var result = entity.EntityState?.AttributesJson?.GetProperty("watts").Deserialize<Dictionary<DateTime, int>>()?.OrderBy(t => t.Key).ToDictionary();
        if (result != null)
        {
          float interval = 0.25f;
          for (int i = 0; i < result.Count; i++)
          {
            var r = result.ElementAt(i).Key;
            if (result[r] == 0)
              continue;
            if (i < result.Count - 1)
            {
              var r_next = result.ElementAt(i + 1).Key;
              interval = (float)(r_next - r).TotalHours;
            }
            result[r] = (int)(result[r] * interval);
          }
          return result;
        }
      }
      catch { }
      return [];
    }
    private int GetHourlyHouseEnergyUsageHistory(int hour)
    {
      using var db = new EnergyHistoryDb(new DataOptions().UseSQLite(String.Format("Data Source={0}", _config.DBLocation)));
#pragma warning disable CS8629 // Nullable value type may be null.
      var hourlies = db.Hourlies.Where(h => h.Timestamp.Hour == hour && h.Houseenergy != null).Select(h => new KeyValuePair<DateTime, int>(h.Timestamp, (int)h.Houseenergy)).ToDictionary();
#pragma warning restore CS8629 // Nullable value type may be null.
      DateTime now = DateTime.Now;
      double sum = 0;
      double weightSum = 0;

      foreach (var pair in hourlies)
      {
        double weight = Math.Exp((now - pair.Key).TotalDays); // weight is based on the recency of the timestamp
        sum += pair.Value * weight;
        weightSum += weight;
      }

      return (int)(sum / weightSum);
    }
    private int GetWeekDayyHouseEnergyUsageHistory(DayOfWeek dayOfWeek)
    {
      using var db = new EnergyHistoryDb(new DataOptions().UseSQLite(String.Format("Data Source={0}", _config.DBLocation)));
      var dailies = db.Dailies.Where(h => h.Timestamp.DayOfWeek == dayOfWeek && h.Houseenergy != null).Select(s => s.Houseenergy);
#pragma warning disable CS8629 // Nullable value type may be null.
      return dailies.Any() ? (int)dailies.Average() : 0;
#pragma warning restore CS8629 // Nullable value type may be null.
    }
    private int GetDayOfYearyHouseEnergyUsageHistory(int dayOfYear)
    {
      using var db = new EnergyHistoryDb(new DataOptions().UseSQLite(String.Format("Data Source={0}", _config.DBLocation)));
      var dailies = db.Dailies.Where(h => h.Timestamp.DayOfYear == dayOfYear && h.Houseenergy != null).Select(s => s.Houseenergy);
#pragma warning disable CS8629 // Nullable value type may be null.
      return dailies.Any() ? (int)dailies.Average() : 0;
#pragma warning restore CS8629 // Nullable value type may be null.
    }
  }
}
