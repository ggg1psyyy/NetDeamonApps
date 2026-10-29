using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NetDaemon.HassModel.Entities;
using NetDeamon.apps.PVControl.Managers;
using NetDeamon.apps.PVControl.Simulator;
using static NetDeamon.apps.PVControl.PVControlCommon;

namespace NetDeamon.apps.PVControl.WarmWater;

/// <summary>
/// Runs the warm-water tank: learns the <see cref="TankModel"/> from HA history, plans the heating
/// with <see cref="WarmWaterPlanner"/> against the house simulation's prices, starts the planned
/// one-time heatings (<see cref="WarmWaterControl"/>) and publishes plan and settings as HA
/// entities. HA access lives here; the decisions are in the pure classes.
/// </summary>
public sealed class WarmWaterController
{
  /// <summary>Configuration (YAML).</summary>
  public WarmWaterConfig Config { get; }
  /// <summary>HA entity-id part of the name (<see cref="Extensions.ToSlug"/>).</summary>
  public string Slug => Config.Name.ToSlug();

  /// <summary>Current tank model.</summary>
  public TankModel Model { get; private set; } = TankModel.Default;
  /// <summary>Time of the last model fit.</summary>
  public DateTime? LastFit { get; private set; }
  /// <summary>The optimized plan (published in every mode, controls only in Auto).</summary>
  public WarmWaterPlan Plan { get; private set; } = WarmWaterPlan.Empty;
  /// <summary>Forecast of the heat pump's own automatic, for Off/PlanOnly and to show the saving.</summary>
  public WarmWaterPlan Automatic { get; private set; } = WarmWaterPlan.Empty;
  public DateTime? LastPlan { get; private set; }
  public string LastPlanReason { get; private set; } = "";
  public string ControlReason { get; private set; } = "";

  Entity _modeEntity = null!, _comfortTempEntity = null!, _boostEntity = null!, _boostTempEntity = null!, _planEntity = null!;
  readonly List<(ComfortPeriodConfig Config, Entity Time, Entity Enabled)> _periods = [];
  readonly RunningIntAverage _power = new(TimeSpan.FromMinutes(2));
  string? _replanRequest;
  bool _lastHeating;
  DateTime? _lastCommand, _lastStart, _lastStop, _boostSince;
  bool _wasActive;
  bool _boostSawRunning;

  /// <summary>Creates the controller and follows the heat pump power (average for the house load).</summary>
  /// <param name="config">Configuration (YAML).</param>
  public WarmWaterController(WarmWaterConfig config)
  {
    Config = config;
    if (Config.PowerEntity is not null)
    {
      if (Config.PowerEntity.TryGetStateValue(out float p)) _power.AddValue((int)Math.Round(p));
      Config.PowerEntity.StateChanges().Subscribe(_ =>
      {
        if (Config.PowerEntity.TryGetStateValue(out float w)) _power.AddValue((int)Math.Round(w));
      });
    }
  }

  // ── Live state ─────────────────────────────────────────────────────────────────────────────
  public WarmWaterMode Mode => _modeEntity?.State is { } s && Enum.TryParse(s, true, out WarmWaterMode m) ? m : WarmWaterMode.Off;
  public double ComfortTemp => _comfortTempEntity is not null && _comfortTempEntity.TryGetStateValue(out float v, numericalGetBaseValue: false) ? v : Config.ComfortTempDefault;
  public double BoostTemp => _boostTempEntity is not null && _boostTempEntity.TryGetStateValue(out float v, numericalGetBaseValue: false) ? v : Config.BoostTempDefault;
  /// <summary>A boost is going: the boost button was pressed and the boost hasn't ended yet (kept in memory only).</summary>
  public bool BoostOn => _boostSince is not null;
  double? Temperature => Config.TemperatureEntity.TryGetStateValue(out float t, numericalGetBaseValue: false) ? t : null;
  bool IsOn(Entity e) => e.State?.Equals("on", StringComparison.OrdinalIgnoreCase) ?? false;
  bool Heating => IsOn(Config.HeatingActiveEntity);

  /// <summary>Heat pump power while it heats warm water — subtracted from the measured house load like a schedulable load's.</summary>
  public int ActivePowerW => Heating && _power.GetAverage() > 100 ? _power.GetAverage() : 0;

