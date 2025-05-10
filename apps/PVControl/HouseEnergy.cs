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
    private readonly RunningIntAverage _loadRunningAverage;
    private readonly RunningIntAverage _pvRunningAverage;
    private readonly RunningIntAverage _gridRunningAverage;
    private float _lastImportEnergySum;
    private float _lastExportEnergySum;
    private string _currentInverterRunMode = "unknown";
    /// <summary>
    /// Default Efficiency if not set in config@
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

      _loadRunningAverage = new RunningIntAverage(TimeSpan.FromMinutes(5));
      if (PVCC_Config.CurrentHouseLoadEntity is null)
        throw new NullReferenceException("HouseLoadEntity not available");
      if (PVCC_Config.CurrentHouseLoadEntity.TryGetStateValue(out int load))
        _loadRunningAverage.AddValue(load);

      _pvRunningAverage = new RunningIntAverage(TimeSpan.FromMinutes(5));
      if (PVCC_Config.CurrentPVPowerEntity is null)
        throw new NullReferenceException("CurrentPVPowerEntity not available");
      if (PVCC_Config.CurrentPVPowerEntity.TryGetStateValue(out int pv))
        _pvRunningAverage.AddValue(pv);

      _gridRunningAverage = new RunningIntAverage(TimeSpan.FromMinutes(1));
      if (PVCC_Config.CurrentGridPowerEntity.TryGetStateValue(out int grid))
        _gridRunningAverage.AddValue(grid);
      
      if (PVCC_Config.DailyExportEnergyEntity is null || PVCC_Config.DailyImportEnergyEntity is null)
        throw new NullReferenceException("DailyEnergyEntities not available");
      if (PVCC_Config.DailyExportEnergyEntity.TryGetStateValue(out float lastExportEnergySum))
        _lastExportEnergySum = lastExportEnergySum / 1000;
      if (PVCC_Config.DailyImportEnergyEntity.TryGetStateValue(out float lastImportEnergySum))
        _lastImportEnergySum = lastImportEnergySum / 1000;

      if (PVCC_Config.InverterStatusEntity.TryGetStateValue(out string inverterStatus))
        _currentInverterRunMode = inverterStatus;
      
      if (string.IsNullOrEmpty(PVCC_Config.DBLocation))
        throw new NullReferenceException("No DBLocation available");
      Prediction_Load = new HourlyWeightedAverageLoadPrediction(PVCC_Config.DBFullLocation, 10);

      if (PVCC_Config.ForecastPVEnergyTodayEntities is null || PVCC_Config.ForecastPVEnergyTomorrowEntities is null)
        throw new NullReferenceException("PV Forecast entities are not available");
      Prediction_PV = new OpenMeteoSolarForecastPrediction(PVCC_Config.ForecastPVEnergyTodayEntities, PVCC_Config.ForecastPVEnergyTomorrowEntities);

      Prediction_NetEnergy = new NetEnergyPrediction(Prediction_PV, Prediction_Load, _loadRunningAverage, _pvRunningAverage);

      if (PVCC_Config.BatterySoCEntity is null)
        throw new NullReferenceException("BatterySoCEntity not available");
      Prediction_BatterySoC = new BatterySoCPrediction(Prediction_NetEnergy, PVCC_Config.BatterySoCEntity, BatteryCapacity);

      PVCC_Config.CurrentImportPriceEntity?.StateAllChanges().SubscribeAsync(async _ => await UserStateChanged(PVCC_Config.CurrentImportPriceEntity));
      PVCC_Config.CurrentBatteryPowerEntity?.StateChanges().SubscribeAsync(async _ => await UserStateChanged(PVCC_Config.CurrentBatteryPowerEntity));
      PVCC_Config.CurrentPVPowerEntity?.StateChanges().SubscribeAsync(async _ => await UserStateChanged(PVCC_Config.CurrentPVPowerEntity));
      PVCC_Config.CurrentHouseLoadEntity?.StateChanges().SubscribeAsync(async _ => await UserStateChanged(PVCC_Config.CurrentHouseLoadEntity));
      PVCC_Config.DailyExportEnergyEntity.StateChanges().SubscribeAsync(async _ => await UserStateChanged(PVCC_Config.DailyExportEnergyEntity));
      PVCC_Config.DailyImportEnergyEntity.StateChanges().SubscribeAsync(async _ => await UserStateChanged(PVCC_Config.DailyImportEnergyEntity));
      PVCC_Config.CurrentGridPowerEntity.StateChanges().SubscribeAsync(async _ => await UserStateChanged(PVCC_Config.CurrentGridPowerEntity));
      PVCC_Config.InverterStatusEntity.StateChanges().SubscribeAsync(async _ => await UserStateChanged(PVCC_Config.InverterStatusEntity));
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
    /// Enforce the set preferred minimal Soc, if not enforced, it's allowed to go down to AbsoluteMinimalSoC to reach cheaper prices or PV charge
    /// </summary>
    public bool EnforcePreferredSoC { get; set; }
    /// <summary>
    /// UserSetting: Discharge if the export price is high and we can stay over preferred minimal SoC and still reach 100% SoC
    /// </summary>
    public bool OpportunisticDischarge { get; set; }
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
        _loadRunningAverage.AddValue(load);
      }
      if (entity.EntityId == PVCC_Config.CurrentPVPowerEntity?.EntityId && PVCC_Config.CurrentPVPowerEntity.TryGetStateValue(out int pv))
      {
        _pvRunningAverage.AddValue(pv);
      }
      if (entity.EntityId == PVCC_Config.DailyExportEnergyEntity?.EntityId && PVCC_Config.DailyExportEnergyEntity.TryGetStateValue(out float export))
      {
        float diff = (export / 1000) - _lastExportEnergySum;
        if (diff > 0)
        {
          SumEnergyExportEarningsTotal += diff * CurrentEnergyExportPriceTotal;
        }
        _lastExportEnergySum = export / 1000;
      }
      if (entity.EntityId == PVCC_Config.DailyImportEnergyEntity?.EntityId && PVCC_Config.DailyImportEnergyEntity.TryGetStateValue(out float import))
      {
        float diff = (import / 1000) - _lastImportEnergySum;
        if (diff > 0)
        {
          SumEnergyImportCostTotal += diff * CurrentEnergyImportPriceTotal;
          SumEnergyImportCostEnergyOnly += diff * CurrentEnergyImportPriceEnergyOnly;
          SumEnergyImportCostNetworkOnly += diff * CurrentEnergyImportPriceNetworkOnly;
        }
        _lastImportEnergySum = import / 1000;
      }
      if (entity.EntityId == PVCC_Config.CurrentGridPowerEntity?.EntityId && PVCC_Config.CurrentGridPowerEntity.TryGetStateValue(out int grid))
      {
        _gridRunningAverage.AddValue(grid);
      }
      if (entity.EntityId == PVCC_Config.InverterStatusEntity?.EntityId && PVCC_Config.InverterStatusEntity.TryGetStateValue(out string inverterStatus))
      {
        PVCC_Logger.LogInformation("Inverter RunMode changed from {CurrentInverterRunMode} to {InverterStatus}", _currentInverterRunMode, inverterStatus);
        _currentInverterRunMode = inverterStatus;
        // if the inverter switches back to normal mode, we send the reset signal before switching back to the selected mode
        if (_currentInverterRunMode == PVCC_Config.InverterStatusNormalString)
        {
          _resetCounter = 2;
          PVCC_Logger.LogInformation("Inverter returned to normal run mode, sending {ResetCounter} reset signal(s)", _resetCounter);
          _currentMode = new InverterState(InverterModes.reset, ForceChargeReasons.None);
        }
      }
    }

    private int _resetCounter = 0;
    private InverterState _currentMode = new InverterState(InverterModes.normal, ForceChargeReasons.None);
    private InverterState CalculateNewInverterMode(InverterState currentMode, NeedToChargeResult need, bool debugOut = false)
    {
      DateTime now = DateTime.Now;
      DateTime cheapestToday = CheapestImportWindowToday.StartTime;
      
      // send the reset signal until the counter reaches 0
      if (_currentMode.Mode == InverterModes.reset && _resetCounter > 0)
      {
        PVCC_Logger.LogDebug("Reset signal active (Counter: {count}) - Switching to {InverterModes}", _resetCounter, InverterModes.reset);
        _resetCounter--;
        return new InverterState(InverterModes.reset, ForceChargeReasons.None);
      }
      
#if !DEBUG
      if (OverrideMode != InverterModes.automatic)
      {
        var mode = OverrideMode;
        var reason = ForceChargeReasons.UserMode;
        if (currentMode.Mode != mode && debugOut)
          PVCC_Logger.LogDebug("Override mode active - Switching to {InverterModes}", mode);
        return new InverterState(mode, reason);
      }
#endif
      // fix the inverter problem that it doesn't switch to battery if the load is under ~200W
      if (currentMode.Mode == InverterModes.normal && CurrentAverageGridPower is > 50 and < 300 && BatterySoc > PreferredMinimalSoC)
      {
        // we just need to switch for a few seconds to force_discharge, afterwards the inverter keeps using the battery  
        PVCC_Logger.LogInformation("Inverter didn't automatically switch to Battery! Grid: {grid}, PV: {pv}, Load: {load}, Soc: {soc}", CurrentAverageGridPower, CurrentAveragePVPower, CurrentAverageHouseLoad, BatterySoc);
        _gridRunningAverage.Reset();
        return new InverterState(InverterModes.force_discharge, ForceChargeReasons.None);
      }
      
      // negative import price
      if (CurrentEnergyImportPriceTotal < 0)
      {
        var mode = BatterySoc <= 95 ? InverterModes.force_charge : InverterModes.grid_only;
        var reason = ForceChargeReasons.ImportPriceNegative;
        if (currentMode.Mode != mode && debugOut)
          PVCC_Logger.LogDebug("Negative Importprice {F} ct/kWh - Switching to {InverterModes}", CurrentEnergyImportPriceTotal, mode);
        return new InverterState(mode, reason);
      }

      // negative export price
      if (CurrentEnergyExportPriceTotal < 0)
      {
        var mode = InverterModes.house_only;
        var reason = ForceChargeReasons.ExportPriceNegative;
        if (currentMode.Mode != mode && debugOut)
          PVCC_Logger.LogDebug("Negative Exportprice {F} ct/kWh - Switching to {InverterModes}", CurrentEnergyExportPriceTotal, mode);
        return new InverterState(mode, reason);
      }

      // Opportunistic Discharge
      if (OpportunisticDischarge)
      {
        // prevent hysteresis on feedin priority 
        double maxSocDuration = (currentMode.Mode == InverterModes.feedin_priority) ? 1.5 : 2.0;
        // prices for now and the next two hours
        float priceNow = PriceListExport.FirstOrDefault(x => x.StartTime == now.Date.AddHours(now.Hour)).Price;
        float priceNextHour = PriceListExport.FirstOrDefault(x => x.StartTime == now.Date.AddHours(now.Hour + 1)).Price;
        float priceNextHourAndOne = PriceListExport.FirstOrDefault(x => x.StartTime == now.Date.AddHours(now.Hour + 2)).Price;
        
        // we are in PV period and have positive PV 
        if (!need.NeedToCharge && CurrentPVPeriod == PVPeriods.InPVPeriod && _pvRunningAverage.GetAverage() > _loadRunningAverage.GetAverage() + 200
          && MaxSocDurationToday > maxSocDuration && CurrentEnergyExportPriceTotal >= 0 && BatterySoc > (EnforcePreferredSoC ? PreferredMinimalSoC : AbsoluteMinimalSoC) + 3
          // only if it's getting cheaper, otherwise it's better to fill up and sell the overflow (because of sinusoidal nature of prices)
          && priceNow >= priceNextHour && priceNextHour >= priceNextHourAndOne)
        {
          // so we keep FeedInPriority mode
          var mode = InverterModes.feedin_priority;
          var reason = ForceChargeReasons.OpportunisticDischarge;
          if (currentMode.Mode != mode && debugOut)
            PVCC_Logger.LogDebug("In PV period, still reaching 100% SoC - Switching to {InverterModes}", mode);
          return new InverterState(mode, reason);
        }

        // since the price distribution is mostly sinusoidal over a day, we choose only the two highest maxima every day
        var sellPriceMaxima = PriceListExport.GetLocalMaxima(end:now.Date.AddDays(1)).OrderByDescending(t => t.Price).Select(t => t.StartTime).Take(2);
        if (sellPriceMaxima.Any(t => t.Date == now.Date && t.Hour == now.Hour) && CurrentEnergyExportPriceTotal / 100 >= ForceChargeMaxPrice)
        {
          // by default we stay at/above PreferredMinimalSoC
          int minAllowedSoc = PreferredMinimalSoC;
          // if the time is shortly before or in the solar time, we can go down to AbsoluteMinimalSoC
          if ((CurrentPVPeriod == PVPeriods.InPVPeriod || (CurrentPVPeriod == PVPeriods.BeforePV && (FirstRelevantPVEnergyToday - now).TotalHours is > 0 and < 4)))
            minAllowedSoc = AbsoluteMinimalSoC + 2;
          
          // NeedToCharge is off and min SoC stays over minimum (+4 to prevent hysteresis)
          if (!need.NeedToCharge && need.EstimatedSoc >= minAllowedSoc + 4)
          {
            var mode = InverterModes.force_discharge;
            var reason = ForceChargeReasons.OpportunisticDischarge;
            if (currentMode.Mode != mode && debugOut)
              PVCC_Logger.LogDebug("No need to charge, SoC stays over {soc}% - Switching to {InverterModes}", minAllowedSoc + 4, mode);
            return new InverterState(mode, reason);
          }
          // still no need to charge but battery already low, so we keep the inverter in feedin_priority mode as long as pv > load 
          else if (!need.NeedToCharge && _pvRunningAverage.GetAverage() > _loadRunningAverage.GetAverage() + 200)
          {
            var mode = InverterModes.feedin_priority;
            var reason = ForceChargeReasons.OpportunisticDischarge;
            if (currentMode.Mode != mode && debugOut)
              PVCC_Logger.LogDebug("Still reaching 100% SoC - Switching to {InverterModes}", mode);
            return new InverterState(mode, reason);
          }
          // if it's running, turn it off when we reached the minimum
          else if (ForceChargeReason == ForceChargeReasons.OpportunisticDischarge && currentMode.Mode == InverterModes.force_discharge && (need.EstimatedSoc <= minAllowedSoc + 2 || need.NeedToCharge))
          {
            var mode = InverterModes.normal;
            var reason = ForceChargeReasons.None;
            if (currentMode.Mode != mode && debugOut)
              PVCC_Logger.LogDebug("Reached minimal allowed SoC {soc}% or NeedToCharge - Switching to {InverterModes}", minAllowedSoc + 2, mode);
            return new InverterState(mode, reason);
          }
        }
      }

      if (ForceCharge && now > cheapestToday.AddHours(-1) && now < cheapestToday.AddHours(2))
      {
        // don't recalculate if charging already started
        if (ForceChargeReason == ForceChargeReasons.ForcedChargeAtMinimumPrice && currentMode.Mode == InverterModes.force_charge && BatterySoc <= Math.Min(98, ForceChargeTargetSoC+2))
        {
          return currentMode;
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
            if (PriceListImport.FirstOrDefault(p => p.StartTime == chargeStart.AddHours(-1)).Price < ForceChargeMaxPrice)
              chargeStart = cheapestToday.AddMinutes(-(chargeTime - 50));
          }
        }
        if (now > chargeStart && now < chargeStart.AddMinutes(chargeTime+10) && BatterySoc < Math.Min(96, ForceChargeTargetSoC))
        {
          //PVCC_Logger.LogInformation("ForceCharge cheapestToday starttime now: {start}", chargeStart);
          var mode = InverterModes.force_charge;
          var reason = ForceChargeReasons.ForcedChargeAtMinimumPrice;
          if (currentMode.Mode != mode && debugOut)
            PVCC_Logger.LogDebug("Charging in cheapest slot  - Switching to {InverterModes}", mode);
          return new InverterState(mode, reason);
        }
      }

      if (
        need.NeedToCharge
        // stay 30 seconds away from extremes, to make sure we have the same time as the provider
        && DateTime.Now > BestChargeTime.StartTime.AddSeconds(30) && DateTime.Now < BestChargeTime.EndTime.AddSeconds(-30)
        // don't charge over 98% SoC as it gets really slow and inefficient and don't start over 96%
        && (currentMode.Mode == InverterModes.force_charge && BatterySoc <= 98 || currentMode.Mode != InverterModes.force_charge && BatterySoc <= 96)
        )
      {
        // if chargetime is in the latest quarter of the current hour and the next hour is cheaper, it's better to just import energy normally, without force charging
        if ((ForceChargeReason == ForceChargeReasons.GoingUnderAbsoluteMinima || ForceChargeReason == ForceChargeReasons.GoingUnderPreferredMinima) &&
          CurrentPriceRank > GetPriceRank(now.AddHours(1)) && need.LatestChargeTime.Date == now.Date && need.LatestChargeTime.Hour == now.Hour && need.LatestChargeTime.Minute >= 45)
        {
          var mode = InverterModes.normal;
          var reason = ForceChargeReasons.None;
          if (currentMode.Mode != mode && debugOut)
            PVCC_Logger.LogDebug("Don't charge in last quarter hour if next hour is cheaper - Switching to {InverterModes}", mode);
          return new InverterState(mode, reason);
        }
        else
        { 
          var mode = InverterModes.force_charge;
          var reason = need.EstimatedSoc <= AbsoluteMinimalSoC + 2 ? ForceChargeReasons.GoingUnderAbsoluteMinima : ForceChargeReasons.GoingUnderPreferredMinima;
          if (currentMode.Mode != mode && debugOut)
            PVCC_Logger.LogDebug("NeedToCharge now - Switching to {InverterModes}", mode);
          return new InverterState(mode, reason);
        }
      }
      if (currentMode.Mode != InverterModes.normal && debugOut)
        PVCC_Logger.LogDebug("No special situation - Switching to {InverterModes}", InverterModes.normal);
      return new InverterState(InverterModes.normal, ForceChargeReasons.None);
    }
    public InverterModes ProposedMode
    {
      get
      {
        var newMode = CalculateNewInverterMode(_currentMode, NeedToChargeFromExternal, true);
        _currentMode = newMode;
        return _currentMode.Mode;
      }
    }
    private NeedToChargeResult _needToChargeFromExternalCache;
