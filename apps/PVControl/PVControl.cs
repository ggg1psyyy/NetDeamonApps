using NetDaemon.Extensions.MqttEntityManager;
using NetDaemon.Extensions.Scheduler;
using NetDaemon.HassModel.Entities;
using System.Globalization;
using System.Linq;
using System.Reactive.Concurrency;
using System.Threading;
using System.Threading.Tasks;
using static NetDeamon.apps.PVControl.PVControlCommon;

namespace NetDeamon.apps.PVControl
{
  [NetDaemonApp]
#if DEBUG
  [Focus]
#endif
  public class PVControl : IAsyncInitializable
  {
    // nullforgiving "!" used to supress CS8618 for now
    private HouseEnergy _house = null!;
    #region Created Entities
    private Entity _modeEntity = null!;
    private Entity _battery_RemainingTimeEntity = null!;
    private Entity _battery_RemainingEnergyEntity = null!;
    private Entity _needToChargeFromGridTodayEntity = null!;
    private Entity _battery_StatusEntity = null!;
    private Entity _info_EstimatedMaxSoCTodayEntity = null!;
    private Entity _info_EstimatedMaxSoCTomorrowEntity = null!;
    private Entity _info_EstimatedMinSoCTodayEntity = null!;
    private Entity _info_EstimatedMinSoCTomorrowEntity = null!;
    private Entity _prefBatterySoCEntity = null!;
    private Entity _enforcePreferredSocEntity = null!;
    private Entity _forceChargeEntity = null!;
    private Entity _forceChargeTargetSoCEntity = null!;
    private Entity _forceChargeMaxPriceEntity = null!;
    private Entity _info_chargeTodayEntity = null!;
    private Entity _info_dischargeTodayEntity = null!;
    private Entity _info_chargeTomorrowEntity = null!;
    private Entity _info_dischargeTomorrowEntity = null!;
    private Entity _info_PredictedSoCEntity = null!;
    private Entity _info_PredictedChargeEntity = null!;
    private Entity _info_PredictedDischargeEntity = null!;
    private Entity _overrideModeEntity = null!;
    private Entity _RunHeavyLoadsNowEntity = null!;
    private Entity _currentImportPriceBruttoEntity = null!;
    private Entity _currentExportPriceBruttoEntity = null!;
    private Entity _sumImportCostBruttoEntity = null!;
    private Entity _sumImportCostEnergyOnlyEntity = null!;
    private Entity _sumImportCostNetworkOnlyEntity = null!;
    private Entity _sumExportEarningsBruttoEntity = null!;
    private Entity _sumImportExportNetCostEntity = null!;
    private Entity _bestExportPriceEntity = null!;
    private Entity _bestImportPriceEntity = null!;
    private Entity _enableOpportunisticExport = null!;
    #endregion

