using NetDaemon.Extensions.MqttEntityManager;
using NetDaemon.Extensions.Scheduler;
using NetDaemon.HassModel.Entities;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reactive.Concurrency;
using System.Threading;
using System.Threading.Tasks;

namespace NetDeamon.apps.PVControl
{
  public class PVConfig
  {
    public string? DBLocation { get; set; }
    public Entity? CurrentImportPriceEntity { get; set; }
    public Entity? CurrentExportPriceEntity { get; set; }
    public Entity? CurrentHouseLoadEntity { get; set; }
    public Entity? CurrentPVPowerEntity { get; set; }
    public Entity? CurrentBatteryPowerEntity { get; set; }
    public float? InverterEfficiency { get; set; }
    public Entity? TodayPVEnergyEntity { get; set; }
    public List<Entity>? ForecastPVEnergyTodayEntities { get; set; }
    public List<Entity>? ForecastPVEnergyTomorrowEntities { get; set; }
    public Entity? BatterySoCEntity { get; set; }
    public Entity? BatteryCapacityEntity { get; set; }
    public float? BatteryCapacityValue { get; set; }
    public Entity? MaxBatteryChargeCurrrentEntity { get; set; }
    public int? MaxBatteryChargeCurrrentValue { get; set; }
    public Entity? MinBatterySoCEntity { get; set; }
    public int? MinBatterySoCValue { get; set; }
  }

  [NetDaemonApp]
#if DEBUG
  [Focus]
#endif
  public class PVControl : IAsyncInitializable
  {
    private readonly IHaContext _context;
    private readonly IMqttEntityManager _entityManager;
    private readonly ILogger<PVControl> _logger;
    private readonly PVConfig _config;
    private readonly IScheduler _scheduler;

    private readonly HouseEnergy _house;
    #region Created Entities
    private Entity _modeEntity;
    private Entity _battery_RemainingTimeEntity;
    private Entity _battery_RemainingEnergyEntity;
    private Entity _needToChargeFromGridTodayEntity;
    private Entity _battery_StatusEntity;
    private Entity _info_EstimatedMaxSoCTodayEntity;
    private Entity _info_EstimatedMaxSoCTomorrowEntity;
    private Entity _info_EstimatedMinSoCTodayEntity;
    private Entity _info_EstimatedMinSoCTomorrowEntity;
    private Entity _prefBatterySoCEntity;
    private Entity _enforcePreferredSocEntity;
    private Entity _forceChargeEntity;
    private Entity _forceChargeTargetSoCEntity;
    private Entity _forceChargeMaxPriceEntity;
    private Entity _info_chargeTodayEntity;
    private Entity _info_dischargeTodayEntity;
    private Entity _info_chargeTomorrowEntity;
    private Entity _info_dischargeTomorrowEntity;
    private Entity _info_PredictedSoCEntity;
    private Entity _info_PredictedChargeEntity;
    private Entity _info_PredictedDischargeEntity;
    private Entity _overrideModeEntity;
    private Entity _RunHeavyLoadsNowEntity;
    #endregion