  /// <summary>The warm-water heating the house simulation should expect: the plan in Auto, else the automatic forecast.</summary>
  /// <param name="now">Current time.</param>
  /// <returns>Runs not over yet as extra loads.</returns>
  public List<ExtraLoad> ExtraLoads(DateTime now)
  {
    var runs = Mode == WarmWaterMode.Auto || BoostOn ? Plan.Runs : Automatic.Runs;
    return runs.Where(r => r.End > now).Select(r => new ExtraLoad
    {
      Name = Config.Name, Priority = 0,
      StartTime = r.Start < now ? now : r.Start, EndTime = r.End, PowerW = r.PowerW,
    }).ToList();
  }

  /// <summary>Plans again on the next cycle; the first reason wins.</summary>
  /// <param name="reason">Why.</param>
  public void RequestReplan(string reason) => _replanRequest ??= reason;

  // ── Planning ───────────────────────────────────────────────────────────────────────────────
  /// <summary>Why the plan must be made again now.</summary>
  /// <param name="now">Current time.</param>
  /// <returns>The reason; null to keep the plan.</returns>
  public string? ReplanReason(DateTime now)
  {
    if (LastPlan is not { } last) return "first plan";
    if (_replanRequest is { } requested) return requested;
    if (Heating != _lastHeating) return "heating started/stopped";
    // The heat pump starts ~2.5 min after the switch, so a run can outlast its planned end —
    // replan from the real temperature before the control would switch it off short of its target.
    if (IsOn(Config.OneTimeActiveEntity) && !Plan.Runs.Any(r => TimeRange.Contains(r.Start, r.End, now))
        && Plan.Runs.Any(r => r.End <= now && r.End > last)) return "run outlasted its plan";
    if (now.RoundToNearestQuarterHour() != last.RoundToNearestQuarterHour()) return "new quarter-hour";
    if ((now - last).TotalMinutes >= Config.ReplanIntervalMinutes) return "interval";
    return null;
  }

  /// <summary>PVControl's hourly weather forecast (outside temperature → heat pump efficiency); null if not configured.</summary>
  public LoadModel.WeatherForecast? Weather { get; set; }
  /// <summary>End of the published prices at the last plan; the simulation estimated the prices after it.</summary>
  public DateTime? PricesKnownUntil { get; private set; }
  /// <summary>The last plan scaled prices by the outside temperature.</summary>
  public bool OutsideEfficiencyUsed { get; private set; }

  /// <summary>Plans against the house simulation (heating prices per slot) and forecasts the automatic.</summary>
  /// <param name="baseInput">Simulation input without warm-water heating.</param>
  /// <param name="baseline">Simulation of <paramref name="baseInput"/>.</param>
  /// <param name="now">Current time.</param>
  /// <param name="reason">Why (published).</param>
  public void Replan(SimulationInput baseInput, SimulationResult baseline, DateTime now, string reason)
  {
    _replanRequest = null;
    _lastHeating = Heating;
    LastPlan = now;
    LastPlanReason = reason;
    if (Temperature is not { } temp || baseline.Count == 0)
    {
      Plan = Automatic = WarmWaterPlan.Empty;
      LastPlanReason = $"{reason} — no tank temperature";
      return;
    }

    var slots = baseline.Slots.Select(s => s.Time).ToList();
    var horizonEnd = slots[^1].AddMinutes(15);
    var prices = WarmWaterPlanner.HeatingPricesPerKwh(baseInput, baseline, slots, now, (int)Math.Round(Model.PowerW(45)));
    // Unpublished hours are priced by the simulation from estimated prices (PriceList.WithEstimatedPrices).
    PricesKnownUntil = baseInput.PriceHorizonEnd(now);
    OutsideEfficiencyUsed = Weather is { } weather && !weather.IsStale(now) && Config.EfficiencyChangePerK > 0;
    if (OutsideEfficiencyUsed)
      prices = WarmWaterPlanner.WithOutsideEfficiency(prices, slots.Select(s => Weather!.Temperature(s)).ToList(), Config.EfficiencyChangePerK);
    var periods = _periods
      .Where(p => IsOn(p.Enabled) && SchedulableLoadRuntime.TryParseReadyBy(p.Time.State, out _))
      .Select(p => (SchedulableLoadRuntime.TryParseReadyBy(p.Time.State, out var t) ? t : default, p.Config.DurationMinutes));
    var comfort = WarmWaterControl.ComfortWindows(periods, now, horizonEnd, ComfortTemp + Config.ComfortMarginK);

    // A heating that runs is never stopped (WarmWaterControl), so the plan heats it to its target
    // first: the boost, the one-time setpoint, or the heat pump's automatic setpoint.
    bool oneTime = IsOn(Config.OneTimeActiveEntity);
    double? forced = null;
    if (BoostOn && temp < BoostTemp) forced = Math.Max(BoostTemp, oneTime && OneTimeSetpoint is { } s ? s : 0);
    else if (oneTime && OneTimeSetpoint is { } oneTimeSetpoint) forced = oneTimeSetpoint;
    else if (Heating && !oneTime && AutomaticSetpoint is { } auto) forced = auto;

    var request = new PlanRequest(now, temp, slots, prices, Model, comfort, Config.MaxTemp, forced, Config.RunStartCostEur);
    Plan = WarmWaterPlanner.Plan(request);
    Automatic = AutomaticSetpoint is { } setpoint
      ? WarmWaterPlanner.PredictAutomatic(request, setpoint, Config.AutomaticHysteresisK, Heating)
      : WarmWaterPlan.Empty;
  }

