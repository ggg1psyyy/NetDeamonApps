﻿using Microsoft.AspNetCore.Mvc.Formatters;
using Microsoft.Extensions.Options;
using NetDaemon.Client;
using NetDaemon.Client.HomeAssistant.Extensions;
using NetDaemon.Extensions.MqttEntityManager;
using NetDaemon.Extensions.Scheduler;
using NetDaemon.HassModel.Entities;
using NetDaemon.HassModel.Integration;
using PVControl;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reactive.Concurrency;
using System.Reflection;
using System.Runtime.Intrinsics.Arm;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Transactions;

//#pragma warning disable CS1998
namespace PVControl
{
  public class PVConfig
  {
    public String? DBLocation { get; set; }
    public Entity? CurrentImportPriceEntity { get; set; }
    public Entity? CurrentExportPriceEntity { get; set; }
    public Entity? CurrentHouseLoadEntity { get; set; }
    public Entity? CurrentPVPowerEntity { get; set; }
    public Entity? CurrentBatteryPowerEntity { get; set; }
    public float? InverterEfficiency { get; set; } 
    public Entity? TodayPVEnergyEntity { get; set; }
    public List<Entity>? ForecastPVEnergyTodayEntities { get; set; }
    public List<Entity>? ForecastPVEnergyTomorrowEntities{ get; set; }
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
    private readonly IHomeAssistantConnection _connection;
    private readonly IHomeAssistantApiManager _apiManager;
    
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
    private Entity _info_chargeTodayEntity;
    private Entity _info_dischargeTodayEntity;
    private Entity _info_chargeTomorrowEntity;
    private Entity _info_dischargeTomorrowEntity;
    #endregion

