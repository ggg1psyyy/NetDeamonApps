using System;
using NetDaemon.Extensions.MqttEntityManager;
using NetDaemon.Extensions.Scheduler;
using NetDaemon.HassModel.Entities;
using System.Collections.Generic;
using System.Linq;
using System.Dynamic;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NetDaemon.AppModel;
using NetDaemon.HassModel;
using NetDaemon.Client;

namespace NetDeamon.apps.PVControl
{
  /// <summary>Whether the battery needs a grid charge (<see cref="Simulator.EnergySimulator"/>).</summary>
  public struct NeedToChargeResult
  {
    /// <summary>A grid charge is needed.</summary>
    public bool NeedToCharge;
    /// <summary>Latest time to start charging.</summary>
    public DateTime LatestChargeTime;
    /// <summary>SoC at the critical point (or the lowest point without a need), %.</summary>
    public int EstimatedSoc;

    /// <summary>Creates the result.</summary>
    /// <param name="estimatedSoc">SoC at the critical point, %.</param>
    /// <param name="latestChargeTime">Latest time to start charging.</param>
    /// <param name="needToCharge">A grid charge is needed.</param>
    public NeedToChargeResult(int estimatedSoc, DateTime latestChargeTime, bool needToCharge)
    {
      EstimatedSoc = estimatedSoc;
      LatestChargeTime = latestChargeTime;
      NeedToCharge = needToCharge;
    }
  }

  /// <summary>A snapshot of the house.</summary>
  /// <param name="houseLoad">House load, W.</param>
  /// <param name="soC">Battery SoC, %.</param>
  /// <param name="inverterState">Inverter mode.</param>
  /// <param name="pvPower">PV power, W.</param>
  public struct SystemState(int houseLoad, int soC, InverterState inverterState, int pvPower)
  {
    public int PVPower = pvPower;
    public int HouseLoad = houseLoad;
    public int SoC = soC;
    public InverterState InverterState = inverterState;
  }
  /// <summary>An inverter command: mode, reason and the two modifiers.</summary>
  /// <param name="mode">Inverter mode.</param>
  /// <param name="modeReason">Why this mode.</param>
  /// <param name="batteryChargeEnable">PV surplus may charge the battery.</param>
  /// <param name="exportAllowed">PV surplus may be exported.</param>
  public struct InverterState(InverterModes mode = InverterModes.normal, ForceChargeReasons modeReason = ForceChargeReasons.None, bool batteryChargeEnable = true, bool exportAllowed = true)
  {
    public InverterModes Mode = mode;
    public ForceChargeReasons ModeReason = modeReason;
    public bool BatteryChargeEnable = batteryChargeEnable;
    /// <summary>PV surplus may be exported; false at a negative export price, independent of <see cref="Mode"/>.</summary>
    public bool ExportAllowed = exportAllowed;
    /// <summary>A grid charge at full power (SolaX: Manual Force Charge), not the slower remote-controlled
    /// (VPP) path; decided when the charge starts (<see cref="Simulator.EnergySimulator.ChargesBulk"/>).</summary>
    public bool Bulk;
  }
  /// <summary>Shared dependencies of PVControl (HA context, MQTT, logger, config, scheduler, API), set once by the app.</summary>
  public class PVControlCommon
  {
    /// <summary>Single instance (<see cref="PVCCInstance"/>).</summary>
    private PVControlCommon() { }
    private static readonly PVControlCommon _instance = new();
    public static PVControlCommon PVCCInstance => _instance;
    public static IHaContext PVCC_HaContext { get; private set; } = null!;
    public static IMqttEntityManager PVCC_EntityManager { get; private set; } = null!;
    public static ILogger<PVControl> PVCC_Logger { get; private set; } = null!;
    public static PVConfig PVCC_Config { get; private set; } = null!;
    public static DisposableScheduler PVCC_Scheduler { get; private set; } = null!;
    public static IHomeAssistantApiManager PVCC_ApiManager { get; private set; } = null!;