  double? AutomaticSetpoint => Config.AutomaticSetpointEntity is not null
    && Config.AutomaticSetpointEntity.TryGetStateValue(out float s, numericalGetBaseValue: false) ? s : null;

  double? OneTimeSetpoint => Config.OneTimeSetpointEntity.TryGetStateValue(out float s, numericalGetBaseValue: false) ? s : null;

  /// <summary>On/off of a binary entity; null while unavailable (the heat pump's KNX link drops out now and then).</summary>
  static bool? OnOff(Entity e) => e.State?.ToLowerInvariant() switch { "on" => true, "off" => false, _ => null };

  // ── Control ────────────────────────────────────────────────────────────────────────────────
  /// <summary>Grid import left under the import cap for a new heating, W; null = no cap (set by PVControl each cycle).</summary>
  public int? ImportHeadroomW { get; set; }

  /// <summary>The heat pump entities the control needs were readable in the last cycle (health sensor).</summary>
  public bool HeatPumpAvailable { get; private set; } = true;

  /// <summary>How often the tank model is learned again.</summary>
  public static readonly TimeSpan RefitInterval = TimeSpan.FromDays(1);
  /// <summary>Wait after a failed refit (HA history unavailable) before the next try.</summary>
  public static readonly TimeSpan RefitRetry = TimeSpan.FromHours(1);

  /// <summary>Whether the tank model is due to be learned again: a day after the last successful fit, at most once an hour.</summary>
  /// <param name="lastFit">Last successful fit; null = none yet.</param>
  /// <param name="lastAttempt">Last try; null = none yet.</param>
  /// <param name="now">Current time.</param>
  public static bool RefitDue(DateTime? lastFit, DateTime? lastAttempt, DateTime now) =>
    (lastFit is null || now - lastFit.Value >= RefitInterval) && (lastAttempt is null || now - lastAttempt.Value >= RefitRetry);

  private DateTime? _lastFitAttempt;
  private Task? _refit;

  /// <summary>Starts the daily refit in the background and carries out this cycle's control decision (<see cref="WarmWaterControl.Decide"/>).</summary>
  /// <param name="now">Current time.</param>
  public async Task ActAsync(DateTime now)
  {
    // The refit reads ~10 days of HA history (20–40 requests): off the 15-s cycle, one at a time.
    if (_refit is not { IsCompleted: false } && RefitDue(LastFit, _lastFitAttempt, now))
      _refit = Task.Run(() => RefitAsync(CancellationToken.None));

    if (Temperature is not { } temp
        || OnOff(Config.OneTimeSwitchEntity) is not { } switchOn
        || OnOff(Config.OneTimeActiveEntity) is not { } oneTimeActive)
    {
      ControlReason = "Heat pump unavailable";
      HeatPumpAvailable = false;
      return;
    }
    HeatPumpAvailable = true;
    if (BoostOn)
      _boostSawRunning |= oneTimeActive;
    if (_wasActive && !oneTimeActive) _lastStop = now;
    _wasActive = oneTimeActive;
    var decision = WarmWaterControl.Decide(new ControlInput(now, temp, switchOn, oneTimeActive, OneTimeSetpoint, _lastCommand, _lastStart,
      Mode, Plan.Runs, Config.MaxTemp, BoostOn, BoostTemp, _boostSince, _boostSawRunning, Config.BoostTimeoutMinutes, _lastStop,
      ImportHeadroomW, (int)Math.Round(Model.PowerW(temp))));
    ControlReason = decision.Reason;

    if (decision.EndBoost is { } why)
    {
      PVCC_Logger.LogInformation("{name}: boost ended ({why})", Config.Name, why);
      ResetBoost();
      RequestReplan("boost ended");
    }
    if (decision.Command == ControlCommand.None)
      return;

    PVCC_Logger.LogInformation("{name}: {command} {target} at {temp} °C ({reason})", Config.Name, decision.Command, decision.Target, temp, decision.Reason);
    _lastCommand = now;
    switch (decision.Command)
    {
      case ControlCommand.Start:
        _lastStart = now;
        SetOneTimeSetpoint(decision.Target!.Value);
        // Give the setpoint a moment to reach the heat pump before starting.
        await Task.Delay(TimeSpan.FromSeconds(2));
        PVCC_HaContext.CallService("switch", "turn_on", data: new { entity_id = Config.OneTimeSwitchEntity.EntityId });
        break;
      case ControlCommand.Stop:
        PVCC_HaContext.CallService("switch", "turn_off", data: new { entity_id = Config.OneTimeSwitchEntity.EntityId });
        break;
      case ControlCommand.SetSetpoint:
        SetOneTimeSetpoint(decision.Target!.Value);
        break;
    }
  }

