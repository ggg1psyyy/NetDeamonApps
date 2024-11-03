using LinqToDB;
using NetDaemon.HassModel.Entities;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace PVControl
{
  public class HouseEnergy
  {
    private readonly PVConfig _config;
    private readonly RunningIntAverage _battChargeAverage;
    private readonly RunningIntAverage _LoadRunningAverage;
    private readonly RunningIntAverage _PVRunningAverage;
    /// <summary>
    /// Default Efficiency if not set in config
    /// </summary>
    private readonly float _defaultInverterEfficiency = 0.9f;
    public Prediction Prediction_Load
    { get; private set; }
    public Prediction Prediction_PV
    { get; private set; }
    public Prediction Prediction_NetEnergy
    { get; private set; }
    public Prediction Prediction_BatterySoC
    { get; private set; }

    public HouseEnergy(PVConfig config)
    { 
      _config = config;
      _battChargeAverage = new RunningIntAverage(TimeSpan.FromMinutes(1));
      if (_config.CurrentBatteryPowerEntity is null)
        throw new NullReferenceException("BatteryPowerEntity not available");
      if (_config.CurrentBatteryPowerEntity.TryGetStateValue(out int bat))
        _battChargeAverage.AddValue(bat);

      _LoadRunningAverage = new RunningIntAverage(TimeSpan.FromMinutes(5));
      if (_config.CurrentHouseLoadEntity is null)
        throw new NullReferenceException("HouseLoadEntity not available");
      if (_config.CurrentHouseLoadEntity.TryGetStateValue(out int load))
        _LoadRunningAverage.AddValue(load);

      _PVRunningAverage = new RunningIntAverage(TimeSpan.FromMinutes(5));
      if (_config.CurrentPVPowerEntity is null)
        throw new NullReferenceException("CurrentPVPowerEntity not available");
      if (_config.CurrentPVPowerEntity.TryGetStateValue(out int pv))
        _PVRunningAverage.AddValue(pv);

      if (string.IsNullOrEmpty(_config.DBLocation))
        throw new NullReferenceException("No DBLocation available");
      Prediction_Load = new HourlyWeightedAverageLoadPrediction(_config.DBLocation);

      if (_config.ForecastPVEnergyTodayEntities is null || _config.ForecastPVEnergyTomorrowEntities is null)
        throw new NullReferenceException("PV Forecast entities are not available");
      Prediction_PV  = new OpenMeteoSolarForecastPrediction(_config.ForecastPVEnergyTodayEntities, _config.ForecastPVEnergyTomorrowEntities);

      Prediction_NetEnergy = new NetEnergyPrediction(Prediction_PV, Prediction_Load, _LoadRunningAverage, _PVRunningAverage);

      if (_config.BatterySoCEntity is null)
        throw new NullReferenceException("BatterySoCEntity not available");
      Prediction_BatterySoC = new BatterySoCPrediction(Prediction_NetEnergy, _config.BatterySoCEntity, BatteryCapacity);

      _config.CurrentImportPriceEntity?.StateAllChanges().SubscribeAsync(async _ => await UserStateChanged(_config.CurrentImportPriceEntity));
      _config.CurrentBatteryPowerEntity?.StateChanges().SubscribeAsync(async _ => await UserStateChanged(_config.CurrentBatteryPowerEntity));
      _config.CurrentPVPowerEntity?.StateChanges().SubscribeAsync(async _ => await UserStateChanged(_config.CurrentPVPowerEntity));
      _config.CurrentHouseLoadEntity?.StateChanges().SubscribeAsync(async _ => await UserStateChanged(_config.CurrentHouseLoadEntity));
      PreferredMinBatterySoC = 30;
      EnforcePreferredSoC = false;
      _dailySoCPrediction = [];
      _dailyChargePrediction = [];
      _dailyDischargePrediction = [];
      _priceListCache = [];
    }
    public bool ForceCharge { get; set; }
    /// <summary>
    /// Enforce the set preferred minimal Soc, if not enforced it's allowed to go down to AbsoluteMinimalSoC to reach cheaper prices or PV charge
    /// </summary>
    public bool EnforcePreferredSoC {  get; set; }
    public int PreferredMinBatterySoC { get; set; }
    public InverterModes OverrideMode{  get; set; }
    public int ForceChargeMaxPrice { get; set; }
    public int ForceChargeTargetSoC { get; set; }
#pragma warning disable CS1998 // Async method lacks 'await' operators and will run synchronously
    private async Task UserStateChanged(Entity entity)
#pragma warning restore CS1998 // Async method lacks 'await' operators and will run synchronously
    {
      if (entity.EntityId == _config.CurrentImportPriceEntity?.EntityId)
      {
        UpdatePriceList();
      }
      if (entity.EntityId == _config.CurrentBatteryPowerEntity?.EntityId && _config.CurrentBatteryPowerEntity.TryGetStateValue(out int bat))
      {
        _battChargeAverage.AddValue(bat);
      }
      if (entity.EntityId == _config.CurrentHouseLoadEntity?.EntityId && _config.CurrentHouseLoadEntity.TryGetStateValue(out int load))
      {
        _LoadRunningAverage.AddValue(load);
      }
      if (entity.EntityId == _config.CurrentPVPowerEntity?.EntityId && _config.CurrentPVPowerEntity.TryGetStateValue(out int pv))
      {
        _PVRunningAverage.AddValue(pv);
      }
    }
    private InverterModes _currentMode;
    public InverterModes ProposedMode
    {
      get
      {
        if (OverrideMode != InverterModes.automatic)
        {
          ForceChargeReason = ForceChargeReasons.UserMode;
          _currentMode = OverrideMode;
          return OverrideMode;
        }

        if (CurrentEnergyImportPrice < CurrentEnergyExportPrice * -1 && CurrentEnergyImportPrice < 0)
        {
          _currentMode = InverterModes.grid_only;
          ForceChargeReason = ForceChargeReasons.ImportPriceUnderExportPrice;
          return InverterModes.grid_only;
        }

        else if (
          NeedToChargeFromExternal.Item1
          // stay 2 minutes away from extremes, to make sure we have the same time as the provider
          && DateTime.Now > BestChargeTime.StartTime.AddMinutes(2) && DateTime.Now < BestChargeTime.EndTime.AddMinutes(-2)
          // don't charge over 98% SoC as it get's really slow and inefficient and don't start over 96%
          && ((_currentMode == InverterModes.force_charge && BatterySoc <= 98) || (_currentMode != InverterModes.force_charge && BatterySoc <= 96))
          )
        {
          _currentMode = InverterModes.force_charge;
          return InverterModes.force_charge;
        }
        else
        {
          //ForceChargeReason = ForceChargeReasons.None;
          _currentMode = InverterModes.normal;
          return InverterModes.normal;
        }
      }
    }
    private Tuple<bool, DateTime, int>? _needToChargeFromExternalCache;
    private DateTime _lastCalculatedNeedToCharge;
    /// <summary>
    /// returns:
    /// * Do we need to charge from grid
    /// * When do we reach minimal Soc
    /// * what's the estimated SoC at this time
    /// </summary>
    public Tuple<bool, DateTime, int> NeedToChargeFromExternal
    {
      get
      {
        DateTime now = DateTime.Now;
#if !DEBUG
        // cache the result for 5 seconds, so that it's only calulated once per run
        if (_lastCalculatedNeedToCharge != default && _needToChargeFromExternalCache != null && Math.Abs((now - _lastCalculatedNeedToCharge).TotalSeconds) < 5)
        {
          return _needToChargeFromExternalCache;
        }
#endif
        ForceChargeReason = ForceChargeReasons.None;
        var estSoC = Prediction_BatterySoC.TodayAndTomorrow;

        if (ForceCharge)
        { 
          // if ForceCharge is enabled we always charge once a day at the absolute cheapest period (only if price < ForceChargeMaxPrice set by user)
          // and only so far that we reach 100% via PV today
          var maxSoCToday = estSoC.FirstMaxOrDefault(now, now.Date.AddDays(1));
          // keep a wiggle room of 1 hour to prevent higher loads than predicted to oszilate the charging if pv max reached is very near the end of the day
          var minsToLastPV = (LastRelevantPVEnergyToday - maxSoCToday.Key).TotalMinutes;
          bool wiggle = (_currentMode == InverterModes.force_charge ? minsToLastPV <= 60 : minsToLastPV <= 0) || maxSoCToday.Value < 100;
          // ForceCharge is only allowed to max. 95%
          ForceChargeTargetSoC = Math.Min(ForceChargeTargetSoC, 95);
          // hysteresis prevention
          int forceChargeTo = _currentMode == InverterModes.force_charge ? ForceChargeTargetSoC + 2 : ForceChargeTargetSoC;
          if (BatterySoc < forceChargeTo && CheapestWindowToday.Price < ForceChargeMaxPrice && wiggle && IsNowCheapestWindowToday)
          {
            ForceChargeReason = ForceChargeReasons.ForcedChargeAtMinimumPrice;
            _needToChargeFromExternalCache = new Tuple<bool, DateTime, int>(true, now, BatterySoc);
            _lastCalculatedNeedToCharge = now;
            return _needToChargeFromExternalCache;
          }
        }

        int minSoC = EnforcePreferredSoC ? PreferredMinimalSoC : AbsoluteMinimalSoC;
        if (_currentMode == InverterModes.force_charge)
          // while charging increase minCharge to prevent hysteresis
          minSoC++;

        // when do we reach mincharge
        var minReached = estSoC.FirstUnderOrDefault(minSoC, start: now);
        if (minReached.Key == default)
          // we don't reach minima, so what's the lowest
          minReached = estSoC.FirstMinOrDefault(start: now);

        // what's the next max
        var maxReached = estSoC.FirstMaxOrDefault(start: now);

        // charge to Max if in absolut cheapest period when we never reach 100% SoC today or tomorrow with PV only and if we have at least 12h price preview
        if (PriceList.Where(p => p.StartTime > now).Count() > 12)
          if (CurrentEnergyImportPrice < PriceList.Where(p => p.StartTime > now).Min(p => p.Price) && maxReached.Value < 99 && estSoC.Where(e => e.Key.Date == now.Date.AddDays(1)).Max(e => e.Value) < 99)
            minSoC = 100;

        bool minBeforeMax = minReached.Key < maxReached.Key;
        bool minUnderDefined = minReached.Value <= minSoC;
        bool maxOver100 = maxReached.Value >= 100;

        // We need to force charge if we estimate to fall to minCharge before we reach max or max < 100
        bool needCharge = minUnderDefined && (minBeforeMax || !maxOver100);
        if (needCharge)
          ForceChargeReason = minSoC <= AbsoluteMinimalSoC+2 ? ForceChargeReasons.GoingUnderAbsoluteMinima : ForceChargeReasons.GoingUnderPreferredMinima;
        _needToChargeFromExternalCache = new Tuple<bool, DateTime, int>(needCharge, minReached.Key, minReached.Value);
        _lastCalculatedNeedToCharge = now;
        return _needToChargeFromExternalCache;
      }
    }
    private ForceChargeReasons _forceChargeReason;
    public ForceChargeReasons ForceChargeReason 
    { 
      get => OverrideMode == InverterModes.automatic ? _forceChargeReason : ForceChargeReasons.UserMode; 
      private set => _forceChargeReason = value; 
    }
    public RunHeavyLoadReasons RunHeavyLoadReason { get; private set; }
    /// <summary>
    /// Tells if it's a good time to run heavy loads now
    /// </summary>
    public RunHeavyLoadsStatus RunHeavyLoadsNow
    {
      get
      {
        var estSoC = Prediction_BatterySoC.TodayAndTomorrow;
        var now = DateTime.Now;
        // if we're already force_charging we're sure to be in a cheap window so it should be allowed
        if (_currentMode == InverterModes.force_charge)
        {
          if (IsNowCheapestWindowTotal)
          {
            RunHeavyLoadReason = RunHeavyLoadReasons.ChargingAtCheapestPrice;
            return RunHeavyLoadsStatus.Yes;
          }
          else
          {
            RunHeavyLoadReason = RunHeavyLoadReasons.Charging;
            return RunHeavyLoadsStatus.IfNecessary;
          }
        }
        // in PVperiod
        if (CurrentPVPeriod == PVPeriods.InPVPeriod)
        {
          // as long as we still reach over 97% SoC via PV it's always ok
          var maxSocRestOfToday = estSoC.FirstMaxOrDefault(now, LastRelevantPVEnergyToday);
          if (maxSocRestOfToday.Value > 97)
          {
            RunHeavyLoadReason = RunHeavyLoadReasons.WillReach100;
            return RunHeavyLoadsStatus.Yes;
          }
        }
        // we allow it as long as we don't go under PreferredSoC
        var firstPV = CurrentPVPeriod == PVPeriods.BeforePV ? FirstRelevantPVEnergyToday : FirstRelevantPVEnergyTomorrow;
        var minSocTilFirstPV = estSoC.FirstMinOrDefault(now, firstPV);
        if (minSocTilFirstPV.Value > PreferredMinimalSoC)
        {
          RunHeavyLoadReason = RunHeavyLoadReasons.WillStayOverPreferredMinima;
          return RunHeavyLoadsStatus.Yes;
        }
        else if (minSocTilFirstPV.Value > AbsoluteMinimalSoC+3)
        {
          RunHeavyLoadReason = RunHeavyLoadReasons.WillStayOverAbsoluteMinima;
          return RunHeavyLoadsStatus.IfNecessary;
        }

        // otherwise 
        if (BatterySoc > PreferredMinimalSoC)
        {
          RunHeavyLoadReason = RunHeavyLoadReasons.CurrentlyOverPreferredMinima;
          return RunHeavyLoadsStatus.IfNecessary;
        }
        else if (BatterySoc > AbsoluteMinimalSoC)
        {
          RunHeavyLoadReason = RunHeavyLoadReasons.CurrentlyOberAbsoluteMinima;
          return RunHeavyLoadsStatus.Prevent;
        }
        RunHeavyLoadReason = RunHeavyLoadReasons.WillGoUnderAbsoluteMinima;
        return RunHeavyLoadsStatus.No;
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
        // add 2% to prevent inverter from shutting off early and needing to import probably expensive energy
        return minAllowedSoC + 2;
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
        if (CurrentAverageBatteryChargeDischargePower > -10 && CurrentAverageBatteryChargeDischargePower < 10)
          return BatteryStatuses.idle;
        else if (CurrentAverageBatteryChargeDischargePower > 0)
          return BatteryStatuses.charging;
        else if (CurrentAverageBatteryChargeDischargePower < 0)
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
    /// Currently usable energy in battery down to <see cref="HouseEnergy.AbsoluteMinimalSoC"/> or <see cref="HouseEnergy.PreferredMinimalSoC"/> depending on <see cref="HouseEnergy.EnforcePreferredSoC"/> in Wh
    /// </summary>
    public int UsableBatteryEnergy
    {
      get
      {
        return CalculateBatteryEnergyAtSoC(BatterySoc, EnforcePreferredSoC ? PreferredMinimalSoC : AbsoluteMinimalSoC);
      }
    }
    public int ReserveBatteryEnergy
    {
      get
      {
        return CalculateBatteryEnergyAtSoC(EnforcePreferredSoC ? PreferredMinimalSoC : AbsoluteMinimalSoC, 0);
      }
    }
    public DateTime FirstRelevantPVEnergyToday
    {
      get
      {
        var result = Prediction_NetEnergy.Today.Where(f => f.Value > 50).Select(f => f.Key).FirstOrDefault();
        return result != default ? result : DateTime.Now.Date.AddDays(2).AddMinutes(-1);
      }
    }
    public DateTime FirstRelevantPVEnergyTomorrow
    {
      get
      {
        var result = Prediction_NetEnergy.Tomorrow.Where(f => f.Value > 50).Select(f => f.Key).FirstOrDefault();
        return result != default ? result : DateTime.Now.Date.AddDays(2).AddMinutes(-1);
      }
    }
    public DateTime LastRelevantPVEnergyToday
    {
      get
      {
        var result = Prediction_NetEnergy.Today.Where(f => f.Value > 50).Select(f => f.Key).LastOrDefault();
        return result != default ? result : DateTime.Now.Date.AddDays(2).AddMinutes(-1);
      }
    }
    public DateTime LastRelevantPVEnergyTomorrow
    {
      get
      {
        var result = Prediction_NetEnergy.Tomorrow.Where(f => f.Value > 50).Select(f => f.Key).LastOrDefault();
        return result != default ? result : DateTime.Now.Date.AddDays(2).AddMinutes(-1);
      }
    }
    public int EstimatedTimeToBatteryFullOrEmpty
    {
      get
      {
        if (CurrentAverageBatteryChargeDischargePower > -10 && CurrentAverageBatteryChargeDischargePower < 10)
          return 0;
        else if (CurrentAverageBatteryChargeDischargePower > 0)
          return (int)CalculateChargingDurationWh(BatterySoc, 100, CurrentAverageBatteryChargeDischargePower);
        else if (CurrentAverageBatteryChargeDischargePower < 0)
          return (int)CalculateChargingDurationWh(BatterySoc, PreferredMinimalSoC, CurrentAverageBatteryChargeDischargePower);
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
    public int CurrentAverageBatteryChargeDischargePower
    {
      get
      {
        return _battChargeAverage.GetAverage();
      }
    }
    public int CurrentAverageHouseLoad
    {
      get
      {
        return _LoadRunningAverage.GetAverage();
      }
    }
    public int CurrentAveragePVPower
    {
      get
      {
        return _PVRunningAverage.GetAverage();
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
      float e = ((float)BatteryCapacity * s - (float)BatteryCapacity * ms);
      return (int) e;
    }
    private List<EpexPriceTableEntry> SortPriceListByCheapestPeriod(DateTime start, DateTime end, int hours=1)
    {
      if (hours == 0)
        hours = 1;

      if (start.Hour == end.Hour && start.Date == end.Date)
        return PriceList.Where(p => p.StartTime.Date == start.Date && p.StartTime.Hour == start.Hour).ToList();

      var prices = PriceList.Where(p => p.EndTime >= start &&  p.StartTime <= end);
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
    public EpexPriceTableEntry CheapestWindowToday
    {
      get
      {
        return SortPriceListByCheapestPeriod(DateTime.Now.Date, DateTime.Now.Date.AddDays(1)).First();
      }
    }
    public EpexPriceTableEntry CheapestWindowTotal
    {
      get
      {
        return PriceList.OrderBy(p => p.Price).First();
      }
    }
    public bool IsNowCheapestWindowToday
    {
      get
      {
        var cheapest = CheapestWindowToday;
        var now = DateTime.Now;
        return now > cheapest.StartTime && now < cheapest.EndTime;
      }
    }
    public bool IsNowCheapestWindowTotal
    {
      get
      {
        var cheapest = CheapestWindowTotal;
        var now = DateTime.Now;
        return now > cheapest.StartTime && now < cheapest.EndTime;
      }
    }
    public PVPeriods CurrentPVPeriod
    {
      get
      {
        var now = DateTime.Now;
        if (now < FirstRelevantPVEnergyToday)
          return PVPeriods.BeforePV;
        else if (now > LastRelevantPVEnergyToday)
          return PVPeriods.AfterPV;
        else
          return PVPeriods.InPVPeriod;
      }
    }
    public void UpdatePredictions()
    {
      if (DateTime.Now.Hour == 0 && Math.Abs((DateTime.Now - Prediction_Load.LastDataUpdate).TotalMinutes) > 60)
        Prediction_Load.UpdateData();
      Prediction_PV.UpdateData();
      Prediction_NetEnergy.UpdateData();
      Prediction_BatterySoC.UpdateData();
    }
    #region daily snapshot for comparing
    public DateTime LastSnapshotUpdate { get; private set; } = default;
    private void UpdateSnapshots()
    {
      DateTime now = DateTime.Now;
      if (_dailySoCPrediction.Count == 0 || _dailyChargePrediction.Count == 0 || _dailyDischargePrediction.Count == 0 || (now.Hour == 0 && now.Minute == 1) || (now - LastSnapshotUpdate).TotalMinutes > 24*60)
      {
        _dailyChargePrediction = Prediction_PV.TodayAndTomorrow.GetRunningSumsDaily();
        _dailyDischargePrediction = Prediction_Load.TodayAndTomorrow.GetRunningSumsDaily();
        var netPrediction = new NetEnergyPrediction(Prediction_PV, Prediction_Load, null, null, false);
        _dailySoCPrediction = new BatterySoCPrediction(netPrediction, _config.BatterySoCEntity, BatteryCapacity).TodayAndTomorrow;
        LastSnapshotUpdate = now;
      }
    }
    private Dictionary<DateTime, int> _dailySoCPrediction;
    public Dictionary<DateTime, int> DailyBatterySoCPredictionTodayAndTomorrow
    {
      get
      {
        UpdateSnapshots();
        return _dailySoCPrediction;
      }
    }
    private Dictionary<DateTime, int> _dailyChargePrediction;
    public Dictionary<DateTime, int> DailyChargePredictionTodayAndTomorrow
    {
      get
      {
        UpdateSnapshots();
        return _dailyChargePrediction;
      }
    }
    private Dictionary<DateTime, int> _dailyDischargePrediction;
    public Dictionary<DateTime, int> DailyDischargePredictionTodayAndTomorrow
    {
      get
      {
        UpdateSnapshots();
        return _dailyDischargePrediction;
      }
    }
    #endregion
  }
}
