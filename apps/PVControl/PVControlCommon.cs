﻿using NetDaemon.Extensions.MqttEntityManager;
using NetDaemon.Extensions.Scheduler;
using NetDaemon.HassModel.Entities;
using System.Collections.Generic;
using System.Dynamic;
using System.Reflection;
using System.Threading.Tasks;

namespace NetDeamon.apps.PVControl
{
  public struct NeedToChargeResult
  {
    public bool NeedToCharge;
    public DateTime LatestChargeTime;
    public int EstimatedSoc;

    public NeedToChargeResult(int estimatedSoc, DateTime latestChargeTime, bool needToCharge)
    {
      EstimatedSoc = estimatedSoc;
      LatestChargeTime = latestChargeTime;
      NeedToCharge = needToCharge;
    }
  }

  public struct SystemState(int pVPower, int houseLoad, int soC, InverterState inverterState, int pvPower)
  {
    public int PVPower = pvPower;
    public int HouseLoad = houseLoad;
    public int SoC = soC;
    public InverterState InverterState = inverterState;
  }
  public struct InverterState(InverterModes mode = InverterModes.normal, ForceChargeReasons modeReason = ForceChargeReasons.None, bool batteryChargeEnable = true)
  {
    public InverterModes Mode = mode;
    public ForceChargeReasons ModeReason = modeReason;
    public bool BatteryChargeEnable = batteryChargeEnable;
  }
  public class PVControlCommon
  {
    private PVControlCommon() { }
    private static readonly PVControlCommon _instance = new();
    public static PVControlCommon PVCCInstance => _instance;
    public static IHaContext PVCC_HaContext { get; private set; } = null!;
    public static IMqttEntityManager PVCC_EntityManager { get; private set; } = null!;
    public static ILogger<PVControl> PVCC_Logger { get; private set; } = null!;
    public static PVConfig PVCC_Config { get; private set; } = null!;
    public static DisposableScheduler PVCC_Scheduler { get; private set; } = null!;

    public void Initialize(IHaContext haContext, IMqttEntityManager mqttEntityManager, ILogger<PVControl> logger, IAppConfig<PVConfig> pVConfig, DisposableScheduler scheduler)
    {
      PVCC_HaContext = haContext;
      PVCC_EntityManager = mqttEntityManager; 
      PVCC_Logger = logger;
      PVCC_Config = pVConfig.Value;
      PVCC_Scheduler = scheduler;
    }
    ~PVControlCommon() 
    {
      PVCC_HaContext = null!;
      PVCC_EntityManager = null!;
      PVCC_Logger = null!;
      PVCC_Config = null!;
      PVCC_Scheduler = null!;
    }
    public static async Task<Entity> RegisterSensor(string id, string name, string deviceClass, string icon, object? addConfig = null, string defaultValue = "", bool reRegister = false)
    {
      var identifiers = new[] { "pv_control" };
      var device = new { identifiers, name = "PV Control", model = "PV Control", manufacturer = "AH", sw_version = 0.5 };
      Entity entity = new(PVCC_HaContext, id);
      if (entity?.State == null || reRegister)
      {
        if (reRegister && entity?.State != null)
        {
          await PVCC_EntityManager.RemoveAsync(id);
        }

        dynamic dynamicConfig = new ExpandoObject();
        if (addConfig != null)
        {
          foreach (var property in addConfig.GetType().GetProperties())
          {
            ((IDictionary<string, object>)dynamicConfig)[property.Name] = property.GetValue(addConfig);
          }
        }
        dynamicConfig.device = device;
        dynamicConfig.icon = icon;
        await PVCC_EntityManager.CreateAsync(id, new EntityCreationOptions
        {
          Name = name,
          DeviceClass = deviceClass,
        },
        dynamicConfig
        ).ConfigureAwait(false);
        entity = new Entity(PVCC_HaContext, id);
        if (!string.IsNullOrEmpty(defaultValue))
          await PVCC_EntityManager.SetStateAsync(entity.EntityId, defaultValue);
      }
      return entity;
    }
  }
  public partial class PVConfig
  {
    public string DBLocation { get; set; } = default!;

    public string DBFullLocation
    {
      get
      {
        #if DEBUG
        var path = Assembly.GetExecutingAssembly().Location;
        return System.IO.Path.GetDirectoryName(path) + "\\" + DBLocation;
        #else
        return DBLocation;
        #endif
      }
    }
    public List<Entity> CurrentImportPriceEntities { get; set; } = null!;
    public float ImportPriceMultiplier { get; set; } = default;
    public float ImportPriceAddition { get; set; } = default;
    public float ImportPriceNetwork { get; set; } = default;
    public float ImportPriceTax { get; set; } = default;
    public Entity CurrentExportPriceEntity { get; set; } = null!;
    public bool ExportPriceIsVariable { get; set; } = default;
    public float ExportPriceMultiplier { get; set; } = default;
    public float ExportPriceAddition { get; set; } = default;
    public float ExportPriceNetwork { get; set; } = default;
    public float ExportPriceTax { get; set; } = default;
    public Entity InverterStatusEntity { get; set; } = null!;
    public string InverterStatusNormalString { get; set; } = "Normal Mode";
    public Entity DailyImportEnergyEntity { get; set; } = null!;
    public Entity DailyExportEnergyEntity { get; set; } = null!;
    public Entity CurrentHouseLoadEntity { get; set; } = null!;
    public Entity CurrentPVPowerEntity { get; set; } = null!;
    public Entity CurrentBatteryPowerEntity { get; set; } = null!;
    public float InverterEfficiency { get; set; } = default;
    public Entity TodayPVEnergyEntity { get; set; } = null!;
    public Entity CurrentGridPowerEntity { get; set; } = null!;
    public List<Entity> ForecastPVEnergyTodayEntities { get; set; } = [];
    public List<Entity> ForecastPVEnergyTomorrowEntities { get; set; } =[];
    public Entity BatterySoCEntity { get; set; } = null!;
    public Entity BatteryCapacityEntity { get; set; } = null!;
    public float BatteryCapacityValue { get; set; } = default;
    public Entity MaxBatteryChargeCurrrentEntity { get; set; } = null!;
    public int MaxBatteryChargeCurrrentValue { get; set; }
    public Entity MinBatterySoCEntity { get; set; } = null!;
    public int MinBatterySoCValue { get; set; } = default;
    public Entity CurrentImportPriceEntity
    {
      get
      {
        foreach (var entity in CurrentImportPriceEntities)
        {
          if (entity.TryGetJsonAttribute("data", out _))
            return entity;
        }
        return null!;
      }
    }
    public int MaxBatteryChargePower
    {
      get
      {
        int maxPower = MaxBatteryChargeCurrrentValue != default ? MaxBatteryChargeCurrrentValue : 10;
        if (MaxBatteryChargeCurrrentEntity is not null && MaxBatteryChargeCurrrentEntity.TryGetStateValue(out int max))
          maxPower = max;

        return maxPower;
      }
    }
    public string EnergyCostDBLocation { get; set; } = default!;
    public string EnergyCostDBFullLocation
    {
      get
      {
        #if DEBUG
        var path = Assembly.GetExecutingAssembly().Location;
        return System.IO.Path.GetDirectoryName(path) + "\\" + EnergyCostDBLocation;
        #else
        return EnergyCostDBLocation;
        #endif
      }
    }
    public Entity TotalImportEnergyEntity { get; set; } = null!;
    public Entity TotalExportEnergyEntity { get; set; } = null!;
  }
}
