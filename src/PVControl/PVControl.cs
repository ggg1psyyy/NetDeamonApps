using NetDaemon.Client;
using NetDaemon.Extensions.MqttEntityManager;
using NetDaemon.HassModel.Entities;
using NetDeamon.apps;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reactive.Concurrency;
using System.Threading;
using System.Threading.Tasks;
using LinqToDB;
using NetDaemon.HassModel;
using PVControl;
using System;
using Microsoft.Extensions.Logging;
using NetDaemon.AppModel;
using NetDaemon.Extensions.Scheduler;
using static NetDeamon.apps.PVControl.PVControlCommon;

namespace NetDeamon.apps.PVControl
{
  [NetDaemonApp]
#if DEBUG
  [Focus]
#endif
  /// <summary>The PVControl app: registers the HA entities, reacts to user changes and runs the 15-s cycle (simulation, inverter command, loads, sensors).</summary>
  public class PVControl : IAsyncInitializable
  {
    // nullforgiving "!" used to supress CS8618 for now
    private HouseEnergy _house = null!;
    #region Created Entities
    private Entity _modeEntity = null!;
    private Entity _battChargeEnabledEntity = null!;
    private Entity _exportAllowedEntity = null!;
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
    private Entity _info_SoCSnapshotEntity = null!;
    private Entity _info_PredictedChargeEntity = null!;
    private Entity _info_PredictedDischargeEntity = null!;
    private Entity _overrideModeEntity = null!;
    private Entity _currentImportPriceBruttoEntity = null!;
    private Entity _currentExportPriceBruttoEntity = null!;
    private Entity _activeNetworkPricePeriodEntity = null!;
    private Entity _sumImportCostBruttoEntity = null!;
    private Entity _sumImportCostEnergyOnlyEntity = null!;
    private Entity _sumImportCostNetworkOnlyEntity = null!;
    private Entity _sumExportEarningsBruttoEntity = null!;
    private Entity _sumImportExportNetCostEntity = null!;
    private Entity _batteryAvgCostPerKwhEntity = null!;
    private Entity _bestExportPriceEntity = null!;
    private Entity _bestImportPriceEntity = null!;
    private Entity _enableOpportunisticExport = null!;
    #endregion
    private String _energyLogDBConnectionString = "Data Source=";
    private bool _logEnergy = false;
    private DateTime _nextQuarterHour = DateTime.Now.GetNextQuarterHour();
    private Costs _lastCostsEntry = new();
    private readonly SemaphoreSlim _schedLock = new(1, 1);
    private bool _schedPendingRerun = false;
    /// <summary>Sets up the shared context (<see cref="PVControlCommon"/>).</summary>
    /// <param name="ha">HA context.</param>
    /// <param name="entityManager">MQTT entity manager.</param>
    /// <param name="config">App configuration (PVControl.yaml).</param>
    /// <param name="scheduler">Scheduler for the cycle.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="apiManager">HA REST API.</param>
    public PVControl(IHaContext ha, IMqttEntityManager entityManager, IAppConfig<PVConfig> config, IScheduler scheduler, ILogger<PVControl> logger, IHomeAssistantApiManager apiManager)
    {
      CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
      PVCCInstance.Initialize(ha, entityManager, logger, config, (NetDaemon.Extensions.Scheduler.DisposableScheduler)scheduler, apiManager);
      if (string.IsNullOrWhiteSpace(PVCC_Config.DBLocation))
        PVCC_Config.DBLocation = PVConfig.DefaultDBLocation;
      
      PVCC_Logger.LogInformation("DB Location: {loc}", PVCC_Config.DBFullLocation);

      if (!CheckConfiguration())
        throw new Exception("Error initializing configuration");

      PVCC_Logger.LogInformation("Finished PVControl constructor");
    }
    async Task IAsyncInitializable.InitializeAsync(CancellationToken cancellationToken)
    {
      // Converter often needs some time after restart of HA to give values. Everything
      // downstream depends on a real BatterySoC reading, so we wait for it here rather than in
      // the constructor — NetDaemon loads apps one at a time and awaits each one's
      // InitializeAsync before starting the next, so waiting here (instead of blocking a raw
      // thread with Thread.Sleep in the constructor) at least logs progress and stays cancellable.
      int waitSeconds = 0;
      while (!PVCC_Config.BatterySoCEntity.TryGetStateValue<int>(out _))
      {
        if (waitSeconds % 30 == 0)
          PVCC_Logger.LogWarning("Waiting for {entity} to become available ({seconds}s so far)",
            PVCC_Config.BatterySoCEntity.EntityId, waitSeconds);
        await Task.Delay(1000, cancellationToken);
        waitSeconds++;
      }

      // Entities of earlier versions (the EV's own mode/target/charge_now, heavy loads, the inverter command
      // sensor — now part of sensor.pv_control_mode) are removed; once they're gone from HA this is a no-op.
      foreach (var id in new[] {
        "select.pv_control_ev_charging_mode",
        "number.pv_control_ev_target_soc",
        "binary_sensor.pv_control_ev_charge_now",
        "sensor.pv_control_run_heavyloads_now",
        "sensor.pv_control_inverter_command",
      })
      {
        if (new Entity(PVCC_HaContext, id).State != null)
          await PVCC_EntityManager.RemoveAsync(id);
      }
      _house = new HouseEnergy();
      await _house.SeedSoCHistoryAsync(cancellationToken);
      await RegisterControlSensors(false);
      #region Load settings from HA if available

      _house.Costs.SumExportEarningsEntity = _sumExportEarningsBruttoEntity;
      _house.Costs.SumImportCostBruttoEntity = _sumImportCostBruttoEntity;
      _house.Costs.SumImportCostEnergyOnlyEntity = _sumImportCostEnergyOnlyEntity;
      _house.Costs.SumImportCostNetworkOnlyEntity = _sumImportCostNetworkOnlyEntity;
      _house.Costs.SumImportExportNetCostEntity = _sumImportExportNetCostEntity;
      _house.Costs.BatteryAvgCostEntity = _batteryAvgCostPerKwhEntity;

      if (_forceChargeMaxPriceEntity.TryGetStateValue(out float maxPrice))
        _house.Prices.ForceChargeMaxPrice = maxPrice;
      if (_forceChargeTargetSoCEntity.TryGetStateValue(out int targetSoC))
        _house.ForceChargeTargetSoC = targetSoC;
      if (_forceChargeEntity.TryGetStateValue(out bool forceCharge))
        _house.EnableCheapForceCharge = forceCharge;
      if (_overrideModeEntity.TryGetStateValue(out InverterModes mode))
        _house.OverrideMode = mode;
      if (_prefBatterySoCEntity.TryGetStateValue(out int prefSoC))
        _house.Battery.PreferredMinBatterySoC = prefSoC;
      if (_enforcePreferredSocEntity.TryGetStateValue(out bool enforcePrefSoC))
        _house.Battery.EnforcePreferredSoC = enforcePrefSoC;
      if (_enableOpportunisticExport.TryGetStateValue(out bool enableOpp))
        _house.OpportunisticDischarge = enableOpp;

      #endregion
      #region Cost Logging DB
      // only enable if DB location is set
      if (!string.IsNullOrWhiteSpace(PVCC_Config.EnergyCostDBLocation))
      {
        _energyLogDBConnectionString += PVCC_Config.EnergyCostDBFullLocation;
        PVCC_Logger.LogInformation("Energy Log DB Location: {loc} - Energy logging enabled", PVCC_Config.EnergyCostDBFullLocation);
        await using (var db = new EnergyCostDb(new DataOptions().UseSQLite(_energyLogDBConnectionString)))
        {
          db.CreateTable<Costs>(tableOptions: TableOptions.CheckExistence);
          _lastCostsEntry = db.CostEntries.OrderByDescending(c => c.Timestamp).FirstOrDefault()!;
          if (_lastCostsEntry is null)
          {
            _lastCostsEntry = new Costs
            {
              Timestamp = DateTime.Now,
              GridExport = 0,
              GridImport = 0,
              GridExportTotal = PVCC_Config.TotalExportEnergyEntity.TryGetStateValue(out float export) ? export : 0.0f,
              GridImportTotal = (float)((ReadImportCounters().FirstOrDefault(v => v is not null) ?? 0) * 1000), // the cost log is in Wh
              BasePrice = 0,
              ExportPriceProvider = 0,
              ExportPriceTotal = 0,
              ImportPriceProvider = 0,
              ImportPriceTotal = 0,
            };
            db.Insert(_lastCostsEntry);
            PVCC_Logger.LogInformation("No existing entries found in DB, creating new entry with timestamp {ts}", _lastCostsEntry.Timestamp);
          }
          else
          {
            PVCC_Logger.LogInformation("Last timestamp found: {ts}, continuing from there", _lastCostsEntry.Timestamp);
          }
        }
        _logEnergy = true;
      }
      #endregion
      (await PVCC_EntityManager.PrepareCommandSubscriptionAsync(_prefBatterySoCEntity.EntityId).ConfigureAwait(false)).SubscribeAsync(async state => await UserStateChanged(_prefBatterySoCEntity, state));
      (await PVCC_EntityManager.PrepareCommandSubscriptionAsync(_forceChargeMaxPriceEntity.EntityId).ConfigureAwait(false)).SubscribeAsync(async state => await UserStateChanged(_forceChargeMaxPriceEntity, state));
      (await PVCC_EntityManager.PrepareCommandSubscriptionAsync(_forceChargeTargetSoCEntity.EntityId).ConfigureAwait(false)).SubscribeAsync(async state => await UserStateChanged(_forceChargeTargetSoCEntity, state));
      (await PVCC_EntityManager.PrepareCommandSubscriptionAsync(_enforcePreferredSocEntity.EntityId).ConfigureAwait(false)).SubscribeAsync(async state => await UserStateChanged(_enforcePreferredSocEntity, state));
      (await PVCC_EntityManager.PrepareCommandSubscriptionAsync(_forceChargeEntity.EntityId).ConfigureAwait(false)).SubscribeAsync(async state => await UserStateChanged(_forceChargeEntity, state));
      (await PVCC_EntityManager.PrepareCommandSubscriptionAsync(_overrideModeEntity.EntityId).ConfigureAwait(false)).SubscribeAsync(async state => await UserStateChanged(_overrideModeEntity, state));
      (await PVCC_EntityManager.PrepareCommandSubscriptionAsync(_enableOpportunisticExport.EntityId).ConfigureAwait(false)).SubscribeAsync(async state => await UserStateChanged(_enableOpportunisticExport, state));

      PVCC_Logger.LogInformation("Setting up command subscriptions for {count} schedulable loads", _house.SchedulableLoads.Count);
      foreach (var load in _house.SchedulableLoads)
      {
        if (load.ModeEntity is not null)
        {
          PVCC_Logger.LogInformation("Subscribing to mode commands for {load} ({id})", load.Config.Name, load.ModeEntity.EntityId);
          (await PVCC_EntityManager.PrepareCommandSubscriptionAsync(load.ModeEntity.EntityId).ConfigureAwait(false))
            .SubscribeAsync(async state => await UserStateChanged(load.ModeEntity, state));
        }
        else
        {
          PVCC_Logger.LogInformation("Load {load}: ModeEntity is null (static mode from config)", load.Config.Name);
        }

        if (load.TargetLevelEntity is not null)
        {
          PVCC_Logger.LogInformation("Subscribing to target level commands for {load} ({id})", load.Config.Name, load.TargetLevelEntity.EntityId);
          (await PVCC_EntityManager.PrepareCommandSubscriptionAsync(load.TargetLevelEntity.EntityId)
              .ConfigureAwait(false))
            .SubscribeAsync(async state => await UserStateChanged(load.TargetLevelEntity, state));
        }
        else
        {
          PVCC_Logger.LogInformation("Load {load}: TargetLevelEntity is null (static target from config)", load.Config.Name);
        }

        if (load.ReadyByEntity is not null)
          (await PVCC_EntityManager.PrepareCommandSubscriptionAsync(load.ReadyByEntity.EntityId).ConfigureAwait(false))
            .SubscribeAsync(async state => await UserStateChanged(load.ReadyByEntity, state));
        if (load.EnforceReadyByEntity is not null)
          (await PVCC_EntityManager.PrepareCommandSubscriptionAsync(load.EnforceReadyByEntity.EntityId).ConfigureAwait(false))
            .SubscribeAsync(async state => await UserStateChanged(load.EnforceReadyByEntity, state));
        if (load.EnergyPerLevelEntity is not null)
          (await PVCC_EntityManager.PrepareCommandSubscriptionAsync(load.EnergyPerLevelEntity.EntityId).ConfigureAwait(false))
            .SubscribeAsync(async state => await UserStateChanged(load.EnergyPerLevelEntity, state));
        if (load.ChargePowerEntity is not null)
          (await PVCC_EntityManager.PrepareCommandSubscriptionAsync(load.ChargePowerEntity.EntityId).ConfigureAwait(false))
            .SubscribeAsync(async state => await UserStateChanged(load.ChargePowerEntity, state));

        // AvailabilityEntity is a plain external HA entity (not an app-owned command entity
        // like ModeEntity/TargetLevelEntity above), so it uses the same StateChanges()
        // subscription pattern as other external sensors — recompute immediately on toggle
        // instead of waiting for the next 15 s cron tick.
        if (load.Config.AvailabilityEntity is not null)
        {
          PVCC_Logger.LogInformation("Subscribing to availability changes for {load} ({id})", load.Config.Name, load.Config.AvailabilityEntity.EntityId);
          load.Config.AvailabilityEntity.StateChanges().SubscribeAsync(async _ =>
          {
            load.RequestReplan("availability changed");
            load.ClearOffLatch();
#if !DEBUG
            await ScheduledOperations();
#endif
          });
        }
      }

      if (_house.WarmWater is { } warmWater)
      {
        foreach (var entity in warmWater.CommandEntities)
          (await PVCC_EntityManager.PrepareCommandSubscriptionAsync(entity.EntityId).ConfigureAwait(false))
            .SubscribeAsync(async state => await UserStateChanged(entity, state));
        await warmWater.RefitAsync(cancellationToken);
      }

#if !DEBUG
      PVCC_Scheduler.ScheduleCron("*/15 * * * * *", async () => await ScheduledOperations(), true);
#endif

#if DEBUG
      var x = _house.ProposedState;
      _house.UpdatePredictions();
#endif
    }
    /// <summary>Applies a change of a PVControl control entity (mode, SoC limits, load settings, …) and requests a replan.</summary>
    /// <param name="entity">The changed entity; null = ignored.</param>
    /// <param name="newState">Its new state.</param>
    private async Task UserStateChanged(Entity? entity, string newState)
    {
      if (entity == null)
        return;
      PVCC_Logger.LogDebug("Entering UserStateChanged - Entity: {ent}, newState: {state}", entity.EntityId, newState);
      // Any user change can affect a load's decision — search all windows again right away.
      _house.RequestReplan($"user changed {entity.EntityId}");
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
          _house.Battery.PreferredMinBatterySoC = value;
        await PVCC_EntityManager.SetStateAsync(entity.EntityId, _house.Battery.PreferredMinBatterySoC.ToString());
      }
      if (entity.EntityId == _enforcePreferredSocEntity.EntityId && entity.State is not null)
      {
        _house.Battery.EnforcePreferredSoC = newState.Equals("on", StringComparison.CurrentCultureIgnoreCase);
        await PVCC_EntityManager.SetStateAsync(entity.EntityId, _house.Battery.EnforcePreferredSoC ? "ON" : "OFF");
      }
      if (entity.EntityId == _forceChargeEntity.EntityId && entity.State is not null)
      {
        _house.EnableCheapForceCharge = newState.Equals("on", StringComparison.CurrentCultureIgnoreCase);
        await PVCC_EntityManager.SetStateAsync(entity.EntityId, _house.EnableCheapForceCharge ? "ON" : "OFF");
      }
      if (entity.EntityId == _enableOpportunisticExport.EntityId && entity.State is not null)
      {
        _house.OpportunisticDischarge = newState.Equals("on", StringComparison.CurrentCultureIgnoreCase);
        await PVCC_EntityManager.SetStateAsync(entity.EntityId, _house.OpportunisticDischarge ? "ON" : "OFF");
      }
      if (entity.EntityId == _forceChargeMaxPriceEntity.EntityId && entity.State is not null)
      {
        if (float.TryParse(newState, System.Globalization.NumberStyles.Any, CultureInfo.InvariantCulture, out float value))
          _house.Prices.ForceChargeMaxPrice = value * _forceChargeMaxPriceEntity.GetUnitMultiplicator();
        await PVCC_EntityManager.SetStateAsync(entity.EntityId, ((float)Math.Round(_house.Prices.ForceChargeMaxPrice / _forceChargeMaxPriceEntity.GetUnitMultiplicator(), 2)).ToString(CultureInfo.InvariantCulture));
      }
      if (entity.EntityId == _forceChargeTargetSoCEntity.EntityId && entity.State is not null)
      {
        if (int.TryParse(newState, out int value))
          _house.ForceChargeTargetSoC = value;
        await PVCC_EntityManager.SetStateAsync(entity.EntityId, _house.ForceChargeTargetSoC.ToString());
      }
      if (_house.WarmWater is not null)
        await _house.WarmWater.HandleCommandAsync(entity, newState);
      // Handle auto-created mode and target-level entities for each schedulable load.
      foreach (var load in _house.SchedulableLoads)
      {
        if (load.ModeEntity is not null && entity.EntityId == load.ModeEntity.EntityId)
        {
          var echoed = Enum.TryParse(newState, ignoreCase: true, out LoadSchedulingMode m)
            ? m.ToString()
            : LoadSchedulingMode.Off.ToString();
          PVCC_Logger.LogInformation("Load mode changed: {load} → {mode}", load.Config.Name, echoed);
          await PVCC_EntityManager.SetStateAsync(entity.EntityId, echoed);
        }
        if (load.TargetLevelEntity is not null && entity.EntityId == load.TargetLevelEntity.EntityId)
        {
          if (float.TryParse(newState, System.Globalization.NumberStyles.Any, CultureInfo.InvariantCulture, out float tl))
          {
            PVCC_Logger.LogInformation("Load target level changed: {load} → {level}", load.Config.Name, tl);
            await PVCC_EntityManager.SetStateAsync(entity.EntityId, tl.ToString(CultureInfo.InvariantCulture));
          }
        }
        if (load.ReadyByEntity is not null && entity.EntityId == load.ReadyByEntity.EntityId)
        {
          // Echo a valid time in HA's HH:mm:ss format; an unparsable value keeps the old one.
          if (Managers.SchedulableLoadRuntime.TryParseReadyBy(newState, out var t))
          {
            var echoed = t.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
            PVCC_Logger.LogInformation("Load ready-by changed: {load} → {time}", load.Config.Name, echoed);
            await PVCC_EntityManager.SetStateAsync(entity.EntityId, echoed);
          }
        }
        // Hand-set learned values: take them as the new starting point for further learning.
        if (load.EnergyPerLevelEntity is not null && entity.EntityId == load.EnergyPerLevelEntity.EntityId
            && float.TryParse(newState, NumberStyles.Any, CultureInfo.InvariantCulture, out float perLevel) && perLevel > 0)
        {
          load.EnergyPerLevelKwh = perLevel;
          load.EnergyLearner.Reset();
          await PVCC_EntityManager.SetStateAsync(entity.EntityId, perLevel.ToString("F3", CultureInfo.InvariantCulture));
        }
        if (load.ChargePowerEntity is not null && entity.EntityId == load.ChargePowerEntity.EntityId
            && float.TryParse(newState, NumberStyles.Any, CultureInfo.InvariantCulture, out float power) && power > 0)
        {
          load.LearnedPower = Math.Round(power);
          await PVCC_EntityManager.SetStateAsync(entity.EntityId, load.LearnedPowerW.ToString(CultureInfo.InvariantCulture));
        }
        if (load.EnforceReadyByEntity is not null && entity.EntityId == load.EnforceReadyByEntity.EntityId)
        {
          bool on = newState.Equals("on", StringComparison.OrdinalIgnoreCase);
          PVCC_Logger.LogInformation("Load enforce ready-by changed: {load} → {on}", load.Config.Name, on);
          await PVCC_EntityManager.SetStateAsync(entity.EntityId, on ? "ON" : "OFF");
        }
      }
#if !DEBUG
      await ScheduledOperations();
#endif
    }
    /// <summary>Runs one cycle (<see cref="ScheduledOperationsCore"/>) and publishes the health; a cycle requested while one runs follows right after.</summary>
    private async Task ScheduledOperations()
    {
      if (!await _schedLock.WaitAsync(0))
      {
        _schedPendingRerun = true;
        return;
      }
      try
      {
        try
        {
          await ScheduledOperationsCore();
          _cycleFailures = 0;
        }
        catch (Exception ex)
        {
          _cycleFailures++;
          _lastCycleError = ex.Message;
          PVCC_Logger.LogError(ex, "ScheduledOperations threw an exception");
        }
        try
        {
          await PublishHealthAsync(DateTime.Now);
        }
        catch (Exception ex)
        {
          PVCC_Logger.LogError(ex, "Publishing the health status failed");
        }
      }
      finally
      {
        _schedLock.Release();
      }
      if (_schedPendingRerun)
      {
        _schedPendingRerun = false;
        await ScheduledOperations();
      }
    }
    private bool _socUnavailableLogged;

