using System;
using System.Globalization;
using NetDaemon.HassModel.Entities;
using NetDeamon.apps.PVControl.Managers;
using NetDeamon.apps.PVControl.Predictions;
using NetDeamon.apps.PVControl.Simulator;
using NetDeamon.apps.PVControl.WarmWater;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NetDaemon.Client;
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
    private readonly RunningIntAverage _loadRunningAverage;
    private readonly RunningIntAverage _pvRunningAverage;
    private readonly RunningIntAverage _gridRunningAverage;
    private string _currentInverterRunMode = "unknown";
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
      Battery = new BatteryState();
      Costs = new CostTracker();

      _loadRunningAverage = new RunningIntAverage(TimeSpan.FromMinutes(5));
      if (PVCC_Config.CurrentHouseLoadEntity is null)
        throw new NullReferenceException("HouseLoadEntity not available");
      if (PVCC_Config.CurrentHouseLoadEntity.TryGetStateValue(out int load))
        _loadRunningAverage.AddValue(load);

      _pvRunningAverage = new RunningIntAverage(TimeSpan.FromMinutes(15));
      if (PVCC_Config.CurrentPVPowerEntity is null)
        throw new NullReferenceException("CurrentPVPowerEntity not available");
      if (PVCC_Config.CurrentPVPowerEntity.TryGetStateValue(out int pv))
        _pvRunningAverage.AddValue(pv);

      _gridRunningAverage = new RunningIntAverage(TimeSpan.FromMinutes(1));
      if (PVCC_Config.CurrentGridPowerEntity.TryGetStateValue(out int grid))
        _gridRunningAverage.AddValue(grid);

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
      if (!string.IsNullOrEmpty(PVCC_Config.WarmWater?.HistoryDbColumn))
        excludeColumns.Add(PVCC_Config.WarmWater.HistoryDbColumn);
      Prediction_Load = new HourlyWeightedAverageLoadPrediction(PVCC_Config.DBFullLocation, 10, excludeColumns: excludeColumns);

      if (PVCC_Config.ForecastPVEnergyTodayEntities is null || PVCC_Config.ForecastPVEnergyTomorrowEntities is null)
        throw new NullReferenceException("PV Forecast entities are not available");
      Prediction_PV = new OpenMeteoSolarForecastPrediction(PVCC_Config.ForecastPVEnergyTodayEntities, PVCC_Config.ForecastPVEnergyTomorrowEntities);

      Prediction_NetEnergy = new NetEnergyPrediction(Prediction_PV, Prediction_Load, _loadRunningAverage, _pvRunningAverage, true);

      if (PVCC_Config.BatterySoCEntity is null)
        throw new NullReferenceException("BatterySoCEntity not available");
      Prediction_BatterySoC = new BatterySoCPrediction();
      Battery.SoCPrediction = Prediction_BatterySoC;

      Snapshots = new DailySnapshots(Prediction_PV, Prediction_Load, Prediction_BatterySoC, () => _simulationResult!);

      // Subscribe to every candidate price entity (not just the one currently resolved as
      // "active") so failover to/from a fallback provider is picked up live instead of only
      // after a restart re-evaluates CurrentImportPriceEntity/CurrentExportPriceEntity fresh.
      foreach (var priceEntity in PVCC_Config.CurrentImportPriceEntities)
        priceEntity.StateAllChanges().SubscribeAsync(async _ => await UserStateChanged(priceEntity));
      if (PVCC_Config.CurrentExportPriceEntities is not null)
        foreach (var priceEntity in PVCC_Config.CurrentExportPriceEntities)
          priceEntity.StateAllChanges().SubscribeAsync(async _ => await UserStateChanged(priceEntity));
      PVCC_Config.CurrentBatteryPowerEntity?.StateChanges().SubscribeAsync(async _ => await UserStateChanged(PVCC_Config.CurrentBatteryPowerEntity));
      PVCC_Config.CurrentPVPowerEntity?.StateChanges().SubscribeAsync(async _ => await UserStateChanged(PVCC_Config.CurrentPVPowerEntity));
      PVCC_Config.CurrentHouseLoadEntity?.StateChanges().SubscribeAsync(async _ => await UserStateChanged(PVCC_Config.CurrentHouseLoadEntity));
      PVCC_Config.DailyExportEnergyEntity.StateChanges().SubscribeAsync(async _ => await UserStateChanged(PVCC_Config.DailyExportEnergyEntity));
      PVCC_Config.DailyImportEnergyEntity.StateChanges().SubscribeAsync(async _ => await UserStateChanged(PVCC_Config.DailyImportEnergyEntity));
      PVCC_Config.BatteryInputEnergyEntity?.StateChanges().SubscribeAsync(async _ => await UserStateChanged(PVCC_Config.BatteryInputEnergyEntity));
      PVCC_Config.CurrentGridPowerEntity.StateChanges().SubscribeAsync(async _ => await UserStateChanged(PVCC_Config.CurrentGridPowerEntity));
      PVCC_Config.InverterStatusEntity.StateChanges().SubscribeAsync(async _ => await UserStateChanged(PVCC_Config.InverterStatusEntity));

      if (PVCC_Config.WarmWater is { } wwConfig)
        WarmWater = new WarmWaterController(wwConfig);

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

    }
    // ── Sub-objects ───────────────────────────────────────────────────────────────────────
    public BatteryState Battery { get; }
    public CostTracker Costs { get; }
    public DailySnapshots Snapshots { get; }

    /// <summary>UserSetting: ForceCharge to 100%</summary>
    public bool EnableCheapForceCharge { get; set; }
    /// <summary>
    /// UserSetting: Discharge if the export price is high and we can stay over preferred minimal SoC and still reach 100% SoC
    /// </summary>
    public bool OpportunisticDischarge { get; set; }
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
    /// <summary>
    /// Restart hysteresis band (in SoC %) applied above the Priority/PriorityPlus SoC floor in
    /// FindLoadWindow — see LoadSchedulingDecision.PrioritySoCGateOk.
    /// </summary>
    private const int PrioritySoCHysteresisPct = 5;
    /// <summary>
    /// Minimum SoC % above the hard floor required before the PV-bypass exception in
    /// PrioritySoCGateOk is even considered. Without this, the bypass could fire the instant SoC
    /// ticked up to exactly the floor (since a typical PV reading often already exceeds a single
    /// load's charge rate), restarting and draining straight back under the floor within
    /// minutes — see PrioritySoCGateOk's doc comment.
    /// </summary>
    private const int PvBypassMinSocBufferPct = 3;
    /// <summary>Sum of average power (W) from all schedulable loads that are actively drawing above their minimum threshold.</summary>
    private int ActiveSchedulableLoadPowerW() => SchedulableLoads
      .Where(l => l.PowerAverage != null && l.PowerAverage.GetAverage() > l.Config.MinActivePowerW)
      .Sum(l => l.PowerAverage!.GetAverage()) + (WarmWater?.ActivePowerW ?? 0);

    /// <summary>Warm-water tank planner; null when not configured.</summary>
    public WarmWaterController? WarmWater { get; }

    /// <summary>
    /// Updates the load's learned typical power from an instantaneous reading (see
    /// ChargePowerLearner); persisted when the rounded value moves by 10 W. Also ends the
    /// energy-per-level measurement once the load has stopped.
    /// </summary>
    private static async Task LearnPowerAsync(SchedulableLoadRuntime load, float measuredW)
    {
      if (!load.IsDrawingPower)
        load.EnergyLearner.Reset();

      if (load.PowerLearner.Observe(DateTime.Now, measuredW, load.Config.MinActivePowerW, load.LearnedPower) is not { } learned)
        return;
      int before = load.LearnedPowerW;
      load.LearnedPower = learned;
      if (load.LearnedPowerW / 10 != before / 10 && load.ChargePowerEntity is not null)
        await PVCC_EntityManager.SetStateAsync(load.ChargePowerEntity.EntityId, load.LearnedPowerW.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>Updates the load's learned energy per level unit from the energy counter and level sensor.</summary>
    private static async Task LearnEnergyPerLevelAsync(SchedulableLoadRuntime load)
    {
      if (!load.IsDrawingPower || load.Config.CurrentLevelEntity is null
          || !load.Config.CurrentLevelEntity.TryGetStateValue(out float level))
        return;
      if (load.EnergyLearner.Observe(level, load.TotalEnergyKwh, load.EnergyPerLevelKwh) is not { } estimate)
        return;
      bool persist = MathF.Round(estimate, 3) != MathF.Round(load.EnergyPerLevelKwh, 3);
      load.EnergyPerLevelKwh = estimate;
      if (persist && load.EnergyPerLevelEntity is not null)
        await PVCC_EntityManager.SetStateAsync(load.EnergyPerLevelEntity.EntityId, estimate.ToString("F3", CultureInfo.InvariantCulture));
    }

    private async Task UserStateChanged(Entity entity)
    {
      if (PVCC_Config.CurrentImportPriceEntities.Any(e => e.EntityId == entity.EntityId)
        || (PVCC_Config.CurrentExportPriceEntities?.Any(e => e.EntityId == entity.EntityId) ?? false))
      {
        Prices.UpdatePriceList();
        RequestReplan("prices updated");
      }
      if (entity.EntityId == PVCC_Config.CurrentBatteryPowerEntity?.EntityId && PVCC_Config.CurrentBatteryPowerEntity.TryGetStateValue(out int bat))
      {
        Battery.AddBatteryPowerValue(bat);

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
        _loadRunningAverage.AddValue(load - ActiveSchedulableLoadPowerW());
      }
      if (entity.EntityId == PVCC_Config.CurrentPVPowerEntity?.EntityId && PVCC_Config.CurrentPVPowerEntity.TryGetStateValue(out int pv))
      {
        _pvRunningAverage.AddValue(pv);
      }
      if (entity.EntityId == PVCC_Config.DailyExportEnergyEntity?.EntityId && PVCC_Config.DailyExportEnergyEntity.TryGetStateValue(out float export))
      {
        await Costs.OnExportEnergyChangedAsync(export, Prices.PriceListExport);
      }
      if (entity.EntityId == PVCC_Config.DailyImportEnergyEntity?.EntityId && PVCC_Config.DailyImportEnergyEntity.TryGetStateValue(out float import))
      {
        await Costs.OnImportEnergyChangedAsync(import, Prices.PriceListImport, Prices.CurrentEnergyImportPriceEnergyOnly, Prices.CurrentEnergyImportPriceNetworkOnly);
      }
      if (entity.EntityId == PVCC_Config.BatteryInputEnergyEntity?.EntityId && PVCC_Config.BatteryInputEnergyEntity.TryGetStateValue(out float batInput))
      {
        int gridPowerW = PVCC_Config.CurrentGridPowerEntity.TryGetStateValue(out int gp) ? gp : 0;
        await Costs.OnBatteryInputEnergyChangedAsync(batInput, gridPowerW, Prices.PriceListImport);
      }
      foreach (var schedLoad in SchedulableLoads.Where(l => l.Config.ActualPowerEntity is not null
        && entity.EntityId == l.Config.ActualPowerEntity!.EntityId))
      {
        if (schedLoad.Config.ActualPowerEntity!.TryGetStateValue(out float p))
        {
          schedLoad.PowerAverage!.AddValue((int)Math.Round(p));
          await LearnPowerAsync(schedLoad, p);
        }
      }
      foreach (var schedLoad in SchedulableLoads.Where(l => l.Config.ActualEnergyEntity is not null
        && entity.EntityId == l.Config.ActualEnergyEntity!.EntityId))
      {
        if (!schedLoad.Config.ActualEnergyEntity!.TryGetStateValue(out float energy, numericalGetBaseValue: false))
          continue;
        float diff = energy - schedLoad.LastEnergySum;
        // Clamp: a single delta larger than 2 h at the load's typical power is a sensor glitch.
        float maxDiff = Math.Max(schedLoad.Config.AvgPowerW, schedLoad.LearnedPowerW) * 2f / 1000f;
        if (diff > maxDiff)
        {
          PVCC_Logger.LogWarning("Ignoring implausibly large energy delta {Diff:F3} kWh (>{Max:F3}) for {Name} — likely sensor glitch",
            diff, maxDiff, schedLoad.Config.Name);
        }
        else if (diff > 0)
        {
          // Decompose energy source: PV direct (free) → grid direct → battery (avg cost).
          int schedPowerW = schedLoad.PowerAverage?.GetAverage() ?? schedLoad.LearnedPowerW;
          float pvSurplusW = Math.Max(0f, CurrentAveragePVPower - CurrentAverageHouseLoad);
          float pvFraction      = schedPowerW > 0 ? Math.Min(1f, pvSurplusW / schedPowerW) : 0f;
          float remainFraction  = 1f - pvFraction;
          float gridFraction    = schedPowerW > 0 ? Math.Min(remainFraction, Math.Max(0f, CurrentAverageGridPower) / schedPowerW) : 0f;
          float batteryFraction = remainFraction - gridFraction;
          float effectivePrice  = gridFraction * Prices.PriceListImport.GetPrice(DateTime.Now)
                                + batteryFraction * Costs.BatteryAvgCostPerKwh;

          // Maintain totals in memory — never read back from the entity to avoid the
          // MQTT read-modify-write race that causes exponential accumulation.
          schedLoad.TotalEnergyKwh += diff;
          schedLoad.TotalCostEur   += diff * effectivePrice;
          if (schedLoad.TotalEnergyKwhEntity is not null)
            await PVCC_EntityManager.SetStateAsync(schedLoad.TotalEnergyKwhEntity.EntityId, schedLoad.TotalEnergyKwh.ToString(CultureInfo.InvariantCulture));
          if (schedLoad.TotalCostEurEntity is not null)
            await PVCC_EntityManager.SetStateAsync(schedLoad.TotalCostEurEntity.EntityId, schedLoad.TotalCostEur.ToString(CultureInfo.InvariantCulture));
          await LearnEnergyPerLevelAsync(schedLoad);
        }
        schedLoad.LastEnergySum = energy;
      }
      if (entity.EntityId == PVCC_Config.CurrentGridPowerEntity?.EntityId && PVCC_Config.CurrentGridPowerEntity.TryGetStateValue(out int grid))
      {
        _gridRunningAverage.AddValue(grid);
      }
      if (entity.EntityId == PVCC_Config.InverterStatusEntity?.EntityId && PVCC_Config.InverterStatusEntity.TryGetStateValue(out string inverterStatus)
          && inverterStatus != _currentInverterRunMode)
      {
        PVCC_Logger.LogInformation("Inverter RunMode changed from {CurrentInverterRunMode} to {InverterStatus}", _currentInverterRunMode, inverterStatus);
        if (IsReturnToNormalMode(_currentInverterRunMode, inverterStatus, PVCC_Config.InverterStatusNormalString))
        {
          _resetCounter = 2;
          PVCC_Logger.LogInformation("Inverter returned to normal run mode, sending {ResetCounter} reset signal(s)", _resetCounter);
          _currentMode = new InverterState(InverterModes.reset, ForceChargeReasons.None);
        }
        _currentInverterRunMode = inverterStatus;
      }
    }

    /// <summary>
    /// Whether the inverter came back to normal mode from another mode (not from remote mode),
    /// so the reset signal must be sent before switching back to the selected mode. Only a real
    /// change counts: when the integration briefly reports "unknown" (live 2026-10-19 14:03:20 –
    /// 14:03:34, all SolaX sensors), the unknown state isn't stored, so the reading coming back
    /// equals the last known mode and must not trigger a reset.
    /// </summary>
    public static bool IsReturnToNormalMode(string previous, string current, string normalString) =>
      current != previous && previous != "Normal (R)" && current == normalString;

    private int _resetCounter = 0;
    private int _bugFixCounter = 0;
    private readonly Dictionary<DateTime, int> _actualSoCHistory = new();
    private InverterState _currentMode = new InverterState(InverterModes.normal, ForceChargeReasons.None, true);
    private SimulationResult? _simulationResult;

    /// <summary>
    /// Seeds <see cref="_actualSoCHistory"/> from the HA history API on startup, so past SoC slots
    /// are populated with real values immediately rather than waiting for the first tick.
    /// </summary>
    public async Task SeedSoCHistoryAsync(CancellationToken ct)
    {
      var midnight = DateTime.Now.Date;
      var (ok, history) = await PVCC_ApiManager.GetEntityHistoryAsync(
        PVCC_Config.BatterySoCEntity, midnight, ct, getMinimal: true, endDateTime: DateTime.Now);
      if (!ok) return;

      // Parse and sort all valid readings into a local-time ordered list.
      // Using "last reading at or before each slot boundary" rather than rounding each entry
      // to the nearest slot — this avoids brief sensor glitches (inverter briefly reporting
      // wrong values) corrupting a slot when the bad reading happens to be the last change
      // in a rounding window.
      var readings = history
        .Where(e => int.TryParse(e.State, out _))
        .Select(e => (Time: e.LastChanged.ToLocalTime(), Soc: int.Parse(e.State)))
        .OrderBy(e => e.Time)
        .ToList();
      if (readings.Count == 0) return;

      int idx = 0;
      for (var slot = midnight; slot < DateTime.Now; slot = slot.AddMinutes(15))
      {
        // Advance to the last reading whose time is at or before this slot boundary.
        while (idx + 1 < readings.Count && readings[idx + 1].Time <= slot)
          idx++;
        if (readings[idx].Time <= slot)
          _actualSoCHistory[slot] = readings[idx].Soc;
      }
    }

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

      // Build the base SimulationInput without extra loads. FindEVChargingWindow will run
      // multiple test simulations to find the valid EV charging window, then we run the
      // final simulation with those EV ExtraLoads included.
      var baseInput = new SimulationInput
      {
        StartTime = now,
        StartSocPercent = Battery.BatterySoc,
        BatteryCapacityWh = Battery.BatteryCapacity,
        AbsoluteMinSocPercent = Battery.AbsoluteMinimalSoC,
        PreferredMinSocPercent = Battery.PreferredMinimalSoC,
        EnforcePreferredSoc = Battery.EnforcePreferredSoC,
        MaxChargePowerAmps = PVCC_Config.MaxBatteryChargePower,
        InverterEfficiency = Battery.InverterEfficiency,
        ImportPrices = Prices.PriceListImport,
        ExportPrices = Prices.PriceListExport,
        LoadPredictionWh = Prediction_Load.TodayAndTomorrow,
        PVPredictionWh = NetEnergyPrediction.WithRunningAvgCorrection(Prediction_PV.TodayAndTomorrow, _pvRunningAverage.GetAverage(), ActualPVEnergyTodayWh(), now),
        ExtraLoads = extraLoads ?? [],
        EnableCheapForceCharge = EnableCheapForceCharge,
        OpportunisticDischarge = OpportunisticDischarge,
        ForceChargeMaxPrice = Prices.ForceChargeMaxPrice,
        ForceChargeTargetSocPercent = ForceChargeTargetSoC,
        CurrentMode = _currentMode,
      };

      // Run the baseline simulation once — its grid import and PV window boundaries
      // are used by every FindLoadWindow call to compare against test simulations.
      var baseResult = EnergySimulator.Simulate(baseInput);

      // House load staying off its forecast means the plans assumed a load that isn't happening.
      if (_loadDeviation.Observe(now, CurrentAverageHouseLoad, Prediction_Load.CurrentValue * 4))
        RequestReplan("house load off forecast");

      // Warm water first: the tank needs heating whatever the other loads do, so the loads below
      // plan against a baseline that already contains it.
      if (WarmWater is not null)
      {
        if (WarmWater.ReplanReason(now) is { } wwReason)
          WarmWater.Replan(baseInput, baseResult, now, wwReason);
        var wwLoads = WarmWater.ExtraLoads(now);
        if (wwLoads.Count > 0)
        {
          baseInput = baseInput.WithExtraLoads([.. baseInput.ExtraLoads, .. wwLoads]);
          baseResult = EnergySimulator.Simulate(baseInput);
        }
      }

      // Find valid window for each schedulable load (highest priority first) — only when it's
      // due (ReplanPolicy); otherwise its last decision stays and is simulated below as is.
      foreach (var load in SchedulableLoads.OrderByDescending(l => l.Config.Priority))
      {
        float level = load.Config.CurrentLevelEntity is not null && load.Config.CurrentLevelEntity.TryGetStateValue(out float lv) ? lv : -1;
        var snapshot = new ReplanSnapshot(now, Battery.BatterySoc, level, load.IsDrawingPower, IsLatched(load, now));
        var reason = ReplanPolicy.Reason(load.LastPlan, snapshot, load.Config.ReplanIntervalMinutes, load.TakeReplanRequest());
        if (reason is null) continue;
        FindLoadWindow(load, baseInput, baseResult);
        load.LastPlan = snapshot with { Latched = IsLatched(load, now) };
        load.LastPlanReason = reason;
      }

      // Run final simulation with all found ExtraLoads merged in.
      var allExtraLoads = SchedulableLoads.SelectMany(l => l.ExtraLoads).ToList();
      var finalInput = allExtraLoads.Count > 0
        ? baseInput.WithExtraLoads([.. baseInput.ExtraLoads, .. allExtraLoads])
        : baseInput;

      _simulationResult = EnergySimulator.Simulate(finalInput);

      // Build the two-day SoC dict for Prediction_BatterySoC:
      //   - simulation covers now→end-of-tomorrow (filled below from _simulationResult)
      //   - past slots of today (midnight→now) use actual SoC history from HA (seeded on
      //     startup), falling back to previously predicted values for slots after startup.
      var fullSoC = new Dictionary<DateTime, int>();
      fullSoC.ClearAndCreateEmptyPredictionData(); // fills today 00:00 → tomorrow 23:45 with 0s

      var startSlot = now.RoundToNearestQuarterHour();
      foreach (var slot in _simulationResult.Slots)
        if (fullSoC.ContainsKey(slot.Time))
          fullSoC[slot.Time] = slot.SoC;

      foreach (var t in fullSoC.Keys.Where(k => k < startSlot).ToList())
        fullSoC[t] = _actualSoCHistory.GetValueOrDefault(t, Prediction_BatterySoC.TodayAndTomorrow.GetValueOrDefault(t, 0));

      Prediction_BatterySoC.UpdateData(fullSoC);
    }

    // ── Schedulable load window finding ──────────────────────────────────────────────────────
    // The simulation is the oracle: we run it with candidate ExtraLoad windows and check
    // whether the result satisfies the mode-specific conditions. The first valid start slot
    // determines ChargeNow and the ExtraLoads injected into the final simulation.

    /// <summary>
    /// Finds the valid scheduling window for a load by iterating over candidate start slots
    /// and running a test simulation for each. Updates load.ChargeNow/ChargeReason/PredictedEnd.
    /// The baseline and its grid-supply slots are pre-computed once in RunSimulation.
    /// </summary>
    private void FindLoadWindow(SchedulableLoadRuntime load, SimulationInput baseInput, SimulationResult baseResult)
    {
      bool wasActive = load.ChargeNow;
      DecideLoadWindow(load, baseInput, baseResult);

      var now = DateTime.Now;
      var settings = new SessionStartSettings(
        baseInput.PreferredMinSocPercent, baseInput.AbsoluteMinSocPercent, baseInput.EnforcePreferredSoc,
        baseInput.ForceChargeMaxPrice, load.TargetLevel, load.ReadyByTime);

      // Min-off latch: a restart within MinOffMinutes of the last stop is held back — the planned
      // session moves to the end of the off-time. Only restarts: stops are never delayed.
      if (load.ChargeNow && !wasActive
          && SchedulableLoadRuntime.OffLatchUntil(load.SessionStopTime, load.Config.MinOffMinutes, now,
               load.SessionStopMode == load.Mode && load.SessionStopSettings == settings) is { } until)
      {
        var notBefore = until.RoundToNearestQuarterHour();
        if (notBefore < until) notBefore = notBefore.AddMinutes(15);
        var delayed = SchedulableLoadRuntime.DelayUntil(load.ExtraLoads, notBefore);
        load.ExtraLoads = delayed;
        load.PredictedEnd = delayed.Count > 0 ? delayed.Max(e => e.EndTime) : load.PredictedEnd;
        load.ChargeNow = false;
        load.ChargeReason = $"Off-latched ({load.Config.Name}: {(now - load.SessionStopTime!.Value).TotalMinutes:F0}/{load.Config.MinOffMinutes} min min-off) — {load.ChargeReason}";
      }

      // Session bookkeeping from the cycle's FINAL decision only: a lower ruleset's "no" that a
      // higher one overrides within the same cycle (e.g. Priority's dusk stop, then PriorityPlus's
      // price rule) must not end the session, or the min-on latch would lose its start time.
      if (load.ChargeNow && !wasActive)
      {
        load.SessionStartTime = now;
        load.SessionStartMode = load.Mode;
        load.SessionStartSettings = settings;
        load.SessionStopTime = null;
      }
      else if (!load.ChargeNow)
      {
        if (wasActive)
        {
          load.SessionStopTime = now;
          load.SessionStopMode = load.Mode;
          load.SessionStopSettings = settings;
        }
        load.SessionStartTime = null;
        load.SessionStartMode = null;
        load.SessionStartSettings = null;
      }
    }

    private void DecideLoadWindow(SchedulableLoadRuntime load, SimulationInput baseInput, SimulationResult baseResult)
    {
      var now = DateTime.Now;
      var currentSlot = now.RoundToNearestQuarterHour();
      bool wasActive = load.ChargeNow;
      // Last cycle's final result. Rulesets lower in the climb may SetResult before the latch or
      // the PriorityPlus scan run (e.g. Optimal's "scheduled tomorrow" outside PV hours), so
      // those must read these copies, not load.ExtraLoads/PredictedEnd as they are by then.
      var previousLoads = load.ExtraLoads;
      var previousEnd = load.PredictedEnd;
      load.PlannedSessions = [];

      // Tracks whether ANY call to SetResult has happened yet this cycle. Checked by the
      // ladder's generic "No valid window" catch-all at the very end: once set, a ruleset's own
      // (possibly deferred, possibly more specific) result stands as final, and the generic
      // catch-all is skipped rather than overwriting it.
      bool decided = false;

      void SetResult(List<ExtraLoad> extraLoads, bool chargeNow, string reason, DateTime? end)
      {
        decided = true;
        load.ExtraLoads = extraLoads;
        load.ChargeNow = chargeNow;
        load.ChargeReason = reason;
        load.PredictedEnd = end;
      }

      if (load.Mode == LoadSchedulingMode.Off)
      { SetResult([], false, "Off", null); return; }

      if (load.Config.AvailabilityEntity is not null &&
          (!load.Config.AvailabilityEntity.TryGetStateValue(out bool available) || !available))
      { SetResult([], false, $"{load.Config.Name} not available (not present/plugged in)", null); return; }

      if (load.Config.CurrentLevelEntity is null || load.EnergyPerLevelKwh <= 0)
      { SetResult([], false, "CurrentLevelEntity or energy per level not configured", null); return; }

      if (load.CurrentLevel >= load.TargetLevel)
      { SetResult([], false, $"Target reached ({load.CurrentLevel:F0}{load.Config.LevelUnit} ≥ {load.TargetLevel:F0}{load.Config.LevelUnit})", null); return; }

      int chargeRateW = load.EffectivePowerW;
      if (chargeRateW <= 0)
      { SetResult([], false, "EffectivePowerW is 0 — check the Charge Power number / AvgPowerW config", null); return; }

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
      // Every step's session end is capped by tomorrowMax (not todayMax — computed below only
      // for the early "no window at all" bail-out): each mode's own predicate (WillReachMaxSocToday,
      // IsOvernightMinSocOk, …) is what actually constrains how far a session may run, letting a
      // session that already satisfies its mode's requirement extend into the evening/overnight
      // — e.g. Optimal draining the battery after reaching 100% via PV, or Priority/PriorityPlus
      // continuing to drain past sunset down to minSoC.

      // Helpers — all capture the local scope (currentSlot, chargeRateW, load, baseInput, …)
      // RunSim returns a full SimulationResult so predicates can use its derived properties
      // (WillReachMaxSocToday, IsOvernightMinSocOk, HasNewGridVs, IsGridCheapVs) directly.
      SimulationResult RunSimFrom(DateTime start, DateTime end)
      {
        var testLoad = new ExtraLoad { Name = load.Config.Name, Priority = load.Config.Priority, StartTime = start, EndTime = end, PowerW = chargeRateW };
        return EnergySimulator.Simulate(baseInput.WithExtraLoads([.. baseInput.ExtraLoads, testLoad]));
      }
      SimulationResult RunSim(DateTime end) => RunSimFrom(currentSlot, end);

      // True when a session from `start` to `end` is long enough to avoid the min-window
      // oscillation guard — either the load's natural duration is already shorter than
      // MinWindowMinutes (it ends cleanly via "target reached" next cycle, so a marginal window
      // flipping on/off isn't a concern), no minimum is configured, or the window itself is
      // already at least that long. Shared by every "found a session" call site below —
      // FindMax and each real-time gate's recovery-slot prediction — so the guard's definition
      // can't drift between them.
      bool MeetsMinWindow(DateTime start, DateTime end) =>
        durationMinutes < load.Config.MinWindowMinutes ||
        load.Config.MinWindowMinutes <= 0 ||
        (end - start).TotalMinutes >= load.Config.MinWindowMinutes;

      // Binary search: max session end in (currentSlot, maxEnd] satisfying predicate.
      // Predicate must be monotone: shorter session → easier to satisfy.
      // Returns null if no 15-min session satisfies it, or if the found window is shorter
      // than MinWindowMinutes (prevents oscillation from marginal windows).
      DateTime? FindMax(DateTime maxEnd, Func<SimulationResult, bool> predicate)
      {
        var end = FindMaxSessionEnd(currentSlot, maxEnd, RunSim, predicate);
        return end is not null && MeetsMinWindow(currentSlot, end.Value) ? end : null;
      }

      // Today's and tomorrow's window ends (capped to full session duration if shorter).
      // Fall back to far-future when no PV is forecast (null) so the session is only
      // bounded by durationMinutes — same behaviour as the old PVWindows fallback.
      var farFuture   = currentSlot.AddDays(2);
      var todayMax    = Min(currentSlot.AddMinutes(durationMinutes), baseResult.LastRelevantPVEnergyToday    ?? farFuture);
      var tomorrowMax = Min(currentSlot.AddMinutes(durationMinutes), baseResult.FirstRelevantPVEnergyTomorrow ?? farFuture);

      if (todayMax <= currentSlot && tomorrowMax <= currentSlot)
      { SetResult([], false, $"No charging window before next PV ({load.Config.Name})", null); return; }

      // The published import-price horizon (today + tomorrow once published) — bounds
      // PriorityPlus's bonus ruleset's forward scan below: once prices run out there's nothing
      // left to evaluate for a price-based decision.
      var priceHorizonEnd = baseInput.ImportPrices.Select(p => p.EndTime).DefaultIfEmpty(currentSlot).Max();

      // Optional "ready by" deadline — only PriorityPlus's price scan uses it (it may then exceed
      // the price threshold to finish in time); Optimal/Priority keep their own rules.
      DateTime? readyBy = load.ReadyByTime is { } rb ? SchedulableLoadRuntime.NextOccurrence(rb, now) : null;

      // ── Numbered-mode ladder, climbed from the bottom ───────────────────────────────────────
      // Optimal/Priority/PriorityPlus (LoadSchedulingMode = 10/20/30) are each a complete,
      // self-contained ruleset. Starting from the selected mode's own ruleset, the function
      // runs Optimal's ruleset first (every mode is >= Optimal); if it returns ChargeNow=true,
      // done — nothing else matters. If it returns false, and the selected mode is higher than
      // Optimal, Priority's complete ruleset gets its turn, same logic. If that also returns
      // false and the mode is PriorityPlus, PriorityPlus's one additional (bonus) ruleset gets
      // the final say. The gaps in the mode numbering are deliberate room for a future ruleset
      // to slot in without touching this structure.
      //
      // Optimal's ruleset doesn't decide anything "about Optimal mode" — it just answers "would
      // PV alone cover this," which is equally meaningful regardless of which outer mode
      // selected it, so it needs no change to run for Priority/PriorityPlus too: only its outer
      // mode-equality check (now provided by the climb below) is gone, its internal SoC-
      // threshold logic is untouched.
      //
      // Each ruleset function returns true once it has produced a result via SetResult
      // (whether that result is ChargeNow=true or a definite/deferred "no"), and false if it
      // genuinely decided nothing at all. The latter can only happen for whichever ruleset ends
      // up being the mode's own ceiling (nothing above it to climb to), and matches today's
      // existing fallthrough behaviour exactly: it reaches the same generic "No valid window"
      // fallback at the very bottom that every mode already shares.

      // Shared by both rulesets below — fires regardless of which one's own blocks led here,
      // exactly where it always has. The simulation re-runs every 15 s and at borderline SoC
      // levels a predicate can flip on/off every cycle; once a session starts, keep it active
      // for MinWindowMinutes before letting the simulation stop it. Hard-stop safety checks
      // inside each ruleset (SoC gates, Optimal's stop floor) always run BEFORE this and take
      // immediate effect regardless of the latch — the latch is for simulation transients, not
      // hardware state.
      //
      // Bypassed when load.Mode no longer matches SessionStartMode, OR when any of the live
      // settings that fed the original decision (SessionStartSettings — min SoC floors,
      // EnforcePreferredSoc, the force-charge price threshold, the target level) have since
      // changed: the latch exists to smooth over marginal predicate flips WITHIN one decision
      // made under one set of conditions, not to keep echoing a PREVIOUS decision after the
      // user deliberately changes mode OR any of the settings that decision was based on — any
      // such manual change should take effect on the next cycle, not silently wait out up to
      // MinWindowMinutes of a decision that no longer reflects the current configuration.
      bool TryMinWindowLatch()
      {
        var currentSettings = new SessionStartSettings(
          baseInput.PreferredMinSocPercent, baseInput.AbsoluteMinSocPercent, baseInput.EnforcePreferredSoc,
          baseInput.ForceChargeMaxPrice, load.TargetLevel, load.ReadyByTime);

        if (wasActive && load.SessionStartTime.HasValue && load.Config.MinWindowMinutes > 0
            && load.SessionStartMode == load.Mode
            && load.SessionStartSettings == currentSettings)
        {
          var elapsedMin = (now - load.SessionStartTime.Value).TotalMinutes;
          if (elapsedMin < load.Config.MinWindowMinutes)
          {
            var latchedLoads = previousLoads
              .Select(e => new ExtraLoad { Name = e.Name, Priority = e.Priority, StartTime = currentSlot, EndTime = e.EndTime, PowerW = e.PowerW })
              .ToList();
            SetResult(latchedLoads, true,
              $"Latched ({load.Config.Name}: {elapsedMin:F0}/{load.Config.MinWindowMinutes} min min-on)",
              previousEnd);
            return true;
          }
        }
        return false;
      }

      // ── Optimal's ruleset ────────────────────────────────────────────────────────────────────
      // House reaches ~100% even WITH EV running; overnight OK; no new grid. Solar-surplus mode:
      // only active during the PV window (InPVPeriod) — outside PV hours the simulation sits at
      // the edge of overnight-SoC thresholds and oscillates every 15 s as tiny SoC changes flip
      // the binary-search result, so outside today's PV window this instead searches for a
      // window in tomorrow's PV period so the scheduled run is still visible on the dashboard
      // (chargeNow=false, ExtraLoad carries the future window).
      //
      // Start gate: never activates when current SoC < PreferredMinimalSoC, regardless of
      //   EnforcePreferredSoC. Prevents morning charging that would draw from an already-low
      //   battery before PV has had a chance to recharge it.
      // Stop gate (hysteresis): once active, continue only while
      //   SoC ≥ max(preferred − 10 %, absolute + 5 %) — the −10 % band prevents on/off cycling
      //   near the start threshold; the absolute+5 floor keeps the stop threshold safely above
      //   the hardware minimum even when preferred is configured close to absolute.
      void RunOptimalRuleset()
      {
        if (baseResult.CurrentPVPeriod != PVPeriods.InPVPeriod)
        {
          var tomorrowPVStart = baseResult.FirstRelevantPVEnergyTomorrow;
          var tomorrowPVEnd   = baseResult.LastRelevantPVEnergyTomorrow;
          if (tomorrowPVStart.HasValue && tomorrowPVEnd.HasValue)
          {
            var tomorrowOptMax = Min(tomorrowPVStart.Value.AddMinutes(durationMinutes), tomorrowPVEnd.Value);
            if (tomorrowOptMax > tomorrowPVStart.Value)
            {
              var end = FindMaxSessionEnd(tomorrowPVStart.Value, tomorrowOptMax,
                e => RunSimFrom(tomorrowPVStart.Value, e),
                sim => sim.WillReachMaxSocTomorrow && sim.IsOvernightMinSocOk() && !sim.HasNewGridVs(baseResult));
              if (end is not null && MeetsMinWindow(tomorrowPVStart.Value, end.Value))
              {
                SetResult(
                  [new ExtraLoad { Name = load.Config.Name, Priority = load.Config.Priority, StartTime = tomorrowPVStart.Value, EndTime = end.Value, PowerW = chargeRateW }],
                  false,
                  $"Scheduled tomorrow (Optimal {load.Config.Name}: {load.CurrentLevel:F0} → {load.TargetLevel:F0}{load.Config.LevelUnit})",
                  end);
                return;
              }
            }
          }
          SetResult([], false, $"Optimal: outside PV window ({baseResult.CurrentPVPeriod})", null);
          return;
        }

        int preferred  = Battery.PreferredMinimalSoC;
        int stopFloor  = Math.Max(preferred - 10, Battery.AbsoluteMinimalSoC + 5);
        int currentSoC = Battery.BatterySoc;

        if (!wasActive && currentSoC < preferred)
        {
          // Even though we can't start now, scan the base trajectory for when SoC is predicted
          // to recover to preferred, then search for a valid window from that point so the
          // dashboard can show a predicted start/end time.
          var recoverySlot = baseResult.Slots
            .Where(s => s.Time > currentSlot && s.SoC >= preferred)
            .Select(s => s.Time)
            .FirstOrDefault();

          if (recoverySlot != default)
          {
            bool recoveryIsToday = recoverySlot.Date == now.Date;
            var  pvEnd     = recoveryIsToday ? baseResult.LastRelevantPVEnergyToday
                                             : baseResult.LastRelevantPVEnergyTomorrow;
            var  recoveryMax = Min(recoverySlot.AddMinutes(durationMinutes), pvEnd ?? farFuture);

            if (recoveryMax > recoverySlot)
            {
              var end = FindMaxSessionEnd(recoverySlot, recoveryMax,
                e => RunSimFrom(recoverySlot, e),
                sim => (recoveryIsToday ? sim.WillReachMaxSocToday : sim.WillReachMaxSocTomorrow)
                       && sim.IsOvernightMinSocOk() && !sim.HasNewGridVs(baseResult));

              if (end is not null && MeetsMinWindow(recoverySlot, end.Value))
              {
                SetResult(
                  [new ExtraLoad { Name = load.Config.Name, Priority = load.Config.Priority, StartTime = recoverySlot, EndTime = end.Value, PowerW = chargeRateW }],
                  false,
                  $"Optimal: SoC {currentSoC}% < preferred {preferred}% — expected start ~{recoverySlot:HH:mm}", end);
                return;
              }
            }
          }

          SetResult([], false,
            $"Optimal: SoC {currentSoC}% < preferred min {preferred}% — waiting for battery to charge", null);
          return;
        }
        if (wasActive && currentSoC < stopFloor)
        {
          SetResult([], false,
            $"Optimal: SoC {currentSoC}% dropped below stop floor {stopFloor}% (preferred {preferred}% − 10%)", null);
          return;
        }

        if (TryMinWindowLatch()) return;

        var mainEnd = FindMax(tomorrowMax, sim =>
          sim.WillReachMaxSocToday && sim.IsOvernightMinSocOk() && !sim.HasNewGridVs(baseResult));
        if (mainEnd is not null)
        {
          SetResult([new ExtraLoad { Name = load.Config.Name, Priority = load.Config.Priority, StartTime = currentSlot, EndTime = mainEnd.Value, PowerW = chargeRateW }],
            true, $"Charging (Optimal {load.Config.Name}: {load.CurrentLevel:F0} → {load.TargetLevel:F0}{load.Config.LevelUnit}, bat={Battery.BatterySoc}%)", mainEnd);
        }
      }

      // ── Priority's ruleset (shared verbatim by PriorityPlus, via the climb below) ──────────
      // May draw the battery down to its configured minimum — Optimal's ruleset above already
      // established PV alone doesn't cover this. The floor is the same for Priority and
      // PriorityPlus: PreferredMinSoC when EnforcePreferredSoC is on, AbsoluteMinSoC when off.
      void RunPriorityRuleset()
      {
        // Schedule new sessions as late as possible before sunrise. Nothing recharges the
        // battery between sunset and sunrise, so the battery's lowest point of the whole night
        // is the same regardless of WHEN within that window the EV runs (same total Wh consumed
        // by sunrise either way) — the real difference is how LONG the battery sits at that
        // reduced level with little headroom left. A session that starts at dusk leaves it
        // exposed for the rest of the night; one timed to finish right as the sun comes up
        // leaves it exposed for only its own duration — minimizing the TIME an unpredicted load
        // spike the simulation's forecast never saw coming has to find a depleted battery.
        //
        // An already-active session is NOT affected here — see the dusk-stop check below, which
        // only cuts off a session once it's past its own anti-oscillation latch, not one still
        // settling in.
        if (!wasActive && baseResult.CurrentPVPeriod is PVPeriods.AfterPV or PVPeriods.BeforePV)
        {
          // The relevant upcoming sunrise: today's own (if we're already in the pre-dawn hours
          // before it) or tomorrow's (if it's still evening and today's sunrise already passed).
          var nextSunrise = baseResult.CurrentPVPeriod == PVPeriods.BeforePV
            ? baseResult.FirstRelevantPVEnergyToday
            : baseResult.FirstRelevantPVEnergyTomorrow;

          if (nextSunrise.HasValue && nextSunrise.Value > currentSlot)
          {
            // The ideal target: as late as possible while still leaving enough time to fully
            // complete the session — never earlier than now.
            var idealStart = Max(currentSlot, nextSunrise.Value.AddMinutes(-durationMinutes));

            // Does the ideal placement actually stay safe? Simulating the EV continuously from
            // idealStart through sunrise is the WORST case for that start point (longest
            // possible draw) — a shorter session from the same start can only be safer, so if
            // this passes, the full natural duration from idealStart is guaranteed safe too.
            //
            // If it does NOT pass — e.g. the house's own unassisted overnight trajectory already
            // bottoms out just above the floor right before sunrise, so even a single 15-minute
            // EV slice added anywhere pushes the whole rest of the night's (permanently lowered,
            // nothing recharges it before sunrise) trajectory under the floor — searching for a
            // SHORTER session from this same idealStart cannot help, since the problem isn't
            // duration, it's that idealStart itself is already too early for the thin remaining
            // margin. The only lever that helps is starting LATER, even at the cost of a
            // shorter, partial charge — exactly "use up as much as possible" once the ideal case
            // isn't available.
            var start = FindLatestSessionStart(idealStart, nextSunrise.Value, load.Config.MinWindowMinutes,
              s => RunSimFrom(s, nextSunrise.Value),
              sim => sim.IsOvernightMinSocOk() && !sim.HasNewGridVs(baseResult));

            if (start is not null)
            {
              // Safe for the full remaining window from `start` through sunrise, so a shorter
              // session (stopping once the EV's own energy need is met) is safe too.
              var end = Min(start.Value.AddMinutes(durationMinutes), nextSunrise.Value);

              if (MeetsMinWindow(start.Value, end))
              {
                if (start.Value <= currentSlot)
                {
                  // Last call — waiting any longer risks not finishing before sunrise. Start now.
                  SetResult(
                    [new ExtraLoad { Name = load.Config.Name, Priority = load.Config.Priority, StartTime = currentSlot, EndTime = end, PowerW = chargeRateW }],
                    true,
                    $"Charging ({load.Mode} {load.Config.Name}: last call before sunrise, {load.CurrentLevel:F0} → {load.TargetLevel:F0}{load.Config.LevelUnit}, bat={Battery.BatterySoc}%)",
                    end);
                }
                else
                {
                  // Still time to spare — defer the start to minimize how long the battery sits
                  // drawn down overnight.
                  SetResult(
                    [new ExtraLoad { Name = load.Config.Name, Priority = load.Config.Priority, StartTime = start.Value, EndTime = end, PowerW = chargeRateW }],
                    false,
                    $"Scheduled ~{start.Value:HH:mm} ({load.Mode} {load.Config.Name}: deferring to minimize overnight battery drawdown time)",
                    end);
                }
                return;
              }
            }
          }

          SetResult([], false,
            $"{load.Mode}: outside PV hours ({baseResult.CurrentPVPeriod}) — deferring to minimize overnight battery drawdown time", null);
          return;
        }

        // Real-time battery SoC gate: PreferredMinSoC when EnforcePreferredSoC is on,
        // AbsoluteMinSoC when off.
        //
        // Without this gate, the simulation's IsOvernightMinSocOk looks at the NEXT overnight
        // window (sunset today → sunrise tomorrow). In the pre-dawn hours that window is 18h
        // away — the battery will have fully recharged by then, so the check always passes even
        // while the current overnight drain pushes SoC below the required floor. This gate
        // provides a direct live-SoC check the simulation cannot substitute for.
        //
        // Restarting uses a Schmitt trigger (LoadSchedulingDecision.PrioritySoCGateOk) rather
        // than the bare floor: without hysteresis, a session that just stopped at the floor
        // restarts on the next tiny SoC uptick and immediately drains back under the floor
        // again. PV surplus that already covers the charge rate bypasses the wait since it
        // won't draw the battery down further.
        {
          int socFloor = Battery.EnforcePreferredSoC
            ? Battery.PreferredMinimalSoC
            : Battery.AbsoluteMinimalSoC;
          int currentSoC = Battery.BatterySoc;
          int netPvW = CurrentAveragePVPower - CurrentAverageHouseLoad;

          if (!LoadSchedulingDecision.PrioritySoCGateOk(wasActive, currentSoC, socFloor, netPvW, chargeRateW, PrioritySoCHysteresisPct, PvBypassMinSocBufferPct))
          {
            // Three distinct reasons the gate can block a restart — report the one that's
            // actually true, rather than always blaming "PV insufficient" regardless of cause.
            // Always state the REAL restart thresholds (PV-covered buffer floor, full hysteresis
            // floor), even while SoC is still below socFloor itself — crossing socFloor alone is
            // never enough to restart, and a message that only mentions socFloor reads as if it were.
            int restartFloor   = socFloor + PrioritySoCHysteresisPct;
            int pvRestartFloor = socFloor + PvBypassMinSocBufferPct;
            string reason;
            if (currentSoC < socFloor)
              reason = wasActive
                ? $"{load.Mode}: SoC {currentSoC}% < floor {socFloor}% — stopping, battery depleted"
                : $"{load.Mode}: SoC {currentSoC}% < floor {socFloor}% — waiting to recover above {socFloor}%, then {pvRestartFloor}%+ with PV or {restartFloor}%+ to restart";
            else if (currentSoC < pvRestartFloor)
              reason = $"{load.Mode}: SoC {currentSoC}% < restart floor {restartFloor}% (need {pvRestartFloor}%+ before a PV-covered restart is even considered) — waiting for battery to recover";
            else
              reason = $"{load.Mode}: SoC {currentSoC}% < restart floor {restartFloor}% (net PV {netPvW}W < {chargeRateW}W needed) — waiting for battery to recover or PV to cover the load";

            // Even though we can't start now, scan the base trajectory for when SoC is predicted
            // to recover past the (PV-independent) restart floor, then search for a valid window
            // from that point so the dashboard can still show a predicted start/end/SoC — same
            // recovery-slot pattern as Optimal's ruleset. Only while stopped: a session being
            // force-stopped by a real SoC drop has nothing to predict.
            if (!wasActive)
            {
              var recoverySlot = baseResult.Slots
                .Where(s => s.Time > currentSlot && s.SoC >= restartFloor)
                .Select(s => s.Time)
                .FirstOrDefault();

              if (recoverySlot != default)
              {
                bool recoveryIsToday = recoverySlot.Date == now.Date;
                var pvEnd = recoveryIsToday ? baseResult.LastRelevantPVEnergyToday : baseResult.LastRelevantPVEnergyTomorrow;
                var recoveryMax = Min(recoverySlot.AddMinutes(durationMinutes), pvEnd ?? farFuture);

                if (recoveryMax > recoverySlot)
                {
                  var end = FindMaxSessionEnd(recoverySlot, recoveryMax, e => RunSimFrom(recoverySlot, e),
                    sim => sim.IsOvernightMinSocOk() && !sim.HasNewGridVs(baseResult));

                  if (end is not null && MeetsMinWindow(recoverySlot, end.Value))
                  {
                    SetResult(
                      [new ExtraLoad { Name = load.Config.Name, Priority = load.Config.Priority, StartTime = recoverySlot, EndTime = end.Value, PowerW = chargeRateW }],
                      false, $"{reason} — expected start ~{recoverySlot:HH:mm}", end);
                    return;
                  }
                }
              }
            }

            SetResult([], false, reason, null);
            return;
          }
        }

        if (TryMinWindowLatch()) return;

        // Stop a long-running active session at dusk. Companion to the late-start scheduling
        // above, for a session that was already running when the sun set. Only fires in the
        // evening (AfterPV) — NOT pre-dawn (BeforePV), exactly where draining right up to
        // sunrise is the point. Only reached once the min-window latch above has let a freshly
        // started session finish its own anti-oscillation window — a session still settling in
        // is never cut off mid-latch, but a "normal" long-running daytime charge that's still
        // going at dusk is stopped here rather than continuing through the riskiest part of the
        // night.
        if (wasActive && baseResult.CurrentPVPeriod == PVPeriods.AfterPV)
        {
          SetResult([], false,
            $"{load.Mode}: stopping at dusk to preserve overnight battery margin — resumes pre-dawn",
            null);
          return;
        }

        // Overnight OK, no new grid; house does NOT need to reach 100%. Uses tomorrowMax so a
        // continuous session can extend past sunset, draining the battery down to minSoC — but
        // the two blocks above mean this is now mainly reached during the day (continuing a
        // session that started in daylight) or in the pre-dawn hours (the late-start block
        // above already started it there, as late as possible before sunrise, and this just
        // continues finding its end).
        var stepEnd = FindMax(tomorrowMax, sim => sim.IsOvernightMinSocOk() && !sim.HasNewGridVs(baseResult));
        if (stepEnd is not null)
        {
          SetResult([new ExtraLoad { Name = load.Config.Name, Priority = load.Config.Priority, StartTime = currentSlot, EndTime = stepEnd.Value, PowerW = chargeRateW }],
            true, $"Charging ({load.Mode} {load.Config.Name}: {load.CurrentLevel:F0} → {load.TargetLevel:F0}{load.Config.LevelUnit}, bat={Battery.BatterySoc}%)", stepEnd);
        }
      }

      // ── PriorityPlus's bonus ruleset ─────────────────────────────────────────────────────────
      // Tried only once Priority's complete ruleset above has already concluded it can't charge
      // this cycle. Day or night doesn't matter here, only price — see FindBestPriceAwareStart.
      void RunPriorityPlusBonusRuleset()
      {
        // A running session is never stopped just to wait for a cheaper start later. A planned
        // later start from last cycle stays preferred (see FindBestPriceAwareStart).
        var planned = SessionPlanner.FindBestPriceAwareStart(currentSlot, priceHorizonEnd, durationMinutes, RunSimFrom, baseResult,
          readyBy, wasActive ? float.PositiveInfinity : SessionPlanner.DefaultMinSavingFraction,
          preferredStart: wasActive ? null : previousLoads.FirstOrDefault()?.StartTime);
        if (planned is not { } p) return;

        var end = p.Start.AddMinutes(durationMinutes);
        if (!MeetsMinWindow(p.Start, end)) return;

        string why = p.Choice switch
        {
          StartChoice.Cheaper          => $"cheapest window, {p.SavingPercent:F0}% cheaper than the earliest",
          StartChoice.DeadlineCheapest => $"ready by {readyBy:HH:mm} — cheapest start that finishes in time, above price threshold",
          StartChoice.DeadlineNow      => $"ready by {readyBy:HH:mm} — can't finish in time, charging now",
          _                            => "earliest cheap grid-assisted window",
        };
        bool startNow = p.Start <= currentSlot;
        SetResult(
          [new ExtraLoad { Name = load.Config.Name, Priority = load.Config.Priority, StartTime = p.Start, EndTime = end, PowerW = chargeRateW }],
          startNow,
          startNow
            ? $"Charging (PriorityPlus {load.Config.Name}: {why}, {load.CurrentLevel:F0} → {load.TargetLevel:F0}{load.Config.LevelUnit}, bat={Battery.BatterySoc}%)"
            : $"Scheduled ~{p.Start:HH:mm} (PriorityPlus {load.Config.Name}: {why})",
          end);
      }

      // `load.ChargeNow` otherwise still holds its STALE value from the previous cycle here —
      // e.g. left over as true from Emergency mode, or from before a manual mode switch — until
      // some ruleset below explicitly calls SetResult. Reset it so a ruleset that falls through
      // without deciding anything (common — e.g. Optimal's conditions just don't match) is never
      // misread by the "if (load.ChargeNow) return;" checks below as having just decided to
      // charge, which would stop the climb on a leftover value instead of an actual decision.
      load.ChargeNow = false;

      // The climb. For the currently selected mode, each tier at or below its number gets a
      // turn in ascending order. ChargeNow == true stops immediately — nothing higher matters
      // once something allows charging. Otherwise each tier runs to completion regardless of
      // whether it "won"; the final `if (!decided)` below only fires the generic fallback if
      // NO tier (including the mode's own ceiling) ever called SetResult at all — a tier's own
      // "no" (even just a deferred prediction) always stands once reached.
      void Climb()
      {
        if ((int)load.Mode >= (int)LoadSchedulingMode.Optimal)
        {
          RunOptimalRuleset();
          if (load.ChargeNow) return;
        }

        if ((int)load.Mode >= (int)LoadSchedulingMode.Priority)
        {
          RunPriorityRuleset();
          if (load.ChargeNow) return;
        }

        if ((int)load.Mode >= (int)LoadSchedulingMode.PriorityPlus)
        {
          RunPriorityPlusBonusRuleset();
          if (load.ChargeNow) return;
        }

        if (!decided)
          SetResult([], false, $"No valid window ({load.Mode} {load.Config.Name}: {load.CurrentLevel:F0}{load.Config.LevelUnit}, bat={Battery.BatterySoc}%)", null);
      }
      Climb();

      // Preview of the sessions after this one (e.g. a PV session ending at dusk, then a cheap
      // window after midnight). Only PriorityPlus can price-plan ahead: Optimal/Priority decide
      // on PV and battery state as it is at the time. Never fed into the control simulation.
      if (load.Mode == LoadSchedulingMode.PriorityPlus)
        load.PlannedSessions = SessionPlanner.PlanFollowUpSessions(baseInput, load.ExtraLoads, energyNeededWh, priceHorizonEnd, readyBy);
    }

    /// <summary>
    /// Binary-searches for the maximum session end in (currentSlot, maxEnd] where predicate
    /// is satisfied by the simulation result. Assumes predicate is monotone: if it holds for
    /// a session ending at T, it also holds for any session ending before T. Returns null if
    /// even a single 15-minute session fails the predicate.
    /// </summary>
    private static DateTime? FindMaxSessionEnd(
      DateTime currentSlot, DateTime maxEnd,
      Func<DateTime, SimulationResult> runSim,
      Func<SimulationResult, bool> predicate)
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

    /// <summary>
    /// Finds the latest session start in [minStart, fixedEnd) where running continuously from
    /// that start through fixedEnd satisfies predicate, and the resulting window
    /// (fixedEnd − start) is at least <paramref name="minSessionMinutes"/> long. Used to
    /// schedule a session as late as possible against a fixed deadline, rather than as long as
    /// possible from a fixed start.
    ///
    /// Deliberately a linear scan from fixedEnd backward, NOT a binary search like
    /// FindMaxSessionEnd: predicate includes HasNewGridVs, whose force_charge timing is
    /// price-driven and can jump between discrete price windows as the EV's start time shifts,
    /// rather than varying smoothly — so "a later start is never harder to satisfy than an
    /// earlier one" doesn't reliably hold, and a binary search can skip straight over a valid
    /// later start whose immediate neighbours happen to fail. The scan tests every 15-minute
    /// candidate from fixedEnd backward (skipping any that wouldn't meet
    /// minSessionMinutes — e.g. the load's own MinWindowMinutes anti-oscillation floor) and
    /// returns the first (latest) one that passes, which is correct regardless of whether
    /// predicate is monotone.
    /// </summary>
    private static DateTime? FindLatestSessionStart(
      DateTime minStart, DateTime fixedEnd, int minSessionMinutes,
      Func<DateTime, SimulationResult> runSimFrom,
      Func<SimulationResult, bool> predicate)
    {
      int maxSteps = (int)(fixedEnd - minStart).TotalMinutes / 15;
      int minSteps = Math.Max(1, (minSessionMinutes + 14) / 15);
      if (maxSteps < minSteps) return null;

      for (int step = minSteps; step <= maxSteps; step++)
      {
        var candidateStart = fixedEnd.AddMinutes(-step * 15);
        if (predicate(runSimFrom(candidateStart)))
          return candidateStart;
      }
      return null;
    }

    private static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;
    private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;

    public InverterState ProposedState
    {
      get
      {
        var now = DateTime.Now;
        if (_simulationResult is null || _simulationResult.Slots.Count == 0)
          return _currentMode;

        var currentSlot = _simulationResult.Slots.FirstOrDefault(s => s.Time == now.RoundToNearestQuarterHour())
                          ?? _simulationResult.Slots[0];
        _currentMode = currentSlot.State;

        // ── User override ──────────────────────────────────────────────────────────────────
        // Applied here (not in the simulator) so the simulation always reflects autonomous
        // logic. The override only affects the command sent to the inverter right now.
        if (OverrideMode != InverterModes.automatic)
        {
          _currentMode = new InverterState(OverrideMode, ForceChargeReasons.UserMode);
          return _currentMode;
        }

        // ── Reset signal ──────────────────────────────────────────────────────────────────
        // After the inverter returns from manual/remote mode, _resetCounter is set to 2.
        // Override the simulation result with reset for those ticks so the inverter hardware
        // has time to re-initialise battery control before we resume normal commands.
        if (_resetCounter > 0)
        {
          _resetCounter--;
          _currentMode = new InverterState(InverterModes.reset, ForceChargeReasons.None);
          return _currentMode;
        }

        // ── Inverter bug fix ──────────────────────────────────────────────────────────────
        // Some SMA inverters fail to switch to battery in "normal" mode at low house loads
        // (50–300 W), drawing from the grid instead. This can't be predicted or simulated —
        // it's detected live by watching the running-average grid power.
        // After ~1 min of the condition (4 consecutive ticks), briefly force-discharge to
        // kick the inverter back into battery mode.
        if (_currentMode.Mode == InverterModes.normal
            && CurrentAverageGridPower is > 50 and < 300
            && Battery.BatterySoc > Math.Max(Battery.PreferredMinimalSoC, Battery.AbsoluteMinimalSoC))
        {
          if (_bugFixCounter <= 4)
          {
            _bugFixCounter++;
          }
          else
          {
            _bugFixCounter = 0;
            _currentMode = new InverterState(InverterModes.force_discharge, ForceChargeReasons.BugFixMode);
          }
        }
        else
        {
          _bugFixCounter = 0;
        }

        return _currentMode;
      }
    }

    public int CurrentAverageHouseLoad => _loadRunningAverage.GetAverage();

    public int CurrentAveragePVPower => _pvRunningAverage.GetAverage();

    private int ActualPVEnergyTodayWh()
    {
      if (PVCC_Config.TodayPVEnergyEntity.TryGetStateValue(out float wh))
        return (int)wh;
      return 0;
    }

    public int CurrentAverageGridPower => _gridRunningAverage.GetAverage() * -1;

    public void AddAverageGridPowerValue(int value)
    {
      _gridRunningAverage.AddValue(value);
    }

    /// <summary>The simulation timeline (now → end of tomorrow) from the last RunSimulation() call.</summary>
    public IReadOnlyList<SimulationSlot> SimulationTimeline => _simulationResult?.Slots ?? [];

    /// <summary>Full simulation result including PV windows and derived predicates.</summary>
    public SimulationResult? SimulationState => _simulationResult;

    public void UpdatePredictions(bool all = false)
    {
      RunSimulation();
    }

    private readonly LoadDeviationTracker _loadDeviation = new();

    /// <summary>Whether the load's min-on or min-off latch currently holds (ignoring bypasses).</summary>
    private static bool IsLatched(SchedulableLoadRuntime load, DateTime now) =>
      load.ChargeNow
        ? load.SessionStartTime is { } s && (now - s).TotalMinutes < load.Config.MinWindowMinutes
        : SchedulableLoadRuntime.OffLatchUntil(load.SessionStopTime, load.Config.MinOffMinutes, now, true) is not null;

    /// <summary>Makes every schedulable load search its window again on the next cycle.</summary>
    public void RequestReplan(string reason)
    {
      foreach (var load in SchedulableLoads)
        load.RequestReplan(reason);
      WarmWater?.RequestReplan(reason);
    }
  }
}