  void SetOneTimeSetpoint(double value) =>
    PVCC_HaContext.CallService("number", "set_value", data: new { entity_id = Config.OneTimeSetpointEntity.EntityId, value });

  void ResetBoost()
  {
    _boostSince = null;
    _boostSawRunning = false;
  }

  // ── Learning ───────────────────────────────────────────────────────────────────────────────
  /// <summary>Learns the tank model from the last <see cref="WarmWaterConfig.HistoryDays"/> days of HA history.</summary>
  /// <param name="ct">Cancels the history requests.</param>
  public async Task RefitAsync(CancellationToken ct)
  {
    _lastFitAttempt = DateTime.Now;
    try
    {
      var end = DateTime.Now;
      var start = end.AddDays(-Config.HistoryDays);
      var (okT, tempHistory) = await PVCC_ApiManager.GetEntityHistoryAsync(Config.TemperatureEntity, start, ct, getMinimal: true, endDateTime: end);
      var (okH, heatHistory) = await PVCC_ApiManager.GetEntityHistoryAsync(Config.HeatingActiveEntity, start, ct, getMinimal: true, endDateTime: end);
      if (!okT || !okH)
      {
        PVCC_Logger.LogWarning("{name}: no history for the tank model — keeping {model}", Config.Name, Model);
        return;
      }
      var temps = tempHistory
        .Where(h => double.TryParse(h.State, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
        .Select(h => (Time: h.LastChanged.ToLocalTime(), Temp: double.Parse(h.State, CultureInfo.InvariantCulture)))
        .OrderBy(h => h.Time).ToList();
      var intervals = OnIntervals(heatHistory.Select(h => (h.LastChanged.ToLocalTime(), h.State)));

      var runs = new List<HeatingRun>();
      foreach (var (a, b) in intervals)
      {
        double? power = null;
        if (Config.PowerEntity is not null && b - a >= TankModel.MinFitRun)
        {
          var (okP, powerHistory) = await PVCC_ApiManager.GetEntityHistoryAsync(Config.PowerEntity, a, ct, getMinimal: true, endDateTime: b);
          if (okP)
            power = StepFunction.TimeWeightedAverage(powerHistory
              .Where(h => double.TryParse(h.State, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
              .Select(h => (h.LastChanged.ToLocalTime(), (double?)double.Parse(h.State, CultureInfo.InvariantCulture))).ToList(), a, b);
        }
        runs.Add(new HeatingRun(a, b, StepFunction.ValueAt(temps, a), StepFunction.ValueAt(temps, b), power));
      }
      Model = TankModel.Fit(temps, runs, TankModel.Default);
      LastFit = DateTime.Now;   // only a successful fit: a failed one is tried again after RefitRetry, not a day later
      PVCC_Logger.LogInformation("{name}: tank model from {temps} readings and {runs} runs: {model}", Config.Name, temps.Count, runs.Count, Model);
      RequestReplan("tank model updated");
    }
    catch (Exception ex)
    {
      PVCC_Logger.LogError(ex, "{name}: learning the tank model failed — keeping {model}", Config.Name, Model);
    }
  }

  /// <summary>On → off intervals of a binary sensor history; a running interval is left out.</summary>
  /// <param name="history">(time, state) in time order.</param>
  /// <returns>The intervals.</returns>
  public static List<(DateTime Start, DateTime End)> OnIntervals(IEnumerable<(DateTime Time, string State)> history)
  {
    var result = new List<(DateTime, DateTime)>();
    DateTime? on = null;
    foreach (var (time, state) in history.OrderBy(h => h.Time))
    {
      if (state == "on") on ??= time;
      else if (on is { } start)
      {
        result.Add((start, time));
        on = null;
      }
    }
    return result;
  }



  // ── HA entities ───────────────────────────────────────────────────────────────────────────
  /// <summary>Entities whose changes are commands for this controller.</summary>
  public IEnumerable<Entity> CommandEntities =>
    new[] { _modeEntity, _comfortTempEntity, _boostEntity, _boostTempEntity }
      .Concat(_periods.SelectMany(p => new[] { p.Time, p.Enabled }));

  /// <summary>Registers the warm-water HA entities (mode, comfort, boost, periods, plan sensors).</summary>
  /// <param name="reset">Re-register all entities.</param>
  public async Task RegisterEntitiesAsync(bool reset)
  {
    string p = $"pv_control_{Slug}";
    _modeEntity = await RegisterSensor($"select.{p}_mode", $"{Config.Name} - Mode", "select", "mdi:water-boiler",
      addConfig: new { options = Enum.GetNames<WarmWaterMode>() },
      defaultValue: WarmWaterMode.PlanOnly.ToString(), reRegister: reset);
    _comfortTempEntity = await RegisterSensor($"number.{p}_comfort_temp", $"{Config.Name} - Comfort Temperature", "temperature", "mdi:thermometer-water",
      addConfig: new { min = 30, max = Config.MaxTemp, step = 0.5, mode = "slider", unit_of_measurement = "°C" },
      defaultValue: Config.ComfortTempDefault.ToString(CultureInfo.InvariantCulture), reRegister: reset);
    _boostTempEntity = await RegisterSensor($"number.{p}_boost_temp", $"{Config.Name} - Boost Temperature", "temperature", "mdi:thermometer-chevron-up",
      addConfig: new { min = 30, max = Config.MaxTemp, step = 0.5, mode = "slider", unit_of_measurement = "°C" },
      defaultValue: Config.BoostTempDefault.ToString(CultureInfo.InvariantCulture), reRegister: reset);
    // Up to 2026-10-20 the boost was a switch; a running heating is never stopped, so switching it
    // off did almost nothing — it's a button now. Remove the old switch from HA.
    if (new Entity(PVCC_HaContext, $"switch.{p}_boost").State is not null)
      await PVCC_EntityManager.RemoveAsync($"switch.{p}_boost");
    _boostEntity = await RegisterSensor($"button.{p}_boost", $"{Config.Name} - Boost", null, "mdi:shower-head",
      reRegister: reset);
    _planEntity = await RegisterSensor($"sensor.{p}_plan", $"{Config.Name} - Plan", null, "mdi:water-boiler-auto",
      defaultValue: "unknown", reRegister: reset);
    foreach (var period in Config.ComfortPeriods)
    {
      string ps = $"{p}_{period.Name.ToSlug()}";
      var time = await RegisterSensor($"time.{ps}", $"{Config.Name} - {period.Name}", null, "mdi:clock-outline",
        defaultValue: SchedulableLoadRuntime.TryParseReadyBy(period.DefaultTime, out var t) ? t.ToString("HH:mm:ss", CultureInfo.InvariantCulture) : "07:00:00",
        reRegister: reset);
      var enabled = await RegisterSensor($"switch.{ps}_enabled", $"{Config.Name} - {period.Name} enabled", "switch", "mdi:calendar-check",
        defaultValue: "ON", reRegister: reset);
      _periods.Add((period, time, enabled));
    }
  }

  /// <summary>Handles a command for one of this controller's entities.</summary>
  /// <param name="entity">The changed entity.</param>
  /// <param name="newState">Its new state.</param>
  /// <returns>False if the entity isn't one of this controller's.</returns>
  public async Task<bool> HandleCommandAsync(Entity entity, string newState)
  {
    string id = entity.EntityId;
    if (!CommandEntities.Any(e => e.EntityId == id)) return false;
    RequestReplan($"user changed {id}");

    if (id == _modeEntity.EntityId)
    {
      var mode = Enum.TryParse(newState, true, out WarmWaterMode m) ? m : WarmWaterMode.Off;
      PVCC_Logger.LogInformation("{name}: mode → {mode}", Config.Name, mode);
      await PVCC_EntityManager.SetStateAsync(id, mode.ToString());
    }
    else if (id == _comfortTempEntity.EntityId || id == _boostTempEntity.EntityId)
    {
      if (double.TryParse(newState, NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
        await PVCC_EntityManager.SetStateAsync(id, Math.Clamp(v, 30, Config.MaxTemp).ToString(CultureInfo.InvariantCulture));
    }
    else if (id == _boostEntity.EntityId)
    {
      // Button press (payload "PRESS"). Pressing again during a boost restarts its timeout.
      PVCC_Logger.LogInformation("{name}: boost pressed — heating to {temp} °C", Config.Name, BoostTemp);
      _boostSince = DateTime.Now;
      _boostSawRunning = false;
    }
    else if (_periods.Any(p => p.Time.EntityId == id))
    {
      if (SchedulableLoadRuntime.TryParseReadyBy(newState, out var t))
        await PVCC_EntityManager.SetStateAsync(id, t.ToString("HH:mm:ss", CultureInfo.InvariantCulture));
    }
    else
    {
      await PVCC_EntityManager.SetStateAsync(id, newState.Equals("on", StringComparison.OrdinalIgnoreCase) ? "ON" : "OFF");
    }
    return true;
  }

  /// <summary>Publishes the plan, forecast and state sensors.</summary>
  /// <param name="now">Current time.</param>
  public async Task PublishAsync(DateTime now)
  {
    var current = Plan.Runs.FirstOrDefault(r => TimeRange.Contains(r.Start, r.End, now));
    var next = Plan.Runs.FirstOrDefault(r => r.Start > now);
    string state = BoostOn ? $"boost to {BoostTemp:F1} °C"
      : current != default ? $"heating to {current.TargetTemp:F1} °C"
      : next != default ? $"{next.Start:HH:mm} → {next.TargetTemp:F1} °C"
      : "no heating planned";
    await PVCC_EntityManager.SetStateAsync(_planEntity.EntityId, state);

    static object Runs(WarmWaterPlan plan) => plan.Runs.Select(r => new
    {
      start = r.Start.ToISO8601(), end = r.End.ToISO8601(),
      from_temp = Math.Round(r.StartTemp, 1), to_temp = Math.Round(r.TargetTemp, 1), power_w = r.PowerW,
    }).ToList();
    await PVCC_EntityManager.SetAttributesAsync(_planEntity.EntityId, new
    {
      mode = Mode.ToString(),
      control = ControlReason,
      temperature = Temperature,
      comfort_temp = ComfortTemp,
      boost = BoostOn,
      boost_since = _boostSince?.ToISO8601(),
      prices_known_until = PricesKnownUntil?.ToISO8601(),
      outside_efficiency = OutsideEfficiencyUsed,
      plan_cost_eur = Math.Round(Plan.CostEur, 3),
      automatic_cost_eur = Math.Round(Automatic.CostEur, 3),
      comfort_deficit_k = Math.Round(Plan.ComfortDeficitK, 1),
      automatic_comfort_deficit_k = Math.Round(Automatic.ComfortDeficitK, 1),
      runs = Runs(Plan),
      automatic_runs = Runs(Automatic),
      // Every 30 min — the full 15-min forecast would come close to the 16 kB attribute limit.
      temp_forecast = Plan.Temps.Where((_, i) => i % 2 == 0).Select(t => new object[] { t.Time.ToISO8601(), Math.Round(t.Temp, 1) }).ToList(),
      model = new
      {
        heating_k_per_h = Math.Round(Model.HeatingRateKPerHour, 2),
        loss_k_per_h_at_42 = Math.Round(Model.LossKPerHour(42, 12) - Model.UsageKPerHour[12], 2),
        loss_k_per_h_at_55 = Math.Round(Model.LossKPerHour(55, 12) - Model.UsageKPerHour[12], 2),
        power_w_at_45 = Math.Round(Model.PowerW(45)),
        usage_k_per_h = Model.UsageKPerHour.Select(u => Math.Round(u, 2)).ToList(),
        fitted = LastFit?.ToISO8601() ?? "n/a",
      },
      last_plan = LastPlan?.ToISO8601() ?? "n/a",
      last_plan_reason = LastPlanReason,
    });
  }
}