    public PVControl(IHaContext ha, IMqttEntityManager entityManager, IAppConfig<PVConfig> config, IScheduler scheduler, ILogger<PVControl> logger, IHomeAssistantConnection connection, IHomeAssistantApiManager apiManager)
    {
      CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
      _context = ha;
      _entityManager = entityManager;
      _logger = logger;
      _config = config.Value;
      _scheduler = scheduler;
      _connection = connection;
      _apiManager = apiManager;
      
      if (String.IsNullOrWhiteSpace(_config.DBLocation))
        _config.DBLocation = "apps/DataLogger/energy_history.db";

      if (!CheckConfiguration())
        throw new Exception("Error initializing configuration");

      _house = new HouseEnergy(_config);

      _modeEntity = new Entity(_context, "sensor.pv_control_mode");
      _battery_StatusEntity = new Entity(_context, "sensor.pv_control_battery_status");
      _battery_RemainingTimeEntity = new Entity(_context, "sensor.pv_control_battery_remainingtime");
      _battery_RemainingEnergyEntity = new Entity(_context, "sensor.pv_control_battery_remainingenergy");
      _needToChargeFromGridTodayEntity = new Entity(_context, "binary_sensor.pv_control_need_to_charge_from_grid_today");
      _prefBatterySoCEntity = new Entity(_context, "input_number.pv_control_preferredbatterycharge");
      _enforcePreferredSocEntity = new Entity(_context, "input_boolean.pv_control_enforce_preferred_soc");
      _info_EstimatedMaxSoCTodayEntity = new Entity(_context, "sensor.pv_control_info_max_soc_today");
      _info_EstimatedMinSoCTodayEntity = new Entity(_context, "sensor.pv_control_info_min_soc_today");
      _info_EstimatedMaxSoCTomorrowEntity = new Entity(_context, "sensor.pv_control_info_max_soc_tomorrow");
      _info_EstimatedMinSoCTomorrowEntity = new Entity(_context, "sensor.pv_control_info_min_soc_tomorrow");
      _info_chargeTodayEntity = new Entity(_context, "sensor.pv_control_estimated_remaining_charge_today");
      _info_chargeTomorrowEntity = new Entity(_context, "sensor.pv_control_estimated_charge_tomorrow");
      _info_dischargeTodayEntity = new Entity(_context, "sensor.pv_control_estimated_remaining_discharge_today");
      _info_dischargeTomorrowEntity = new Entity(_context, "sensor.pv_control_estimated_discharge_tomorrow");

#if DEBUG
      _house.EnforcePreferredSoC = true;
      _house.PreferredMinBatterySoC = 70;
      var X = _house.CurrentEnergyImportPrice;
      var Y = _house.NeedToChargeFromExternal;
      var Z = _house.BestChargeTime;
#endif

      _logger.LogInformation("Finished PVControl constructor");
    }
    async Task IAsyncInitializable.InitializeAsync(CancellationToken cancellationToken)
    {
      //await _entityManager.RemoveAsync("sensor.car_charger_battery");
      if (await RegisterSensors(cancellationToken))
      {
        _prefBatterySoCEntity?.StateChanges().SubscribeAsync(async _ => await UserStateChanged(_prefBatterySoCEntity));
        _enforcePreferredSocEntity?.StateChanges().SubscribeAsync(async _ => await UserStateChanged(_enforcePreferredSocEntity));
        await UserStateChanged(_prefBatterySoCEntity);
        await UserStateChanged(_enforcePreferredSocEntity);
#if DEBUG
        await ScheduledOperations();
        //_scheduler.ScheduleCron("*/30 * * * * *", async () => await ScheduledOperations(), true);
#else
        _scheduler.ScheduleCron("*/15 * * * * *", async () => await ScheduledOperations(), true);
#endif
      }
      else
      {
        _logger.LogError("Error registering sensors");
      }
    }
#pragma warning disable CS1998 // Async method lacks 'await' operators and will run synchronously
    private async Task UserStateChanged(Entity? entity)
#pragma warning restore CS1998 // Async method lacks 'await' operators and will run synchronously
    {
      if (entity == null)
        return;

      if (entity.EntityId == _prefBatterySoCEntity.EntityId)
      {
        if (entity.TryGetStateValue<int>(out int value))
          _house.PreferredMinBatterySoC = value;
      }
      if (entity.EntityId == _enforcePreferredSocEntity.EntityId && entity.State is not null)
      {
        _house.EnforcePreferredSoC = entity.IsOn();
      }
    }
    private async Task ScheduledOperations()
    {
      _logger.LogDebug("Entering Schedule");

      await _entityManager.SetStateAsync(_modeEntity.EntityId, _house.ProposedMode.ToString());
      DateTime now = DateTime.Now;

      var nextCheapest = _house.BestChargeTime;
      var attr_Mode = new
      {
        next_charge_window_start = nextCheapest.StartTime.ToISO8601(),
        next_charge_window_end = nextCheapest.EndTime.ToISO8601(),
        price = nextCheapest.Price.ToString(CultureInfo.InvariantCulture),
      };
      await _entityManager.SetAttributesAsync(_modeEntity.EntityId, attr_Mode);

      await _entityManager.SetStateAsync(_battery_RemainingTimeEntity.EntityId, _house.EstimatedTimeToBatteryFullOrEmpty.ToString(CultureInfo.InvariantCulture));
      var attr_RemainingTime = new
      {
        Estimated_time = now.AddMinutes(_house.EstimatedTimeToBatteryFullOrEmpty).ToISO8601(),
        next_relevant_pv_charge = _house.FirstRelevantPVEnergyToday.ToISO8601(),
        avg_battery_charge_or_discharge_Power = _house.AverageBatteryChargeDischargePower.ToString(CultureInfo.InvariantCulture) + " W",
        status = _house.BatteryStatus.ToString(),
      };
      await _entityManager.SetAttributesAsync(_battery_RemainingTimeEntity.EntityId, attr_RemainingTime);

      await _entityManager.SetStateAsync(_battery_StatusEntity.EntityId, _house.BatteryStatus.ToString());
      var attr_batStatus = new
      {
        avg_battery_charge_or_discharge_Power = _house.AverageBatteryChargeDischargePower.ToString(CultureInfo.InvariantCulture) + " W",
        current_SoC = _house.BatterySoc.ToString(CultureInfo.InvariantCulture) + "%",
      };
      await _entityManager.SetAttributesAsync(_battery_StatusEntity.EntityId, attr_batStatus);

      await _entityManager.SetStateAsync(_battery_RemainingEnergyEntity.EntityId, _house.UsableBatteryEnergy.ToString(CultureInfo.InvariantCulture));
      var attr_RemainingEnergy = new
      {
        min_allowed_SoC = _house.PreferredMinimalSoC.ToString() + "%",
        remaining_energy_at_min_battery_soc = _house.ReserveBatteryEnergy.ToString(CultureInfo.InvariantCulture) + " Wh",
        remaining_energy_to_zero_soc = _house.CalculateBatteryEnergyAtSoC(_house.BatterySoc, 0).ToString(CultureInfo.InvariantCulture) + " Wh",
        battery_capacity = _house.BatteryCapacity.ToString(CultureInfo.InvariantCulture) + " Wh",
      };
      await _entityManager.SetAttributesAsync(_battery_RemainingEnergyEntity.EntityId, attr_RemainingEnergy);

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

      var est_soc_today = _house.EstimatedBatterySoCTodayAndTomorrow.Where(s => s.Key.Date == now.Date && s.Key >= now).ToDictionary();
      var est_soc_tomorrow = _house.EstimatedBatterySoCTodayAndTomorrow.Where(s => s.Key.Date == now.AddDays(1).Date).ToDictionary();

      if (est_soc_today.Count > 0)
      {
        var min_soc_today = est_soc_today.First(s => s.Value == est_soc_today.Values.Min());
        await _entityManager.SetStateAsync(_info_EstimatedMinSoCTodayEntity.EntityId, min_soc_today.Value.ToString(CultureInfo.InvariantCulture));
        var attr_Min_SoC_Today = new
        {
          time = min_soc_today.Key.ToISO8601(),
          data = est_soc_today,
        };
        await _entityManager.SetAttributesAsync(_info_EstimatedMinSoCTodayEntity.EntityId, attr_Min_SoC_Today);

        var max_soc_today = est_soc_today.First(s => s.Value == est_soc_today.Values.Max());
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
        var min_soc_tomorrow = est_soc_tomorrow.First(s => s.Value == est_soc_tomorrow.Values.Min());
        await _entityManager.SetStateAsync(_info_EstimatedMinSoCTomorrowEntity.EntityId, min_soc_tomorrow.Value.ToString(CultureInfo.InvariantCulture));
        var attr_Min_SoC_Tomorrow = new
        {
          time = min_soc_tomorrow.Key.ToISO8601(),
          data = est_soc_tomorrow,
        };
        await _entityManager.SetAttributesAsync(_info_EstimatedMinSoCTomorrowEntity.EntityId, attr_Min_SoC_Tomorrow);

        var max_soc_tomorrow = est_soc_tomorrow.First(s => s.Value == est_soc_tomorrow.Values.Max());
        await _entityManager.SetStateAsync(_info_EstimatedMaxSoCTomorrowEntity.EntityId, max_soc_tomorrow.Value.ToString(CultureInfo.InvariantCulture));
        var attr_Max_SoC_Tomorrow = new
        {
          time = max_soc_tomorrow.Key.ToISO8601(),
          data = est_soc_tomorrow,
        };
        await _entityManager.SetAttributesAsync(_info_EstimatedMaxSoCTomorrowEntity.EntityId, attr_Max_SoC_Tomorrow);
      }

      var charge = _house.PVForecastTodayAndTomorrow;
      var chargeToday = charge.Where(c => c.Key >= now && c.Key.Date == now.Date).ToDictionary();
      var chargeTomorrow = charge.Where(c => c.Key.Date == now.Date.AddDays(1)).ToDictionary();
      var discharge = _house.EstimatedEnergyUsageTodayAndTomorrow;
      var dischargeToday = discharge.Where(c => c.Key >= now && c.Key.Date == now.Date).ToDictionary();
      var dischargeTomorrow = discharge.Where(c => c.Key.Date == now.Date.AddDays(1)).ToDictionary();

      if (chargeToday.Count > 0)
      {
        await _entityManager.SetStateAsync(_info_chargeTodayEntity.EntityId, chargeToday.Sum(c => c.Value).ToString(CultureInfo.InvariantCulture));
        var attr_chargeToday = new
        {
          data = charge.Where(c => c.Key.Date == now.Date).ToDictionary(),
        };
        await _entityManager.SetAttributesAsync(_info_chargeTodayEntity.EntityId, attr_chargeToday);
      }
      if (chargeTomorrow.Count > 0)
      {
        await _entityManager.SetStateAsync(_info_chargeTomorrowEntity.EntityId, chargeTomorrow.Sum(c => c.Value).ToString(CultureInfo.InvariantCulture));
        var attr_chargeTomorrow = new
        {
          data = chargeTomorrow,
        };
        await _entityManager.SetAttributesAsync(_info_chargeTomorrowEntity.EntityId, attr_chargeTomorrow);
      }

      if (dischargeToday.Count > 0)
      {
        await _entityManager.SetStateAsync(_info_dischargeTodayEntity.EntityId, dischargeToday.Sum(c => c.Value).ToString(CultureInfo.InvariantCulture));
        var attr_dischargeToday = new
        {
          data = discharge.Where(c => c.Key.Date == now.Date).ToDictionary(),
        };
        await _entityManager.SetAttributesAsync(_info_dischargeTodayEntity.EntityId, attr_dischargeToday);
      }
      if (dischargeTomorrow.Count > 0)
      {
        await _entityManager.SetStateAsync(_info_dischargeTomorrowEntity.EntityId, dischargeTomorrow.Sum(c => c.Value).ToString(CultureInfo.InvariantCulture));
        var attr_dischargeTomorrow = new
        {
          data = dischargeTomorrow,
        };
        await _entityManager.SetAttributesAsync(_info_dischargeTomorrowEntity.EntityId, attr_dischargeTomorrow);
      }

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
    private async Task<bool> RegisterSensors(CancellationToken cancellationToken)
    {
      try
      {
        var identifiers = new[] { "pv_control" };
        var device = new { identifiers, name = "PV Control", model = "PV Control", manufacturer = "AH", sw_version = 0.1 };
        //await _connection.DeleteInputBooleanHelperAsync("pv_control_enforcepreferredsoc", cancellationToken);
        if (_enforcePreferredSocEntity?.State is null)
        {
          await _connection.CreateInputBooleanHelperAsync(
            name: "PV_Control Enforce Preferred SoC",
            cancelToken: cancellationToken
            );
          _enforcePreferredSocEntity = new Entity(_context, "input_boolean.pv_control_enforcepreferredsoc");
        }
        //await _connection.DeleteInputNumberHelperAsync("pv_control_preferredbatterycharge", cancellationToken);
        if (_prefBatterySoCEntity?.State is null)
        {
          await _connection.CreateInputNumberHelperAsync(
            name: "PV_Control PreferredBatteryCharge",
            min: 10,
            max: 100,
            step: 5,
            initial: 30,
            unitOfMeasurement: "%",
            mode: "slider",
            cancelToken: cancellationToken
            );
          _prefBatterySoCEntity = new Entity(_context, "input_number.pv_control_preferredbatterycharge");
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
            options = Enum.GetNames(typeof(HouseEnergy.InverterModes)),
            device
          }).ConfigureAwait(false);
          _modeEntity = new Entity(_context, "sensor.pv_control_mode");
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
            options = Enum.GetNames(typeof(HouseEnergy.BatteryStatuses)),
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
        await _entityManager.RemoveAsync("sensor.pv_control_estimated_remaining_discharge_tomorrow");
        await _entityManager.RemoveAsync("sensor.pv_control_estimated_remaining_charge_tomorrow");
        //await _entityManager.RemoveAsync("sensor.pv_control_battery_status");
        //await _entityManager.RemoveAsync("binary_sensor.pv_control_need_to_charge_from_grid_today");
        //await _entityManager.RemoveAsync("sensor.pv_control_battery_remainingenergy");
        //await _entityManager.RemoveAsync("sensor.pv_control_battery_remainingtime");
        //await _entityManager.RemoveAsync("sensor.pv_control_mode");

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