#if !DEBUG
    private DateTime _lastCalculatedNeedToCharge;
#endif
    /// <summary>
    /// returns:
    /// * Do we need to charge from grid
    /// * When do we reach minimal Soc
    /// * what's the estimated SoC at this time
    /// </summary>
    public NeedToChargeResult NeedToChargeFromExternal
    {
      get
      {
        DateTime now = DateTime.Now;
#if !DEBUG
        // cache the result for 5 seconds so that it's only calulated once per run
        if (_lastCalculatedNeedToCharge != default && Math.Abs((now - _lastCalculatedNeedToCharge).TotalSeconds) < 5)
        {
          return _needToChargeFromExternalCache;
        }
#endif
        var estSoC = Prediction_BatterySoC.TodayAndTomorrow;
        int minSoC = EnforcePreferredSoC ? PreferredMinimalSoC : AbsoluteMinimalSoC;
        if (_currentMode.Mode == InverterModes.force_charge)
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
        // be very pessimistic and substract 10% of the targettime, so we are relatively sure to reach the next price minima 
        int quarterhoursTilCharge = (int)(((minReached.Key - now).TotalMinutes * 0.1)/ 15);
        _needToChargeFromExternalCache = new NeedToChargeResult(
          needToCharge: needCharge,
          latestChargeTime: minReached.Key.AddMinutes(-quarterhoursTilCharge * 15),
          estimatedSoc: minReached.Value);
#if !DEBUG
        _lastCalculatedNeedToCharge = now;
#endif
        return _needToChargeFromExternalCache;
      }
    }
    public ForceChargeReasons ForceChargeReason => _currentMode.ModeReason;
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
        if (_currentMode.Mode == InverterModes.force_charge)
        {
          if (IsNowCheapestImportWindowTotal)
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
    /// How much power in W is available for additional loads
    /// </summary>
    public int AvailablePower
    {
      get
      {
        return 0;
      }
    }
    /// <summary>
    /// How much energy in Wh is available for additional loads
    /// </summary>
    public int AvailableEnergy
    {
      get
      {
        return 0;
      }
    }
    /// <summary>
    /// Current State of Charge of the house battery in %
    /// </summary>
    public int BatterySoc => PVCC_Config.BatterySoCEntity is not null && PVCC_Config.BatterySoCEntity.TryGetStateValue(out int soc) ? soc : 0;

    /// <summary>
    /// Minimal SoC of battery which may not be used normally
    /// if override is active, we try not to go below, but allow if it's cheaper to wait, but we can never go under AbsoluteMinimalSoC (Inverter set limit)
    /// </summary>
    public int PreferredMinimalSoC =>
      // Preferred can never be lower than AbsoluteMinimalSoC
      Math.Max(PreferredMinBatterySoC, AbsoluteMinimalSoC);

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
    private float InverterEfficiency => PVCC_Config.InverterEfficiency != default ? PVCC_Config.InverterEfficiency : _defaultInverterEfficiency;

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
    /// return the batterystatus according to the current average charge power
    /// </summary>
    public BatteryStatuses BatteryStatus
    {
      get
      {
        if (CurrentAverageBatteryChargeDischargePower is > -10 and < 10)
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
    /// <summary>
    /// Currently usable energy in battery down to <see cref="AbsoluteMinimalSoC"/> or <see cref="PreferredMinimalSoC"/> depending on <see cref="EnforcePreferredSoC"/> in Wh
    /// </summary>
    public int UsableBatteryEnergy => CalculateBatteryEnergyAtSoC(BatterySoc, EnforcePreferredSoC ? PreferredMinimalSoC : AbsoluteMinimalSoC);

    public int ReserveBatteryEnergy => CalculateBatteryEnergyAtSoC(EnforcePreferredSoC ? PreferredMinimalSoC : AbsoluteMinimalSoC, 0);

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
        if (CurrentAverageBatteryChargeDischargePower is > -10 and < 10)
          return 0;
        else if (CurrentAverageBatteryChargeDischargePower > 0)
          return CalculateChargingDurationWh(BatterySoc, 100, CurrentAverageBatteryChargeDischargePower);
        else if (CurrentAverageBatteryChargeDischargePower < 0)
          return CalculateChargingDurationWh(BatterySoc, PreferredMinimalSoC, CurrentAverageBatteryChargeDischargePower);
        else
          return 0;
      }
    }
    public int EstimatedChargeTimeAtMinima => CalculateChargingDurationA(NeedToChargeFromExternal.EstimatedSoc, 100, PVCC_Config.MaxBatteryChargePower);

    public int CurrentAverageBatteryChargeDischargePower => _battChargeAverage.GetAverage();

    public int CurrentAverageHouseLoad => _loadRunningAverage.GetAverage();

    public int CurrentAveragePVPower => _pvRunningAverage.GetAverage();

    public int CurrentAverageGridPower => _gridRunningAverage.GetAverage() * -1;

    public PriceTableEntry BestChargeTime
    {
      get
      {
        var need = NeedToChargeFromExternal;
        if (need.NeedToCharge)
          return UpcomingPriceList.Where(p => p.StartTime <= need.LatestChargeTime).OrderBy(p => p.Price).First();
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
      int pred_Soc_At_StartTime = Prediction_BatterySoC.TodayAndTomorrow.GetEntryAtTime(startTime).Value;
      int diff = pred_Soc_At_StartTime - startSoc;
      var pred_New = Prediction_BatterySoC.TodayAndTomorrow.Select(kvp => new KeyValuePair<DateTime, int>(kvp.Key, kvp.Value - diff)).ToDictionary();
      return pred_New.FirstUnderOrDefault(minSoc, start: startTime).Key;
    }
    public int CalculateSocNeedeedToReachTime(DateTime startTime, DateTime endTime, int minSoc = -1)
    {
      if (minSoc < 0)
        minSoc = PreferredMinimalSoC;
      int pred_Soc_At_EndTime = Prediction_BatterySoC.TodayAndTomorrow.GetEntryAtTime(endTime).Value;
      int diff = pred_Soc_At_EndTime - minSoc;
      var pred_New = Prediction_BatterySoC.TodayAndTomorrow.Select(kvp => new KeyValuePair<DateTime, int>(kvp.Key, kvp.Value - diff)).ToDictionary();
      return pred_New.GetEntryAtTime(startTime).Value;
    }
    private List<PriceTableEntry> UpcomingPriceList
    {
      get
      {
        DateTime currentHour = DateTime.Now.Date.AddHours(DateTime.Now.Hour);
        return PriceListImport.Where(p => p.StartTime >= currentHour).OrderBy(p => p.StartTime).ToList();
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
      var priceRankAtTime = PriceListRanked.FirstOrDefault(r => r.Value.StartTime.Hour == dateTime.Hour);
      return priceRankAtTime.Key;
    }
    private int GetPricePercentage(DateTime dateTime)
    {
      var pricePercentageAtTime = PriceListPercentage.FirstOrDefault(r => r.Item2.StartTime.Hour == dateTime.Hour);
      return pricePercentageAtTime?.Item1 ?? -1;
    }
    public int CurrentPriceRank => GetPriceRank(DateTime.Now);

    public int CurrentPricePercentage => GetPricePercentage(DateTime.Now);
    private List<PriceTableEntry> _priceListCache;
    private void UpdatePriceList()
    {
      _priceListCache = [];
    }
    private List<PriceTableEntry> PriceListNetto
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
                p.Price * 100 
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
            PVCC_Config.CurrentExportPriceEntity.TryGetStateValue(out float value) ? value : 0
            )).ToList();
        }
      }
    }
    private float CalculateBruttoPriceExport(float nettoPrice, bool inclNetworkPrice)
    {
      return (nettoPrice * PVCC_Config.ExportPriceMultiplier + PVCC_Config.ExportPriceAddition + (inclNetworkPrice ? PVCC_Config.ExportPriceNetwork : 0)) * (1 + PVCC_Config.ExportPriceTax);
    }
    private float CalculateBruttoPriceImport(float nettoPrice, bool inclNetworkPrice)
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
    public float SumEnergyImportCostEnergyOnly { get; set; } = 0.0f;
    public float SumEnergyImportCostNetworkOnly { get; set; } = 0.0f;
    public float SumEnergyImportCostTotal { get; set; } = 0.0f;
    public float SumEnergyExportEarningsTotal { get; set; } = 0.0f;
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
    public double MaxSocDurationToday
    {
      get
      {
        var maxSocRestOfToday = Prediction_BatterySoC.Today.FirstMaxOrDefault(start: DateTime.Now);
        if (maxSocRestOfToday.Value < 99)
          return 0;
        var firstUnderMax = Prediction_BatterySoC.Today.FirstUnderOrDefault(99, maxSocRestOfToday.Key);
        var span = firstUnderMax.Key - maxSocRestOfToday.Key;
        return span.TotalHours;
      }
    }
    public bool WillReachMaxSocToday
    {
      get
      {
        var maxSocRestOfToday = Prediction_BatterySoC.Today.FirstMaxOrDefault(start: DateTime.Now);
        return maxSocRestOfToday.Value >= 99;
      }
    }
    public bool WillReachmaxSocTomorrow
    {
      get
      {
        var maxSocTomorrow = Prediction_BatterySoC.Tomorrow.FirstMaxOrDefault(start: DateTime.Now);
        return maxSocTomorrow.Value >= 99;
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
      if (_dailySoCPrediction.Count == 0 || _dailyChargePrediction.Count == 0 || _dailyDischargePrediction.Count == 0 || now is { Hour: 0, Minute: 1 } || (now - LastSnapshotUpdate).TotalMinutes > 24 * 60)
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
