using System;
using System.Globalization;
using NetDaemon.HassModel.Entities;
using NetDeamon.apps.PVControl.Managers;
using NetDeamon.apps.PVControl.Predictions;
using NetDeamon.apps.PVControl.Simulator;
using System.Collections.Generic;
using System.Linq;
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

    // ── Battery average cost tracking ────────────────────────────────────────────────────
    private float _batteryAvgCostPerKwh;
    private DateTime _lastBatteryPowerTime = DateTime.MinValue;
    private int _lastBatteryPowerW;
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
          if (schedLoad.Config.ActualEnergyEntity.TryGetStateValue(out float initEnergy, numericalGetBaseValue: false))
            schedLoad.LastEnergySum = initEnergy;
          schedLoad.Config.ActualEnergyEntity.StateChanges().SubscribeAsync(async _ => await UserStateChanged(schedLoad.Config.ActualEnergyEntity));
        }
      }

      PreferredMinBatterySoC = 30;
      EnforcePreferredSoC = false;
      _dailySoCPrediction = [];
      _dailyChargePrediction = [];
      _dailyDischargePrediction = [];
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
    public int ForceChargeTargetSoC { get; set; }

    public PriceManager Prices { get; } = new();

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

    private Entity? _batteryAvgCostEntity;
    public Entity? BatteryAvgCostEntity
    {
      get => _batteryAvgCostEntity;
      set
      {
        _batteryAvgCostEntity = value;
        // Restore persisted value on startup so attribution is correct immediately.
        // Use numericalGetBaseValue:false — the value is stored raw in €/kWh, no unit conversion needed.
        // Clamp to a plausible range; corrupt values (e.g. 14 M €/kWh) are silently discarded.
        if (value != null && value.TryGetStateValue(out float v, numericalGetBaseValue: false) && v is > 0f and <= 10f)
          _batteryAvgCostPerKwh = v;
      }
    }

    private async Task AddToSumEntityAsync(Entity? entity, float deltaEur)
    {
      if (entity is null) return;
      // numericalGetBaseValue:false — value is stored raw in €, no unit-multiplier conversion.
      float current = entity.TryGetStateValue(out float v, numericalGetBaseValue: false) ? v : 0f;
      await PVCC_EntityManager.SetStateAsync(entity.EntityId, (current + deltaEur).ToString(CultureInfo.InvariantCulture));
    }

    private async Task UpdateNetCostEntityAsync()
    {
      if (SumImportExportNetCostEntity is null || SumImportCostBruttoEntity is null || SumExportEarningsEntity is null) return;
      float imp = SumImportCostBruttoEntity.TryGetStateValue(out float i, numericalGetBaseValue: false) ? i : 0f;
      float exp = SumExportEarningsEntity.TryGetStateValue(out float e, numericalGetBaseValue: false) ? e : 0f;
      await PVCC_EntityManager.SetStateAsync(SumImportExportNetCostEntity.EntityId, (imp - exp).ToString(CultureInfo.InvariantCulture));
    }

    private async Task UserStateChanged(Entity entity)
    {
      if (entity.EntityId == PVCC_Config.CurrentImportPriceEntity?.EntityId)
      {
        Prices.UpdatePriceList();
      }
      if (entity.EntityId == PVCC_Config.CurrentBatteryPowerEntity?.EntityId && PVCC_Config.CurrentBatteryPowerEntity.TryGetStateValue(out int bat))
      {
        _battChargeAverage.AddValue(bat);

        var now = DateTime.Now;
        if (_lastBatteryPowerTime != DateTime.MinValue && _lastBatteryPowerW > 0)
        {
          double deltaHours = Math.Min((now - _lastBatteryPowerTime).TotalHours, 0.25);
          float deltaKwh = (float)(_lastBatteryPowerW * deltaHours / 1000.0);

          // PV surplus available for battery after house base load and active EV loads.
          int evPowerW = SchedulableLoads
            .Where(l => l.PowerAverage != null && l.PowerAverage.GetAverage() > l.Config.MinActivePowerW)
            .Sum(l => l.PowerAverage!.GetAverage());
          float pvSurplusW = Math.Max(0f, CurrentAveragePVPower - CurrentAverageHouseLoad - evPowerW);
          float pvFraction = Math.Min(1f, pvSurplusW / _lastBatteryPowerW);
          float sourcePrice = (1f - pvFraction) * Prices.CurrentEnergyImportPriceTotal;

          // Weighted average: blend existing stored energy cost with new charge cost.
          float currentStoredKwh = Math.Max(0.1f, BatterySoc * BatteryCapacity / 100f / 1000f);
          _batteryAvgCostPerKwh = (currentStoredKwh * _batteryAvgCostPerKwh + deltaKwh * sourcePrice)
                                  / (currentStoredKwh + deltaKwh);
          if (_batteryAvgCostEntity != null)
            await PVCC_EntityManager.SetStateAsync(_batteryAvgCostEntity.EntityId,
              _batteryAvgCostPerKwh.ToString(CultureInfo.InvariantCulture));
        }
        _lastBatteryPowerW = bat;
        _lastBatteryPowerTime = now;
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
          await AddToSumEntityAsync(SumExportEarningsEntity, diff * Prices.CurrentEnergyExportPriceTotal);
          await UpdateNetCostEntityAsync();
        }
        _lastExportEnergySum = export / 1000;
      }
      if (entity.EntityId == PVCC_Config.DailyImportEnergyEntity?.EntityId && PVCC_Config.DailyImportEnergyEntity.TryGetStateValue(out float import))
      {
        float diff = (import / 1000) - _lastImportEnergySum;
        if (diff > 0)
        {
          await AddToSumEntityAsync(SumImportCostBruttoEntity, diff * Prices.CurrentEnergyImportPriceTotal);
          await AddToSumEntityAsync(SumImportCostEnergyOnlyEntity, diff * Prices.CurrentEnergyImportPriceEnergyOnly);
          await AddToSumEntityAsync(SumImportCostNetworkOnlyEntity, diff * Prices.CurrentEnergyImportPriceNetworkOnly);
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
        schedLoad.Config.ActualEnergyEntity!.TryGetStateValue(out float energy, numericalGetBaseValue: false);
        float diff = energy - schedLoad.LastEnergySum;
        // Clamp: a single delta larger than 2 h at AvgPowerW is a sensor glitch.
        float maxDiff = schedLoad.Config.AvgPowerW * 2f / 1000f;
        if (diff > maxDiff)
        {
          PVCC_Logger.LogWarning("Ignoring implausibly large energy delta {Diff:F3} kWh (>{Max:F3}) for {Name} — likely sensor glitch",
            diff, maxDiff, schedLoad.Config.Name);
        }
        else if (diff > 0)
        {
          // Decompose energy source: PV direct (free) → grid direct → battery (avg cost).
          int evPowerW = schedLoad.PowerAverage?.GetAverage() ?? schedLoad.Config.AvgPowerW;
          float pvSurplusW = Math.Max(0f, CurrentAveragePVPower - CurrentAverageHouseLoad);
          float pvFraction      = evPowerW > 0 ? Math.Min(1f, pvSurplusW / evPowerW) : 0f;
          float remainFraction  = 1f - pvFraction;
          float gridFraction    = evPowerW > 0 ? Math.Min(remainFraction, Math.Max(0f, CurrentAverageGridPower) / evPowerW) : 0f;
          float batteryFraction = remainFraction - gridFraction;
          float effectivePrice  = gridFraction * Prices.CurrentEnergyImportPriceTotal
                                + batteryFraction * _batteryAvgCostPerKwh;

          // Maintain totals in memory — never read back from the entity to avoid the
          // MQTT read-modify-write race that causes exponential accumulation.
          schedLoad.TotalEnergyKwh += diff;
          schedLoad.TotalCostEur   += diff * effectivePrice;
          if (schedLoad.TotalEnergyKwhEntity is not null)
            await PVCC_EntityManager.SetStateAsync(schedLoad.TotalEnergyKwhEntity.EntityId, schedLoad.TotalEnergyKwh.ToString(CultureInfo.InvariantCulture));
          if (schedLoad.TotalCostEurEntity is not null)
            await PVCC_EntityManager.SetStateAsync(schedLoad.TotalCostEurEntity.EntityId, schedLoad.TotalCostEur.ToString(CultureInfo.InvariantCulture));
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
        ImportPrices = Prices.PriceListImport,
        ExportPrices = Prices.PriceListExport,
        LoadPredictionWh = Prediction_Load.TodayAndTomorrow,
        PVPredictionWh = Prediction_PV.TodayAndTomorrow,
        ExtraLoads = extraLoads ?? [],
        ForceCharge = ForceCharge,
        OpportunisticDischarge = OpportunisticDischarge,
        ForceChargeMaxPrice = Prices.ForceChargeMaxPrice,
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
        FindLoadWindow(load, baseInput, baseResult, baseForceChargeSlots);

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
    private void FindLoadWindow(SchedulableLoadRuntime load, SimulationInput baseInput, List<SimulationSlot> baseResult, HashSet<DateTime> baseForceChargeSlots)
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

      // Binary-search for the MAXIMUM session that satisfies each step's conditions.
      // All condition predicates are monotone: a shorter session is never harder to satisfy,
      // so the search returns the longest session that still passes — e.g. for Optimal this is
      // "charge the EV as long as possible while the house battery still reaches ~100% via PV".
      //
      // Steps 1+2 cap to today's PV window (EV only during daytime so the house battery is not
      // drained overnight by a long session). Step 3 (PriorityPlus only) extends to next sunrise
      // to allow cheap overnight grid charging.

      // Helpers — all capture the local scope (currentSlot, chargeRateW, load, baseInput, …)
      List<SimulationSlot> RunSim(DateTime end)
      {
        var testLoad = new ExtraLoad { Name = load.Config.Name, Priority = load.Config.Priority, StartTime = currentSlot, EndTime = end, PowerW = chargeRateW };
        return EnergySimulator.Simulate(SimWithExtraLoads(baseInput, [.. baseInput.ExtraLoads, testLoad]));
      }
      // HasNewGrid: true if the test simulation adds ANY new force_charge slot not in the baseline.
      // Checking only the overnight window (as before) missed daytime force_charge caused by EV drain —
      // the simulation would schedule a grid top-up at e.g. 16:00 (before sunset) to prevent the battery
      // going below AbsMin, which HasNewGrid didn't detect because 16:00 < LastRelevantPVEnergyToday.
      bool HasNewGrid(List<SimulationSlot> sim) => sim.Any(s =>
        s.State.Mode == InverterModes.force_charge
        && !baseForceChargeSlots.Contains(s.Time));
      // IsGridCheap: true if all new grid imports (at any time) are at or below ForceChargeMaxPrice.
      bool IsGridCheap(List<SimulationSlot> sim) => !sim.Any(s =>
        s.State.Mode == InverterModes.force_charge
        && !baseForceChargeSlots.Contains(s.Time)
        && !Prices.PriceListImport.Any(p => p.StartTime <= s.Time && p.EndTime > s.Time && p.Price <= Prices.ForceChargeMaxPrice));

      // Binary search: max session end in (currentSlot, maxEnd] satisfying predicate.
      // Predicate must be monotone: shorter session → easier to satisfy.
      // Returns null if no 15-min session satisfies it.
      DateTime? FindMax(DateTime maxEnd, Func<List<SimulationSlot>, bool> predicate)
        => FindMaxSessionEnd(currentSlot, maxEnd, RunSim, predicate);

      // Today's and tomorrow's window ends (capped to full session duration if shorter)
      var todayMax    = Min(currentSlot.AddMinutes(durationMinutes), LastRelevantPVEnergyToday);
      var tomorrowMax = Min(currentSlot.AddMinutes(durationMinutes), FirstRelevantPVEnergyTomorrow);

      if (todayMax <= currentSlot && tomorrowMax <= currentSlot)
      { SetResult([], false, $"No charging window before next PV ({load.Config.Name})", null); return; }

      // Step 1: Optimal — house reaches ~100% even WITH EV running; overnight OK; no new grid.
      // Optimal mode only. Session extends to tomorrowMax so the load can drain the battery
      // overnight after it reaches 100 % via PV — matching the mode definition:
      //   "after reaching 100% AND after PV stops, Optimal may run until house SoC hits minSoC."
      // Priority/PriorityPlus skip this step and use Step 2 (no 100% requirement).
      if (load.Mode == LoadSchedulingMode.Optimal)
      {
        var end = FindMax(tomorrowMax, sim =>
          SimWillReachMaxSocToday(sim, now) && SimOvernightMinSocOk(sim) && !HasNewGrid(sim));
        if (end is not null)
        {
          SetResult([new ExtraLoad { Name = load.Config.Name, Priority = load.Config.Priority, StartTime = currentSlot, EndTime = end.Value, PowerW = chargeRateW }],
            true, $"Charging (Optimal {load.Config.Name}: {load.CurrentLevel:F0} → {load.TargetLevel:F0}{load.Config.LevelUnit}, bat={BatterySoc}%)", end);
          return;
        }
      }

      // Step 2: Priority — overnight OK, no new grid; house does NOT need to reach 100%.
      // Priority and PriorityPlus only. Uses tomorrowMax so a continuous session can extend
      // past sunset — the EV drains the battery from whatever level it reaches at dusk down to minSoC.
      // Priority always enforces PreferredMinSoC (regardless of EnforcePreferredSoC setting);
      // PriorityPlus may go to AbsoluteMinSoC when EnforcePreferredSoC is off.
      if (load.Mode is LoadSchedulingMode.Priority or LoadSchedulingMode.PriorityPlus)
      {
        bool priorityEnforcePreferred = load.Mode == LoadSchedulingMode.Priority;
        var end = FindMax(tomorrowMax, sim => SimOvernightMinSocOk(sim, priorityEnforcePreferred) && !HasNewGrid(sim));
        if (end is not null)
        {
          SetResult([new ExtraLoad { Name = load.Config.Name, Priority = load.Config.Priority, StartTime = currentSlot, EndTime = end.Value, PowerW = chargeRateW }],
            true, $"Charging (Priority {load.Config.Name}: {load.CurrentLevel:F0} → {load.TargetLevel:F0}{load.Config.LevelUnit}, bat={BatterySoc}%)", end);
          return;
        }
      }

      // Step 3: PriorityPlus — base-case overnight OK; any new grid import only at cheap prices.
      // The EV session extends into the overnight window; what matters is that the battery would
      // survive the night without the EV (base case), and the EV runs on cheap grid power.
      if (load.Mode == LoadSchedulingMode.PriorityPlus && SimOvernightMinSocOk(baseResult))
      {
        var end = FindMax(tomorrowMax, sim => IsGridCheap(sim));
        if (end is not null)
        {
          SetResult([new ExtraLoad { Name = load.Config.Name, Priority = load.Config.Priority, StartTime = currentSlot, EndTime = end.Value, PowerW = chargeRateW }],
            true, $"Charging (PriorityPlus {load.Config.Name}: {load.CurrentLevel:F0} → {load.TargetLevel:F0}{load.Config.LevelUnit}, bat={BatterySoc}%)", end);
          return;
        }
      }

      SetResult([], false, $"No valid window ({load.Mode} {load.Config.Name}: {load.CurrentLevel:F0}{load.Config.LevelUnit}, bat={BatterySoc}%)", null);
    }

    /// <summary>True if the test simulation shows house battery reaching ≥ 99 % today.</summary>
    private static bool SimWillReachMaxSocToday(List<SimulationSlot> result, DateTime now)
      => result.Any(s => s.Time.Date == now.Date && s.SoC >= 99);

    /// <summary>
    /// True if the test simulation shows the battery stays above the effective minimum SoC
    /// throughout the overnight window (sunset today → first PV tomorrow).
    /// Uses PreferredMinimalSoC when EnforcePreferredSoC is set or <paramref name="alwaysEnforcePreferred"/> is true;
    /// AbsoluteMinimalSoC otherwise (only PriorityPlus with enforce off).
    /// </summary>
    private bool SimOvernightMinSocOk(List<SimulationSlot> result, bool alwaysEnforcePreferred = false)
    {
      int minSoC = (alwaysEnforcePreferred || EnforcePreferredSoC) ? PreferredMinimalSoC : AbsoluteMinimalSoC;
      var overnight = result.Where(s => s.Time >= LastRelevantPVEnergyToday && s.Time <= FirstRelevantPVEnergyTomorrow).ToList();
      return overnight.Count == 0 || overnight.Min(s => s.SoC) >= minSoC;
    }

    /// <summary>
    /// Binary-searches for the maximum session end in (currentSlot, maxEnd] where predicate
    /// is satisfied by the simulation result. Assumes predicate is monotone: if it holds for
    /// a session ending at T, it also holds for any session ending before T. Returns null if
    /// even a single 15-minute session fails the predicate.
    /// </summary>
    private static DateTime? FindMaxSessionEnd(
      DateTime currentSlot, DateTime maxEnd,
      Func<DateTime, List<SimulationSlot>> runSim,
      Func<List<SimulationSlot>, bool> predicate)
    {
      int maxSteps = (int)(maxEnd - currentSlot).TotalMinutes / 15;
      if (maxSteps <= 0) return null;

      // Fast path: longest session satisfies — most common case.
      if (predicate(runSim(currentSlot.AddMinutes(maxSteps * 15))))
        return currentSlot.AddMinutes(maxSteps * 15);

      // Binary search: find rightmost step in [1, maxSteps-1] where predicate is true.
      int lo = 1, hi = maxSteps - 1, best = -1;
      while (lo <= hi)
      {
        int mid = (lo + hi) / 2;
        if (predicate(runSim(currentSlot.AddMinutes(mid * 15))))
        { best = mid; lo = mid + 1; }
        else
        { hi = mid - 1; }
      }
      return best >= 0 ? currentSlot.AddMinutes(best * 15) : null;
    }

    private static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;

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
        var negativeImportPrices = Prices.PriceListImport.Where(p => p.StartTime.Date == now.Date && p.Price < 0).ToList();
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