    #region Price publication
    private Entity? _pricePublicationEntity;
    private readonly PricePublicationTracker _pricePublication = new();
    /// <summary>The same per price source (entity id): which provider delivers tomorrow first — HA's recorder doesn't keep their price lists.</summary>
    private readonly Dictionary<string, PricePublicationTracker> _sourcePublication = new();
    private PricePublicationTracker SourceTracker(string entityId) =>
      _sourcePublication.TryGetValue(entityId, out var t) ? t : _sourcePublication[entityId] = new();
    private DateTime _pricePublicationPublished;

    /// <summary>Restores the recorded price publication times from the sensor's "days" attribute after a restart.</summary>
    private void RestorePricePublications()
    {
      try
      {
        if (_pricePublicationEntity is null || !_pricePublicationEntity.TryGetJsonAttribute("days", out System.Text.Json.JsonElement days)
            || days.ValueKind != System.Text.Json.JsonValueKind.Array)
          return;
        _pricePublication.Restore(days.EnumerateArray().Select(d => new PricePublication(
          DateTime.Parse(d.GetProperty("day").GetString()!, CultureInfo.InvariantCulture),
          DateTime.Parse(d.GetProperty("published_at").GetString()!, CultureInfo.InvariantCulture))));
        foreach (var d in days.EnumerateArray())
          if (d.TryGetProperty("sources", out var sources) && sources.ValueKind == System.Text.Json.JsonValueKind.Object)
            foreach (var source in sources.EnumerateObject())
              SourceTracker(source.Name).Restore([new PricePublication(
                DateTime.Parse(d.GetProperty("day").GetString()!, CultureInfo.InvariantCulture),
                DateTime.Parse(source.Value.GetString()!, CultureInfo.InvariantCulture))]);
      }
      catch (Exception ex)
      {
        PVCC_Logger.LogWarning(ex, "Price publication: previous days not restored");
      }
    }