    /// <summary>Sets the shared dependencies.</summary>
    /// <param name="haContext">HA context.</param>
    /// <param name="mqttEntityManager">MQTT entity manager.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="pVConfig">App configuration.</param>
    /// <param name="scheduler">Scheduler.</param>
    /// <param name="apiManager">HA REST API.</param>
    public void Initialize(IHaContext haContext, IMqttEntityManager mqttEntityManager, ILogger<PVControl> logger, IAppConfig<PVConfig> pVConfig, DisposableScheduler scheduler, IHomeAssistantApiManager apiManager)
    {
      PVCC_HaContext = haContext;
      PVCC_EntityManager = mqttEntityManager;
      PVCC_Logger = logger;
      PVCC_Config = pVConfig.Value;
      PVCC_Scheduler = scheduler;
      PVCC_ApiManager = apiManager;
    }
    ~PVControlCommon()
    {
      PVCC_HaContext = null!;
      PVCC_EntityManager = null!;
      PVCC_Logger = null!;
      PVCC_Config = null!;
      PVCC_Scheduler = null!;
      PVCC_ApiManager = null!;
    }
    /// <summary>What registering an entity does: publish its discovery config, and set the default state.</summary>
    /// <param name="exists">The entity exists in HA.</param>
    /// <param name="reRegister">Remove and create again.</param>
    /// <param name="republish">Publish the config although it exists.</param>
    /// <param name="hasOptions">The config has an option list (select/enum): published on every start, so HA's
    /// list follows the code (a retired option left in it is selectable but unknown to PVControl).</param>
    /// <returns>(publish the config, set the default state — only for a new entity, a republish keeps the state).</returns>
    public static (bool Publish, bool SetDefault) Registration(bool exists, bool reRegister, bool republish, bool hasOptions) =>
      (!exists || reRegister || republish || hasOptions, !exists || reRegister);

    /// <summary>Creates a PVControl entity via MQTT discovery (device "PV Control") unless it exists.</summary>
    /// <param name="id">Entity id, e.g. "sensor.pv_control_version".</param>
    /// <param name="name">Display name.</param>
    /// <param name="deviceClass">HA device class; null = none.</param>
    /// <param name="icon">mdi icon.</param>
    /// <param name="addConfig">Extra discovery config (unit, options, min/max, …).</param>
    /// <param name="defaultValue">State set after (re)creating; "" = none.</param>
    /// <param name="reRegister">Remove and create again (drops the entity's config in HA).</param>
    /// <param name="republish">Publish the config although the entity exists (HA takes device info, e.g. the firmware version, only from it).</param>
    /// <returns>The entity.</returns>
    public static async Task<Entity> RegisterSensor(string id, string name, string? deviceClass, string icon, object? addConfig = null, string defaultValue = "", bool reRegister = false, bool republish = false)
    {
      var identifiers = new[] { "pv_control" };
      var device = new { identifiers, name = "PV Control", model = "PV Control", manufacturer = "AH", sw_version = AppVersion.Current };
      Entity entity = new(PVCC_HaContext, id);
      bool hasOptions = addConfig?.GetType().GetProperty("options") is not null;
      var (publish, setDefault) = Registration(entity?.State != null, reRegister, republish, hasOptions);
      if (publish)
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
            ((IDictionary<string, object>)dynamicConfig)[property.Name] = property.GetValue(addConfig)!;
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
        if (setDefault && !string.IsNullOrEmpty(defaultValue))
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
    public List<NetworkPricePeriod>? ImportPriceNetworkPeriods { get; set; }
    public float ImportPriceTax { get; set; } = default;
    public PriceResolution ImportPriceResolution { get; set; } = PriceResolution.Hourly;
    /// <summary>Risk premium on estimated (not yet published) prices in the simulation: import
    /// × (1 + premium), export × (1 − premium) — an estimated slot must be clearly better to win.</summary>
    public float EstimatedPricePremium { get; set; } = 0.05f;
    public bool ExportPriceSameAsImport { get; set; } = true;
    public bool ExportPriceFixed { get; set; } = default;
    public Entity? ExportPriceFixedEntity { get; set; }
    public float ExportPriceFixedValue { get; set; } = default;
    public List<Entity>? CurrentExportPriceEntities { get; set; }
    public float ExportPriceMultiplier { get; set; } = default;
    public float ExportPriceAddition { get; set; } = default;
    public float ExportPriceNetwork { get; set; } = default;
    public float ExportPriceTax { get; set; } = default;
    public PriceResolution ExportPriceResolution { get; set; } = PriceResolution.Hourly;
    public Entity InverterStatusEntity { get; set; } = null!;
    public Entity DailyImportEnergyEntity { get; set; } = null!;
    public Entity DailyExportEnergyEntity { get; set; } = null!;
    public Entity? BatteryInputEnergyEntity { get; set; }
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
    /// <summary>The entity in <see cref="CurrentImportPriceEntities"/> whose price data reaches
    /// furthest — all sources are equal, the first to publish tomorrow wins (see
    /// <see cref="PriceManager.FurthestSource"/>).</summary>
    public Entity CurrentImportPriceEntity => FurthestPriceEntity(CurrentImportPriceEntities)!;
    /// <summary>Same as <see cref="CurrentImportPriceEntity"/> for <see cref="CurrentExportPriceEntities"/>.
    /// Only used for the independent-variable export price case (neither
    /// <see cref="ExportPriceSameAsImport"/> nor <see cref="ExportPriceFixed"/>).</summary>
    public Entity? CurrentExportPriceEntity => FurthestPriceEntity(CurrentExportPriceEntities);

    static Entity? FurthestPriceEntity(List<Entity>? entities) =>
      entities is null ? null
      : PriceManager.FurthestSource(entities.Select(PriceManager.DataEnd).ToList()) is { } i ? entities[i] : null;
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
    /// <summary>Grid import counters (kWh, never resetting), in priority order: the first is used while it
    /// works, the next ones only while it fails (<see cref="ImportCap.PriorityCounter"/>).</summary>
    public List<Entity> TotalImportEnergyEntities { get; set; } = [];
    public Entity TotalExportEnergyEntity { get; set; } = null!;

    /// <summary>Highest mean grid import of any 15-min block, W (the network power price counts the month's
    /// highest quarter-hour); 0 = no limit.</summary>
    public int MaxGridImportW { get; set; }
    /// <summary>How long a requested change takes to show at the meter, s (automation delays, ramps);
    /// the import cap guard assumes the current power continues that long.</summary>
    public int ImportCapReactionSeconds { get; set; } = 90;
    /// <summary>VPP (remote-controlled) grid charge power at 0 % SoC, W — the default line until the curve is
    /// learned (<see cref="Managers.VppChargeCurve"/>).</summary>
    public double VppChargePowerAt0PctW { get; set; } = 5950;
    /// <summary>Change of the VPP grid charge power per % SoC, W (negative: falls with the SoC).</summary>
    public double VppChargePowerSlopeWPerPct { get; set; } = -41;
    /// <summary>HA's feedback whether the inverter runs the published inverter command (on = in sync; attribute
    /// <c>blocked</c> = the reconciler gave up); null = not checked.</summary>
    public Entity? InverterSyncEntity { get; set; }
    /// <summary>Battery voltage while charging (W per A of the charge current); 230 V without it.</summary>
    public Entity? BatteryVoltageEntity { get; set; }
    /// <summary>Battery voltage, V: <see cref="BatteryVoltageEntity"/> in 5-V steps (the plan shouldn't follow every fluctuation), else 230.</summary>
    public int BatteryVoltageV => BatteryVoltageEntity is not null && BatteryVoltageEntity.TryGetStateValue(out float v) && v > 100 ? (int)Math.Round(v / 5) * 5 : 230;
  }

