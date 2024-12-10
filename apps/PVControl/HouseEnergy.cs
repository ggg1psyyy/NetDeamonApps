using LinqToDB;
using LinqToDB.DataProvider;
using NetDaemon.HassModel.Entities;
using NetDeamon.apps.PVControl.Predictions;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using static NetDeamon.apps.PVControl.PVControlCommon;

namespace NetDeamon.apps.PVControl
{
  public class HouseEnergy
  {
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

    public HouseEnergy()
    {
      _battChargeAverage = new RunningIntAverage(TimeSpan.FromMinutes(1));
      if (PVCC_Config.CurrentBatteryPowerEntity is null)
        throw new NullReferenceException("BatteryPowerEntity not available");
      if (PVCC_Config.CurrentBatteryPowerEntity.TryGetStateValue(out int bat))
        _battChargeAverage.AddValue(bat);

      _LoadRunningAverage = new RunningIntAverage(TimeSpan.FromMinutes(5));
      if (PVCC_Config.CurrentHouseLoadEntity is null)
        throw new NullReferenceException("HouseLoadEntity not available");
      if (PVCC_Config.CurrentHouseLoadEntity.TryGetStateValue(out int load))
        _LoadRunningAverage.AddValue(load);

      _PVRunningAverage = new RunningIntAverage(TimeSpan.FromMinutes(5));
      if (PVCC_Config.CurrentPVPowerEntity is null)
        throw new NullReferenceException("CurrentPVPowerEntity not available");
      if (PVCC_Config.CurrentPVPowerEntity.TryGetStateValue(out int pv))
        _PVRunningAverage.AddValue(pv);

      if (string.IsNullOrEmpty(PVCC_Config.DBLocation))
        throw new NullReferenceException("No DBLocation available");
      Prediction_Load = new HourlyWeightedAverageLoadPrediction(PVCC_Config.DBLocation, 10);

      if (PVCC_Config.ForecastPVEnergyTodayEntities is null || PVCC_Config.ForecastPVEnergyTomorrowEntities is null)
        throw new NullReferenceException("PV Forecast entities are not available");
      Prediction_PV = new OpenMeteoSolarForecastPrediction(PVCC_Config.ForecastPVEnergyTodayEntities, PVCC_Config.ForecastPVEnergyTomorrowEntities);

      Prediction_NetEnergy = new NetEnergyPrediction(Prediction_PV, Prediction_Load, _LoadRunningAverage, _PVRunningAverage);

      if (PVCC_Config.BatterySoCEntity is null)
        throw new NullReferenceException("BatterySoCEntity not available");
      Prediction_BatterySoC = new BatterySoCPrediction(Prediction_NetEnergy, PVCC_Config.BatterySoCEntity, BatteryCapacity);

      PVCC_Config.CurrentImportPriceEntity?.StateAllChanges().SubscribeAsync(async _ => await UserStateChanged(PVCC_Config.CurrentImportPriceEntity));
      PVCC_Config.CurrentBatteryPowerEntity?.StateChanges().SubscribeAsync(async _ => await UserStateChanged(PVCC_Config.CurrentBatteryPowerEntity));
      PVCC_Config.CurrentPVPowerEntity?.StateChanges().SubscribeAsync(async _ => await UserStateChanged(PVCC_Config.CurrentPVPowerEntity));
      PVCC_Config.CurrentHouseLoadEntity?.StateChanges().SubscribeAsync(async _ => await UserStateChanged(PVCC_Config.CurrentHouseLoadEntity));
      PreferredMinBatterySoC = 30;
      EnforcePreferredSoC = false;
      _dailySoCPrediction = [];
      _dailyChargePrediction = [];
      _dailyDischargePrediction = [];
      _priceListCache = [];
    }
    /// <summary>
    /// UserSetting: ForceCharge to 100%
    /// </summary>
    public bool ForceCharge { get; set; }
    /// <summary>
    /// Enforce the set preferred minimal Soc, if not enforced it's allowed to go down to AbsoluteMinimalSoC to reach cheaper prices or PV charge
    /// </summary>
    public bool EnforcePreferredSoC { get; set; }
    public int PreferredMinBatterySoC { get; set; }
    public InverterModes OverrideMode { get; set; }
    public int ForceChargeMaxPrice { get; set; }
    public int ForceChargeTargetSoC { get; set; }
#pragma warning disable CS1998 // Async method lacks 'await' operators and will run synchronously
    private async Task UserStateChanged(Entity entity)
#pragma warning restore CS1998 // Async method lacks 'await' operators and will run synchronously
    {
      if (entity.EntityId == PVCC_Config.CurrentImportPriceEntity?.EntityId)
      {
        UpdatePriceList();
      }
      if (entity.EntityId == PVCC_Config.CurrentBatteryPowerEntity?.EntityId && PVCC_Config.CurrentBatteryPowerEntity.TryGetStateValue(out int bat))
      {
        _battChargeAverage.AddValue(bat);
      }
      if (entity.EntityId == PVCC_Config.CurrentHouseLoadEntity?.EntityId && PVCC_Config.CurrentHouseLoadEntity.TryGetStateValue(out int load))
      {
        _LoadRunningAverage.AddValue(load);
      }
      if (entity.EntityId == PVCC_Config.CurrentPVPowerEntity?.EntityId && PVCC_Config.CurrentPVPowerEntity.TryGetStateValue(out int pv))
      {
        _PVRunningAverage.AddValue(pv);
      }
    }
    private InverterModes _currentMode;
    public InverterModes ProposedMode
    {
      get
      {
#if DEBUG
        DateTime now = DateTime.Now.Date.AddHours(2.1);
#else
        DateTime now = DateTime.Now;
#endif
        DateTime cheapestToday = CheapestWindowToday.StartTime;
        var need = NeedToChargeFromExternal;
        if (OverrideMode != InverterModes.automatic)
        {
          ForceChargeReason = ForceChargeReasons.UserMode;
          _currentMode = OverrideMode;
          return _currentMode;
        }

        if (CurrentEnergyImportPrice < CurrentEnergyExportPrice * -1 && CurrentEnergyImportPrice < 0)
        {
          _currentMode = InverterModes.grid_only;
          ForceChargeReason = ForceChargeReasons.ImportPriceUnderExportPrice;
          return _currentMode;
        }

        if (ForceCharge && now > cheapestToday.AddHours(-1) && now < cheapestToday.AddHours(2))
        {
          // don't recalculate if charging already started
          if (ForceChargeReason == ForceChargeReasons.ForcedChargeAtMinimumPrice && _currentMode == InverterModes.force_charge && BatterySoc <= Math.Min(98, ForceChargeTargetSoC+2))
          {
            return _currentMode;
          }
          int socAtBestTime = Prediction_BatterySoC.Today.GetEntryAtTime(cheapestToday).Value;
          int chargeTime = CalculateChargingDurationA(socAtBestTime, 100, PVCC_Config.MaxBatteryChargePower);
          int rankBefore = GetPriceRank(cheapestToday.AddHours(-1));
          int rankAfter = GetPriceRank(cheapestToday.AddHours(1));
          DateTime chargeStart = cheapestToday;
          if (chargeTime > 60)
          {
            if (rankBefore < rankAfter)
            {
              if (PriceList.Where(p => p.StartTime == chargeStart.AddHours(-1)).FirstOrDefault().Price < ForceChargeMaxPrice)
                chargeStart = cheapestToday.AddMinutes(-(chargeTime - 50));
            }
          }

          if (now > chargeStart && now < chargeStart.AddMinutes(chargeTime+10) && BatterySoc < Math.Min(96, ForceChargeTargetSoC))
          {
            //PVCC_Logger.LogInformation("ForceCharge cheapestToday starttime now: {start}", chargeStart);
            _currentMode = InverterModes.force_charge;
            ForceChargeReason = ForceChargeReasons.ForcedChargeAtMinimumPrice;
            return _currentMode;
          }
        }

        if (
          need.Item1
          // stay 30 seconds away from extremes, to make sure we have the same time as the provider
          && DateTime.Now > BestChargeTime.StartTime.AddSeconds(30) && DateTime.Now < BestChargeTime.EndTime.AddSeconds(-30)
          // don't charge over 98% SoC as it get's really slow and inefficient and don't start over 96%
          && (_currentMode == InverterModes.force_charge && BatterySoc <= 98 || _currentMode != InverterModes.force_charge && BatterySoc <= 96)
          )
        {
          // if chargetime is in the latest quarter of the current hour and next hour is cheaper, it's better to just import energy normally, without force charging
          if ((ForceChargeReason == ForceChargeReasons.GoingUnderAbsoluteMinima || ForceChargeReason == ForceChargeReasons.GoingUnderPreferredMinima) &&
            CurrentPriceRank > GetPriceRank(now.AddHours(1)) && need.Item2.Date == now.Date && need.Item2.Hour == now.Hour && need.Item2.Minute >= 45)
          {
            _currentMode = InverterModes.normal;
            return _currentMode;
          }
          else
          { 
            _currentMode = InverterModes.force_charge;
            return _currentMode;
          }
        }

        _currentMode = InverterModes.normal;
        return _currentMode;
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

        bool minBeforeMax = minReached.Key < maxReached.Key;
        bool minUnderDefined = minReached.Value <= minSoC;
        bool maxOver100 = maxReached.Value >= 100;

        // We need to force charge if we estimate to fall to minCharge before we reach max or max < 100
        bool needCharge = minUnderDefined && (minBeforeMax || !maxOver100);
        if (needCharge)
          ForceChargeReason = minSoC <= AbsoluteMinimalSoC + 2 ? ForceChargeReasons.GoingUnderAbsoluteMinima : ForceChargeReasons.GoingUnderPreferredMinima;
        // be very pessimistic and substract 10% of the targettime, so we are relatively sure to reach the next price minima 
        int quarterhoursTilCharge = (int)(((minReached.Key - now).TotalMinutes * 0.1)/ 15);
        _needToChargeFromExternalCache = new Tuple<bool, DateTime, int>(needCharge, minReached.Key.AddMinutes(-quarterhoursTilCharge*15), minReached.Value);
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
        else if (minSocTilFirstPV.Value > AbsoluteMinimalSoC + 3)
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
          RunHeavyLoadReason = RunHeavyLoadReasons.CurrentlyOverAbsoluteMinima;
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
        return PVCC_Config.BatterySoCEntity is not null && PVCC_Config.BatterySoCEntity.TryGetStateValue(out int soc) ? soc : 0;
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
        int minAllowedSoC = PVCC_Config.MinBatterySoCValue != default ? PVCC_Config.MinBatterySoCValue : 0;
        if (PVCC_Config.MinBatterySoCEntity is not null && PVCC_Config.MinBatterySoCEntity.TryGetStateValue(out int minSoc))
          minAllowedSoC = minSoc;
        // add 2% to prevent inverter from shutting off early and needing to import probably expensive energy
        return minAllowedSoC + 2;
      }
    }
    private float InverterEfficiency
    {
      get
      {
        return PVCC_Config.InverterEfficiency != default ? PVCC_Config.InverterEfficiency : _defaultInverterEfficiency;
      }
    }
    /// <summary>
    /// BatteryCapacity in Wh
    /// </summary>
    public int BatteryCapacity
    {
      get
      {
        float batteryCapacity = PVCC_Config.BatteryCapacityValue != default ? PVCC_Config.BatteryCapacityValue : 0;
        if (PVCC_Config.BatteryCapacityEntity is not null && PVCC_Config.BatteryCapacityEntity.TryGetStateValue(out float battCapacity))
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
    /// <summary>
    /// remaining PV yield forecast for today in WH
    /// </summary>
    public float CurrentEnergyImportPrice
    {
      get
      {
        return PVCC_Config.CurrentImportPriceEntity?.State is not null && PVCC_Config.CurrentImportPriceEntity.TryGetStateValue(out float value) ? value : 0;
      }
    }
    public float CurrentEnergyExportPrice
    {
      get
      {
        return PVCC_Config.CurrentExportPriceEntity is not null && PVCC_Config.CurrentExportPriceEntity.TryGetStateValue(out float value) ? value : 0;
      }
    }
    /// <summary>
    /// Currently usable energy in battery down to <see cref="AbsoluteMinimalSoC"/> or <see cref="PreferredMinimalSoC"/> depending on <see cref="EnforcePreferredSoC"/> in Wh
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
          return CalculateChargingDurationWh(BatterySoc, 100, CurrentAverageBatteryChargeDischargePower);
        else if (CurrentAverageBatteryChargeDischargePower < 0)
          return CalculateChargingDurationWh(BatterySoc, PreferredMinimalSoC, CurrentAverageBatteryChargeDischargePower);
        else
          return 0;
      }
    }
    public int EstimatedChargeTimeAtMinima
    {
      get
      {
        return CalculateChargingDurationA(NeedToChargeFromExternal.Item3, 100, PVCC_Config.MaxBatteryChargePower);
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
        if (need.Item1)
          return UpcomingPriceList.Where(p => p.StartTime <= need.Item2).OrderBy(p => p.Price).First();
        else
          return UpcomingPriceList.OrderBy(p => p.Price).First();
      }
    }
    private int CalculateChargingDurationWh(int startSoC, int endSoC, int pow)
    {
      float sS = (float)startSoC / 100;
      float eS = (float)endSoC / 100;

      float reqEnergy = (eS - sS) * BatteryCapacity * InverterEfficiency;
      float duration = reqEnergy / pow;

      return (int)(duration * 60);
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
      float e = BatteryCapacity * s - BatteryCapacity * ms;
      return (int)e;
    }
    public DateTime CalculateRuntime(DateTime startTime, int startSoc, int minSoc = -1)
    {
      if (minSoc < 0)
        minSoc = PreferredMinimalSoC;
      int pred_soc_at_startTime = Prediction_BatterySoC.TodayAndTomorrow.GetEntryAtTime(startTime).Value;
      int diff = pred_soc_at_startTime - startSoc;
      var pred_new = Prediction_BatterySoC.TodayAndTomorrow.Select(kvp => new KeyValuePair<DateTime, int>(kvp.Key, kvp.Value - diff)).ToDictionary();
      return pred_new.FirstUnderOrDefault(minSoc, start: startTime).Key;
    }
    public int CalculateSocNeedeedToReachTime(DateTime startTime, DateTime endTime, int minSoc = -1)
    {
      if (minSoc < 0)
        minSoc = PreferredMinimalSoC;
      int pred_soc_at_endTime = Prediction_BatterySoC.TodayAndTomorrow.GetEntryAtTime(endTime).Value;
      int diff = pred_soc_at_endTime - minSoc;
      var pred_new = Prediction_BatterySoC.TodayAndTomorrow.Select(kvp => new KeyValuePair<DateTime, int>(kvp.Key, kvp.Value - diff)).ToDictionary();
      return pred_new.GetEntryAtTime(startTime).Value;
    }
    private List<EpexPriceTableEntry> UpcomingPriceList
    {
      get
      {
        DateTime currentHour = DateTime.Now.Date.AddHours(DateTime.Now.Hour);
        return PriceList.Where(p => p.StartTime >= currentHour).OrderBy(p => p.StartTime).ToList();
      }
    }
    private Dictionary<int, EpexPriceTableEntry> PriceListRanked
    {
      get
      {
        Dictionary<int, EpexPriceTableEntry> result = [];
        int rank = 1;
        foreach (var entry in PriceList.OrderBy(p => p.Price))
        {
          result.Add(rank, entry);
          rank++;
        }
        return result.OrderBy(r => r.Value.StartTime).ToDictionary();
      }
    }
    private List<Tuple<int, EpexPriceTableEntry>> PriceListPercentage
    {
      get
      {
        List<Tuple<int, EpexPriceTableEntry>> result = [];
        float minPrice = PriceList.Min(p => p.Price);
        float maxPrice = PriceList.Max(p => p.Price);
        foreach (var entry in PriceList)
        {
          result.Add(new Tuple<int, EpexPriceTableEntry>(maxPrice - minPrice == 0 ? 0 : (int)Math.Round((entry.Price - minPrice) / (maxPrice - minPrice) * 100, 0), entry));
        }
        return result.OrderBy(r => r.Item2.StartTime).ToList();
      }
    }
    private int GetPriceRank(DateTime dateTime)
    {
      var priceRankAtTime = PriceListRanked.Where(r => r.Value.StartTime.Hour == dateTime.Hour).FirstOrDefault();
      return priceRankAtTime.Key;
    }
    private int GetPricePercentage(DateTime dateTime)
    {
      var pricePercentageAtTime = PriceListPercentage.Where(r => r.Item2.StartTime.Hour == dateTime.Hour).FirstOrDefault();
      return pricePercentageAtTime is null ? -1 : pricePercentageAtTime.Item1;
    }
    public int CurrentPriceRank
    {
      get
      {
        return GetPriceRank(DateTime.Now);
      }
    }
    public int CurrentPricePercentage
    {
      get
      {
        return GetPricePercentage(DateTime.Now);
      }
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
          if (PVCC_Config.CurrentImportPriceEntity is not null && PVCC_Config.CurrentImportPriceEntity.TryGetJsonAttribute("data", out JsonElement data))
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
        DateTime now = DateTime.Now;
        return PriceList.Where(p => p.StartTime >= now.Date && p.EndTime <= now.Date.AddDays(1)).OrderBy(p => p.Price).FirstOrDefault();
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
    public void UpdatePredictions(bool all = false)
    {
      DateTime now = DateTime.Now;
      if ((Prediction_Load.Today.First().Key.Date < now.Date && now.Second >= 30) || all)
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
      if (_dailySoCPrediction.Count == 0 || _dailyChargePrediction.Count == 0 || _dailyDischargePrediction.Count == 0 || now.Hour == 0 && now.Minute == 1 || (now - LastSnapshotUpdate).TotalMinutes > 24 * 60)
      {
        _dailyChargePrediction = Prediction_PV.TodayAndTomorrow.GetRunningSumsDaily();
        _dailyDischargePrediction = Prediction_Load.TodayAndTomorrow.GetRunningSumsDaily();
        var netPrediction = new NetEnergyPrediction(Prediction_PV, Prediction_Load, null!, null!, false);
        _dailySoCPrediction = new BatterySoCPrediction(netPrediction, PVCC_Config.BatterySoCEntity, BatteryCapacity).TodayAndTomorrow;
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