    /// <summary>Publishes sensor.pv_control_price_publication: today's publication time (or "pending"), the last 14 days, the end of the published prices.</summary>
    /// <param name="now">Current time.</param>
    private async Task PublishPricePublicationAsync(DateTime now)
    {
      if (_pricePublicationEntity is null) return;
      var until = _house.Prices.PriceListImport.End;
      bool changed = _pricePublication.Observe(now, until);
      if (changed)
        PVCC_Logger.LogInformation("Tomorrow's prices published (in PVControl's price list at {Time:HH:mm:ss})", now);
      foreach (var source in PVCC_Config.CurrentImportPriceEntities)
        if (SourceTracker(source.EntityId).Observe(now, PriceManager.DataEnd(source)))
        {
          changed = true;
          PVCC_Logger.LogInformation("Tomorrow's prices from {Source} at {Time:HH:mm:ss}", source.EntityId, now);
        }
      if (!changed && now - _pricePublicationPublished < TimeSpan.FromMinutes(5))
        return;
      _pricePublicationPublished = now;
      var today = _pricePublication.PublishedToday(now);
      await PVCC_EntityManager.SetStateAsync(_pricePublicationEntity.EntityId,
        today?.ToString("HH:mm") ?? (until >= now.Date.AddDays(2) ? "published" : "pending"));
      await PVCC_EntityManager.SetAttributesAsync(_pricePublicationEntity.EntityId, new
      {
        prices_until = until?.ToISO8601(),
        days = _pricePublication.Days.Select(d => new
        {
          day = d.Day.ToString("yyyy-MM-dd"),
          published_at = d.PublishedAt.ToISO8601(),
          // When each source first covered tomorrow (only sources seen without it that day).
          sources = _sourcePublication
            .Where(s => s.Value.Days.Any(x => x.Day == d.Day))
            .ToDictionary(s => s.Key, s => s.Value.Days.First(x => x.Day == d.Day).PublishedAt.ToISO8601()),
        }).ToList(),
      });
    }
    #endregion

    #region Load forecast (weather model, shadow comparison)
    private Entity? _loadForecastEntity;
    private readonly LoadModel.NightForecastTracker _nightTracker = new();
    private DateTime _loadForecastPublished, _lastNightEvaluation;

    /// <summary>The scored nights survive a restart in the sensor's "nights" attribute.</summary>
    private void RestoreNights()
    {
      try
      {
        if (_loadForecastEntity is null || !_loadForecastEntity.TryGetJsonAttribute("nights", out System.Text.Json.JsonElement nights)
            || nights.ValueKind != System.Text.Json.JsonValueKind.Array)
          return;
        _nightTracker.Restore(nights.EnumerateArray().Select(n => new LoadModel.NightResult(
          DateTime.Parse(n.GetProperty("night").GetString()!, CultureInfo.InvariantCulture),
          n.GetProperty("model_kwh").GetDouble() * 1000, n.GetProperty("average_kwh").GetDouble() * 1000,
          n.TryGetProperty("actual_kwh", out var a) && a.ValueKind == System.Text.Json.JsonValueKind.Number ? a.GetDouble() * 1000 : null)));
      }
      catch (Exception ex)
      {
        PVCC_Logger.LogWarning(ex, "Load forecast: previous night results not restored");
      }
    }

    /// <summary>Publishes sensor.pv_control_load_forecast: tonight's (22:00–08:00) weather-model load next to the average, the heat pump part, the next 24 h and the scored nights.</summary>
    /// <param name="now">Current time.</param>
    private async Task PublishLoadForecastAsync(DateTime now)
    {
      if (_loadForecastEntity is null || _house.WeatherLoad is not { } weatherLoad)
        return;
      var night = LoadModel.NightForecastTracker.NightOf(now);
      double model = LoadModel.NightForecastTracker.NightSum(weatherLoad.TodayAndTomorrow, night);
      double average = LoadModel.NightForecastTracker.NightSum(_house.AverageLoad.TodayAndTomorrow, night);
      bool changed = weatherLoad.Source == "model" && _nightTracker.TakeSnapshot(now, model, average);
      if (now.Hour >= 8 && now - _lastNightEvaluation >= TimeSpan.FromMinutes(15)
          && _nightTracker.Nights.Any(n => n.Actual is null && now >= n.Night.AddDays(1).AddHours(8)))
      {
        _lastNightEvaluation = now;
        changed |= _nightTracker.Evaluate(now, n => weatherLoad.ActualNightSum(n, now));
      }
      if (!changed && now - _loadForecastPublished < TimeSpan.FromMinutes(5))
        return;
      _loadForecastPublished = now;

      var (nightFrom, nightTo) = LoadModel.NightForecastTracker.NightWindow(night);
      var nightHours = weatherLoad.Hours.Where(h => TimeRange.Contains(nightFrom, nightTo, h.Hour)).ToList();
      var averageSlots = _house.AverageLoad.TodayAndTomorrow;
      var (maeModel, maeAverage, scored) = _nightTracker.MeanAbsoluteError();
      static double Kwh(double wh) => Math.Round(wh / 1000, 2);
      await PVCC_EntityManager.SetStateAsync(_loadForecastEntity.EntityId, Kwh(model).ToString(CultureInfo.InvariantCulture));
      await PVCC_EntityManager.SetAttributesAsync(_loadForecastEntity.EntityId, new
      {
        night = night.ToString("yyyy-MM-dd"),
        source = weatherLoad.Source,
        used_by_pvcontrol = ReferenceEquals(_house.Prediction_Load, weatherLoad),
        average_kwh = Kwh(average),
        night_base_kwh = Kwh(nightHours.Sum(h => h.Base)),
        night_heat_pump_kwh = Kwh(nightHours.Sum(h => h.HeatPump)),
        night_temp24 = nightHours.Count > 0 ? Math.Round(nightHours.Average(h => h.Temp24), 1) : (double?)null,
        heating_on = weatherLoad.HeatingOn,
        heat_pump_scale = weatherLoad.Model is { } m ? Math.Round(m.HeatDemand.Scale, 2) : (double?)null,
        weather_forecast = _house.Weather?.LastUpdate?.ToISO8601(),
        next_24h = weatherLoad.Hours.Where(h => h.Hour >= now.AddHours(-1) && h.Hour < now.AddHours(24)).Select(h => new
        {
          hour = h.Hour.ToString("HH:mm"),
          model_wh = (int)Math.Round(h.Base + h.HeatPump),
          average_wh = Enumerable.Range(0, 4).Sum(q => averageSlots.GetValueOrDefault(h.Hour.AddMinutes(15 * q))),
          heat_pump_wh = (int)Math.Round(h.HeatPump),
          temp24 = Math.Round(h.Temp24, 1),
        }).ToList(),
        scored_nights = scored,
        model_mae_kwh = Kwh(maeModel),
        average_mae_kwh = Kwh(maeAverage),
        nights = _nightTracker.Nights.Select(n => new
        {
          night = n.Night.ToString("yyyy-MM-dd"),
          model_kwh = Kwh(n.Model),
          average_kwh = Kwh(n.Average),
          actual_kwh = n.Actual is { } a ? Kwh(a) : (double?)null,
        }).ToList(),
      });
    }
    #endregion

    #region Import cap
    private Entity? _importQuarterEntity, _importMonthPeakEntity;
    private ImportCap.QuarterImport? _quarterImportField;
    private ImportCap.QuarterImport _quarterImport => _quarterImportField ??= new(PVCC_Config.TotalImportEnergyEntities.Count);
    private readonly ImportCap.MonthPeakTracker _monthPeak = new();
    private ImportCap.GuardDecision _importCap;
    private double? _chargeLimitA;
    private InverterCommand? _inverterCommand;
    private int _restoredCommandSeq;
    private bool _chargeThrottled;
    private DateTime _inverterCommandIssued;
    /// <summary>The planned charge's target SoC as shown (<see cref="InverterCommand.SteadyTarget"/>).</summary>
    private (int TargetSocPercent, DateTime End)? _shownChargeTarget;
    private Entity? _vppCurveEntity;
    private DateTime _vppCurvePublished;
    private DateTime? _inverterOutOfSyncSince;
    private DateTime _importSensorsPublished;
    private ImportCap.QuarterResult? _lastOverCap;
    private double?[]? _costImportSince;
    /// <summary>Mode published last (and since when): a started grid charge runs at least
    /// <see cref="InverterCommand.MinChargeRun"/>.</summary>
    private InverterState _publishedState = new(InverterModes.normal);
    private DateTime _publishedStateSince;

    /// <summary>The import counters in priority order (kWh); null where one has no usable reading.</summary>
    private static List<double?> ReadImportCounters() => PVCC_Config.TotalImportEnergyEntities
      .Select(e => e.TryGetStateValue(out float wh) && wh > 0 ? wh / 1000.0 : (double?)null).ToList(); // TryGetStateValue already gives Wh

    /// <summary>
    /// The inverter command for sensor.pv_control_mode's attributes: the battery power (W, + charges,
    /// − discharges), whether a grid charge runs on the full-power path (<c>bulk</c>), a version <c>seq</c>
    /// that grows with every change, and the planned charge (energy, target SoC, end). HA applies it
    /// (script.pv_control_apply_mode) and a reconciler keeps the inverter in it. The power is the hardware
    /// maximum, or the import cap's limit while it throttles below what the path charges, at the measured
    /// battery voltage — taken when the command changes, not with every voltage step. A bulk charge carries
    /// the limit as its charge current (HA writes it before Manual Mode starts); its room comes from the
    /// live guard and, for the charge the plan runs, from the plan's lowest room over the rest of the charge
    /// (<see cref="Simulator.SimulationResult.GridChargeRoomW"/>: a planned EV session included), so it starts
    /// at a current that fits instead of being lowered when the EV comes on.
    /// </summary>
    /// <param name="state">Mode published this cycle (its <see cref="InverterState.Bulk"/> as planned).</param>
    /// <param name="now">Current time.</param>
    /// <param name="charge">The grid charge the plan runs now or next (<see cref="InverterCommand.NextCharge"/>); null = none.</param>
    /// <returns>The command (also kept in <see cref="_inverterCommand"/>, with its charge limit and throttling).</returns>
    private InverterCommand UpdateInverterCommand(InverterState state, DateTime now, PlannedCharge? charge)
    {
      var mode = state.Mode;
      double maxA = PVCC_Config.MaxBatteryChargePower;
      int voltage = PVCC_Config.BatteryVoltageV;
      bool gridCharging = mode is InverterModes.force_charge or InverterModes.force_charge_grid_only;
      double? planRoomA = gridCharging && charge is { } c && c.Start <= now && _house.SimulationState?.GridChargeRoomW(now, c.End) is { } roomW
        ? ImportCap.ImportCapGuard.PlanRoomA(roomW, voltage)
        : null;
      double roomA = Math.Min(_importCap.RoomA, planRoomA ?? double.MaxValue);
      double limitA = ImportCap.ImportCapGuard.NextLimit(_chargeLimitA, roomA, maxA, gridCharging);
      if (limitA != _chargeLimitA && gridCharging)
        PVCC_Logger.LogInformation("Import cap: battery charge limit {Old} → {New:F0} A (room {Room:F1} A{Plan}; {Reason})", _chargeLimitA?.ToString("F0") ?? "-", limitA,
          roomA, planRoomA is { } p ? $", plan {p:F1} A" : "", _importCap.Reason);
      _chargeLimitA = limitA;
      // The cap only counts below what the path charges anyway: VPP by the SoC curve, bulk at the maximum.
      bool forced = _house.OverrideMode == InverterModes.force_charge;
      bool bulk = InverterCommand.BulkFor(mode, state.Bulk, forced);
      int vppW = _house.VppCurve.MaxW(_house.Battery.BatterySoc);
      double pathW = bulk ? maxA * voltage : Math.Min(maxA * voltage, vppW);
      _chargeThrottled = gridCharging && limitA * voltage < pathW;
      var command = InverterCommand.Next(_inverterCommand, mode, _chargeThrottled ? limitA : maxA, bulk, voltage, _restoredCommandSeq);
      if (command != _inverterCommand)
        _inverterCommandIssued = now;
      _inverterCommand = command;
      return command;
    }