    public PVControl(IHaContext ha, IMqttEntityManager entityManager, IAppConfig<PVConfig> config, IScheduler scheduler, ILogger<PVControl> logger)
    {
      CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
      // Converter often needs some time after restart of HA to give values
      while (!config.Value.BatterySoCEntity.TryGetStateValue<int>(out _))
        System.Threading.Thread.Sleep(1000);
      PVCCInstance.Initialize(ha, entityManager, logger, config, (NetDaemon.Extensions.Scheduler.DisposableScheduler)scheduler);
      if (string.IsNullOrWhiteSpace(PVCC_Config.DBLocation))
        PVCC_Config.DBLocation = "apps/DataLogger/energy_history.db";
      
      PVCC_Logger.LogInformation("DB Location: {loc}", PVCC_Config.DBFullLocation);

      if (!CheckConfiguration())
        throw new Exception("Error initializing configuration");

      PVCC_Logger.LogInformation("Finished PVControl constructor");
    }
    async Task IAsyncInitializable.InitializeAsync(CancellationToken cancellationToken)
    {
      if (await RegisterControlSensors())
      {
        _house = new HouseEnergy();
        #region Load settings from HA if available

        if (_sumExportEarningsBruttoEntity.TryGetStateValue(out float sumEarningsTotal))
          _house.SumEnergyExportEarningsTotal = sumEarningsTotal * 100;
        if (_sumImportCostBruttoEntity.TryGetStateValue(out float sumImportTotal))
          _house.SumEnergyImportCostTotal = sumImportTotal * 100;
        if (_sumImportCostEnergyOnlyEntity.TryGetStateValue(out float sumImportEnergy))
          _house.SumEnergyImportCostEnergyOnly = sumImportEnergy * 100;
        if (_sumImportCostNetworkOnlyEntity.TryGetStateValue(out float sumImportNetwork))
          _house.SumEnergyImportCostNetworkOnly = sumImportNetwork * 100;

        if (_forceChargeMaxPriceEntity.TryGetStateValue(out int maxPrice))
          _house.ForceChargeMaxPrice = maxPrice;
        if (_forceChargeTargetSoCEntity.TryGetStateValue(out int targetSoC))
          _house.ForceChargeTargetSoC = targetSoC;
        if (_forceChargeEntity.TryGetStateValue(out bool forceCharge))
          _house.ForceCharge = forceCharge;
        if (_overrideModeEntity.TryGetStateValue(out InverterModes mode))
          _house.OverrideMode = mode;
        if (_prefBatterySoCEntity.TryGetStateValue(out int prefSoC))
          _house.PreferredMinBatterySoC = prefSoC;
        if (_enforcePreferredSocEntity.TryGetStateValue(out bool enforcePrefSoC))
          _house.EnforcePreferredSoC = enforcePrefSoC;
        if (_enableOpportunisticExport.TryGetStateValue(out bool enableOpp))
          _house.OpportunisticDischarge = enableOpp;

        #endregion

        (await PVCC_EntityManager.PrepareCommandSubscriptionAsync(_prefBatterySoCEntity.EntityId).ConfigureAwait(false)).SubscribeAsync(async state => await UserStateChanged(_prefBatterySoCEntity, state));
        (await PVCC_EntityManager.PrepareCommandSubscriptionAsync(_forceChargeMaxPriceEntity.EntityId).ConfigureAwait(false)).SubscribeAsync(async state => await UserStateChanged(_forceChargeMaxPriceEntity, state));
        (await PVCC_EntityManager.PrepareCommandSubscriptionAsync(_forceChargeTargetSoCEntity.EntityId).ConfigureAwait(false)).SubscribeAsync(async state => await UserStateChanged(_forceChargeTargetSoCEntity, state));
        (await PVCC_EntityManager.PrepareCommandSubscriptionAsync(_enforcePreferredSocEntity.EntityId).ConfigureAwait(false)).SubscribeAsync(async state => await UserStateChanged(_enforcePreferredSocEntity, state));
        (await PVCC_EntityManager.PrepareCommandSubscriptionAsync(_forceChargeEntity.EntityId).ConfigureAwait(false)).SubscribeAsync(async state => await UserStateChanged(_forceChargeEntity, state));
        (await PVCC_EntityManager.PrepareCommandSubscriptionAsync(_overrideModeEntity.EntityId).ConfigureAwait(false)).SubscribeAsync(async state => await UserStateChanged(_overrideModeEntity, state));
        (await PVCC_EntityManager.PrepareCommandSubscriptionAsync(_enableOpportunisticExport.EntityId).ConfigureAwait(false)).SubscribeAsync(async state => await UserStateChanged(_enableOpportunisticExport, state));

        var manager = new Managers.LoadManager(_house);
#if !DEBUG
        PVCC_Scheduler.ScheduleCron("*/15 * * * * *", async () => await ScheduledOperations(), true);
#endif 

#if DEBUG
        var x = _house.ProposedMode;
        _house.UpdatePredictions(true);
#endif

      }
      else
      {
        PVCC_Logger.LogError("Error registering sensors");
      }
    }
    private async Task UserStateChanged(Entity? entity, string newState)
    {
      if (entity == null)
        return;
      newState = newState.ToLower();

      if (entity.EntityId == _overrideModeEntity.EntityId && entity.State is not null)
      {
        if (Enum.TryParse(newState, out InverterModes modeselect))
          _house.OverrideMode = modeselect;
        else
          _house.OverrideMode = InverterModes.automatic;
        await PVCC_EntityManager.SetStateAsync(entity.EntityId, _house.OverrideMode.ToString());
      }
      if (entity.EntityId == _prefBatterySoCEntity.EntityId)
      {
        if (int.TryParse(newState, out int value))
          _house.PreferredMinBatterySoC = value;
        await PVCC_EntityManager.SetStateAsync(entity.EntityId, _house.PreferredMinBatterySoC.ToString());
      }
      if (entity.EntityId == _enforcePreferredSocEntity.EntityId && entity.State is not null)
      {
        _house.EnforcePreferredSoC = newState.Equals("on", StringComparison.CurrentCultureIgnoreCase);
        await PVCC_EntityManager.SetStateAsync(entity.EntityId, _house.EnforcePreferredSoC ? "ON" : "OFF");
      }
      if (entity.EntityId == _forceChargeEntity.EntityId && entity.State is not null)
      {
        _house.ForceCharge = newState.Equals("on", StringComparison.CurrentCultureIgnoreCase);
        await PVCC_EntityManager.SetStateAsync(entity.EntityId, _house.ForceCharge ? "ON" : "OFF");
      }
      if (entity.EntityId == _enableOpportunisticExport.EntityId && entity.State is not null)
      {
        _house.OpportunisticDischarge = newState.Equals("on", StringComparison.CurrentCultureIgnoreCase);
        await PVCC_EntityManager.SetStateAsync(entity.EntityId, _house.OpportunisticDischarge ? "ON" : "OFF");
      }
      if (entity.EntityId == _forceChargeMaxPriceEntity.EntityId && entity.State is not null)
      {
        if (int.TryParse(newState, out int value))
          _house.ForceChargeMaxPrice = value;
        await PVCC_EntityManager.SetStateAsync(entity.EntityId, _house.ForceChargeMaxPrice.ToString());
      }
      if (entity.EntityId == _forceChargeTargetSoCEntity.EntityId && entity.State is not null)
      {
        if (int.TryParse(newState, out int value))
          _house.ForceChargeTargetSoC = value;
        await PVCC_EntityManager.SetStateAsync(entity.EntityId, _house.ForceChargeTargetSoC.ToString());
      }
#if !DEBUG
      await ScheduledOperations(); 
#endif
    }
    private async Task ScheduledOperations()
    {
      PVCC_Logger.LogDebug("Entering Schedule");
      DateTime now = DateTime.Now;
      PVCC_Logger.LogDebug("Updating Predictions");
      _house.UpdatePredictions();
      if (_house.Prediction_Load.TodayAndTomorrow.First().Key.Date != now.Date)
      {
        PVCC_Logger.LogError("Prediction doesn't start with today");
        _house.UpdatePredictions(true);
      }
      PVCC_Logger.LogDebug("Finished Updating Predictions");
      await PVCC_EntityManager.SetStateAsync(_modeEntity.EntityId, _house.ProposedMode.ToString());
      #region Mode
      var nextCheapest = _house.BestChargeTime;
      var attr_Mode = new
      {
        next_charge_window_start = nextCheapest.StartTime.ToISO8601(),
        next_charge_window_end = nextCheapest.EndTime.ToISO8601(),
        price = nextCheapest.Price.ToString(CultureInfo.InvariantCulture),
        charge_Reason = _house.ForceChargeReason.ToString(),
      };
      await PVCC_EntityManager.SetAttributesAsync(_modeEntity.EntityId, attr_Mode);
      #endregion
      #region RunHeavyLoads
      await PVCC_EntityManager.SetStateAsync(_RunHeavyLoadsNowEntity.EntityId, _house.RunHeavyLoadsNow.ToString());
      var attr_HeavyLoad = new
      {
        Reason = _house.RunHeavyLoadReason.ToString(),
      };
      await PVCC_EntityManager.SetAttributesAsync(_RunHeavyLoadsNowEntity.EntityId, attr_HeavyLoad);
      #endregion
      #region Remaining battery
      await PVCC_EntityManager.SetStateAsync(_battery_RemainingTimeEntity.EntityId, _house.EstimatedTimeToBatteryFullOrEmpty.ToString(CultureInfo.InvariantCulture));
      var attr_RemainingTime = new
      {
        Estimated_time = now.AddMinutes(_house.EstimatedTimeToBatteryFullOrEmpty).ToISO8601(),
        next_relevant_pv_charge = _house.FirstRelevantPVEnergyToday.ToISO8601(),
        avg_battery_charge_or_discharge_Power = _house.CurrentAverageBatteryChargeDischargePower.ToString(CultureInfo.InvariantCulture) + " W",
        status = _house.BatteryStatus.ToString(),
      };
      await PVCC_EntityManager.SetAttributesAsync(_battery_RemainingTimeEntity.EntityId, attr_RemainingTime);
      #endregion
      #region Battery status
      await PVCC_EntityManager.SetStateAsync(_battery_StatusEntity.EntityId, _house.BatteryStatus.ToString());
      var attr_batStatus = new
      {
        avg_battery_charge_or_discharge_Power = _house.CurrentAverageBatteryChargeDischargePower.ToString(CultureInfo.InvariantCulture) + " W",
        avg_house_load_now = _house.CurrentAverageHouseLoad.ToString(CultureInfo.InvariantCulture) + " W",
        predicted_house_load_now = _house.Prediction_Load.CurrentValue * 4 + " W",
        avg_pv_power_now = _house.CurrentAveragePVPower.ToString(CultureInfo.InvariantCulture) + " W",
        predicted_pv_power_now = _house.Prediction_PV.CurrentValue * 4 + " W",
        current_SoC = _house.BatterySoc.ToString(CultureInfo.InvariantCulture) + "%",
      };
      await PVCC_EntityManager.SetAttributesAsync(_battery_StatusEntity.EntityId, attr_batStatus);
      #endregion
      #region Remaining energy
      await PVCC_EntityManager.SetStateAsync(_battery_RemainingEnergyEntity.EntityId, _house.UsableBatteryEnergy.ToString(CultureInfo.InvariantCulture));
      var attr_RemainingEnergy = new
      {
        min_allowed_SoC = (_house.EnforcePreferredSoC ? _house.PreferredMinimalSoC : _house.AbsoluteMinimalSoC).ToString() + "%",
        remaining_energy_at_min_battery_soc = _house.ReserveBatteryEnergy.ToString(CultureInfo.InvariantCulture) + " Wh",
        remaining_energy_to_zero_soc = _house.CalculateBatteryEnergyAtSoC(_house.BatterySoc, 0).ToString(CultureInfo.InvariantCulture) + " Wh",
        battery_capacity = _house.BatteryCapacity.ToString(CultureInfo.InvariantCulture) + " Wh",
      };
      await PVCC_EntityManager.SetAttributesAsync(_battery_RemainingEnergyEntity.EntityId, attr_RemainingEnergy);
      #endregion
      #region NeedToCharge
      var needToCharge = _house.NeedToChargeFromExternal;
      await PVCC_EntityManager.SetStateAsync(_needToChargeFromGridTodayEntity.EntityId, needToCharge.NeedToCharge ? "ON" : "OFF");
      var attr_Charge = new
      {
        minimal_SoC_allowed = _house.AbsoluteMinimalSoC.ToString(CultureInfo.InvariantCulture) + "%",
        preferred_SoC = _house.PreferredMinimalSoC.ToString(CultureInfo.InvariantCulture) + "%",
        minimal_estimated_SoC = needToCharge.EstimatedSoc.ToString(CultureInfo.InvariantCulture) + "%",
        at_time = needToCharge.LatestChargeTime.ToISO8601(),
        estimated_charge_time = _house.EstimatedChargeTimeAtMinima.ToString(CultureInfo.InvariantCulture) + " min",
      };
      await PVCC_EntityManager.SetAttributesAsync(_needToChargeFromGridTodayEntity.EntityId, attr_Charge);
      #endregion
      #region Prediction
      var curPredSoc = _house.DailyBatterySoCPredictionTodayAndTomorrow.GetEntryAtTime(now);
      if (curPredSoc.Key != default)
      {
        await PVCC_EntityManager.SetStateAsync(_info_PredictedSoCEntity.EntityId, curPredSoc.Value.ToString(CultureInfo.InvariantCulture));
        var attr_pred_soc = new
        {
          current_entry_time = curPredSoc.Key.ToISO8601(),
          last_snapshot = _house.LastSnapshotUpdate.ToISO8601(),
          data = _house.DailyBatterySoCPredictionTodayAndTomorrow.Select(s => new { datetime = s.Key, soc = s.Value }),
        };
        await PVCC_EntityManager.SetAttributesAsync(_info_PredictedSoCEntity.EntityId, attr_pred_soc);
      }
      var curPredCharge = _house.DailyChargePredictionTodayAndTomorrow.GetEntryAtTime(now);
      if (curPredCharge.Key != default)
      {
        await PVCC_EntityManager.SetStateAsync(_info_PredictedChargeEntity.EntityId, curPredCharge.Value.ToString(CultureInfo.InvariantCulture));
        var attr_pred_charge = new
        {
          current_entry_time = curPredCharge.Key.ToISO8601(),
          last_snapshot = _house.LastSnapshotUpdate.ToISO8601(),
          data = _house.DailyChargePredictionTodayAndTomorrow.Select(s => new { datetime = s.Key, charge = s.Value }),
        };
        await PVCC_EntityManager.SetAttributesAsync(_info_PredictedChargeEntity.EntityId, attr_pred_charge);
      }
      var curPredDischarge = _house.DailyDischargePredictionTodayAndTomorrow.GetEntryAtTime(now);
      if (curPredDischarge.Key != default)
      {
        await PVCC_EntityManager.SetStateAsync(_info_PredictedDischargeEntity.EntityId, curPredDischarge.Value.ToString(CultureInfo.InvariantCulture));
        var attr_pred_charge = new
        {
          current_entry_time = curPredDischarge.Key.ToISO8601(),
          last_snapshot = _house.LastSnapshotUpdate.ToISO8601(),
          data = _house.DailyDischargePredictionTodayAndTomorrow.Select(s => new { datetime = s.Key, discharge = s.Value }),
        };
        await PVCC_EntityManager.SetAttributesAsync(_info_PredictedDischargeEntity.EntityId, attr_pred_charge);
      }
      #endregion
      #region SoC estimates
      var est_soc_today = _house.Prediction_BatterySoC.Today.Where(s => s.Key >= now).ToDictionary();
      var est_soc_tomorrow = _house.Prediction_BatterySoC.Tomorrow;

      if (est_soc_today.Count > 0)
      {
        var min_soc_today = est_soc_today.FirstMinOrDefault();
        await PVCC_EntityManager.SetStateAsync(_info_EstimatedMinSoCTodayEntity.EntityId, min_soc_today.Value.ToString(CultureInfo.InvariantCulture));
        var attr_Min_SoC_Today = new
        {
          time = min_soc_today.Key.ToISO8601(),
          data = _house.Prediction_BatterySoC.TodayAndTomorrow.Where(s => s.Key >= now).Select(s => new { datetime = s.Key, soc = s.Value }),
        };
        await PVCC_EntityManager.SetAttributesAsync(_info_EstimatedMinSoCTodayEntity.EntityId, attr_Min_SoC_Today);

        var max_soc_today = est_soc_today.FirstMaxOrDefault();
        await PVCC_EntityManager.SetStateAsync(_info_EstimatedMaxSoCTodayEntity.EntityId, max_soc_today.Value.ToString(CultureInfo.InvariantCulture));
        var attr_Max_SoC_Today = new
        {
          time = max_soc_today.Key.ToISO8601(),
          data = est_soc_today,
        };
        await PVCC_EntityManager.SetAttributesAsync(_info_EstimatedMaxSoCTodayEntity.EntityId, attr_Max_SoC_Today);
      }

      if (est_soc_tomorrow.Count > 0)
      {
        var min_soc_tomorrow = est_soc_tomorrow.FirstMinOrDefault();
        await PVCC_EntityManager.SetStateAsync(_info_EstimatedMinSoCTomorrowEntity.EntityId, min_soc_tomorrow.Value.ToString(CultureInfo.InvariantCulture));
        var attr_Min_SoC_Tomorrow = new
        {
          time = min_soc_tomorrow.Key.ToISO8601(),
          data = est_soc_tomorrow,
        };
        await PVCC_EntityManager.SetAttributesAsync(_info_EstimatedMinSoCTomorrowEntity.EntityId, attr_Min_SoC_Tomorrow);

        var max_soc_tomorrow = est_soc_tomorrow.FirstMaxOrDefault();
        await PVCC_EntityManager.SetStateAsync(_info_EstimatedMaxSoCTomorrowEntity.EntityId, max_soc_tomorrow.Value.ToString(CultureInfo.InvariantCulture));
        var attr_Max_SoC_Tomorrow = new
        {
          time = max_soc_tomorrow.Key.ToISO8601(),
          data = est_soc_tomorrow,
        };
        await PVCC_EntityManager.SetAttributesAsync(_info_EstimatedMaxSoCTomorrowEntity.EntityId, attr_Max_SoC_Tomorrow);
      }
      #endregion
      #region charge/discharge forecasts
      var chargeToday = _house.Prediction_PV.Today;
      var chargeTomorrow = _house.Prediction_PV.Tomorrow;
      var dischargeToday = _house.Prediction_Load.Today;
      var dischargeTomorrow = _house.Prediction_Load.Tomorrow;

      if (chargeToday.Count > 0)
      {
        await PVCC_EntityManager.SetStateAsync(_info_chargeTodayEntity.EntityId, chargeToday.GetSum(start: now).ToString(CultureInfo.InvariantCulture));
        var attr_chargeToday = new
        {
          data = chargeToday,
        };
        await PVCC_EntityManager.SetAttributesAsync(_info_chargeTodayEntity.EntityId, attr_chargeToday);
      }
      if (chargeTomorrow.Count > 0)
      {
        await PVCC_EntityManager.SetStateAsync(_info_chargeTomorrowEntity.EntityId, chargeTomorrow.GetSum().ToString(CultureInfo.InvariantCulture));
        var attr_chargeTomorrow = new
        {
          data = chargeTomorrow,
        };
        await PVCC_EntityManager.SetAttributesAsync(_info_chargeTomorrowEntity.EntityId, attr_chargeTomorrow);
      }

      if (dischargeToday.Count > 0)
      {
        await PVCC_EntityManager.SetStateAsync(_info_dischargeTodayEntity.EntityId, dischargeToday.GetSum(start: now).ToString(CultureInfo.InvariantCulture));
        var attr_dischargeToday = new
        {
          data = dischargeToday,
        };
        await PVCC_EntityManager.SetAttributesAsync(_info_dischargeTodayEntity.EntityId, attr_dischargeToday);
      }
      if (dischargeTomorrow.Count > 0)
      {
        await PVCC_EntityManager.SetStateAsync(_info_dischargeTomorrowEntity.EntityId, dischargeTomorrow.GetSum().ToString(CultureInfo.InvariantCulture));
        var attr_dischargeTomorrow = new
        {
          data = dischargeTomorrow,
        };
        await PVCC_EntityManager.SetAttributesAsync(_info_dischargeTomorrowEntity.EntityId, attr_dischargeTomorrow);
      }
      #endregion
      #region Prices
      await PVCC_EntityManager.SetStateAsync(_currentImportPriceBruttoEntity.EntityId, (_house.CurrentEnergyImportPriceTotal / 100).ToString(CultureInfo.InvariantCulture));
      var attr_currentImportPrice = new
      {
        data = _house.PriceListImport.Select(s => new { start_time = s.StartTime.ToISO8601(), end_time = s.EndTime.ToISO8601(), price_per_kwh = s.Price }),
      };
      await PVCC_EntityManager.SetAttributesAsync(_currentImportPriceBruttoEntity.EntityId, attr_currentImportPrice);

      await PVCC_EntityManager.SetStateAsync(_currentExportPriceBruttoEntity.EntityId, (_house.CurrentEnergyExportPriceTotal / 100).ToString(CultureInfo.InvariantCulture));
      var attr_currentExportPrice = new
      {
        data = _house.PriceListExport.Select(s => new { start_time = s.StartTime.ToISO8601(), end_time = s.EndTime.ToISO8601(), price_per_kwh = s.Price }),
      };
      await PVCC_EntityManager.SetAttributesAsync(_currentExportPriceBruttoEntity.EntityId, attr_currentExportPrice);

      await PVCC_EntityManager.SetStateAsync(_sumExportEarningsBruttoEntity.EntityId, (_house.SumEnergyExportEarningsTotal / 100).ToString(CultureInfo.InvariantCulture));

      await PVCC_EntityManager.SetStateAsync(_sumImportCostBruttoEntity.EntityId, (_house.SumEnergyImportCostTotal / 100).ToString(CultureInfo.InvariantCulture));

      await PVCC_EntityManager.SetStateAsync(_sumImportCostEnergyOnlyEntity.EntityId, (_house.SumEnergyImportCostEnergyOnly / 100).ToString(CultureInfo.InvariantCulture));

      await PVCC_EntityManager.SetStateAsync(_sumImportCostNetworkOnlyEntity.EntityId, (_house.SumEnergyImportCostNetworkOnly / 100).ToString(CultureInfo.InvariantCulture));

      await PVCC_EntityManager.SetStateAsync(_sumImportExportNetCostEntity.EntityId, ((_house.SumEnergyImportCostTotal - _house.SumEnergyExportEarningsTotal) / 100).ToString(CultureInfo.InvariantCulture));

      await PVCC_EntityManager.SetStateAsync(_bestExportPriceEntity.EntityId, (_house.MostExpensiveExportWindowToday.Price / 100).ToString(CultureInfo.InvariantCulture));
      var attr_bestExportPrice = new
      {
        start_time = _house.MostExpensiveExportWindowToday.StartTime.ToISO8601(),
        end_time = _house.MostExpensiveExportWindowToday.EndTime.ToISO8601(),
      };
      await PVCC_EntityManager.SetAttributesAsync(_bestExportPriceEntity.EntityId, attr_bestExportPrice);

      await PVCC_EntityManager.SetStateAsync(_bestImportPriceEntity.EntityId, (_house.CheapestImportWindowToday.Price / 100).ToString(CultureInfo.InvariantCulture));
      var attr_bestImportPrice = new
      {
        start_time = _house.CheapestImportWindowToday.StartTime.ToISO8601(),
        end_time = _house.CheapestImportWindowToday.EndTime.ToISO8601(),
      };
      await PVCC_EntityManager.SetAttributesAsync(_bestImportPriceEntity.EntityId, attr_bestImportPrice);
      #endregion
      PVCC_Logger.LogDebug("Leave Schedule");
    }
    private bool CheckConfiguration()
    {
      bool checkResult = true;

      if (PVCC_Config.CurrentImportPriceEntity?.State is null)
      {
        checkResult = false;
        PVCC_Logger.LogError("{entity} is not available in configuration ({entityid})", "CurrentPriceEntity", PVCC_Config.CurrentImportPriceEntity?.EntityId);
      }
      if (PVCC_Config.CurrentPVPowerEntity?.State is null)
      {
        checkResult = false;
        PVCC_Logger.LogError("{entity} is not available in configuration ({entityid})", "CurrentPVPowerEntity", PVCC_Config.CurrentPVPowerEntity?.EntityId);
      }
      if (PVCC_Config.TodayPVEnergyEntity?.State is null)
      {
        checkResult = false;
        PVCC_Logger.LogError("{entity} is not available in configuration ({entityid})", "TodayPVEnergyEntity", PVCC_Config.TodayPVEnergyEntity?.EntityId);
      }
      //if (_config.ForecastPVEnergyTodayEntity?.State is null)
      //{
      //  checkResult = false;
      //  _logger.LogError("ForecastPVEnergyTodayEntity is not available in configuration (" + _config.ForecastPVEnergyTodayEntity?.EntityId + ")");
      //}
      //if (_config.ForecastPVEnergyTodayEntity?.State is null)
      //{
      //  checkResult = false;
      //  _logger.LogError("ForecastPVEnergyTodayEntity is not available in configuration (" + _config.ForecastPVEnergyTodayEntity?.EntityId + ")");
      //}

      return checkResult;
    }
    private async Task<bool> RegisterControlSensors(bool reset=false)
    {
      //reset = true;
      _overrideModeEntity = await RegisterSensor("select.pv_control_mode_override", "Mode Override", "select", "mdi:form-select",
        addConfig: new { options = Enum.GetNames<InverterModes>() },
        defaultValue: InverterModes.automatic.ToString(),
        reRegister: reset);

      _forceChargeEntity = await RegisterSensor("switch.pv_control_force_charge_at_cheapest_period", "Force charge at cheapest price", "switch", "mdi:transmission-tower",
        defaultValue: "OFF",
        reRegister: reset);

      _enforcePreferredSocEntity = await RegisterSensor("switch.pv_control_enforce_preferred_soc", "Enforce the preferred SoC", "switch", "mdi:battery-plus-variant",
        defaultValue: "OFF",
        reRegister: reset);

      _enableOpportunisticExport = await RegisterSensor("switch.pv_control_enable_opportunistic_export", "Enable opportunistic Export", "switch", "mdi:home-export-outline",
        defaultValue: "OFF",
        reRegister: reset);

      _forceChargeMaxPriceEntity = await RegisterSensor("number.pv_control_max_price_for_forcecharge", "Max price for force charge", "monetary", "mdi:currency-eur",
        addConfig: new
        {
          min = 0,
          max = 50,
          step = 1,
          initial = 0,
          unit_of_measurement = "ct",
          mode = "slider",
        },
        defaultValue: "25",
        reRegister: reset);

      _forceChargeTargetSoCEntity = await RegisterSensor("number.pv_control_forcecharge_target_soc", "Force charge target SoC", "battery", "mdi:battery-alert",
        addConfig: new
        {
          min = 0,
          max = 95,
          step = 5,
          initial = 50,
          unit_of_measurement = "%",
          mode = "slider",
        },
        defaultValue: "50",
        reRegister: reset);

      _prefBatterySoCEntity = await RegisterSensor("number.pv_control_preferredbatterycharge", "Preferred min SoC", "battery", "mdi:battery-unknown",
        addConfig: new
        {
          min = 0,
          max = 100,
          step = 5,
          initial = 30,
          unit_of_measurement = "%",
          mode = "slider",
        },
        defaultValue: "30",
        reRegister: reset);

      _modeEntity = await RegisterSensor("sensor.pv_control_mode", "Mode", "ENUM", "mdi:form-select",
        addConfig: new { options = Enum.GetNames<InverterModes>() },
        defaultValue: InverterModes.normal.ToString(),
        reRegister: reset);

      _RunHeavyLoadsNowEntity = await RegisterSensor("sensor.pv_control_run_heavyloads_now", "Run heavy loads now", "ENUM", "mdi:ev-station",
        addConfig: new { options = Enum.GetNames<RunHeavyLoadsStatus>() },
        defaultValue: RunHeavyLoadsStatus.No.ToString(),
        reRegister: reset);

      _currentImportPriceBruttoEntity = await RegisterSensor("sensor.pv_control_current_import_price_brutto", "Current energy import price (brutto)", "MONETARY", "mdi:currency-eur",
        addConfig: new
        {
          unit_of_measurement = "€/kWh",
        },
        defaultValue: "0",
        reRegister: reset);

      _currentExportPriceBruttoEntity = await RegisterSensor("sensor.pv_control_current_export_price_brutto", "Current energy export price (brutto)", "MONETARY", "mdi:currency-eur",
        addConfig: new
        {
          unit_of_measurement = "€/kWh",
        },
        defaultValue: "0",
        reRegister: reset);

      _sumExportEarningsBruttoEntity = await RegisterSensor("sensor.pv_control_sum_export_earnings_brutto", "Sum of export earnings (brutto)", "MONETARY", "mdi:currency-eur",
        addConfig: new
        {
          unit_of_measurement = "€",
          state_class = "total_increasing",
        },
        defaultValue: "0",
        reRegister: reset);

      _sumImportCostBruttoEntity = await RegisterSensor("sensor.pv_control_sum_import_cost_brutto", "Sum of import costs (brutto)", "MONETARY", "mdi:currency-eur",
        addConfig: new
        {
          unit_of_measurement = "€",
          state_class = "total_increasing",
        },
        defaultValue: "0",
        reRegister: reset);

      _sumImportCostEnergyOnlyEntity = await RegisterSensor("sensor.pv_control_sum_import_cost_energy_only", "Sum of import costs (energy only)", "MONETARY", "mdi:currency-eur",
        addConfig: new
        {
          unit_of_measurement = "€",
          state_class = "total_increasing",
        },
        defaultValue: "0",
        reRegister: reset);

      _sumImportCostNetworkOnlyEntity = await RegisterSensor("sensor.pv_control_sum_import_cost_network_only", "Sum of import costs (network only)", "MONETARY", "mdi:currency-eur",
        addConfig: new
        {
          unit_of_measurement = "€",
          state_class = "total_increasing",
        },
        defaultValue: "0",
        reRegister: reset);

      _sumImportExportNetCostEntity = await RegisterSensor("sensor.pv_control_sum_import_export_net_cost", "Sum of net import/export costs", "MONETARY", "mdi:currency-eur",
        addConfig: new
        {
          unit_of_measurement = "€",
          state_class = "total",
        },
        defaultValue: "0",
        reRegister: reset);

      _bestExportPriceEntity = await RegisterSensor("sensor.pv_control_best_export_price_today", "Best (highest) export price today", "MONETARY", "mdi:currency-eur",
        addConfig: new
        {
          unit_of_measurement = "€/kWh",
          state_class = "measurement",
        },
        defaultValue: "0",
        reRegister: reset);

      _bestImportPriceEntity = await RegisterSensor("sensor.pv_control_best_import_price_today", "Best (lowest) import price today", "MONETARY", "mdi:currency-eur",
        addConfig: new
        {
          unit_of_measurement = "€/kWh",
          state_class = "measurement",
        },
        defaultValue: "0",
        reRegister: reset);

      _battery_StatusEntity = await RegisterSensor("sensor.pv_control_battery_status", "Battery Status", "ENUM", "mdi:battery-charging",
        addConfig: new { options = Enum.GetNames<BatteryStatuses>() },
        defaultValue: BatteryStatuses.unknown.ToString(),
        reRegister: reset);

      _battery_RemainingTimeEntity = await RegisterSensor("sensor.pv_control_battery_remainingtime", "Battery - remaining time", "DURATION", "mdi:timer-alert",
        addConfig: new
        {
          unit_of_measurement = "min",
        },
        defaultValue: "0",
        reRegister: reset);

      _needToChargeFromGridTodayEntity = await RegisterSensor("binary_sensor.pv_control_need_to_charge_from_grid_today", "Need to charge from Grid today", "battery_charging", "mdi:transmission-tower-export",
        defaultValue: "OFF",
        reRegister: reset);

      _battery_RemainingEnergyEntity = await RegisterSensor("sensor.pv_control_battery_remainingenergy", "Battery - available energy", "Energy", "mdi:lightning-bolt-outline",
        addConfig: new
        {
          unit_of_measurement = "Wh",
        },
        defaultValue: "0",
        reRegister: reset);

      _info_EstimatedMaxSoCTodayEntity = await RegisterSensor("sensor.pv_control_info_max_soc_today", "Estimated Max SoC Today", "Battery", "mdi:battery-charging-90",
        addConfig: new
        {
          unit_of_measurement = "%",
        },
        defaultValue: "0",
        reRegister: reset);

      _info_EstimatedMinSoCTodayEntity = await RegisterSensor("sensor.pv_control_info_min_soc_today", "Estimated Min SoC Today", "Battery", "mdi:battery-charging-20",
        addConfig: new
        {
          unit_of_measurement = "%",
        },
        defaultValue: "0",
        reRegister: reset);

      _info_EstimatedMaxSoCTomorrowEntity = await RegisterSensor("sensor.pv_control_info_max_soc_tomorrow", "Estimated Max SoC Tomorrow", "Battery", "mdi:battery-charging-90",
        addConfig: new
        {
          unit_of_measurement = "%",
        },
        defaultValue: "0",
        reRegister: reset);

      _info_EstimatedMinSoCTomorrowEntity = await RegisterSensor("sensor.pv_control_info_min_soc_tomorrow", "Estimated Min SoC Tomorrow", "Battery", "mdi:battery-charging-20",
        addConfig: new
        {
          unit_of_measurement = "%",
        },
        defaultValue: "0",
        reRegister: reset);

      _info_PredictedSoCEntity = await RegisterSensor("sensor.pv_control_info_predicted_soc", "Predicted SoC Now", "Battery", "mdi:calendar-question",
        addConfig: new
        {
          unit_of_measurement = "%",
        },
        defaultValue: "0",
        reRegister: reset);

      _info_PredictedChargeEntity = await RegisterSensor("sensor.pv_control_info_predicted_charge", "Predicted Charge Until Now", "Energy", "mdi:solar-power-variant",
        addConfig: new
        {
          unit_of_measurement = "Wh",
        },
        defaultValue: "0",
        reRegister: reset);

      _info_PredictedDischargeEntity = await RegisterSensor("sensor.pv_control_info_predicted_discharge", "Predicted Discharge Until Now", "Energy", "mdi:home-lightbulb-outline",
        addConfig: new
        {
          unit_of_measurement = "Wh",
        },
        defaultValue: "0",
        reRegister: reset);

      _info_chargeTodayEntity = await RegisterSensor("sensor.pv_control_estimated_remaining_charge_today", "Estimated remaining charge today", "Energy", "mdi:solar-power",
        addConfig: new
        {
          unit_of_measurement = "Wh",
        },
        defaultValue: "0",
        reRegister: reset);

      _info_chargeTomorrowEntity = await RegisterSensor("sensor.pv_control_estimated_charge_tomorrow", "Estimated charge tomorrow", "Energy", "mdi:solar-power",
        addConfig: new
        {
          unit_of_measurement = "Wh",
        },
        defaultValue: "0",
        reRegister: reset);

      _info_dischargeTodayEntity = await RegisterSensor("sensor.pv_control_estimated_remaining_discharge_today", "Estimated remaining discharge today", "Energy", "mdi:home-lightning-bolt",
        addConfig: new
        {
          unit_of_measurement = "Wh",
        },
        defaultValue: "0",
        reRegister: reset);

      _info_dischargeTomorrowEntity = await RegisterSensor("sensor.pv_control_estimated_discharge_tomorrow", "Estimated discharge tomorrow", "Energy", "mdi:home-lightning-bolt",
        addConfig: new
        {
          unit_of_measurement = "Wh",
        },
        defaultValue: "0",
        reRegister: reset);

      return true;
    }
  }
}
