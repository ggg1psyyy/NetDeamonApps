using System;
using System.Globalization;
using NetDaemon.HassModel.Entities;
using NetDeamon.apps.PVControl.Managers;
using NetDeamon.apps.PVControl.Predictions;
using NetDeamon.apps.PVControl.Simulator;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NetDaemon.HassModel;
using static NetDeamon.apps.PVControl.PVControlCommon;
using NetDeamon.apps;
using DateTime = System.DateTime;
using Math = System.Math;
using TimeSpan = System.TimeSpan;

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
      // Collect the DB columns each schedulable load wants stripped from the base prediction,
      // so no load's historical energy is double-counted when it is added back as an ExtraLoad.
      var excludeColumns = PVCC_Config.SchedulableLoads
        .Select(l => l.HistoryDbColumn)
        .Where(c => !string.IsNullOrEmpty(c))
        .Select(c => c!)
        .ToList();
      Prediction_Load = new HourlyWeightedAverageLoadPrediction(PVCC_Config.DBFullLocation, 10, excludeColumns: excludeColumns);

      if (PVCC_Config.ForecastPVEnergyTodayEntities is null || PVCC_Config.ForecastPVEnergyTomorrowEntities is null)
        throw new NullReferenceException("PV Forecast entities are not available");
      Prediction_PV = new OpenMeteoSolarForecastPrediction(PVCC_Config.ForecastPVEnergyTodayEntities, PVCC_Config.ForecastPVEnergyTomorrowEntities);

      Prediction_NetEnergy = new NetEnergyPrediction(Prediction_PV, Prediction_Load, _loadRunningAverage, _pvRunningAverage, true);

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

      // Initialize runtime objects for each schedulable load and wire up their power averages.
      SchedulableLoads = PVCC_Config.SchedulableLoads
        .Select(cfg => new SchedulableLoadRuntime(cfg))
        .ToList();
      foreach (var schedLoad in SchedulableLoads)
      {
        if (schedLoad.Config.ActualPowerEntity is not null)
        {
          schedLoad.PowerAverage = new RunningIntAverage(TimeSpan.FromMinutes(2));
          if (schedLoad.Config.ActualPowerEntity.TryGetStateValue(out float initPow))
            schedLoad.PowerAverage.AddValue((int)Math.Round(initPow));
          schedLoad.Config.ActualPowerEntity.StateChanges().SubscribeAsync(async _ => await UserStateChanged(schedLoad.Config.ActualPowerEntity));
        }
        if (schedLoad.Config.ActualEnergyEntity is not null)
        {
          if (schedLoad.Config.ActualEnergyEntity.TryGetStateValue(out float initEnergy))
            schedLoad.LastEnergySum = initEnergy;
          schedLoad.Config.ActualEnergyEntity.StateChanges().SubscribeAsync(async _ => await UserStateChanged(schedLoad.Config.ActualEnergyEntity));
        }
      }

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
    public float ForceChargeMaxPrice { get; set; }
    public int ForceChargeTargetSoC { get; set; }

    // ── Schedulable loads ─────────────────────────────────────────────────────────────────
    /// <summary>
    /// Runtime state for each schedulable extra load defined in the YAML config.
    /// The simulation oracle (FindLoadWindow) updates ChargeNow/ChargeReason/PredictedEnd
    /// on each entry during every RunSimulation call.
    /// </summary>
    public List<SchedulableLoadRuntime> SchedulableLoads { get; private set; } = [];
    // ── Cost sum entities (HA is the source of truth — no local copy) ─────────────────────
    /// <summary>Set by PVControl after entity registration; HouseEnergy writes directly to these.</summary>
    public Entity? SumExportEarningsEntity { get; set; }
    public Entity? SumImportCostBruttoEntity { get; set; }
    public Entity? SumImportCostEnergyOnlyEntity { get; set; }
    public Entity? SumImportCostNetworkOnlyEntity { get; set; }
    public Entity? SumImportExportNetCostEntity { get; set; }

    private async Task AddToSumEntityAsync(Entity? entity, float deltaEur)
    {
      if (entity is null) return;
      float current = entity.TryGetStateValue(out float v) ? v : 0f;
      await PVCC_EntityManager.SetStateAsync(entity.EntityId, (current + deltaEur).ToString(CultureInfo.InvariantCulture));
    }

    private async Task UpdateNetCostEntityAsync()
    {
      if (SumImportExportNetCostEntity is null || SumImportCostBruttoEntity is null || SumExportEarningsEntity is null) return;
      float imp = SumImportCostBruttoEntity.TryGetStateValue(out float i) ? i : 0f;
      float exp = SumExportEarningsEntity.TryGetStateValue(out float e) ? e : 0f;
      await PVCC_EntityManager.SetStateAsync(SumImportExportNetCostEntity.EntityId, (imp - exp).ToString(CultureInfo.InvariantCulture));
    }

    private async Task UserStateChanged(Entity entity)
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
        // Subtract actually-measured schedulable load power so the average tracks only base
        // house load — matching the historical prediction which excludes these via excludeColumns.
        // Without this, NetEnergyPrediction double-counts the load: once via the elevated
        // running average and once via the ExtraLoad injected into the simulation.
        // Only subtract loads with a confirmed active ActualPowerEntity reading (above
        // MinActivePowerW) — this correctly handles cases where ChargeNow=true but the
        // load isn't actually drawing (e.g. EV not connected).
        int runningLoadW = SchedulableLoads
          .Where(l => l.PowerAverage != null && l.PowerAverage.GetAverage() > l.Config.MinActivePowerW)
          .Sum(l => l.PowerAverage!.GetAverage());
        _loadRunningAverage.AddValue(load - runningLoadW);
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
          await AddToSumEntityAsync(SumExportEarningsEntity, diff * CurrentEnergyExportPriceTotal);
          await UpdateNetCostEntityAsync();
        }
        _lastExportEnergySum = export / 1000;
      }
      if (entity.EntityId == PVCC_Config.DailyImportEnergyEntity?.EntityId && PVCC_Config.DailyImportEnergyEntity.TryGetStateValue(out float import))
      {
        float diff = (import / 1000) - _lastImportEnergySum;
        if (diff > 0)
        {
          await AddToSumEntityAsync(SumImportCostBruttoEntity, diff * CurrentEnergyImportPriceTotal);
          await AddToSumEntityAsync(SumImportCostEnergyOnlyEntity, diff * CurrentEnergyImportPriceEnergyOnly);
          await AddToSumEntityAsync(SumImportCostNetworkOnlyEntity, diff * CurrentEnergyImportPriceNetworkOnly);
          await UpdateNetCostEntityAsync();
        }
        _lastImportEnergySum = import / 1000;
      }
      foreach (var schedLoad in SchedulableLoads.Where(l => l.Config.ActualPowerEntity is not null
        && entity.EntityId == l.Config.ActualPowerEntity!.EntityId
        && l.Config.ActualPowerEntity.TryGetStateValue(out float _)))
      {
        schedLoad.Config.ActualPowerEntity!.TryGetStateValue(out float p);
        schedLoad.PowerAverage!.AddValue((int)Math.Round(p));
      }
      foreach (var schedLoad in SchedulableLoads.Where(l => l.Config.ActualEnergyEntity is not null
        && entity.EntityId == l.Config.ActualEnergyEntity!.EntityId
        && l.Config.ActualEnergyEntity.TryGetStateValue(out float _)))
      {
        schedLoad.Config.ActualEnergyEntity!.TryGetStateValue(out float energy);
        float diff = energy - schedLoad.LastEnergySum;
        if (diff > 0)
        {
          await AddToSumEntityAsync(schedLoad.TotalEnergyKwhEntity, diff);
          await AddToSumEntityAsync(schedLoad.TotalCostEurEntity, diff * CurrentEnergyImportPriceTotal);
        }
        schedLoad.LastEnergySum = energy;
      }
      if (entity.EntityId == PVCC_Config.CurrentGridPowerEntity?.EntityId && PVCC_Config.CurrentGridPowerEntity.TryGetStateValue(out int grid))
      {
        _gridRunningAverage.AddValue(grid);
      }
      if (entity.EntityId == PVCC_Config.InverterStatusEntity?.EntityId && PVCC_Config.InverterStatusEntity.TryGetStateValue(out string inverterStatus))
      {
        PVCC_Logger.LogInformation("Inverter RunMode changed from {CurrentInverterRunMode} to {InverterStatus}", _currentInverterRunMode, inverterStatus);
        // if the inverter switches back to normal mode (but not from remote mode), we send the reset signal before switching back to the selected mode
        if (_currentInverterRunMode != "Normal (R)" && inverterStatus == PVCC_Config.InverterStatusNormalString)
        {
          _resetCounter = 2;
          PVCC_Logger.LogInformation("Inverter returned to normal run mode, sending {ResetCounter} reset signal(s)", _resetCounter);
          _currentMode = new InverterState(InverterModes.reset, ForceChargeReasons.None);
        }
        _currentInverterRunMode = inverterStatus;
      }
    }

    private int _resetCounter = 0;
    private InverterState _currentMode = new InverterState(InverterModes.normal, ForceChargeReasons.None, true);
    private List<SimulationSlot> _simulationResult = [];

    /// <summary>
    /// Runs the two-day forward simulation (today 00:00 – tomorrow 23:45) and updates all
    /// predictions from its output. Call this instead of UpdatePredictions() each cycle.
    /// </summary>
    public void RunSimulation(List<ExtraLoad>? extraLoads = null)
    {
      // Update upstream predictions first
      var now = DateTime.Now;
      // Check the START of the 48h window, not Today.First().
      // After midnight, Today still returns the second half of yesterday's window (e.g. 03/18 entries
      // from a 03/17–03/18 dataset), so Today.First().Key.Date == now.Date is true and load never
      // refreshes — leaving stale 03/17 entries that solar forecast no longer covers.
      if (Prediction_Load.TodayAndTomorrow.First().Key.Date != now.Date)
        Prediction_Load.UpdateData();
      Prediction_PV.UpdateData();
      Prediction_NetEnergy.UpdateData();

      // Build the base SimulationInput without EV loads. FindEVChargingWindow will run
      // multiple test simulations to find the valid EV charging window, then we run the
      // final simulation with those EV ExtraLoads included.
      var baseInput = new SimulationInput
      {
        StartTime = now,
        StartSocPercent = BatterySoc,
        BatteryCapacityWh = BatteryCapacity,
        AbsoluteMinSocPercent = AbsoluteMinimalSoC,
        PreferredMinSocPercent = PreferredMinimalSoC,
        EnforcePreferredSoc = EnforcePreferredSoC,
        MaxChargePowerAmps = PVCC_Config.MaxBatteryChargePower,
        InverterEfficiency = InverterEfficiency,
        ImportPrices = PriceListImport,
        ExportPrices = PriceListExport,
        LoadPredictionWh = Prediction_Load.TodayAndTomorrow,
        PVPredictionWh = Prediction_PV.TodayAndTomorrow,
        ExtraLoads = extraLoads ?? [],
        ForceCharge = ForceCharge,
        OpportunisticDischarge = OpportunisticDischarge,
        ForceChargeMaxPrice = ForceChargeMaxPrice,
        ForceChargeTargetSocPercent = ForceChargeTargetSoC,
        OverrideMode = OverrideMode,
        CurrentMode = _currentMode,
        CurrentResetCounter = _resetCounter,
        CurrentAverageGridPowerW = CurrentAverageGridPower,
      };

      // Run the baseline simulation once to identify naturally-scheduled force_charge slots.
      // Each schedulable load's window search runs against this same baseline.
      var baseResult = EnergySimulator.Simulate(baseInput);
      var baseForceChargeSlots = new HashSet<DateTime>(
        baseResult.Where(s => s.State.Mode == InverterModes.force_charge).Select(s => s.Time));

      // Find valid window for each schedulable load (highest priority first).
      foreach (var load in SchedulableLoads.OrderByDescending(l => l.Config.Priority))
        FindLoadWindow(load, baseInput, baseForceChargeSlots);

      // Run final simulation with all found ExtraLoads merged in.
      var allExtraLoads = SchedulableLoads.SelectMany(l => l.ExtraLoads).ToList();
      var finalInput = allExtraLoads.Count > 0
        ? SimWithExtraLoads(baseInput, [.. baseInput.ExtraLoads, .. allExtraLoads])
        : baseInput;

      _simulationResult = EnergySimulator.Simulate(finalInput);

      // Build the two-day SoC dict for Prediction_BatterySoC:
      //   - simulation covers now→end-of-tomorrow (filled below from _simulationResult)
      //   - past slots of today (midnight→now) are back-filled by reversing net-energy
      var fullSoC = new Dictionary<DateTime, int>();
      fullSoC.ClearAndCreateEmptyPredictionData(); // fills today 00:00 → tomorrow 23:45 with 0s

      var startSlot = now.RoundToNearestQuarterHour();
      foreach (var slot in _simulationResult)
        if (fullSoC.ContainsKey(slot.Time))
          fullSoC[slot.Time] = slot.SoC;

      // For past slots (midnight → now) preserve the previously predicted values rather than
      // recalculating them — back-integrating net energy produces a wobbly reconstructed line
      // that doesn't reflect what the simulation actually predicted at those times.
      foreach (var t in fullSoC.Keys.Where(k => k < startSlot).ToList())
        fullSoC[t] = Prediction_BatterySoC.TodayAndTomorrow.GetValueOrDefault(t, 0);

      Prediction_BatterySoC.UpdateData(fullSoC);
    }

    // ── Schedulable load window finding ──────────────────────────────────────────────────────
    // The simulation is the oracle: we run it with candidate ExtraLoad windows and check
    // whether the result satisfies the mode-specific conditions. The first valid start slot
    // determines ChargeNow and the ExtraLoads injected into the final simulation.

    /// <summary>
    /// Finds the valid scheduling window for a load by iterating over candidate start slots
    /// and running a test simulation for each. Updates load.ChargeNow/ChargeReason/PredictedEnd.
    /// The baseline and its force-charge slots are pre-computed once in RunSimulation.
    /// </summary>
    private void FindLoadWindow(SchedulableLoadRuntime load, SimulationInput baseInput, HashSet<DateTime> baseForceChargeSlots)
    {
      var now = DateTime.Now;
      var currentSlot = now.RoundToNearestQuarterHour();

      void SetResult(List<ExtraLoad> extraLoads, bool chargeNow, string reason, DateTime? end)
      {
        load.ExtraLoads = extraLoads;
        load.ChargeNow = chargeNow;
        load.ChargeReason = reason;
        load.PredictedEnd = end;
      }

      if (load.Mode == LoadSchedulingMode.Off)
      { SetResult([], false, "Off", null); return; }

      if (load.Config.CurrentLevelEntity is null || load.Config.EnergyPerLevelUnitKwh <= 0)
      { SetResult([], false, "CurrentLevelEntity or EnergyPerLevelUnitKwh not configured", null); return; }

      if (load.CurrentLevel >= load.TargetLevel)
      { SetResult([], false, $"Target reached ({load.CurrentLevel:F0}{load.Config.LevelUnit} ≥ {load.TargetLevel:F0}{load.Config.LevelUnit})", null); return; }

      int chargeRateW = load.EffectivePowerW;
      if (chargeRateW <= 0)
      { SetResult([], false, "EffectivePowerW is 0 — check AvgPowerW config", null); return; }

      int energyNeededWh = load.EnergyNeededWh;
      int durationMinutes = energyNeededWh * 60 / chargeRateW;

      // Emergency: always charge immediately, no simulation check.
      if (load.Mode == LoadSchedulingMode.Emergency)
      {
        var endTime = now.AddMinutes(durationMinutes);
        SetResult(
          [new ExtraLoad { Name = load.Config.Name, Priority = int.MaxValue, StartTime = now, EndTime = endTime, PowerW = chargeRateW }],
          true,
          $"Emergency ({load.CurrentLevel:F0} → {load.TargetLevel:F0}{load.Config.LevelUnit})",
          endTime);
        return;
      }

      // One simulation from now to min(full duration, FirstRelevantPVEnergyTomorrow).
      // Fall through from most to least restrictive: Optimal → Priority → PriorityPlus.
      var windowEnd = FirstRelevantPVEnergyTomorrow;
      var sessionEnd = currentSlot.AddMinutes(durationMinutes);
      if (sessionEnd > windowEnd) sessionEnd = windowEnd;

      if (sessionEnd <= currentSlot)
      { SetResult([], false, $"No charging window before next PV ({load.Config.Name})", null); return; }

      var extraLoad = new ExtraLoad { Name = load.Config.Name, Priority = load.Config.Priority, StartTime = currentSlot, EndTime = sessionEnd, PowerW = chargeRateW };
      var simResult = EnergySimulator.Simulate(SimWithExtraLoads(baseInput, [.. baseInput.ExtraLoads, extraLoad]));

      bool overnightOk = SimOvernightMinSocOk(simResult);
      bool needsGrid = simResult.Any(s =>
        s.State.Mode == InverterModes.force_charge
        && !baseForceChargeSlots.Contains(s.Time)
        && s.Time >= LastRelevantPVEnergyToday
        && s.Time <= FirstRelevantPVEnergyTomorrow);
      bool gridOnlyCheap = !simResult.Any(s =>
        s.State.Mode == InverterModes.force_charge
        && !baseForceChargeSlots.Contains(s.Time)
        && s.Time >= LastRelevantPVEnergyToday
        && s.Time <= FirstRelevantPVEnergyTomorrow
        && !PriceListImport.Any(p => p.StartTime <= s.Time && p.EndTime > s.Time && p.Price <= ForceChargeMaxPrice));

      if (load.Mode == LoadSchedulingMode.Optimal
          && SimWillReachMaxSocToday(simResult, now) && overnightOk && !needsGrid)
      {
        SetResult([extraLoad], true, $"Charging (Optimal {load.Config.Name}: {load.CurrentLevel:F0} → {load.TargetLevel:F0}{load.Config.LevelUnit}, bat={BatterySoc}%)", sessionEnd);
        return;
      }

      if (load.Mode is LoadSchedulingMode.Optimal or LoadSchedulingMode.Priority
          && overnightOk && !needsGrid)
      {
        SetResult([extraLoad], true, $"Charging (Priority {load.Config.Name}: {load.CurrentLevel:F0} → {load.TargetLevel:F0}{load.Config.LevelUnit}, bat={BatterySoc}%)", sessionEnd);
        return;
      }

      if (overnightOk && gridOnlyCheap)
      {
        SetResult([extraLoad], true, $"Charging (PriorityPlus {load.Config.Name}: {load.CurrentLevel:F0} → {load.TargetLevel:F0}{load.Config.LevelUnit}, bat={BatterySoc}%)", sessionEnd);
        return;
      }

      SetResult([], false, $"No valid window ({load.Mode} {load.Config.Name}: {load.CurrentLevel:F0}{load.Config.LevelUnit}, bat={BatterySoc}%)", null);
    }

    /// <summary>True if the test simulation shows house battery reaching ≥ 99 % today.</summary>
    private static bool SimWillReachMaxSocToday(List<SimulationSlot> result, DateTime now)
      => result.Any(s => s.Time.Date == now.Date && s.SoC >= 99);

    /// <summary>
    /// True if the test simulation shows the battery stays above the effective minimum SoC
    /// throughout the overnight window (sunset today → first PV tomorrow).
    /// Uses PreferredMinimalSoC when EnforcePreferredSoC is set, AbsoluteMinimalSoC otherwise.
    /// </summary>
    private bool SimOvernightMinSocOk(List<SimulationSlot> result)
    {
      int minSoC = EnforcePreferredSoC ? PreferredMinimalSoC : AbsoluteMinimalSoC;
      var overnight = result.Where(s => s.Time >= LastRelevantPVEnergyToday && s.Time <= FirstRelevantPVEnergyTomorrow).ToList();
      return overnight.Count == 0 || overnight.Min(s => s.SoC) >= minSoC;
    }

    /// <summary>Clones a SimulationInput replacing only its ExtraLoads list.</summary>
    private static SimulationInput SimWithExtraLoads(SimulationInput src, List<ExtraLoad> loads)
      => new()
      {
        StartTime = src.StartTime,
        StartSocPercent = src.StartSocPercent,
        BatteryCapacityWh = src.BatteryCapacityWh,
        AbsoluteMinSocPercent = src.AbsoluteMinSocPercent,
        PreferredMinSocPercent = src.PreferredMinSocPercent,
        EnforcePreferredSoc = src.EnforcePreferredSoc,
        MaxChargePowerAmps = src.MaxChargePowerAmps,
        InverterEfficiency = src.InverterEfficiency,
        ImportPrices = src.ImportPrices,
        ExportPrices = src.ExportPrices,
        LoadPredictionWh = src.LoadPredictionWh,
        PVPredictionWh = src.PVPredictionWh,
        ExtraLoads = loads,
        ForceCharge = src.ForceCharge,
        OpportunisticDischarge = src.OpportunisticDischarge,
        ForceChargeMaxPrice = src.ForceChargeMaxPrice,
        ForceChargeTargetSocPercent = src.ForceChargeTargetSocPercent,
        OverrideMode = src.OverrideMode,
        CurrentMode = src.CurrentMode,
        CurrentResetCounter = src.CurrentResetCounter,
        CurrentAverageGridPowerW = src.CurrentAverageGridPowerW,
      };

    public InverterState ProposedState
    {
      get
      {
        var now = DateTime.Now;
        if (_simulationResult.Count == 0)
          return _currentMode;

        var currentSlot = _simulationResult.FirstOrDefault(s => s.Time == now.RoundToNearestQuarterHour())
                          ?? _simulationResult.First();
        _currentMode = currentSlot.State;

        // Propagate reset counter: if simulation chose reset for this slot the counter decrements
        if (_currentMode.Mode == InverterModes.reset && _resetCounter > 0)
          _resetCounter--;

        return _currentMode;
      }
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
    public int CurrentAverageBatteryChargeDischargePower => _battChargeAverage.GetAverage();

    public int CurrentAverageHouseLoad => _loadRunningAverage.GetAverage();

    public int CurrentAveragePVPower => _pvRunningAverage.GetAverage();

    public int CurrentAverageGridPower => _gridRunningAverage.GetAverage() * -1;

    public void AddAverageGridPowerValue(int value)
    {
      _gridRunningAverage.AddValue(value);
    }

    /// <summary>The simulation timeline (now → end of tomorrow) from the last RunSimulation() call.</summary>
    public IReadOnlyList<SimulationSlot> SimulationTimeline => _simulationResult;

    public bool NegativeImportPriceUpcomingToday
    {
      get
      {
        var now = DateTime.Now;
        var negativeImportPrices = PriceListImport.Where(p => p.StartTime.Date == now.Date && p.Price < 0).ToList();
        return negativeImportPrices.Count > 0 && negativeImportPrices.FirstOrDefault().StartTime > now;
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
      var priceRankAtTime = PriceListRanked.FirstOrDefault(r => r.Value.StartTime.Date == dateTime.Date && r.Value.StartTime.Hour == dateTime.Hour);
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
    public List<PriceTableEntry> PriceListNetto
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
                p.Price
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
            PVCC_Config.CurrentExportPriceEntity.TryGetStateValue(out float value, numericalGetBaseValue: false) ? value : 0
            )).ToList();
        }
      }
    }
    public float CalculateBruttoPriceExport(float nettoPrice, bool inclNetworkPrice)
    {
      return (nettoPrice * PVCC_Config.ExportPriceMultiplier + PVCC_Config.ExportPriceAddition + (inclNetworkPrice ? PVCC_Config.ExportPriceNetwork : 0)) * (1 + PVCC_Config.ExportPriceTax);
    }
    public float CalculateBruttoPriceImport(float nettoPrice, bool inclNetworkPrice)
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
      RunSimulation();
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
        // Snapshot the current simulation result so we can later compare prediction vs. reality.
        // RunSimulation() is always called before UpdateSnapshots() in the 15-min cycle,
        // so Prediction_BatterySoC and _simulationResult already reflect the latest simulation.
        _dailySoCPrediction = new Dictionary<DateTime, int>(Prediction_BatterySoC.TodayAndTomorrow);
        // Also snapshot the inverter mode per slot so the snapshot data set is self-contained.
        _dailyModePrediction = _simulationResult.ToDictionary(s => s.Time, s => s.State.Mode.ToString());
        LastSnapshotUpdate = now;
      }
    }
    private Dictionary<DateTime, int> _dailySoCPrediction;
    private Dictionary<DateTime, string> _dailyModePrediction = [];
    public Dictionary<DateTime, int> DailyBatterySoCPredictionTodayAndTomorrow
    {
      get
      {
        UpdateSnapshots();
        return _dailySoCPrediction;
      }
    }
    public Dictionary<DateTime, string> DailyModePredictionTodayAndTomorrow
    {
      get
      {
        UpdateSnapshots();
        return _dailyModePrediction;
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