  /// <summary>
  /// A time-bounded override for the network import price.
  /// Active when the current month is in [StartMonth, EndMonth] and hour in [StartHour, EndHour).
  /// Both ranges wrap around their boundary when Start &gt; End — e.g. StartMonth=10,
  /// EndMonth=3 covers Oct-Mar across the year boundary (WiNAP); StartHour=22, EndHour=4
  /// covers 22:00-04:00 across midnight.
  /// </summary>
  public class NetworkPricePeriod
  {
    public string Name { get; set; } = string.Empty;
    public int StartMonth { get; set; }
    public int EndMonth { get; set; }
    public int StartHour { get; set; }
    public int EndHour { get; set; }
    public float Price { get; set; }

    /// <summary>Whether the period is active at a time.</summary>
    /// <param name="t">Time to check.</param>
    /// <returns>True if its month and hour lie in the period's ranges.</returns>
    public bool IsActiveAt(DateTime t) =>
      InRange(t.Month, StartMonth, EndMonth, inclusiveEnd: true)
      && InRange(t.Hour, StartHour, EndHour, inclusiveEnd: false);

    /// <summary>Whether a value lies in a range that wraps around when start &gt; end.</summary>
    /// <param name="value">Value (month or hour).</param>
    /// <param name="start">Range start (inclusive).</param>
    /// <param name="end">Range end.</param>
    /// <param name="inclusiveEnd">Whether the end is included.</param>
    private static bool InRange(int value, int start, int end, bool inclusiveEnd) =>
      start <= end
        ? value >= start && (inclusiveEnd ? value <= end : value < end)
        : value >= start || (inclusiveEnd ? value <= end : value < end);
  }
}
