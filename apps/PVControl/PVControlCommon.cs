using NetDaemon.Extensions.MqttEntityManager;
using NetDaemon.Extensions.Scheduler;
using NetDaemon.HassModel.Entities;
using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Linq;
using System.Reactive.Concurrency;
using System.Text;
using System.Threading.Tasks;

namespace NetDeamon.apps.PVControl
{
  public class PVControlCommon
  {
    private PVControlCommon() { }
    private static readonly PVControlCommon _instance = new PVControlCommon();
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
      Entity entity = new Entity(PVCC_HaContext, id);
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
        //conf
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
    public Entity DailyImportEnergyEntity { get; set; } = null!;
    public Entity DailyExportEnergyEntity { get; set; } = null!;
    public Entity CurrentHouseLoadEntity { get; set; } = null!;
    public Entity CurrentPVPowerEntity { get; set; } = null!;
    public Entity CurrentBatteryPowerEntity { get; set; } = null!;
    public float InverterEfficiency { get; set; } = default;
    public Entity TodayPVEnergyEntity { get; set; } = null!;
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
  }
}