    /// <summary>The command attributes of sensor.pv_control_mode (no side effects: <see cref="UpdateInverterCommand"/> first).</summary>
    /// <param name="command">The command of this cycle.</param>
    /// <param name="charge">The grid charge the plan runs now or next; null = none.</param>
    private Dictionary<string, object?> InverterCommandAttributes(InverterCommand command, PlannedCharge? charge) => new()
    {
      ["battery_power_w"] = command.BatteryPowerW,
      ["bulk"] = command.Bulk,
      ["charge_energy_wh"] = charge?.EnergyWh ?? 0,
      ["charge_target_soc"] = charge?.TargetSocPercent,
      ["charge_start"] = charge?.Start.ToISO8601(),
      ["charge_until"] = charge?.End.ToISO8601(),
      ["vpp_charge_limit_w"] = _house.VppCurve.MaxW(_house.Battery.BatterySoc),
      ["seq"] = command.Seq,
      ["issued"] = _inverterCommandIssued.ToISO8601(),
      ["import_cap_reason"] = _importCap.Reason,
      ["cap_kw"] = PVCC_Config.MaxGridImportW / 1000.0,
      ["allowed_kw"] = Math.Round(_importCap.AllowedW / 1000.0, 2),
      ["extra_loads_paused"] = _importCap.PauseExtraLoads,
      ["headroom_w"] = _importCap.HeadroomW,
      ["voltage_v"] = PVCC_Config.BatteryVoltageV,
      ["battery_current_a"] = command.CurrentA,
      ["max_current_a"] = PVCC_Config.MaxBatteryChargePower,
    };

    /// <summary>The grid charge the plan runs now or next (<see cref="InverterCommand.NextCharge"/>) — the one lookup
    /// behind sensor.pv_control_mode's charge attributes and binary_sensor.pv_control_need_to_charge_from_grid_today.</summary>
    /// <param name="now">Current time.</param>
    private PlannedCharge? PlannedGridCharge(DateTime now) =>
      InverterCommand.NextCharge(_house.SimulationTimeline, now, _house.Battery.BatteryCapacity);

    /// <summary>
    /// Learns the VPP charge curve (<see cref="Managers.VppChargeCurve"/>) from a remote-controlled grid
    /// charge that runs below its command: not bulk, not throttled by the import cap, in sync, ramped up
    /// (a minute after the command), the measured battery power clearly under the command. Published as
    /// sensor.pv_control_vpp_charge_limit once a minute (the buckets persist there).
    /// </summary>
    /// <param name="now">Current time.</param>
    private async Task LearnVppCurveAsync(DateTime now)
    {
      int soc = _house.Battery.BatterySoc;
      if (_inverterCommand is { Mode: InverterModes.force_charge or InverterModes.force_charge_grid_only, Bulk: false } c
          && !_chargeThrottled && InverterInSync && now - _inverterCommandIssued >= TimeSpan.FromMinutes(1) && soc is > 5 and < 97
          && _house.Battery.CurrentAverageBatteryChargeDischargePower is var w && w > 0 && w < c.BatteryPowerW - 300)
        _house.VppCurve.Observe(soc, w);
      if (_vppCurveEntity is null || now - _vppCurvePublished < TimeSpan.FromMinutes(1)) return;
      _vppCurvePublished = now;
      var (at0, slope, learned) = _house.VppCurve.Line;
      await PVCC_EntityManager.SetStateAsync(_vppCurveEntity.EntityId, _house.VppCurve.MaxW(soc).ToString(CultureInfo.InvariantCulture));
      await PVCC_EntityManager.SetAttributesAsync(_vppCurveEntity.EntityId, new
      {
        at_0_pct_w = Math.Round(at0),
        slope_w_per_pct = Math.Round(slope, 1),
        learned,
        at_50_pct_w = _house.VppCurve.MaxW(50),
        buckets = _house.VppCurve.Serialize(),
      });
    }

    /// <summary>Restores the learned VPP charge curve from its sensor after a restart.</summary>
    private void RestoreVppCurve()
    {
      if (_vppCurveEntity is not null && _vppCurveEntity.TryGetJsonAttribute("buckets", out System.Text.Json.JsonElement b)
          && b.ValueKind == System.Text.Json.JsonValueKind.String)
        _house.VppCurve.Restore(b.GetString());
    }

    /// <summary>Restores the command published before a restart (<see cref="InverterCommand.FromPublished"/>): an unchanged
    /// command keeps its version, a changed one counts on from it (the HA side compares it with the version it applied).</summary>
    private void RestoreInverterCommand()
    {
      if (!(_modeEntity.TryGetJsonAttribute("seq", out System.Text.Json.JsonElement seq) && seq.TryGetInt32(out int value)))
        return;
      _restoredCommandSeq = value;
      _inverterCommand = InverterCommand.FromPublished(_modeEntity.State,
        _modeEntity.TryGetJsonAttribute("battery_power_w", out var p) && p.TryGetInt32(out int powerW) ? powerW : null,
        _modeEntity.TryGetJsonAttribute("bulk", out var b) && b.ValueKind is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False ? b.GetBoolean() : null,
        value, PVCC_Config.BatteryVoltageV);
      // The current as published (not re-derived from the power at today's voltage), and as the charge limit of a
      // running grid charge: the first cycle after a restart mustn't raise a limit the cap had lowered.
      if (_inverterCommand is { } restored && _modeEntity.TryGetJsonAttribute("battery_current_a", out var a) && a.TryGetDouble(out double currentA))
        _inverterCommand = restored with { CurrentA = currentA };
      if (_inverterCommand is { Mode: InverterModes.force_charge or InverterModes.force_charge_grid_only, CurrentA: > 0 } charging)
        _chargeLimitA = charging.CurrentA;
      _inverterCommandIssued = _modeEntity.TryGetJsonAttribute("issued", out var issued) && issued.ValueKind == System.Text.Json.JsonValueKind.String
        && DateTimeOffset.TryParse(issued.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var at)
        ? at.LocalDateTime : DateTime.Now;
    }

    /// <summary>Restores the health issues that counted before a restart (<see cref="HealthMonitor.Restore"/>) from the sensor's <c>issues</c>.</summary>
    private void RestoreHealthIssues()
    {
      try
      {
        if (!_healthEntity.TryGetJsonAttribute("issues", out System.Text.Json.JsonElement list) || list.ValueKind != System.Text.Json.JsonValueKind.Array)
          return;
        var issues = list.EnumerateArray()
          .Where(i => i.TryGetProperty("key", out _) && i.TryGetProperty("level", out _) && i.TryGetProperty("since", out _))
          .Select(i => new ActiveIssue(i.GetProperty("key").GetString()!,
            Enum.TryParse(i.GetProperty("level").GetString(), out HealthLevel l) ? l : HealthLevel.degraded,
            i.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "",
            Extensions.FromISO8601(i.GetProperty("since").GetString()!)))
          .ToList();
        _health.Restore(DateTime.Now, issues);
      }
      catch (Exception ex)
      {
        PVCC_Logger.LogWarning(ex, "Health: issues not restored");
      }
    }

    /// <summary>Restores the month's import peak from the sensor after a restart.</summary>
    private void RestoreMonthPeak()
    {
      try
      {
        // kW as stored: the base-unit conversion turned 2.82 kW into 2820, which the plausibility check
        // rejected — every restart started the month over (live 2026-10-27/28: 9.60 kW shown as 0.00).
        if (_importMonthPeakEntity is not null && _importMonthPeakEntity.TryGetStateValue(out float kw, numericalGetBaseValue: false)
            && _importMonthPeakEntity.TryGetJsonAttribute("quarter_start", out System.Text.Json.JsonElement start)
            && start.GetString() is { } text)
          _monthPeak.Restore(new ImportCap.QuarterResult(Extensions.FromISO8601(text), kw));
      }
      catch (Exception ex)
      {
        PVCC_Logger.LogWarning(ex, "Import cap: month peak not restored from its sensor");
      }
      // Second source: the DataLogger's highest 15-min import of the month (survives a lost sensor value).
      try
      {
        var monthStart = new DateTime(DateTime.Now.Year, DateTime.Now.Month, 1);
        using var con = OpenHistoryDb();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "SELECT timestamp, maxquarterimport FROM hourly WHERE maxquarterimport IS NOT NULL AND timestamp >= $from ORDER BY maxquarterimport DESC LIMIT 1";
        cmd.Parameters.AddWithValue("$from", monthStart.ToISO8601());
        using var r = cmd.ExecuteReader();
        if (r.Read())
          _monthPeak.Restore(ImportCap.MonthPeakTracker.FromHourlyRow(Extensions.FromISO8601(r.GetString(0)), r.GetDouble(1)));
      }
      catch (Exception ex)
      {
        PVCC_Logger.LogWarning(ex, "Import cap: month peak not restored from the DataLogger");
      }
    }

    /// <summary>
    /// One import cap cycle: the counters, the finished quarter and the month's peak, then the guard's
    /// decision (<see cref="ImportCap.ImportCapGuard"/>) and its sensors. The battery power it allows goes
    /// out with the inverter command (<see cref="InverterCommandAttributes"/>).
    /// </summary>
    /// <param name="now">Current time.</param>
    private async Task ImportCapCycleAsync(DateTime now)
    {
      if (PVCC_Config.TotalImportEnergyEntities.Count == 0)
      {
        // Without a counter the guard is blind: no limit (not the default decision's 0 A); the health check reports it.
        _importCap = new ImportCap.GuardDecision(PVCC_Config.MaxBatteryChargePower, false, int.MaxValue, int.MaxValue, 0, "no import counter configured");
        return;
      }
      if (_quarterImport.Update(now, ReadImportCounters()) is { } finished)
      {
        int capW = PVCC_Config.MaxGridImportW;
        if (capW > 0 && finished.AvgKw * 1000 > capW)
        {
          _lastOverCap = finished;
          PVCC_Logger.LogWarning("Import cap: quarter {Start:HH:mm} imported {Kw:F2} kW (cap {Cap:F1} kW)", finished.Start, finished.AvgKw, capW / 1000.0);
        }
        if (_monthPeak.Observe(finished))
          PVCC_Logger.LogInformation("Import peak this month: {Kw:F2} kW ({Start:dd.MM HH:mm})", finished.AvgKw, finished.Start);
      }

      double maxA = PVCC_Config.MaxBatteryChargePower;
      var running = _house.SchedulableLoads.Where(l => l.ChargeNow).ToList();
      _importCap = PVCC_Config.MaxGridImportW <= 0
        ? new ImportCap.GuardDecision(maxA, false, int.MaxValue, int.MaxValue, 0, "import cap off")
        : ImportCap.ImportCapGuard.Decide(new ImportCap.GuardInput(
            CapW: PVCC_Config.MaxGridImportW,
            ElapsedSeconds: _quarterImport.ElapsedSeconds(now),
            ImportedWh: _quarterImport.ImportedWh,
            GridImportW: PVCC_Config.CurrentGridPowerEntity.TryGetStateValue(out int grid) ? Math.Max(0, -grid) : 0,
            HouseLoadW: PVCC_Config.CurrentHouseLoadEntity.TryGetStateValue(out int house) ? house : 0,
            PvW: PVCC_Config.CurrentPVPowerEntity.TryGetStateValue(out int pv) ? pv : 0,
            ExtraLoadsW: _house.SchedulableLoads.Sum(l => l.PowerAverage?.GetAverage() ?? 0),
            ExtraLoadsOn: running.Count > 0,
            ExtraLoadsNominalW: running.Sum(l => l.LearnedPowerW),
            ExtraLoadsPaused: _importCap.PauseExtraLoads,
            MaxChargeA: maxA,
            VoltageV: PVCC_Config.BatteryVoltageV,
            ReactionSeconds: PVCC_Config.ImportCapReactionSeconds));

      if (now - _importSensorsPublished < TimeSpan.FromMinutes(1))
        return;
      _importSensorsPublished = now;
      if (_importQuarterEntity is not null)
      {
        int active = _quarterImport.Counter.ActiveIndex;
        await PVCC_EntityManager.SetStateAsync(_importQuarterEntity.EntityId, _importCap.ProjectedKw.ToString("F2", CultureInfo.InvariantCulture));
        await PVCC_EntityManager.SetAttributesAsync(_importQuarterEntity.EntityId, new
        {
          quarter_start = _quarterImport.QuarterStart?.ToISO8601(),
          imported_kwh = _quarterImport.ImportedWh is { } wh ? Math.Round(wh / 1000, 3) : (double?)null,
          complete = _quarterImport.Complete,
          counter = active >= 0 ? PVCC_Config.TotalImportEnergyEntities[active].EntityId : "none",
        });
      }
      if (_importMonthPeakEntity is not null && _monthPeak.PeakIn(now) is { } peak)
      {
        await PVCC_EntityManager.SetStateAsync(_importMonthPeakEntity.EntityId, peak.AvgKw.ToString("F2", CultureInfo.InvariantCulture));
        await PVCC_EntityManager.SetAttributesAsync(_importMonthPeakEntity.EntityId, new { quarter_start = peak.Start.ToISO8601() });
      }
    }
    #endregion