    public PVControl(IHaContext ha, IMqttEntityManager entityManager, IAppConfig<PVConfig> config, IScheduler scheduler, ILogger<PVControl> logger)
    {
      CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
      _context = ha;
      _entityManager = entityManager;
      _logger = logger;
      _config = config.Value;
      _scheduler = scheduler;

      if (string.IsNullOrWhiteSpace(_config.DBLocation))
        _config.DBLocation = "apps/DataLogger/energy_history.db";

      if (!CheckConfiguration())
        throw new Exception("Error initializing configuration");

      _house = new HouseEnergy(_config);

      _modeEntity = new Entity(_context, "sensor.pv_control_mode");
      _battery_StatusEntity = new Entity(_context, "sensor.pv_control_battery_status");
      _battery_RemainingTimeEntity = new Entity(_context, "sensor.pv_control_battery_remainingtime");
      _battery_RemainingEnergyEntity = new Entity(_context, "sensor.pv_control_battery_remainingenergy");
      _needToChargeFromGridTodayEntity = new Entity(_context, "binary_sensor.pv_control_need_to_charge_from_grid_today");
      _prefBatterySoCEntity = new Entity(_context, "number.pv_control_preferredbatterycharge");
      _enforcePreferredSocEntity = new Entity(_context, "switch.pv_control_enforce_preferred_soc");
      _info_EstimatedMaxSoCTodayEntity = new Entity(_context, "sensor.pv_control_info_max_soc_today");
      _info_EstimatedMinSoCTodayEntity = new Entity(_context, "sensor.pv_control_info_min_soc_today");
      _info_EstimatedMaxSoCTomorrowEntity = new Entity(_context, "sensor.pv_control_info_max_soc_tomorrow");
      _info_EstimatedMinSoCTomorrowEntity = new Entity(_context, "sensor.pv_control_info_min_soc_tomorrow");
      _info_PredictedSoCEntity = new Entity(_context, "sensor.pv_control_info_predicted_soc");
      _info_PredictedDischargeEntity = new Entity(_context, "sensor.pv_control_info_predicted_discharge");
      _info_PredictedChargeEntity = new Entity(_context, "sensor.pv_control_info_predicted_charge");
      _info_chargeTodayEntity = new Entity(_context, "sensor.pv_control_estimated_remaining_charge_today");
      _info_chargeTomorrowEntity = new Entity(_context, "sensor.pv_control_estimated_charge_tomorrow");
      _info_dischargeTodayEntity = new Entity(_context, "sensor.pv_control_estimated_remaining_discharge_today");
      _info_dischargeTomorrowEntity = new Entity(_context, "sensor.pv_control_estimated_discharge_tomorrow");
      _forceChargeEntity = new Entity(_context, "switch.pv_control_force_charge_at_cheapest_period");
      _forceChargeMaxPriceEntity = new Entity(_context, "number.pv_control_max_price_for_forcecharge");
      _forceChargeTargetSoCEntity = new Entity(_context, "number.pv_control_forcecharge_target_soc");
      _overrideModeEntity = new Entity(_context, "select.pv_control_mode_override");
      _RunHeavyLoadsNowEntity = new Entity(_context, "sensor.pv_control_run_heavyloads_now");

      _logger.LogInformation("Finished PVControl constructor");
    }
    async Task IAsyncInitializable.InitializeAsync(CancellationToken cancellationToken)
    {
      //await _entityManager.RemoveAsync("sensor.car_charger_battery");
      if (await RegisterSensors())
      {
        (await _entityManager.PrepareCommandSubscriptionAsync(_prefBatterySoCEntity.EntityId).ConfigureAwait(false)).SubscribeAsync(async state => await UserStateChanged(_prefBatterySoCEntity, state));
        (await _entityManager.PrepareCommandSubscriptionAsync(_forceChargeMaxPriceEntity.EntityId).ConfigureAwait(false)).SubscribeAsync(async state => await UserStateChanged(_forceChargeMaxPriceEntity, state));
        (await _entityManager.PrepareCommandSubscriptionAsync(_forceChargeTargetSoCEntity.EntityId).ConfigureAwait(false)).SubscribeAsync(async state => await UserStateChanged(_forceChargeTargetSoCEntity, state));
        (await _entityManager.PrepareCommandSubscriptionAsync(_enforcePreferredSocEntity.EntityId).ConfigureAwait(false)).SubscribeAsync(async state => await UserStateChanged(_enforcePreferredSocEntity, state));
        (await _entityManager.PrepareCommandSubscriptionAsync(_forceChargeEntity.EntityId).ConfigureAwait(false)).SubscribeAsync(async state => await UserStateChanged(_forceChargeEntity, state));
        (await _entityManager.PrepareCommandSubscriptionAsync(_overrideModeEntity.EntityId).ConfigureAwait(false)).SubscribeAsync(async state => await UserStateChanged(_overrideModeEntity, state));

        // initialize local values with saved in HA
        if (_prefBatterySoCEntity.State != null)
          await UserStateChanged(_prefBatterySoCEntity, _prefBatterySoCEntity.State);
        if (_forceChargeMaxPriceEntity.State != null)
          await UserStateChanged(_forceChargeMaxPriceEntity, _forceChargeMaxPriceEntity.State);
        if (_forceChargeTargetSoCEntity.State != null)
          await UserStateChanged(_forceChargeTargetSoCEntity, _forceChargeTargetSoCEntity.State);
        if (_enforcePreferredSocEntity.State != null)
          await UserStateChanged(_enforcePreferredSocEntity, _enforcePreferredSocEntity.State);
        if (_forceChargeEntity.State != null)
          await UserStateChanged(_forceChargeEntity, _forceChargeEntity.State);
        if (_overrideModeEntity.State != null)
          await UserStateChanged(_overrideModeEntity, _overrideModeEntity.State);
#if DEBUG
       // _scheduler.ScheduleCron("*/30 * * * * *", async () => await ScheduledOperations(), true);
#else
        _scheduler.ScheduleCron("*/15 * * * * *", async () => await ScheduledOperations(), true);
#endif
#if DEBUG
        var Z = _house.DailyBatterySoCPredictionTodayAndTomorrow;
#endif
      }
      else
      {
        _logger.LogError("Error registering sensors");
      }
    }
    private async Task UserStateChanged(Entity? entity, string newState)
    {
      if (entity == null)
        return;

      if (entity.EntityId == _overrideModeEntity.EntityId && entity.State is not null)
      {
        if (Enum.TryParse(newState, out InverterModes modeselect))
          _house.OverrideMode = modeselect;
        else
          _house.OverrideMode = InverterModes.automatic;
        await _entityManager.SetStateAsync(entity.EntityId, _house.OverrideMode.ToString());
      }
      if (entity.EntityId == _prefBatterySoCEntity.EntityId)
      {
        if (int.TryParse(newState, out int value))
          _house.PreferredMinBatterySoC = value;
        await _entityManager.SetStateAsync(entity.EntityId, _house.PreferredMinBatterySoC.ToString());
      }
      if (entity.EntityId == _enforcePreferredSocEntity.EntityId && entity.State is not null)
      {
        _house.EnforcePreferredSoC = newState.Equals("on", StringComparison.CurrentCultureIgnoreCase);
        await _entityManager.SetStateAsync(entity.EntityId, _house.EnforcePreferredSoC ? "ON" : "OFF");
      }
      if (entity.EntityId == _forceChargeEntity.EntityId && entity.State is not null)
      {
        _house.ForceCharge = newState.Equals("on", StringComparison.CurrentCultureIgnoreCase);
        await _entityManager.SetStateAsync(entity.EntityId, _house.ForceCharge ? "ON" : "OFF");
      }
      if (entity.EntityId == _forceChargeMaxPriceEntity.EntityId && entity.State is not null)
      {
        if (int.TryParse(newState, out int value))
          _house.ForceChargeMaxPrice = value;
        await _entityManager.SetStateAsync(entity.EntityId, _house.ForceChargeMaxPrice.ToString());
      }
      if (entity.EntityId == _forceChargeTargetSoCEntity.EntityId && entity.State is not null)
      {
        if (int.TryParse(newState, out int value))
          _house.ForceChargeTargetSoC = value;
        await _entityManager.SetStateAsync(entity.EntityId, _house.ForceChargeTargetSoC.ToString());
      }
#if !DEBUG
      await ScheduledOperations();
#endif
    }
    private async Task ScheduledOperations()
    {
      _logger.LogDebug("Entering Schedule");
      DateTime now = DateTime.Now;
      _logger.LogDebug("Updating Predictions");
      _house.UpdatePredictions();
      _logger.LogDebug("Finished Updating Predictions");
      await _entityManager.SetStateAsync(_modeEntity.EntityId, _house.ProposedMode.ToString());
      #region Mode
      var nextCheapest = _house.BestChargeTime;
      var attr_Mode = new
      {
        next_charge_window_start = nextCheapest.StartTime.ToISO8601(),
        next_charge_window_end = nextCheapest.EndTime.ToISO8601(),
        price = nextCheapest.Price.ToString(CultureInfo.InvariantCulture),
        charge_Reason = _house.ForceChargeReason.ToString(),
      };
      await _entityManager.SetAttributesAsync(_modeEntity.EntityId, attr_Mode);
      #endregion
      #region RunHeavyLoads
      await _entityManager.SetStateAsync(_RunHeavyLoadsNowEntity.EntityId, _house.RunHeavyLoadsNow.ToString());
      var attr_HeavyLoad = new
      {
        Reason = _house.RunHeavyLoadReason.ToString(),
      };
      await _entityManager.SetAttributesAsync(_RunHeavyLoadsNowEntity.EntityId, attr_HeavyLoad);
      #endregion
      #region Remaining battery
      await _entityManager.SetStateAsync(_battery_RemainingTimeEntity.EntityId, _house.EstimatedTimeToBatteryFullOrEmpty.ToString(CultureInfo.InvariantCulture));
      var attr_RemainingTime = new
      {
        Estimated_time = now.AddMinutes(_house.EstimatedTimeToBatteryFullOrEmpty).ToISO8601(),
        next_relevant_pv_charge = _house.FirstRelevantPVEnergyToday.ToISO8601(),
        avg_battery_charge_or_discharge_Power = _house.CurrentAverageBatteryChargeDischargePower.ToString(CultureInfo.InvariantCulture) + " W",
        status = _house.BatteryStatus.ToString(),
      };
      await _entityManager.SetAttributesAsync(_battery_RemainingTimeEntity.EntityId, attr_RemainingTime);
      #endregion
      #region Battery status
      await _entityManager.SetStateAsync(_battery_StatusEntity.EntityId, _house.BatteryStatus.ToString());
      var attr_batStatus = new
      {
        avg_battery_charge_or_discharge_Power = _house.CurrentAverageBatteryChargeDischargePower.ToString(CultureInfo.InvariantCulture) + " W",
        avg_house_load_now = _house.CurrentAverageHouseLoad.ToString(CultureInfo.InvariantCulture) + " W",
        predicted_house_load_now = _house.Prediction_Load.CurrentValue * 4 + " W",
        avg_pv_power_now = _house.CurrentAveragePVPower.ToString(CultureInfo.InvariantCulture) + " W",
        predicted_pv_power_now = _house.Prediction_PV.CurrentValue * 4 + " W",
        current_SoC = _house.BatterySoc.ToString(CultureInfo.InvariantCulture) + "%",
      };
      await _entityManager.SetAttributesAsync(_battery_StatusEntity.EntityId, attr_batStatus);
      #endregion
      #region Remaining energy
      await _entityManager.SetStateAsync(_battery_RemainingEnergyEntity.EntityId, _house.UsableBatteryEnergy.ToString(CultureInfo.InvariantCulture));
      var attr_RemainingEnergy = new
      {
        min_allowed_SoC = (_house.EnforcePreferredSoC ? _house.PreferredMinimalSoC : _house.AbsoluteMinimalSoC).ToString() + "%",
        remaining_energy_at_min_battery_soc = _house.ReserveBatteryEnergy.ToString(CultureInfo.InvariantCulture) + " Wh",
        remaining_energy_to_zero_soc = _house.CalculateBatteryEnergyAtSoC(_house.BatterySoc, 0).ToString(CultureInfo.InvariantCulture) + " Wh",
        battery_capacity = _house.BatteryCapacity.ToString(CultureInfo.InvariantCulture) + " Wh",
      };
      await _entityManager.SetAttributesAsync(_battery_RemainingEnergyEntity.EntityId, attr_RemainingEnergy);
      #endregion
      #region NeedToCharge
      var needToCharge = _house.NeedToChargeFromExternal;
      await _entityManager.SetStateAsync(_needToChargeFromGridTodayEntity.EntityId, needToCharge.Item1 ? "ON" : "OFF");
      var attr_Charge = new
      {
        minimal_SoC_allowed = _house.AbsoluteMinimalSoC.ToString(CultureInfo.InvariantCulture) + "%",
        preferred_SoC = _house.PreferredMinimalSoC.ToString(CultureInfo.InvariantCulture) + "%",
        minimal_estimated_SoC = needToCharge.Item3.ToString(CultureInfo.InvariantCulture) + "%",
        at_time = needToCharge.Item2.ToISO8601(),
        estimated_charge_time = _house.EstimatedChargeTimeAtMinima.ToString(CultureInfo.InvariantCulture) + " min",
      };
      await _entityManager.SetAttributesAsync(_needToChargeFromGridTodayEntity.EntityId, attr_Charge);
      #endregion
      #region Prediction
      var curPredSoc = _house.DailyBatterySoCPredictionTodayAndTomorrow.GetEntryAtTime(now);
      if (curPredSoc.Key != default)
      {
        await _entityManager.SetStateAsync(_info_PredictedSoCEntity.EntityId, curPredSoc.Value.ToString(CultureInfo.InvariantCulture));
        var attr_pred_soc = new
        {
          current_entry_time = curPredSoc.Key.ToISO8601(),
          last_snapshot = _house.LastSnapshotUpdate.ToISO8601(),
          data = _house.DailyBatterySoCPredictionTodayAndTomorrow.Select(s => new { datetime = s.Key, soc = s.Value }),
        };
        await _entityManager.SetAttributesAsync(_info_PredictedSoCEntity.EntityId, attr_pred_soc);
      }
      var curPredCharge = _house.DailyChargePredictionTodayAndTomorrow.GetEntryAtTime(now);
      if (curPredCharge.Key != default)
      {
        await _entityManager.SetStateAsync(_info_PredictedChargeEntity.EntityId, curPredCharge.Value.ToString(CultureInfo.InvariantCulture));
        var attr_pred_charge = new
        {
          current_entry_time = curPredCharge.Key.ToISO8601(),
          last_snapshot = _house.LastSnapshotUpdate.ToISO8601(),
          data = _house.DailyChargePredictionTodayAndTomorrow.Select(s => new { datetime = s.Key, charge = s.Value }),
        };
        await _entityManager.SetAttributesAsync(_info_PredictedChargeEntity.EntityId, attr_pred_charge);
      }
      var curPredDischarge = _house.DailyDischargePredictionTodayAndTomorrow.GetEntryAtTime(now);
      if (curPredDischarge.Key != default)
      {
        await _entityManager.SetStateAsync(_info_PredictedDischargeEntity.EntityId, curPredDischarge.Value.ToString(CultureInfo.InvariantCulture));
        var attr_pred_charge = new
        {
          current_entry_time = curPredDischarge.Key.ToISO8601(),
          last_snapshot = _house.LastSnapshotUpdate.ToISO8601(),
          data = _house.DailyDischargePredictionTodayAndTomorrow.Select(s => new { datetime = s.Key, discharge = s.Value }),
        };
        await _entityManager.SetAttributesAsync(_info_PredictedDischargeEntity.EntityId, attr_pred_charge);
      }
      #endregion
      #region SoC estimates
      var est_soc_today = _house.Prediction_BatterySoC.Today.Where(s => s.Key >= now).ToDictionary();
      var est_soc_tomorrow = _house.Prediction_BatterySoC.Tomorrow;

      if (est_soc_today.Count > 0)
      {
        var min_soc_today = est_soc_today.FirstMinOrDefault();
        await _entityManager.SetStateAsync(_info_EstimatedMinSoCTodayEntity.EntityId, min_soc_today.Value.ToString(CultureInfo.InvariantCulture));
        var attr_Min_SoC_Today = new
        {
          time = min_soc_today.Key.ToISO8601(),
          data = _house.Prediction_BatterySoC.TodayAndTomorrow.Where(s => s.Key >= now).Select(s => new { datetime = s.Key, soc = s.Value }),
        };
        await _entityManager.SetAttributesAsync(_info_EstimatedMinSoCTodayEntity.EntityId, attr_Min_SoC_Today);

        var max_soc_today = est_soc_today.FirstMaxOrDefault();
        await _entityManager.SetStateAsync(_info_EstimatedMaxSoCTodayEntity.EntityId, max_soc_today.Value.ToString(CultureInfo.InvariantCulture));
        var attr_Max_SoC_Today = new
        {
          time = max_soc_today.Key.ToISO8601(),
          data = est_soc_today,
        };
        await _entityManager.SetAttributesAsync(_info_EstimatedMaxSoCTodayEntity.EntityId, attr_Max_SoC_Today);
      }

      if (est_soc_tomorrow.Count > 0)
      {
        var min_soc_tomorrow = est_soc_tomorrow.FirstMinOrDefault();
        await _entityManager.SetStateAsync(_info_EstimatedMinSoCTomorrowEntity.EntityId, min_soc_tomorrow.Value.ToString(CultureInfo.InvariantCulture));
        var attr_Min_SoC_Tomorrow = new
        {
          time = min_soc_tomorrow.Key.ToISO8601(),
          data = est_soc_tomorrow,
        };
        await _entityManager.SetAttributesAsync(_info_EstimatedMinSoCTomorrowEntity.EntityId, attr_Min_SoC_Tomorrow);

        var max_soc_tomorrow = est_soc_tomorrow.FirstMaxOrDefault();
        await _entityManager.SetStateAsync(_info_EstimatedMaxSoCTomorrowEntity.EntityId, max_soc_tomorrow.Value.ToString(CultureInfo.InvariantCulture));
        var attr_Max_SoC_Tomorrow = new
        {
          time = max_soc_tomorrow.Key.ToISO8601(),
          data = est_soc_tomorrow,
        };
        await _entityManager.SetAttributesAsync(_info_EstimatedMaxSoCTomorrowEntity.EntityId, attr_Max_SoC_Tomorrow);
      }
      #endregion
      #region charge/discharge forecasts
      var chargeToday = _house.Prediction_PV.Today;
      var chargeTomorrow = _house.Prediction_PV.Tomorrow;
      var dischargeToday = _house.Prediction_Load.Today;
      var dischargeTomorrow = _house.Prediction_Load.Tomorrow;

      if (chargeToday.Count > 0)
      {
        await _entityManager.SetStateAsync(_info_chargeTodayEntity.EntityId, chargeToday.GetSum(start: now).ToString(CultureInfo.InvariantCulture));
        var attr_chargeToday = new
        {
          data = chargeToday,
        };
        await _entityManager.SetAttributesAsync(_info_chargeTodayEntity.EntityId, attr_chargeToday);
      }
      if (chargeTomorrow.Count > 0)
      {
        await _entityManager.SetStateAsync(_info_chargeTomorrowEntity.EntityId, chargeTomorrow.GetSum().ToString(CultureInfo.InvariantCulture));
        var attr_chargeTomorrow = new
        {
          data = chargeTomorrow,
        };
        await _entityManager.SetAttributesAsync(_info_chargeTomorrowEntity.EntityId, attr_chargeTomorrow);
      }

      if (dischargeToday.Count > 0)
      {
        await _entityManager.SetStateAsync(_info_dischargeTodayEntity.EntityId, dischargeToday.GetSum(start: now).ToString(CultureInfo.InvariantCulture));
        var attr_dischargeToday = new
        {
          data = dischargeToday,
        };
        await _entityManager.SetAttributesAsync(_info_dischargeTodayEntity.EntityId, attr_dischargeToday);
      }
      if (dischargeTomorrow.Count > 0)
      {
        await _entityManager.SetStateAsync(_info_dischargeTomorrowEntity.EntityId, dischargeTomorrow.GetSum().ToString(CultureInfo.InvariantCulture));
        var attr_dischargeTomorrow = new
        {
          data = dischargeTomorrow,
        };
        await _entityManager.SetAttributesAsync(_info_dischargeTomorrowEntity.EntityId, attr_dischargeTomorrow);
      }
      #endregion
      _logger.LogDebug("Leave Schedule");
    }
    private bool CheckConfiguration()
    {
      bool checkResult = true;

      if (_config.CurrentImportPriceEntity?.State is null)
      {
        checkResult = false;
        _logger.LogError("{entity} is not available in configuration ({entityid})", "CurrentPriceEntity", _config.CurrentImportPriceEntity?.EntityId);
      }
      if (_config.CurrentPVPowerEntity?.State is null)
      {
        checkResult = false;
        _logger.LogError("{entity} is not available in configuration ({entityid})", "CurrentPVPowerEntity", _config.CurrentPVPowerEntity?.EntityId);
      }
      if (_config.TodayPVEnergyEntity?.State is null)
      {
        checkResult = false;
        _logger.LogError("{entity} is not available in configuration ({entityid})", "TodayPVEnergyEntity", _config.TodayPVEnergyEntity?.EntityId);
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
    private async Task<bool> RegisterSensors()
    {
      try
      {
        var identifiers = new[] { "pv_control" };
        var device = new { identifiers, name = "PV Control", model = "PV Control", manufacturer = "AH", sw_version = 0.1 };

        if (_overrideModeEntity?.State is null)
        {
          await _entityManager.CreateAsync("select.pv_control_mode_override", new EntityCreationOptions
          {
            Name = "Mode Override",
            DeviceClass = "select",
            Persist = true,
          }, new
          {
            icon = "mdi:form-select",
            options = Enum.GetNames(typeof(InverterModes)),
            device
          }).ConfigureAwait(false);
          _overrideModeEntity = new Entity(_context, "select.pv_control_mode_override");
          await _entityManager.SetStateAsync(_overrideModeEntity.EntityId, InverterModes.automatic.ToString());
        }

        if (_forceChargeEntity?.State is null)
        {
          await _entityManager.CreateAsync("switch.pv_control_force_charge_at_cheapest_period", new EntityCreationOptions
          {
            Name = "Force charge at cheapest price",
            DeviceClass = "switch",
            Persist = true,
          }, new
          {
            icon = "mdi:transmission-tower",
            device
          }).ConfigureAwait(false);
          _forceChargeEntity = new Entity(_context, "switch.pv_control_force_charge_at_cheapest_period");
          await _entityManager.SetStateAsync(_forceChargeEntity.EntityId, "OFF");
        }

        if (_enforcePreferredSocEntity?.State is null)
        {
          await _entityManager.CreateAsync("switch.pv_control_enforce_preferred_soc", new EntityCreationOptions
          {
            Name = "Enforce the preferred SoC",
            DeviceClass = "switch",
            Persist = true,
          }, new
          {
            icon = "mdi:battery-plus-variant",
            device
          }).ConfigureAwait(false);
          _enforcePreferredSocEntity = new Entity(_context, "switch.pv_control_enforce_preferred_soc");
          await _entityManager.SetStateAsync(_forceChargeEntity.EntityId, "OFF");
        }

        if (_forceChargeMaxPriceEntity?.State is null)
        {
          await _entityManager.CreateAsync("number.pv_control_max_price_for_forcecharge", new EntityCreationOptions
          {
            Name = "Max price for force charge",
            Persist = true,
          }, new
          {
            icon = "mdi:currency-eur",
            min = 0,
            max = 25,
            step = 1,
            initial = 0,
            unitOfMeasurement = "ct",
            mode = "slider",
            device
          }).ConfigureAwait(false);
          _forceChargeMaxPriceEntity = new Entity(_context, "number.pv_control_max_price_for_forcecharge");
          await _entityManager.SetStateAsync(_forceChargeMaxPriceEntity.EntityId, "0");
        }

        if (_forceChargeTargetSoCEntity?.State is null)
        {
          await _entityManager.CreateAsync("number.pv_control_forcecharge_target_soc", new EntityCreationOptions
          {
            Name = "Force charge target SoC",
            Persist = true,
          }, new
          {
            icon = "mdi:battery-alert",
            min = 0,
            max = 95,
            step = 5,
            initial = 50,
            unitOfMeasurement = "%",
            mode = "slider",
            device
          }).ConfigureAwait(false);
          _forceChargeTargetSoCEntity = new Entity(_context, "number.pv_control_forcecharge_target_soc");
          await _entityManager.SetStateAsync(_forceChargeTargetSoCEntity.EntityId, "50");
        }

        if (_prefBatterySoCEntity?.State is null)
        {
          await _entityManager.CreateAsync("number.pv_control_preferredbatterycharge", new EntityCreationOptions
          {
            Name = "Preferred min SoC",
            Persist = true,
          }, new
          {
            icon = "mdi:battery-unknown",
            min = 10,
            max = 100,
            step = 5,
            initial = 30,
            unitOfMeasurement = "%",
            mode = "slider",
            device
          }).ConfigureAwait(false);
          _prefBatterySoCEntity = new Entity(_context, "number.pv_control_preferredbatterycharge");
          await _entityManager.SetStateAsync(_prefBatterySoCEntity.EntityId, "30");
        }

        if (_modeEntity?.State is null)
        {
          await _entityManager.CreateAsync("sensor.pv_control_mode", new EntityCreationOptions
          {
            Name = "Mode",
            DeviceClass = "ENUM",
          }, new
          {
            icon = "mdi:form-select",
            options = Enum.GetNames(typeof(InverterModes)),
            device
          }).ConfigureAwait(false);
          _modeEntity = new Entity(_context, "sensor.pv_control_mode");
        }

        if (_RunHeavyLoadsNowEntity?.State is null)
        {
          await _entityManager.CreateAsync("sensor.pv_control_run_heavyloads_now", new EntityCreationOptions
          {
            Name = "Run heavy loads now",
            DeviceClass = "ENUM",
          }, new
          {
            icon = "mdi:ev-station",
            options = Enum.GetNames(typeof(RunHeavyLoadsStatus)),
            device
          }).ConfigureAwait(false);
          _RunHeavyLoadsNowEntity = new Entity(_context, "sensor.pv_control_run_heavyloads_now");
        }

        if (_battery_StatusEntity?.State is null)
        {
          await _entityManager.CreateAsync("sensor.pv_control_battery_status", new EntityCreationOptions
          {
            Name = "Battery Status",
            DeviceClass = "ENUM",
          }, new
          {
            icon = "mdi:battery-charging",
            options = Enum.GetNames(typeof(BatteryStatuses)),
            device
          }).ConfigureAwait(false);
          _battery_StatusEntity = new Entity(_context, "sensor.pv_control_battery_status");
        }

        if (_battery_RemainingTimeEntity?.State is null)
        {
          await _entityManager.CreateAsync("sensor.pv_control_battery_remainingtime", new EntityCreationOptions
          {
            Name = "Battery - remaining time",
            DeviceClass = "DURATION",
          }, new
          {
            unit_of_measurement = "min",
            icon = "mdi:timer-alert",
            device
          }).ConfigureAwait(false);
          _battery_RemainingTimeEntity = new Entity(_context, "sensor.pv_control_battery_remainingtime");
        }

        if (_needToChargeFromGridTodayEntity?.State is null)
        {
          await _entityManager.CreateAsync("binary_sensor.pv_control_need_to_charge_from_grid_today", new EntityCreationOptions
          {
            Name = "Need to charge from Grid today",
          }, new
          {
            icon = "mdi:transmission-tower-export",
            device
          }).ConfigureAwait(false);
          _needToChargeFromGridTodayEntity = new Entity(_context, "sensor.pv_control_need_to_charge_from_grid_today");
        }

        if (_battery_RemainingEnergyEntity?.State is null)
        {
          await _entityManager.CreateAsync("sensor.pv_control_battery_remainingenergy", new EntityCreationOptions
          {
            Name = "Battery - available energy",
            DeviceClass = "Energy",
          }, new
          {
            unit_of_measurement = "Wh",
            icon = "mdi:lightning-bolt-outline",
            device
          }).ConfigureAwait(false);
          _battery_RemainingEnergyEntity = new Entity(_context, "sensor.pv_control_battery_remainingenergy");
        }

        if (_info_EstimatedMaxSoCTodayEntity?.State is null)
        {
          await _entityManager.CreateAsync("sensor.pv_control_info_max_soc_today", new EntityCreationOptions
          {
            Name = "Estimated Max SoC Today",
            DeviceClass = "Battery",
          }, new
          {
            unit_of_measurement = "%",
            icon = "mdi:battery-charging-90",
            device
          }).ConfigureAwait(false);
          _info_EstimatedMaxSoCTodayEntity = new Entity(_context, "sensor.pv_control_info_max_soc_today");
        }

        if (_info_EstimatedMinSoCTodayEntity?.State is null)
        {
          await _entityManager.CreateAsync("sensor.pv_control_info_min_soc_today", new EntityCreationOptions
          {
            Name = "Estimated Min SoC Today",
            DeviceClass = "Battery",
          }, new
          {
            unit_of_measurement = "%",
            icon = "mdi:battery-charging-20",
            device
          }).ConfigureAwait(false);
          _info_EstimatedMinSoCTodayEntity = new Entity(_context, "sensor.pv_control_info_min_soc_today");
        }

        if (_info_EstimatedMaxSoCTomorrowEntity?.State is null)
        {
          await _entityManager.CreateAsync("sensor.pv_control_info_max_soc_tomorrow", new EntityCreationOptions
          {
            Name = "Estimated Max SoC Tomorrow",
            DeviceClass = "Battery",
          }, new
          {
            unit_of_measurement = "%",
            icon = "mdi:battery-charging-90",
            device
          }).ConfigureAwait(false);
          _info_EstimatedMaxSoCTomorrowEntity = new Entity(_context, "sensor.pv_control_info_max_soc_tomorrow");
        }

        if (_info_EstimatedMinSoCTomorrowEntity?.State is null)
        {
          await _entityManager.CreateAsync("sensor.pv_control_info_min_soc_tomorrow", new EntityCreationOptions
          {
            Name = "Estimated Min SoC Tomorrow",
            DeviceClass = "Battery",
          }, new
          {
            unit_of_measurement = "%",
            icon = "mdi:battery-charging-20",
            device
          }).ConfigureAwait(false);
          _info_EstimatedMinSoCTomorrowEntity = new Entity(_context, "sensor.pv_control_info_min_soc_tomorrow");
        }

        if (_info_PredictedSoCEntity?.State is null)
        {
          await _entityManager.CreateAsync("sensor.pv_control_info_predicted_soc", new EntityCreationOptions
          {
            Name = "Predicted SoC Now",
            DeviceClass = "Battery",
          }, new
          {
            unit_of_measurement = "%",
            icon = "mdi:calendar-question",
            device
          }).ConfigureAwait(false);
          _info_PredictedSoCEntity = new Entity(_context, "sensor.pv_control_info_predicted_soc");
        }

        if (_info_PredictedChargeEntity?.State is null)
        {
          await _entityManager.CreateAsync("sensor.pv_control_info_predicted_charge", new EntityCreationOptions
          {
            Name = "Predicted Charge Until Now",
            DeviceClass = "Energy",
          }, new
          {
            unit_of_measurement = "Wh",
            icon = "mdi:solar-power-variant",
            device
          }).ConfigureAwait(false);
          _info_PredictedChargeEntity = new Entity(_context, "sensor.pv_control_info_predicted_charge");
        }

        if (_info_PredictedDischargeEntity?.State is null)
        {
          await _entityManager.CreateAsync("sensor.pv_control_info_predicted_discharge", new EntityCreationOptions
          {
            Name = "Predicted Discharge Until Now",
            DeviceClass = "Energy",
          }, new
          {
            unit_of_measurement = "Wh",
            icon = "mdi:home-lightbulb-outline",
            device
          }).ConfigureAwait(false);
          _info_PredictedDischargeEntity = new Entity(_context, "sensor.pv_control_info_predicted_discharge");
        }

        if (_info_chargeTodayEntity?.State is null)
        {
          await _entityManager.CreateAsync("sensor.pv_control_estimated_remaining_charge_today", new EntityCreationOptions
          {
            Name = "Estimated remaining charge today",
            DeviceClass = "Energy",
          }, new
          {
            unit_of_measurement = "Wh",
            icon = "mdi:solar-power",
            device
          }).ConfigureAwait(false);
          _info_chargeTodayEntity = new Entity(_context, "sensor.pv_control_estimated_remaining_charge_today");
        }

        if (_info_chargeTomorrowEntity?.State is null)
        {
          await _entityManager.CreateAsync("sensor.pv_control_estimated_charge_tomorrow", new EntityCreationOptions
          {
            Name = "Estimated charge tomorrow",
            DeviceClass = "Energy",
          }, new
          {
            unit_of_measurement = "Wh",
            icon = "mdi:solar-power",
            device
          }).ConfigureAwait(false);
          _info_chargeTomorrowEntity = new Entity(_context, "sensor.pv_control_estimated_charge_tomorrow");
        }

        if (_info_dischargeTodayEntity?.State is null)
        {
          await _entityManager.CreateAsync("sensor.pv_control_estimated_remaining_discharge_today", new EntityCreationOptions
          {
            Name = "Estimated remaining discharge today",
            DeviceClass = "Energy",
          }, new
          {
            unit_of_measurement = "Wh",
            icon = "mdi:home-lightning-bolt",
            device
          }).ConfigureAwait(false);
          _info_dischargeTodayEntity = new Entity(_context, "sensor.pv_control_estimated_remaining_discharge_today");
        }

        if (_info_dischargeTomorrowEntity?.State is null)
        {
          await _entityManager.CreateAsync("sensor.pv_control_estimated_discharge_tomorrow", new EntityCreationOptions
          {
            Name = "Estimated discharge tomorrow",
            DeviceClass = "Energy",
          }, new
          {
            unit_of_measurement = "Wh",
            icon = "mdi:home-lightning-bolt",
            device
          }).ConfigureAwait(false);
          _info_dischargeTomorrowEntity = new Entity(_context, "sensor.pv_control_estimated_discharge_tomorrow");
        }
        //await _entityManager.RemoveAsync("sensor.pv_control_estimated_remaining_discharge_tomorrow");

        return true;
      }
      catch (Exception ex)
      {
        _logger.LogError("Error registering sensors: {message}", ex.Message);
        return false;
      }
    }
  }
}
