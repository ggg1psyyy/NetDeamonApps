using System;
using System.Globalization;
using NetDaemon.HassModel.Entities;
using NetDeamon.apps.PVControl.Managers;
using NetDeamon.apps.PVControl.Predictions;
using NetDeamon.apps.PVControl.Simulator;
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

      _pvRunningAverage = new RunningIntAverage(TimeSpan.FromMinutes(5));
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
      Prediction_Load = new HourlyWeightedAverageLoadPrediction(PVCC_Config.DBFullLocation, 10, excludeColumns: excludeColumns);

      if (PVCC_Config.ForecastPVEnergyTodayEntities is null || PVCC_Config.ForecastPVEnergyTomorrowEntities is null)
        throw new NullReferenceException("PV Forecast entities are not available");
      Prediction_PV = new OpenMeteoSolarForecastPrediction(PVCC_Config.ForecastPVEnergyTodayEntities, PVCC_Config.ForecastPVEnergyTomorrowEntities);

      Prediction_NetEnergy = new NetEnergyPrediction(Prediction_PV, Prediction_Load, _loadRunningAverage, _pvRunningAverage, true);

      if (PVCC_Config.BatterySoCEntity is null)
        throw new NullReferenceException("BatterySoCEntity not available");
      Prediction_BatterySoC = new BatterySoCPrediction(Prediction_NetEnergy, PVCC_Config.BatterySoCEntity, Battery.BatteryCapacity);
      Battery.SoCPrediction = Prediction_BatterySoC;

      PVWindows = new PVWindows(Prediction_NetEnergy, Prediction_BatterySoC);
      Snapshots = new DailySnapshots(Prediction_PV, Prediction_Load, Prediction_BatterySoC, () => _simulationResult!);

      PVCC_Config.CurrentImportPriceEntity?.StateAllChanges().SubscribeAsync(async _ => await UserStateChanged(PVCC_Config.CurrentImportPriceEntity));
      PVCC_Config.CurrentBatteryPowerEntity?.StateChanges().SubscribeAsync(async _ => await UserStateChanged(PVCC_Config.CurrentBatteryPowerEntity));
      PVCC_Config.CurrentPVPowerEntity?.StateChanges().SubscribeAsync(async _ => await UserStateChanged(PVCC_Config.CurrentPVPowerEntity));
      PVCC_Config.CurrentHouseLoadEntity?.StateChanges().SubscribeAsync(async _ => await UserStateChanged(PVCC_Config.CurrentHouseLoadEntity));
      PVCC_Config.DailyExportEnergyEntity.StateChanges().SubscribeAsync(async _ => await UserStateChanged(PVCC_Config.DailyExportEnergyEntity));
      PVCC_Config.DailyImportEnergyEntity.StateChanges().SubscribeAsync(async _ => await UserStateChanged(PVCC_Config.DailyImportEnergyEntity));
      PVCC_Config.BatteryInputEnergyEntity?.StateChanges().SubscribeAsync(async _ => await UserStateChanged(PVCC_Config.BatteryInputEnergyEntity));
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

    }
    // ── Sub-objects ───────────────────────────────────────────────────────────────────────
    public BatteryState Battery { get; }
    public CostTracker Costs { get; }
    public PVWindows PVWindows { get; }
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
    /// <summary>Sum of average power (W) from all schedulable loads that are actively drawing above their minimum threshold.</summary>
    private int ActiveSchedulableLoadPowerW() => SchedulableLoads
      .Where(l => l.PowerAverage != null && l.PowerAverage.GetAverage() > l.Config.MinActivePowerW)
      .Sum(l => l.PowerAverage!.GetAverage());

    private async Task UserStateChanged(Entity entity)
    {
      if (entity.EntityId == PVCC_Config.CurrentImportPriceEntity?.EntityId)
      {
        Prices.UpdatePriceList();
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
          schedLoad.PowerAverage!.AddValue((int)Math.Round(p));
      }
      foreach (var schedLoad in SchedulableLoads.Where(l => l.Config.ActualEnergyEntity is not null
        && entity.EntityId == l.Config.ActualEnergyEntity!.EntityId))
      {
        if (!schedLoad.Config.ActualEnergyEntity!.TryGetStateValue(out float energy, numericalGetBaseValue: false))
          continue;
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
          int schedPowerW = schedLoad.PowerAverage?.GetAverage() ?? schedLoad.Config.AvgPowerW;
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
        PVPredictionWh = NetEnergyPrediction.WithRunningAvgCorrection(Prediction_PV.TodayAndTomorrow, _pvRunningAverage.GetAverage(), now),
        ExtraLoads = extraLoads ?? [],
        EnableCheapForceCharge = EnableCheapForceCharge,
        OpportunisticDischarge = OpportunisticDischarge,
        ForceChargeMaxPrice = Prices.ForceChargeMaxPrice,
        ForceChargeTargetSocPercent = ForceChargeTargetSoC,
        OverrideMode = OverrideMode,
        CurrentMode = _currentMode,
      };

      // Run the baseline simulation once — its ForceChargeSlots and PV window boundaries
      // are used by every FindLoadWindow call to compare against test simulations.
      var baseResult = EnergySimulator.Simulate(baseInput);

      // Find valid window for each schedulable load (highest priority first).
      foreach (var load in SchedulableLoads.OrderByDescending(l => l.Config.Priority))
        FindLoadWindow(load, baseInput, baseResult);

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
    /// The baseline and its force-charge slots are pre-computed once in RunSimulation.
    /// </summary>
    private void FindLoadWindow(SchedulableLoadRuntime load, SimulationInput baseInput, SimulationResult baseResult)
    {
      var now = DateTime.Now;
      var currentSlot = now.RoundToNearestQuarterHour();
      bool wasActive = load.ChargeNow;

      void SetResult(List<ExtraLoad> extraLoads, bool chargeNow, string reason, DateTime? end)
      {
        if (chargeNow && !wasActive)
          load.SessionStartTime = now;          // session just started
        else if (!chargeNow)
          load.SessionStartTime = null;         // session ended
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
      // RunSim returns a full SimulationResult so predicates can use its derived properties
      // (WillReachMaxSocToday, IsOvernightMinSocOk, HasNewGridVs, IsGridCheapVs) directly.
      SimulationResult RunSimFrom(DateTime start, DateTime end)
      {
        var testLoad = new ExtraLoad { Name = load.Config.Name, Priority = load.Config.Priority, StartTime = start, EndTime = end, PowerW = chargeRateW };
        return EnergySimulator.Simulate(baseInput.WithExtraLoads([.. baseInput.ExtraLoads, testLoad]));
      }
      SimulationResult RunSim(DateTime end) => RunSimFrom(currentSlot, end);

      // Binary search: max session end in (currentSlot, maxEnd] satisfying predicate.
      // Predicate must be monotone: shorter session → easier to satisfy.
      // Returns null if no 15-min session satisfies it, or if the found window is shorter
      // than MinWindowMinutes (prevents oscillation from marginal windows).
      DateTime? FindMax(DateTime maxEnd, Func<SimulationResult, bool> predicate)
      {
        var end = FindMaxSessionEnd(currentSlot, maxEnd, RunSim, predicate);
        if (end is null) return null;
        // Skip min-window guard when the natural charge duration is already shorter than
        // MinWindowMinutes: the session will end cleanly via "target reached" next cycle,
        // so oscillation is not a concern. Only apply the guard for longer natural sessions
        // where a marginal window could flip on/off between cycles.
        if (durationMinutes >= load.Config.MinWindowMinutes &&
            load.Config.MinWindowMinutes > 0 &&
            (end.Value - currentSlot).TotalMinutes < load.Config.MinWindowMinutes)
          return null;
        return end;
      }

      // Today's and tomorrow's window ends (capped to full session duration if shorter).
      // Fall back to far-future when no PV is forecast (null) so the session is only
      // bounded by durationMinutes — same behaviour as the old PVWindows fallback.
      var farFuture   = currentSlot.AddDays(2);
      var todayMax    = Min(currentSlot.AddMinutes(durationMinutes), baseResult.LastRelevantPVEnergyToday    ?? farFuture);
      var tomorrowMax = Min(currentSlot.AddMinutes(durationMinutes), baseResult.FirstRelevantPVEnergyTomorrow ?? farFuture);

      if (todayMax <= currentSlot && tomorrowMax <= currentSlot)
      { SetResult([], false, $"No charging window before next PV ({load.Config.Name})", null); return; }

      // Step 1: Optimal — house reaches ~100% even WITH EV running; overnight OK; no new grid.
      // Optimal mode only. Session extends to tomorrowMax so the load can drain the battery
      // overnight after it reaches 100 % via PV — matching the mode definition:
      //   "after reaching 100% AND after PV stops, Optimal may run until house SoC hits minSoC."
      // Priority/PriorityPlus skip this step and use Step 2 (no 100% requirement).
      //
      // Optimal is solar-surplus mode: only active during the PV window (InPVPeriod).
      // Outside PV hours the simulation sits at the edge of overnight-SoC thresholds and
      // oscillates every 15 s as tiny SoC changes flip the binary-search result.
      // Use Priority/PriorityPlus for intentional off-peak / battery-drain charging.
      // Exception: when outside today's PV window, search for a window in tomorrow's PV period
      // so the scheduled run is visible (chargeNow=false, ExtraLoad carries the future window).
      if (load.Mode == LoadSchedulingMode.Optimal && baseResult.CurrentPVPeriod != PVPeriods.InPVPeriod)
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
            if (end is not null &&
                (durationMinutes < load.Config.MinWindowMinutes || load.Config.MinWindowMinutes <= 0 ||
                 (end.Value - tomorrowPVStart.Value).TotalMinutes >= load.Config.MinWindowMinutes))
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

      // ── Minimum on-time latch ────────────────────────────────────────────────────────────────
      // The simulation re-runs every 15 s and at borderline SoC levels the predicate can flip
      // on/off every cycle. Once a session starts, keep it active for MinWindowMinutes before
      // allowing the simulation to stop it — same threshold already used to prevent marginal starts.
      // Hard-stop conditions above (Off, target reached, Emergency, Optimal-outside-PV) always
      // take immediate effect regardless of the latch.
      if (wasActive && load.SessionStartTime.HasValue && load.Config.MinWindowMinutes > 0)
      {
        var elapsedMin = (now - load.SessionStartTime.Value).TotalMinutes;
        if (elapsedMin < load.Config.MinWindowMinutes)
        {
          var latchedLoads = load.ExtraLoads.Count > 0
            ? load.ExtraLoads.Select(e => new ExtraLoad { Name = e.Name, Priority = e.Priority, StartTime = currentSlot, EndTime = e.EndTime, PowerW = e.PowerW }).ToList()
            : load.ExtraLoads;
          SetResult(latchedLoads, true,
            $"Latched ({load.Config.Name}: {elapsedMin:F0}/{load.Config.MinWindowMinutes} min min-on)",
            load.PredictedEnd);
          return;
        }
      }

      if (load.Mode == LoadSchedulingMode.Optimal)
      {
        var end = FindMax(tomorrowMax, sim =>
          sim.WillReachMaxSocToday && sim.IsOvernightMinSocOk() && !sim.HasNewGridVs(baseResult));
        if (end is not null)
        {
          SetResult([new ExtraLoad { Name = load.Config.Name, Priority = load.Config.Priority, StartTime = currentSlot, EndTime = end.Value, PowerW = chargeRateW }],
            true, $"Charging (Optimal {load.Config.Name}: {load.CurrentLevel:F0} → {load.TargetLevel:F0}{load.Config.LevelUnit}, bat={Battery.BatterySoc}%)", end);
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
        var end = FindMax(tomorrowMax, sim => sim.IsOvernightMinSocOk(priorityEnforcePreferred) && !sim.HasNewGridVs(baseResult));
        if (end is not null)
        {
          SetResult([new ExtraLoad { Name = load.Config.Name, Priority = load.Config.Priority, StartTime = currentSlot, EndTime = end.Value, PowerW = chargeRateW }],
            true, $"Charging (Priority {load.Config.Name}: {load.CurrentLevel:F0} → {load.TargetLevel:F0}{load.Config.LevelUnit}, bat={Battery.BatterySoc}%)", end);
          return;
        }
      }

      // Step 3: PriorityPlus — base-case overnight OK; any new grid import only at cheap prices.
      // The EV session extends into the overnight window; what matters is that the battery would
      // survive the night without the EV (base case), and the EV runs on cheap grid power.
      if (load.Mode == LoadSchedulingMode.PriorityPlus && baseResult.IsOvernightMinSocOk())
      {
        var end = FindMax(tomorrowMax, sim => sim.IsGridCheapVs(baseResult));
        if (end is not null)
        {
          SetResult([new ExtraLoad { Name = load.Config.Name, Priority = load.Config.Priority, StartTime = currentSlot, EndTime = end.Value, PowerW = chargeRateW }],
            true, $"Charging (PriorityPlus {load.Config.Name}: {load.CurrentLevel:F0} → {load.TargetLevel:F0}{load.Config.LevelUnit}, bat={Battery.BatterySoc}%)", end);
          return;
        }
      }

      SetResult([], false, $"No valid window ({load.Mode} {load.Config.Name}: {load.CurrentLevel:F0}{load.Config.LevelUnit}, bat={Battery.BatterySoc}%)", null);
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

    private static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;

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

    public int CurrentAverageGridPower => _gridRunningAverage.GetAverage() * -1;

    public void AddAverageGridPowerValue(int value)
    {
      _gridRunningAverage.AddValue(value);
    }

    /// <summary>The simulation timeline (now → end of tomorrow) from the last RunSimulation() call.</summary>
    public IReadOnlyList<SimulationSlot> SimulationTimeline => _simulationResult?.Slots ?? [];

    /// <summary>Full simulation result including PV windows and derived predicates.</summary>
    public SimulationResult? SimulationState => _simulationResult;

    public bool NegativeImportPriceUpcomingToday
    {
      get
      {
        var now = DateTime.Now;
        var negativeImportPrices = Prices.PriceListImport.Where(p => p.StartTime.Date == now.Date && p.Price < 0).ToList();
        return negativeImportPrices.Count > 0 && negativeImportPrices.FirstOrDefault().StartTime > now;
      }
    }

    public void UpdatePredictions(bool all = false)
    {
      RunSimulation();
    }
  }
}