    #region Health
    private Entity _healthEntity = null!;
    // A problem counts after 30 s (two cycles — a single 15-s sensor blip doesn't), and keeps
    // counting until it has been gone for 1 min.
    private readonly HealthMonitor _health = new(TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1));
    private HealthLevel? _lastHealthLevel;
    private int _cycleFailures;
    private string? _lastCycleError;
    private DateTime _lastHourlyRowCheck;
    private DateTime? _lastHourlyRow;

    static bool Unavailable(Entity? e) => e is null || e.State is null or "unknown" or "unavailable";

    /// <summary>Whether the inverter runs the published command (HA's feedback, <see cref="PVConfig.InverterSyncEntity"/>); true if not checked.</summary>
    private static bool InverterInSync => PVCC_Config.InverterSyncEntity is not { } sync || sync.State == "on";

    /// <summary>Opens the DataLogger's history DB (the load prediction's source) read-only.</summary>
    private static Microsoft.Data.Sqlite.SqliteConnection OpenHistoryDb()
    {
      var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={PVCC_Config.DBFullLocation};Mode=ReadOnly;Pooling=False");
      connection.Open();
      return connection;
    }

    /// <summary>Runs all <see cref="HealthChecks"/> on the values of this cycle.</summary>
    /// <param name="now">Current time.</param>
    /// <returns>The current issues.</returns>
    private IEnumerable<HealthIssue> CollectHealthIssues(DateTime now)
    {
      var battery = _house.Battery;
      bool socUnavailable = battery.IsSoCUnavailable(now);
      bool socReadingNow = PVCC_Config.BatterySoCEntity.TryGetStateValue(out int _);
      var issues = HealthChecks.BatterySoC(socReadingNow, socUnavailable, battery.LastGoodSoC).ToList();

      issues.AddRange(HealthChecks.InverterData(new[] { PVCC_Config.InverterStatusEntity, PVCC_Config.CurrentBatteryPowerEntity,
          PVCC_Config.CurrentGridPowerEntity, PVCC_Config.CurrentHouseLoadEntity, PVCC_Config.CurrentPVPowerEntity }
        .Where(e => e is not null && Unavailable(e)).Select(e => e!.EntityId).ToList()));

      var prices = _house.Prices.PriceListImport;
      issues.AddRange(HealthChecks.ImportPrices(now,
        PVCC_Config.CurrentImportPriceEntities.Where(e => PriceManager.DataEnd(e) is null).Select(e => e.EntityId).ToList(),
        PVCC_Config.CurrentImportPriceEntity?.EntityId, prices.Count > 0 ? prices.Max(p => p.EndTime) : null));

      issues.AddRange(HealthChecks.Forecasts(_house.Prediction_Load.DataOK, _house.Prediction_PV.DataOK,
        PVCC_Config.ForecastPVEnergyTodayEntities.Concat(PVCC_Config.ForecastPVEnergyTomorrowEntities)
          .Where(e => !e.TryGetStateValue(out float _)).Select(e => e.EntityId).ToList()));

      issues.AddRange(HealthChecks.Cycle(_cycleFailures, _lastCycleError));
      var sync = PVCC_Config.InverterSyncEntity;
      _inverterOutOfSyncSince = InverterInSync ? null : _inverterOutOfSyncSince ?? now;
      bool blocked = sync is not null && sync.TryGetJsonAttribute("blocked", out System.Text.Json.JsonElement b) && b.ValueKind == System.Text.Json.JsonValueKind.True;
      issues.AddRange(HealthChecks.InverterSync(sync is not null, _inverterOutOfSyncSince, blocked, now));
      issues.AddRange(HealthChecks.ImportCap(PVCC_Config.MaxGridImportW, _quarterImport.Counter.ActiveIndex,
        PVCC_Config.TotalImportEnergyEntities.Select(e => e.EntityId).ToList(), _lastOverCap, now));
      issues.AddRange(HealthChecks.WarmWater(_house.WarmWater is not null, _house.WarmWater?.HeatPumpAvailable ?? true));
      issues.AddRange(HealthChecks.WeatherForecast(_house.Weather is not null, _house.Weather?.LastUpdate, now, LoadModel.WeatherForecast.MaxAge));
      issues.AddRange(HealthChecks.LoadForecast(_house.WeatherLoad is not null && ReferenceEquals(_house.Prediction_Load, _house.WeatherLoad), _house.WeatherLoad?.Source));

      if (now - _lastHourlyRowCheck >= TimeSpan.FromMinutes(5))
      {
        _lastHourlyRowCheck = now;
        _lastHourlyRow = ReadLastHourlyRow();
      }
      issues.AddRange(HealthChecks.DataLogger(now, _lastHourlyRow));
      return issues;
    }

    /// <summary>Timestamp of DataLogger's newest hourly row (the DB the load prediction reads).</summary>
    private static DateTime? ReadLastHourlyRow()
    {
      try
      {
        using var connection = OpenHistoryDb();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT max(timestamp) FROM hourly";
        return command.ExecuteScalar() is string text
          ? Extensions.FromISO8601(text)
          : null;
      }
      catch (Exception ex)
      {
        PVCC_Logger.LogWarning(ex, "Health: reading the energy history DB failed");
        return null;
      }
    }

    /// <summary>Publishes sensor.pv_control_health: ok/degraded/error, the issues and the last cycle time.</summary>
    /// <param name="now">Current time.</param>
    private async Task PublishHealthAsync(DateTime now)
    {
      var status = _health.Evaluate(now, CollectHealthIssues(now));
      if (status.Level != _lastHealthLevel)
      {
        PVCC_Logger.Log(status.Level == HealthLevel.ok ? LogLevel.Information : LogLevel.Warning,
          "Health {Level}: {Issues}", status.Level, string.Join("; ", status.Issues.Select(i => i.Message)));
        _lastHealthLevel = status.Level;
      }
      // Attributes before the level (two MQTT messages): an automation triggered by the level reads the
      // summary right away — published after it, the text was the previous one ("degraded: All checks ok").
      await PVCC_EntityManager.SetAttributesAsync(_healthEntity.EntityId, new
      {
        summary = status.Issues.Count == 0 ? "All checks ok" : string.Join("; ", status.Issues.Select(i => i.Message)),
        issues = status.Issues.Select(i => new { key = i.Key, level = i.Level.ToString(), message = i.Message, since = i.Since.ToISO8601() }).ToList(),
        last_cycle = now.ToISO8601(),
        version = AppVersion.Of(GetType().Assembly),
      });
      await PVCC_EntityManager.SetStateAsync(_healthEntity.EntityId, status.Level.ToString());
    }
    #endregion

    /// <summary>Battery SoC unavailable longer than <see cref="BatteryState.SoCMaxHold"/>: publishes normal mode (battery charge and export on), logged once.</summary>
    /// <param name="now">Current time.</param>
    private async Task PublishSoCUnavailableAsync(DateTime now)
    {
      if (!_socUnavailableLogged)
      {
        PVCC_Logger.LogWarning("Battery SoC unavailable since {Since} — inverter set to normal mode until it's back",
          _house.Battery.LastGoodSoC?.ToString("HH:mm:ss") ?? "start");
        _socUnavailableLogged = true;
      }
      _publishedState = new InverterState(InverterModes.normal);
      var attributes = InverterCommandAttributes(UpdateInverterCommand(_publishedState, now, null), null);
      attributes["next_charge_window_start"] = "none";
      attributes["next_charge_window_end"] = "none";
      attributes["price"] = "0";
      attributes["charge_Reason"] = "SoCUnavailable";
      attributes["soc_unavailable_since"] = _house.Battery.LastGoodSoC?.ToISO8601() ?? "start";
      // Attributes before the state (two MQTT messages): the state change is what HA's automations react to.
      await PVCC_EntityManager.SetAttributesAsync(_modeEntity.EntityId, attributes);
      await PVCC_EntityManager.SetStateAsync(_modeEntity.EntityId, InverterModes.normal.ToString());
      await PVCC_EntityManager.SetStateAsync(_battChargeEnabledEntity.EntityId, "ON");
      await PVCC_EntityManager.SetStateAsync(_exportAllowedEntity.EntityId, "ON");
    }

    /// <summary>One 15-s cycle: weather refresh, simulation, inverter command, load switching and all sensors.</summary>
    private async Task ScheduledOperationsCore()
    {
      PVCC_Logger.LogTrace("Entering Schedule");
      DateTime now = DateTime.Now;
      // Independent of the battery SoC: the cap holds even while PVControl doesn't plan.
      await ImportCapCycleAsync(now);
      // Short SoC dropouts are bridged with the last good value (BatteryState.BatterySoc); after
      // SoCMaxHold nothing is decided from the battery — the inverter runs in its own normal mode.
      if (_house.Battery.IsSoCUnavailable(now))
      {
        await PublishSoCUnavailableAsync(now);
        return;
      }
      if (_socUnavailableLogged)
      {
        PVCC_Logger.LogInformation("Battery SoC available again ({SoC}%), resuming control", _house.Battery.BatterySoc);
        _socUnavailableLogged = false;
      }
      if (_house.Weather is { } weather)
        await weather.RefreshIfDueAsync(now);
      PVCC_Logger.LogTrace("Updating Predictions");
      // Includes the day rollover of the load prediction after midnight (HouseEnergy.RunSimulation).
      _house.UpdatePredictions();
      PVCC_Logger.LogTrace("Finished Updating Predictions");
      await PublishLoadForecastAsync(now);
      await PublishPricePublicationAsync(now);
      // update Grid Values for Inverter Check, as it won't update if it stays the same
      if (PVCC_Config.CurrentGridPowerEntity.TryGetStateValue(out int grid))
        _house.AddAverageGridPowerValue(grid);
      var inverterState = InverterCommand.HoldMinimumRun(_house.ProposedState, _publishedState, _publishedStateSince, now);
      if (inverterState.Mode != _publishedState.Mode)
        _publishedStateSince = now;
      _publishedState = inverterState;
      // The grid charge the plan runs now or next (the running slot counts: it ends after now) — the one
      // lookup behind the mode sensor's charge attributes and the need-to-charge sensor.
      var charge = PlannedGridCharge(now);
      await PublishModeAsync(inverterState, charge, now);
      await PublishLoadsAsync(now);
      await ActAndPublishWarmWaterAsync(now);
      await PublishBatteryAsync(now);
      await PublishNeedToChargeAsync(charge, now);
      await PublishPredictionsAsync(now);
      await PublishPricesAsync(now);
      await LogCostsAsync(now);
      PVCC_Logger.LogTrace("Leave Schedule");
    }

    /// <summary>sensor.pv_control_mode — the decision and the inverter command HA applies (HA/InverterSync.yaml) — and the two switches it carries.</summary>
    /// <param name="inverterState">The state published this cycle.</param>
    /// <param name="charge">The grid charge the plan runs now or next; null = none.</param>
    /// <param name="now">Current time.</param>
    private async Task PublishModeAsync(InverterState inverterState, PlannedCharge? charge, DateTime now)
    {
      var attr_Mode = InverterCommandAttributes(UpdateInverterCommand(inverterState, now, charge), charge);
      _shownChargeTarget = InverterCommand.SteadyTarget(_shownChargeTarget, charge, now);
      attr_Mode["charge_target_soc"] = _shownChargeTarget?.TargetSocPercent;
      attr_Mode["next_charge_window_start"] = charge?.Start.ToISO8601() ?? "none";
      attr_Mode["next_charge_window_end"] = charge?.End.ToISO8601() ?? "none";
      attr_Mode["price"] = (charge is { } c ? _house.Prices.PriceListImport.GetPriceOrMean(c.Start) : 0f).ToString(CultureInfo.InvariantCulture);
      attr_Mode["charge_Reason"] = inverterState.ModeReason.ToString();
      // Attributes before the state (two MQTT messages): the reconciler reacts to the state change and must
      // see the seq/power/bulk that belong to it, not the previous ones.
      await PVCC_EntityManager.SetAttributesAsync(_modeEntity.EntityId, attr_Mode);
      await PVCC_EntityManager.SetStateAsync(_modeEntity.EntityId, inverterState.Mode.ToString());
      await LearnVppCurveAsync(now);
      await PVCC_EntityManager.SetStateAsync(_battChargeEnabledEntity.EntityId, inverterState.BatteryChargeEnable ? "ON" : "OFF");
      await PVCC_EntityManager.SetStateAsync(_exportAllowedEntity.EntityId, inverterState.ExportAllowed ? "ON" : "OFF");
    }

    /// <summary>The schedulable loads' charge_now switches and their plan attributes.</summary>
    /// <param name="now">Current time.</param>
    private async Task PublishLoadsAsync(DateTime now)
    {
      foreach (var load in _house.SchedulableLoads)
      {
        // The import cap pauses a running load on top of its plan (the plan itself stays).
        bool capPaused = load.ChargeNow && _importCap.PauseExtraLoads;
        await PVCC_EntityManager.SetStateAsync(load.ChargeNowEntity.EntityId, load.ChargeNow && !capPaused ? "ON" : "OFF");
        int powerAvgW = load.PowerAverage?.GetAverage() ?? 0;
        int netPvW = _house.CurrentAveragePVPower - Math.Max(0, _house.CurrentAverageHouseLoad - powerAvgW);
        // The whole plan: the current/next session plus any planned follow-ups.
        var allSessions = load.ExtraLoads.Concat(load.PlannedSessions).ToList();
        var chargeTimeline = _house.Prediction_BatterySoC.TodayAndTomorrow
          .Where(s => s.Key >= now)
          .Select(s => new
          {
            datetime = s.Key.ToISO8601(),
            charging = allSessions.Any(e => e.Covers(s.Key)),
          }).ToList();
        // Derived from the actual planned window(s), not TargetLevel — FindLoadWindow's search
        // is often capped short of the full target (PV window, price, SoC gates), so echoing
        // TargetLevel would overstate the real outcome in exactly the cases this is meant to show.
        float plannedWh = (float)allSessions.Sum(e => e.EnergyFromWh(DateTime.Now)); // still to come: a running session from now
        float? chargeEndPredictedSoc = allSessions.Count > 0 && load.EnergyPerLevelKwh > 0
          ? load.CurrentLevel + plannedWh / 1000f / load.EnergyPerLevelKwh
          : null;
        var chargeEndPredicted = load.PlannedSessions.Count > 0 ? load.PlannedSessions[^1].EndTime : load.PredictedEnd;
        var attr_load = new
        {
          charge_status_reason = capPaused ? $"Import cap: paused ({_importCap.Reason}) — plan: {load.ChargeReasonAt(now)}" : load.ChargeReasonAt(now),
          mode = load.Mode.ToString(),
          target_level = load.TargetLevel,
          current_level = load.CurrentLevel,
          level_unit = load.Config.LevelUnit,
          power_avg_w = powerAvgW,
          net_pv_w = netPvW,
          charge_start_predicted = load.ExtraLoads.Count > 0 ? load.ExtraLoads[0].StartTime.ToISO8601() : "n/a",
          charge_end_predicted = chargeEndPredicted?.ToISO8601() ?? "n/a",
          charge_end_predicted_soc = chargeEndPredictedSoc?.ToString("F0") ?? "n/a",
          ready_by = load.ReadyByTime?.ToString("HH:mm", CultureInfo.InvariantCulture) ?? "off",
          energy_per_level_kwh = MathF.Round(load.EnergyPerLevelKwh, 3),
          learned_power_w = load.LearnedPowerW,
          last_plan = load.LastPlan?.Time.ToISO8601() ?? "n/a",
          last_plan_reason = load.LastPlanReason,
          price_scan = load.PriceScan,
          planned_sessions = allSessions.Select(e => new { start = e.StartTime.ToISO8601(), end = e.EndTime.ToISO8601() }).ToList(),
          zz_charge_timeline = chargeTimeline,
        };
        await PVCC_EntityManager.SetAttributesAsync(load.ChargeNowEntity.EntityId, attr_load);
      }
    }

    /// <summary>Lets the warm-water controller act on its plan (start a one-time heating, end a boost) and publish it.</summary>
    /// <param name="now">Current time.</param>
    private async Task ActAndPublishWarmWaterAsync(DateTime now)
    {
      if (_house.WarmWater is { } warmWater)
      {
        // Not the cycle's `now`: the plan was just made (up to ~1 s later) and its current run
        // starts at that time — an earlier `now` found no current run and stopped the heating.
        warmWater.ImportHeadroomW = PVCC_Config.MaxGridImportW > 0 ? _importCap.HeadroomW : null;
        await warmWater.ActAsync(DateTime.Now);
        await warmWater.PublishAsync(now);
      }
    }

    /// <summary>The battery sensors: remaining time, status, remaining energy.</summary>
    /// <param name="now">Current time.</param>
    private async Task PublishBatteryAsync(DateTime now)
    {
      await PVCC_EntityManager.SetStateAsync(_battery_RemainingTimeEntity.EntityId, _house.Battery.EstimatedTimeToBatteryFullOrEmpty.ToString(CultureInfo.InvariantCulture));
      var attr_RemainingTime = new
      {
        Estimated_time = now.AddMinutes(_house.Battery.EstimatedTimeToBatteryFullOrEmpty).ToISO8601(),
        next_relevant_pv_charge = _house.SimulationState?.FirstRelevantPVEnergyToday?.ToISO8601() ?? "none",
        avg_battery_charge_or_discharge_Power = _house.Battery.CurrentAverageBatteryChargeDischargePower.ToString(CultureInfo.InvariantCulture) + " W",
        status = _house.Battery.BatteryStatus.ToString(),
        time_on_max = (_house.SimulationState?.MaxSocDurationToday.TotalHours ?? 0).ToString(CultureInfo.InvariantCulture) + " h",
      };
      await PVCC_EntityManager.SetAttributesAsync(_battery_RemainingTimeEntity.EntityId, attr_RemainingTime);

      await PVCC_EntityManager.SetStateAsync(_battery_StatusEntity.EntityId, _house.Battery.BatteryStatus.ToString());
      var attr_batStatus = new
      {
        avg_battery_charge_or_discharge_Power = _house.Battery.CurrentAverageBatteryChargeDischargePower.ToString(CultureInfo.InvariantCulture) + " W",
        avg_house_load_now = _house.CurrentAverageHouseLoad.ToString(CultureInfo.InvariantCulture) + " W",
        predicted_house_load_now = Energy.SlotW(_house.Prediction_Load.CurrentValue) + " W",
        avg_pv_power_now = _house.CurrentAveragePVPower.ToString(CultureInfo.InvariantCulture) + " W",
        predicted_pv_power_now = Energy.SlotW(_house.Prediction_PV.CurrentValue) + " W",
        current_SoC = _house.Battery.BatterySoc.ToString(CultureInfo.InvariantCulture) + "%",
      };
      await PVCC_EntityManager.SetAttributesAsync(_battery_StatusEntity.EntityId, attr_batStatus);

      await PVCC_EntityManager.SetStateAsync(_battery_RemainingEnergyEntity.EntityId, _house.Battery.UsableBatteryEnergy.ToString(CultureInfo.InvariantCulture));
      var attr_RemainingEnergy = new
      {
        min_allowed_SoC = (_house.Battery.EnforcePreferredSoC ? _house.Battery.PreferredMinimalSoC : _house.Battery.AbsoluteMinimalSoC).ToString() + "%",
        remaining_energy_at_min_battery_soc = _house.Battery.ReserveBatteryEnergy.ToString(CultureInfo.InvariantCulture) + " Wh",
        remaining_energy_to_zero_soc = _house.Battery.CalculateBatteryEnergyAtSoC(_house.Battery.BatterySoc, 0).ToString(CultureInfo.InvariantCulture) + " Wh",
        battery_capacity = _house.Battery.BatteryCapacity.ToString(CultureInfo.InvariantCulture) + " Wh",
      };
      await PVCC_EntityManager.SetAttributesAsync(_battery_RemainingEnergyEntity.EntityId, attr_RemainingEnergy);
    }

    /// <summary>binary_sensor.pv_control_need_to_charge_from_grid_today: the planned grid charge (the same one as the mode sensor's) and the SoC low ahead.</summary>
    /// <param name="charge">The grid charge the plan runs now or next; null = none.</param>
    /// <param name="now">Current time.</param>
    private async Task PublishNeedToChargeAsync(PlannedCharge? charge, DateTime now)
    {
      await PVCC_EntityManager.SetStateAsync(_needToChargeFromGridTodayEntity.EntityId, charge is not null ? "ON" : "OFF");
      var futureSoCEntries = _house.Prediction_BatterySoC.TodayAndTomorrow.Where(s => s.Key >= now).ToList();
      var simMinSoC = futureSoCEntries.Count > 0 ? futureSoCEntries.Min(s => s.Value) : _house.Battery.BatterySoc;
      var simMinSoCTime = futureSoCEntries.Count > 0 ? futureSoCEntries.MinBy(s => s.Value).Key : now;
      var attr_Charge = new
      {
        minimal_SoC_allowed = _house.Battery.AbsoluteMinimalSoC.ToString(CultureInfo.InvariantCulture) + "%",
        preferred_SoC = _house.Battery.PreferredMinimalSoC.ToString(CultureInfo.InvariantCulture) + "%",
        minimal_estimated_SoC = simMinSoC.ToString(CultureInfo.InvariantCulture) + "%",
        at_time = simMinSoCTime.ToISO8601(),
        current_average_gridpower = _house.CurrentAverageGridPower.ToString(CultureInfo.InvariantCulture) + " W",
        reason = (charge?.Reason ?? ForceChargeReasons.None).ToString(),
        charge_start_time = charge?.Start.ToISO8601() ?? "n/a",
      };
      await PVCC_EntityManager.SetAttributesAsync(_needToChargeFromGridTodayEntity.EntityId, attr_Charge);
    }

    /// <summary>The prediction sensors: the SoC timeline and the midnight snapshot, charge/discharge predictions, today's and tomorrow's SoC extremes, PV and load forecasts.</summary>
    /// <param name="now">Current time.</param>
    private async Task PublishPredictionsAsync(DateTime now)
    {
      var curPredSoc = _house.Prediction_BatterySoC.TodayAndTomorrow.GetEntryAtTime(now);
      if (curPredSoc.Key != default)
      {
        await PVCC_EntityManager.SetStateAsync(_info_PredictedSoCEntity.EntityId, curPredSoc.Value.ToString(CultureInfo.InvariantCulture));
        // Build a slot lookup from the live simulation timeline for data_actual
        var simSlotLookup = _house.SimulationTimeline.ToDictionary(s => s.Time);
        var attr_pred_soc = new
        {
          current_entry_time = curPredSoc.Key.ToISO8601(),
          last_snapshot = _house.Snapshots.LastSnapshotUpdate.ToISO8601(),
          // Live simulation: SoC + mode + extra_load flag for every slot today and tomorrow.
          // Slots before "now" have no simulation slot (back-filled SoC only) → mode "past",
          // extra_load false, export_allowed true (no export-suppression to report on a past slot).
          data_actual = _house.Prediction_BatterySoC.TodayAndTomorrow.Select(s =>
          {
            var hasSlot = simSlotLookup.TryGetValue(s.Key, out var slot);
            return new
            {
              datetime = s.Key.ToISO8601(),
              soc = s.Value,
              mode = hasSlot ? slot!.State.Mode.ToString() : "past",
              extra_load = hasSlot && slot!.ExtraLoadWh > 0,
              export_allowed = !hasSlot || slot!.State.ExportAllowed,
            };
          }),
        };
        await PVCC_EntityManager.SetAttributesAsync(_info_PredictedSoCEntity.EntityId, attr_pred_soc);

        // Midnight snapshot — published separately to keep predicted_soc attributes under the 16 kB recorder limit.
        var snapshotModes = _house.Snapshots.DailyModePredictionTodayAndTomorrow;
        var curSnapSoc = _house.Snapshots.DailyBatterySoCPredictionTodayAndTomorrow.GetEntryAtTime(now);
        await PVCC_EntityManager.SetStateAsync(_info_SoCSnapshotEntity.EntityId,
          (curSnapSoc.Key != default ? curSnapSoc.Value : 0).ToString(CultureInfo.InvariantCulture));
        var attr_soc_snapshot = new
        {
          last_snapshot = _house.Snapshots.LastSnapshotUpdate.ToISO8601(),
          data_snapshot = _house.Snapshots.DailyBatterySoCPredictionTodayAndTomorrow.Select(s => new
          {
            datetime = s.Key.ToISO8601(),
            soc = s.Value,
            mode = snapshotModes.TryGetValue(s.Key, out var snapMode) ? snapMode : "unknown",
          }),
        };
        await PVCC_EntityManager.SetAttributesAsync(_info_SoCSnapshotEntity.EntityId, attr_soc_snapshot);
      }
      var curPredCharge = _house.Snapshots.DailyChargePredictionTodayAndTomorrow.GetEntryAtTime(now);
      if (curPredCharge.Key != default)
      {
        await PVCC_EntityManager.SetStateAsync(_info_PredictedChargeEntity.EntityId, curPredCharge.Value.ToString(CultureInfo.InvariantCulture));
        var attr_pred_charge = new
        {
          current_entry_time = curPredCharge.Key.ToISO8601(),
          last_snapshot = _house.Snapshots.LastSnapshotUpdate.ToISO8601(),
          data = _house.Snapshots.DailyChargePredictionTodayAndTomorrow.Select(s => new { datetime = s.Key, charge = s.Value }),
        };
        await PVCC_EntityManager.SetAttributesAsync(_info_PredictedChargeEntity.EntityId, attr_pred_charge);
      }
      var curPredDischarge = _house.Snapshots.DailyDischargePredictionTodayAndTomorrow.GetEntryAtTime(now);
      if (curPredDischarge.Key != default)
      {
        await PVCC_EntityManager.SetStateAsync(_info_PredictedDischargeEntity.EntityId, curPredDischarge.Value.ToString(CultureInfo.InvariantCulture));
        var attr_pred_charge = new
        {
          current_entry_time = curPredDischarge.Key.ToISO8601(),
          last_snapshot = _house.Snapshots.LastSnapshotUpdate.ToISO8601(),
          data = _house.Snapshots.DailyDischargePredictionTodayAndTomorrow.Select(s => new { datetime = s.Key, discharge = s.Value }),
        };
        await PVCC_EntityManager.SetAttributesAsync(_info_PredictedDischargeEntity.EntityId, attr_pred_charge);
      }

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
    }

    /// <summary>The price sensors: import and export price now with their breakdown and lists, the day's best prices.</summary>
    /// <param name="now">Current time.</param>
    private async Task PublishPricesAsync(DateTime now)
    {
      await PVCC_EntityManager.SetStateAsync(_currentImportPriceBruttoEntity.EntityId, _house.Prices.PriceListImport.GetPrice(now).ToString(CultureInfo.InvariantCulture));
      await PVCC_EntityManager.SetStateAsync(_activeNetworkPricePeriodEntity.EntityId, _house.Prices.ActiveNetworkPricePeriodName);
      var attr_currentImportPrice = new
      {
        _netto_price = _house.Prices.PriceListNettoImport.GetPrice(now),
        _provider_price = _house.Prices.CurrentEnergyImportPriceEnergyOnly, 
        _network_price = _house.Prices.PriceListNetworkImport.GetPrice(now),
        data = _house.Prices.PriceListImport.Select(s => new { start_time = s.StartTime.ToISO8601(), end_time = s.EndTime.ToISO8601(), price_per_kwh = s.Price }),
      };
      await PVCC_EntityManager.SetAttributesAsync(_currentImportPriceBruttoEntity.EntityId, attr_currentImportPrice);

      await PVCC_EntityManager.SetStateAsync(_currentExportPriceBruttoEntity.EntityId, _house.Prices.PriceListExport.GetPrice(now).ToString(CultureInfo.InvariantCulture));
      var attr_currentExportPrice = new
      {
        _netto_price = _house.Prices.PriceListNettoExport.GetPrice(now),
        _network_price = _house.Prices.PriceListNetworkExport.GetPrice(now),
        data = _house.Prices.PriceListExport.Select(s => new { start_time = s.StartTime.ToISO8601(), end_time = s.EndTime.ToISO8601(), price_per_kwh = s.Price }),
      };
      await PVCC_EntityManager.SetAttributesAsync(_currentExportPriceBruttoEntity.EntityId, attr_currentExportPrice);


      await PVCC_EntityManager.SetStateAsync(_bestExportPriceEntity.EntityId, _house.Prices.PriceListExport.MostExpensiveToday.Price.ToString(CultureInfo.InvariantCulture));
      var attr_bestExportPrice = new
      {
        start_time = _house.Prices.PriceListExport.MostExpensiveToday.StartTime.ToISO8601(),
        end_time = _house.Prices.PriceListExport.MostExpensiveToday.EndTime.ToISO8601(),
      };
      await PVCC_EntityManager.SetAttributesAsync(_bestExportPriceEntity.EntityId, attr_bestExportPrice);

      await PVCC_EntityManager.SetStateAsync(_bestImportPriceEntity.EntityId, _house.Prices.PriceListImport.CheapestToday.Price.ToString(CultureInfo.InvariantCulture));
      var attr_bestImportPrice = new
      {
        start_time = _house.Prices.PriceListImport.CheapestToday.StartTime.ToISO8601(),
        end_time = _house.Prices.PriceListImport.CheapestToday.EndTime.ToISO8601(),
      };
      await PVCC_EntityManager.SetAttributesAsync(_bestImportPriceEntity.EntityId, attr_bestImportPrice);
    }

    /// <summary>The cost log: one row per quarter (import/export totals and increases, the prices) in the energy costs DB.</summary>
    /// <param name="now">Current time.</param>
    private async Task LogCostsAsync(DateTime now)
    {
      if (_logEnergy && now >= _nextQuarterHour)
      {
        await using (var db = new EnergyCostDb(new DataOptions().UseSQLite(_energyLogDBConnectionString)))
        {
          // Import from the counters in priority order (kWh); each counts from its own value at the last
          // booking (after a restart: the stored total belongs to the counter it fits). The cost log is in Wh.
          var importCounter = _quarterImport.Counter;
          _costImportSince ??= importCounter.SnapshotFrom((_lastCostsEntry.GridImportTotal ?? 0) / 1000.0);
          float gridImport = (float)((importCounter.IncreaseSince(_costImportSince) ?? 0) * 1000);
          float totalImportEnergy = importCounter.ActiveValue is { } kwh ? (float)(kwh * 1000) : _lastCostsEntry.GridImportTotal ?? 0;
          _costImportSince = importCounter.Snapshot();
          // An unavailable export counter keeps the last total and books 0 (the import side falls back the same way).
          var (totalExportEnergy, gridExport) = CostTracker.CounterBooking(
            PVCC_Config.TotalExportEnergyEntity.TryGetStateValue(out float exportNow) ? exportNow : null, _lastCostsEntry.GridExportTotal);
          var basePriceEntry = _house.Prices.PriceListNetto.OrderBy(p => p.StartTime).LastOrDefault(p => p.StartTime < _nextQuarterHour);
          var basePrice = basePriceEntry.Price;
          Costs currentCost = new Costs
          {
            Timestamp = _nextQuarterHour,
            GridImportTotal = totalImportEnergy,
            GridExportTotal = totalExportEnergy,
            GridImport = gridImport,
            GridExport = gridExport,
            BasePrice = basePrice,
            ImportPriceProvider = _house.Prices.CalculateBruttoPriceImport(basePrice, false),
            ImportPriceTotal = _house.Prices.CalculateBruttoPriceImport(basePrice, true),
            ExportPriceProvider = _house.Prices.CalculateBruttoPriceExport(basePrice, false),
            ExportPriceTotal = _house.Prices.CalculateBruttoPriceExport(basePrice, true),
          };
          await db.InsertAsync(currentCost);
          _lastCostsEntry = currentCost;
          PVCC_Logger.LogDebug("Saved energy costs to DB - PriceEntry: {pr}", basePriceEntry.StartTime + " - " + basePriceEntry.EndTime);
        }
        _nextQuarterHour = now.GetNextQuarterHour();
      }
    }
    /// <summary>Checks that the configured entities exist; errors are logged.</summary>
    /// <returns>False if a required entity is missing.</returns>
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
      return checkResult;
    }
    /// <summary>Registers PVControl's HA entities (sensors, selects, numbers, switches) via MQTT and reads their current values.</summary>
    /// <param name="reset">Re-register all entities (drops their configuration in HA).</param>
    private async Task RegisterControlSensors(bool reset = false)
    {
      await RegisterStatusSensorsAsync(reset);
      await RegisterControlsAsync(reset);
      await RegisterPriceSensorsAsync(reset);
      await RegisterBatteryAndPredictionSensorsAsync(reset);
      await RegisterLoadEntitiesAsync(reset);
    }

    /// <summary>Version, health, price publication, import cap, VPP curve and load forecast sensors.</summary>
    /// <param name="reset">Re-register (drops their configuration in HA).</param>
    private async Task RegisterStatusSensorsAsync(bool reset)
    {
      // Deployed build: republished on every start so the device's "Firmware" follows it too.
      var version = await RegisterSensor("sensor.pv_control_version", "Version", null, "mdi:tag-outline", republish: true);
      await PVCC_EntityManager.SetStateAsync(version.EntityId, AppVersion.Of(GetType().Assembly));
      PVCC_Logger.LogInformation("PVControl version {Version}", AppVersion.Of(GetType().Assembly));
      _healthEntity = await RegisterSensor("sensor.pv_control_health", "Health", "ENUM", "mdi:heart-pulse",
        addConfig: new { options = Enum.GetNames<HealthLevel>() },
        defaultValue: HealthLevel.ok.ToString(),
        reRegister: reset);
      RestoreHealthIssues();
      _pricePublicationEntity = await RegisterSensor("sensor.pv_control_price_publication", "Price publication (tomorrow)", null, "mdi:clock-check-outline",
        reRegister: reset);
      RestorePricePublications();
      _importQuarterEntity = await RegisterSensor("sensor.pv_control_import_quarter", "Import this quarter", "power", "mdi:transmission-tower-import",
        addConfig: new { unit_of_measurement = "kW" }, reRegister: reset);
      _importMonthPeakEntity = await RegisterSensor("sensor.pv_control_import_month_peak", "Import peak this month", "power", "mdi:chart-bell-curve",
        addConfig: new { unit_of_measurement = "kW" }, reRegister: reset);
      RestoreMonthPeak();
      _vppCurveEntity = await RegisterSensor("sensor.pv_control_vpp_charge_limit", "VPP charge limit", "power", "mdi:battery-charging-medium",
        addConfig: new { unit_of_measurement = "W" }, reRegister: reset);
      RestoreVppCurve();
      if (_house.WeatherLoad is not null)
      {
        _loadForecastEntity = await RegisterSensor("sensor.pv_control_load_forecast", "Load forecast (night)", "energy", "mdi:home-thermometer-outline",
          addConfig: new { unit_of_measurement = "kWh" }, reRegister: reset);
        RestoreNights();
      }
    }

    /// <summary>The user's controls (mode override, switches, numbers), the mode sensor (the inverter command, restored) and its two switches.</summary>
    /// <param name="reset">Re-register (drops their configuration in HA).</param>
    private async Task RegisterControlsAsync(bool reset)
    {
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
          max = 0.5,
          step = 0.01,
          initial = 0,
          unit_of_measurement = "€/kWh",
          mode = "slider",
        },
        defaultValue: "0.25",
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
        addConfig: new { options = Enum.GetNames<InverterModes>().Where(m => m != nameof(InverterModes.automatic)).ToArray() }, // automatic is the override's "none"
        defaultValue: InverterModes.normal.ToString(),
        reRegister: reset);
      RestoreInverterCommand();
      
      _battChargeEnabledEntity = await RegisterSensor("binary_sensor.pv_control_battery_charging_enabled", "Battery Charging Eabled", "power", "mdi:power-plug-battery-outline",
        defaultValue: "ON",
        reRegister: reset);

      _exportAllowedEntity = await RegisterSensor("binary_sensor.pv_control_export_allowed", "Export Allowed", "power", "mdi:transmission-tower-export",
        defaultValue: "ON",
        reRegister: reset);
    }

    /// <summary>Price and cost sensors.</summary>
    /// <param name="reset">Re-register (drops their configuration in HA).</param>
    private async Task RegisterPriceSensorsAsync(bool reset)
    {
      _currentImportPriceBruttoEntity = await RegisterSensor("sensor.pv_control_current_import_price_brutto", "Current energy import price (brutto)", "MONETARY", "mdi:currency-eur",
        addConfig: new
        {
          unit_of_measurement = "€/kWh",
        },
        defaultValue: "0",
        reRegister: reset);

      _activeNetworkPricePeriodEntity = await RegisterSensor("sensor.pv_control_active_network_price_period", "Active network price period", null, "mdi:calendar-clock",
        defaultValue: "Standard",
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
          state_class = "total",
        },
        defaultValue: "0",
        reRegister: reset);

      _sumImportCostBruttoEntity = await RegisterSensor("sensor.pv_control_sum_import_cost_brutto", "Sum of import costs (brutto)", "MONETARY", "mdi:currency-eur",
        addConfig: new
        {
          unit_of_measurement = "€",
          state_class = "total",
        },
        defaultValue: "0",
        reRegister: reset);

      _sumImportCostEnergyOnlyEntity = await RegisterSensor("sensor.pv_control_sum_import_cost_energy_only", "Sum of import costs (energy only)", "MONETARY", "mdi:currency-eur",
        addConfig: new
        {
          unit_of_measurement = "€",
          state_class = "total",
        },
        defaultValue: "0",
        reRegister: reset);

      _sumImportCostNetworkOnlyEntity = await RegisterSensor("sensor.pv_control_sum_import_cost_network_only", "Sum of import costs (network only)", "MONETARY", "mdi:currency-eur",
        addConfig: new
        {
          unit_of_measurement = "€",
          state_class = "total",
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

      _batteryAvgCostPerKwhEntity = await RegisterSensor("sensor.pv_control_battery_avg_cost_per_kwh", "Battery average stored energy cost", "MONETARY", "mdi:battery-unknown",
        addConfig: new
        {
          unit_of_measurement = "€/kWh",
        },
        defaultValue: "0",
        reRegister: reset);

      _bestExportPriceEntity = await RegisterSensor("sensor.pv_control_best_export_price_today", "Best (highest) export price today", "MONETARY", "mdi:currency-eur",
        addConfig: new
        {
          unit_of_measurement = "€/kWh",
          state_class = "total", // state class 'measurement' which is impossible considering device class ('monetary') ... why the hell ever ...
        },
        defaultValue: "0",
        reRegister: reset);

      _bestImportPriceEntity = await RegisterSensor("sensor.pv_control_best_import_price_today", "Best (lowest) import price today", "MONETARY", "mdi:currency-eur",
        addConfig: new
        {
          unit_of_measurement = "€/kWh",
          state_class = "total",
        },
        defaultValue: "0",
        reRegister: reset);
    }

    /// <summary>Battery status, need-to-charge and prediction sensors.</summary>
    /// <param name="reset">Re-register (drops their configuration in HA).</param>
    private async Task RegisterBatteryAndPredictionSensorsAsync(bool reset)
    {
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

      _needToChargeFromGridTodayEntity = await RegisterSensor("binary_sensor.pv_control_need_to_charge_from_grid_today", "Need to charge from Grid today", "battery_charging", "mdi:transmission-tower-import",
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

      _info_SoCSnapshotEntity = await RegisterSensor("sensor.pv_control_info_soc_snapshot", "SoC Snapshot (Midnight Prediction)", "Battery", "mdi:calendar-clock",
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
    }

    /// <summary>Each schedulable load's entities (mode, target, ready-by, charge_now, totals, learned values) and the warm-water controller's.</summary>
    /// <param name="reset">Re-register (drops their configuration in HA).</param>
    private async Task RegisterLoadEntitiesAsync(bool reset)
    {
      // Auto-create HA entities for each schedulable load.
      foreach (var load in _house.SchedulableLoads)
      {
        if (!load.Config.Mode.HasValue)
          load.ModeEntity = await RegisterSensor(
            $"select.pv_control_{load.Slug}_mode",
            $"{load.Config.Name} - Mode",
            "select", load.Config.Icon,
            addConfig: new { options = Enum.GetNames<LoadSchedulingMode>() },
            defaultValue: LoadSchedulingMode.Off.ToString(),
            reRegister: reset);

        if (!load.Config.TargetLevel.HasValue)
          load.TargetLevelEntity = await RegisterSensor(
            $"number.pv_control_{load.Slug}_target_level",
            $"{load.Config.Name} - Target Level",
            load.Config.LevelUnit == "%" ? "battery" : "temperature",
            load.Config.Icon,
            addConfig: new
            {
              min = load.Config.TargetLevelMin,
              max = load.Config.TargetLevelMax,
              step = load.Config.TargetLevelStep,
              initial = load.Config.TargetLevelDefault,
              unit_of_measurement = load.Config.LevelUnit,
              mode = "slider",
            },
            defaultValue: load.Config.TargetLevelDefault.ToString(CultureInfo.InvariantCulture),
            reRegister: reset);

        load.ReadyByEntity = await RegisterSensor(
          $"time.pv_control_{load.Slug}_ready_by",
          $"{load.Config.Name} - Ready By",
          null, "mdi:clock-end",
          defaultValue: "07:00:00",
          reRegister: reset);

        load.EnforceReadyByEntity = await RegisterSensor(
          $"switch.pv_control_{load.Slug}_enforce_ready_by",
          $"{load.Config.Name} - Enforce Ready By",
          "switch", "mdi:clock-check",
          defaultValue: "OFF",
          reRegister: reset);

        load.ChargeNowEntity = await RegisterSensor(
          $"binary_sensor.pv_control_{load.Slug}_charge_now",
          $"{load.Config.Name} - Charge Now",
          load.Config.ChargeNowDeviceClass, load.Config.Icon,
          defaultValue: "OFF",
          reRegister: reset);

        if (load.Config.ActualEnergyEntity is not null)
        {
          load.TotalEnergyKwhEntity = await RegisterSensor(
            $"sensor.pv_control_{load.Slug}_total_energy",
            $"{load.Config.Name} - Total Energy",
            "Energy", load.Config.Icon,
            addConfig: new { unit_of_measurement = "kWh", state_class = "total_increasing" },
            defaultValue: "0",
            reRegister: reset);

          load.TotalCostEurEntity = await RegisterSensor(
            $"sensor.pv_control_{load.Slug}_total_cost",
            $"{load.Config.Name} - Total Cost",
            "monetary", load.Config.Icon,
            addConfig: new { unit_of_measurement = "€" },
            defaultValue: "0",
            reRegister: reset);

          // Seed in-memory running totals from persisted HA state.
          // Reject values outside plausible range (corrupted by past sensor glitch).
          // numericalGetBaseValue:false — values stored raw in kWh/€, no unit-multiplier conversion.
          if (load.TotalEnergyKwhEntity.TryGetStateValue(out float savedKwh, numericalGetBaseValue: false) && savedKwh is >= 0f and <= 100_000f)
            load.TotalEnergyKwh = savedKwh;
          else
            PVCC_Logger.LogWarning("TotalEnergy for {Name} reset to 0 (persisted value {Val} out of range)", load.Config.Name, savedKwh);

          if (load.TotalCostEurEntity.TryGetStateValue(out float savedEur, numericalGetBaseValue: false) && savedEur is >= 0f and <= 1_000_000f)
            load.TotalCostEur = savedEur;
          else
            PVCC_Logger.LogWarning("TotalCost for {Name} reset to 0 (persisted value {Val} out of range)", load.Config.Name, savedEur);
        }

        // Learned characteristics: created with the YAML values, then the persisted (learned or
        // hand-set) value wins on every later start.
        load.EnergyPerLevelEntity = await RegisterSensor(
          $"number.pv_control_{load.Slug}_energy_per_level",
          $"{load.Config.Name} - Energy per {load.Config.LevelUnit}",
          null, "mdi:lightning-bolt",
          addConfig: new { min = 0.001, max = 10, step = 0.001, mode = "box", unit_of_measurement = $"kWh/{load.Config.LevelUnit}" },
          defaultValue: load.Config.EnergyPerLevelUnitKwh.ToString("F3", CultureInfo.InvariantCulture),
          reRegister: reset);
        if (load.EnergyPerLevelEntity.TryGetStateValue(out float savedPerLevel, numericalGetBaseValue: false) && savedPerLevel > 0)
          load.EnergyPerLevelKwh = savedPerLevel;

        load.ChargePowerEntity = await RegisterSensor(
          $"number.pv_control_{load.Slug}_charge_power",
          $"{load.Config.Name} - Charge Power",
          "power", "mdi:flash",
          addConfig: new { min = 0, max = 50000, step = 10, mode = "box", unit_of_measurement = "W" },
          defaultValue: load.Config.AvgPowerW.ToString(CultureInfo.InvariantCulture),
          reRegister: reset);
        if (load.ChargePowerEntity.TryGetStateValue(out int savedPower, numericalGetBaseValue: false) && savedPower > 0)
          load.LearnedPower = savedPower;
      }

      if (_house.WarmWater is not null)
        await _house.WarmWater.RegisterEntitiesAsync(reset);
    }
  }
}
