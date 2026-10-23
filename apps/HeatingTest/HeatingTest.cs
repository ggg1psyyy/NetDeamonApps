using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Globalization;
using System.Linq;
using System.Reactive.Concurrency;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NetDaemon.AppModel;
using NetDaemon.Extensions.MqttEntityManager;
using NetDaemon.Extensions.Scheduler;
using NetDaemon.HassModel;
using NetDaemon.HassModel.Entities;

namespace NetDeamon.apps.HeatingTest;

/// <summary>
/// Heating step test: on alternating nights lowers the heat pump's heating-curve level by an
/// offset (test) or leaves it alone (reference), and logs the heat pump and every room controller
/// to its own SQLite DB, to learn how the house reacts before any heating optimization is built.
///
/// Fully independent of PVControl: own config (HeatingTest.yaml, <c>Enabled: false</c> → nothing
/// runs), own HA entities (device "Heating Test"), own DB. The only thing it changes is the level
/// number, and it always restores the level it found: at the end of a test, when disarmed, when a
/// room gets too cold, and after a restart (the original level is kept in the DB).
///
/// The decisions are in <see cref="HeatingTestLogic"/> (unit tested); this class is the HA side.
/// </summary>
[NetDaemonApp]
public sealed class HeatingTest : IAsyncInitializable
{
  readonly IHaContext _ha;
  readonly IMqttEntityManager _mqtt;
  readonly ILogger<HeatingTest> _logger;
  readonly IScheduler _scheduler;
  readonly HeatingTestConfig _config;
  readonly HeatingTestDb _db;
  readonly SemaphoreSlim _lock = new(1, 1);

  Entity _armed = null!, _offset = null!, _startTime = null!, _duration = null!, _status = null!;
  HeatingRun? _active;
  HeatingRun? _restore;           // ended test whose original level isn't confirmed back yet
  DateTime? _lastRestoreCommand;
  RunKind? _lastKind;
  DateOnly? _lastAttempt;
  string _lastDecision = "";

  /// <summary>Creates the app.</summary>
  /// <param name="ha">HA context.</param>
  /// <param name="mqtt">MQTT entity manager.</param>
  /// <param name="config">App configuration (HeatingTest.yaml).</param>
  /// <param name="scheduler">Scheduler for the cycle.</param>
  /// <param name="logger">Logger.</param>
  public HeatingTest(IHaContext ha, IMqttEntityManager mqtt, IAppConfig<HeatingTestConfig> config, IScheduler scheduler, ILogger<HeatingTest> logger)
  {
    _ha = ha;
    _mqtt = mqtt;
    _logger = logger;
    _scheduler = scheduler;
    _config = config.Value;
    _db = new HeatingTestDb(_config.DBLocation);
  }

  async Task IAsyncInitializable.InitializeAsync(CancellationToken cancellationToken)
  {
    if (!_config.Enabled)
    {
      _logger.LogInformation("Heating test disabled (HeatingTest.yaml: Enabled: false) — nothing runs");
      return;
    }
    await _db.InitializeAsync();
    _active = await _db.ActiveRunAsync();
    _restore = await _db.UnrestoredTestAsync();
    var last = await _db.LastRunAsync();
    _lastKind = last?.Kind;
    _lastAttempt = last is null ? null : DateOnly.FromDateTime(last.Start);
    if (_active is not null)
      _logger.LogInformation("Heating test: continuing {kind} run {id} (until {end:dd.MM. HH:mm})", _active.Kind, _active.Id, _active.PlannedEnd);

    await RegisterEntitiesAsync();
    foreach (var e in new[] { _armed, _offset, _startTime, _duration })
      (await _mqtt.PrepareCommandSubscriptionAsync(e.EntityId).ConfigureAwait(false))
        .SubscribeAsync(async state => await CommandAsync(e, state));

    int seconds = Math.Clamp(_config.SampleSeconds, 10, 600);
    _scheduler.SchedulePeriodic(TimeSpan.FromSeconds(seconds), async () => await CycleAsync());
    await CycleAsync();
    _logger.LogInformation("Heating test ready: logging every {s} s to {db}", seconds, _config.DBLocation);
  }

  // ── Cycle ──────────────────────────────────────────────────────────────────────────────────
  async Task CycleAsync()
  {
    if (!await _lock.WaitAsync(0)) return;
    try
    {
      var now = DateTime.Now;
      var rooms = ReadRooms();
      await DecideAsync(now, rooms);
      await RestoreIfNeededAsync(now);
      await _db.InsertSampleAsync(ReadSample(now), rooms.Select(r => r.Sample).ToList());
      await PublishAsync(now);
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Heating test cycle failed");
    }
    finally
    {
      _lock.Release();
    }
  }

  async Task DecideAsync(DateTime now, List<(RoomReading Reading, RoomSample Sample)> rooms)
  {
    var settings = Settings();
    int heatingMinutes = await _db.HeatingMinutesSinceAsync(now.AddHours(-24), Math.Clamp(_config.SampleSeconds, 10, 600) / 60.0);
    string? mode = _config.HeatPumpModeEntity?.State;
    bool modeOn = _config.HeatPumpModeEntity is null || (mode is not null && _config.HeatingModes.Contains(mode));
    var d = HeatingTestLogic.Decide(new TestInput(now, settings, _active, _lastKind, _lastAttempt,
      Number(_config.OutdoorEntity), heatingMinutes, rooms.Select(r => r.Reading).ToList(),
      _config.MaxOutdoorAtStart, _config.MinHeatingMinutes24h, mode, modeOn));
    _lastDecision = d.Reason;

    switch (d.Action)
    {
      case TestAction.Skip:
        _lastAttempt = DateOnly.FromDateTime(now);
        _logger.LogInformation("Heating test: {reason}", d.Reason);
        break;

      case TestAction.Start:
        _lastAttempt = DateOnly.FromDateTime(now);
        var end = now.AddHours(settings.DurationHours);
        double? original = d.Kind == RunKind.Test ? Number(_config.LevelEntity) : null;
        if (d.Kind == RunKind.Test && original is null)
        {
          _lastDecision = "skipped: heating-curve level unavailable";
          _logger.LogWarning("Heating test: {reason}", _lastDecision);
          break;
        }
        long id = await _db.StartRunAsync(d.Kind!.Value, now, end, d.Kind == RunKind.Test ? settings.OffsetK : 0, original, Number(_config.OutdoorEntity));
        _active = new HeatingRun(id, d.Kind.Value, now, end, settings.OffsetK, original);
        _lastKind = d.Kind;
        if (original is { } level)
        {
          double testLevel = HeatingTestLogic.TestLevel(level, settings.OffsetK);
          _logger.LogInformation("Heating test: run {id} started — level {from} → {to} until {end:HH:mm}", id, level, testLevel, end);
          SetLevel(testLevel);
        }
        else
          _logger.LogInformation("Heating test: reference run {id} started until {end:HH:mm}", id, end);
        break;

      case TestAction.End when _active is { } run:
        await _db.EndRunAsync(run.Id, now, d.Reason);
        _logger.LogInformation("Heating test: {kind} run {id} ended ({reason})", run.Kind, run.Id, d.Reason);
        if (run.Kind == RunKind.Test && run.OriginalLevel is not null)
        {
          _restore = run;
          _lastRestoreCommand = null;
        }
        _active = null;
        break;
    }
  }

  /// <summary>Sets the original level back after a test, and keeps trying until the heat pump reports it.</summary>
  async Task RestoreIfNeededAsync(DateTime now)
  {
    if (_restore is not { OriginalLevel: { } original } run) return;
    if (Number(_config.LevelEntity) is { } level && Math.Abs(level - original) < 0.05)
    {
      await _db.MarkRestoredAsync(run.Id);
      _logger.LogInformation("Heating test: level {level} restored (run {id})", original, run.Id);
      _restore = null;
      return;
    }
    if (_lastRestoreCommand is { } last && now - last < TimeSpan.FromMinutes(2)) return;
    _lastRestoreCommand = now;
    _logger.LogInformation("Heating test: restoring level {level} (run {id})", original, run.Id);
    SetLevel(original);
  }

  void SetLevel(double value) =>
    _ha.CallService("number", "set_value", data: new { entity_id = _config.LevelEntity.EntityId, value });

  // ── Reading HA ─────────────────────────────────────────────────────────────────────────────
  TestSettings Settings() => new(
    Armed: _armed.State?.Equals("on", StringComparison.OrdinalIgnoreCase) ?? false,
    StartTime: TryTime(_startTime.State, out var t) ? t : TryTime(_config.DefaultStartTime, out var d) ? d : new TimeOnly(22, 0),
    DurationHours: Number(_duration) ?? _config.DefaultDurationHours,
    OffsetK: Number(_offset) ?? _config.DefaultOffsetK);

  static bool TryTime(string? s, out TimeOnly t) =>
    TimeOnly.TryParseExact(s?.Trim(), ["H:mm", "HH:mm", "H:mm:ss", "HH:mm:ss"], CultureInfo.InvariantCulture, DateTimeStyles.None, out t);

  static double? Number(Entity? e) =>
    e?.State is { } s && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : null;

  static bool? OnOff(Entity? e) => e?.State?.ToLowerInvariant() switch { "on" => true, "off" => false, _ => null };

  static double? NumberAttribute(Entity e, string name) =>
    e.TryGetJsonAttribute(name, out JsonElement a) && a.ValueKind == JsonValueKind.Number ? a.GetDouble() : null;

  static string? TextAttribute(Entity e, string name) =>
    e.TryGetJsonAttribute(name, out JsonElement a) && a.ValueKind == JsonValueKind.String ? a.GetString() : null;

  List<(RoomReading Reading, RoomSample Sample)> ReadRooms() => _config.Rooms.Select(r =>
  {
    double? current = NumberAttribute(r.Climate, "current_temperature");
    double? setpoint = NumberAttribute(r.Climate, "temperature");
    return (new RoomReading(r.Name, current, setpoint, r.AbortBelowK),
            new RoomSample(r.Name, current, setpoint, TextAttribute(r.Climate, "hvac_action"), TextAttribute(r.Climate, "preset_mode")));
  }).ToList();

  HeatPumpSample ReadSample(DateTime now) => new(
    now, _active?.Id, _active?.Kind.ToString() ?? "Normal",
    Number(_config.OutdoorEntity), Number(_config.OutdoorHouseEntity),
    Number(_config.FlowEntity), Number(_config.FlowSetpointEntity), Number(_config.ReturnEntity),
    Number(_config.CompressorEntity), Number(_config.PowerEntity), Number(_config.CopEntity),
    Number(_config.LevelEntity), Number(_config.SlopeEntity),
    OnOff(_config.HeatingActiveEntity), OnOff(_config.WarmWaterActiveEntity),
    _config.HeatPumpModeEntity?.State);

  // ── HA entities ───────────────────────────────────────────────────────────────────────────
  // republish: publish the config even if the entity exists (state kept) — HA updates the device's
  // firmware version only from a published config.
  async Task<Entity> RegisterAsync(string id, string name, string? deviceClass, string icon, object? addConfig, string defaultValue, bool republish = false)
  {
    var entity = new Entity(_ha, id);
    bool isNew = entity.State is null;
    if (isNew || republish)
    {
      dynamic cfg = new ExpandoObject();
      var dict = (IDictionary<string, object>)cfg;
      if (addConfig is not null)
        foreach (var p in addConfig.GetType().GetProperties())
          dict[p.Name] = p.GetValue(addConfig)!;
      dict["device"] = new { identifiers = new[] { "heating_test" }, name = "Heating Test", model = "Heating Test", manufacturer = "AH", sw_version = AppVersion.Current };
      dict["icon"] = icon;
      await _mqtt.CreateAsync(id, new EntityCreationOptions { Name = name, DeviceClass = deviceClass }, cfg).ConfigureAwait(false);
      if (isNew)
        await _mqtt.SetStateAsync(id, defaultValue).ConfigureAwait(false);
    }
    return entity;
  }

  async Task RegisterEntitiesAsync()
  {
    _armed = await RegisterAsync("switch.heating_test_armed", "Heating Test - Armed", "switch", "mdi:flask-outline", null, "OFF");
    _offset = await RegisterAsync("number.heating_test_offset", "Heating Test - Level Offset", null, "mdi:thermometer-minus",
      new { min = -6, max = 0, step = 0.5, mode = "box", unit_of_measurement = "K" },
      _config.DefaultOffsetK.ToString(CultureInfo.InvariantCulture));
    _startTime = await RegisterAsync("time.heating_test_start", "Heating Test - Start", null, "mdi:clock-start", null,
      TryTime(_config.DefaultStartTime, out var t) ? t.ToString("HH:mm:ss", CultureInfo.InvariantCulture) : "22:00:00");
    _duration = await RegisterAsync("number.heating_test_duration", "Heating Test - Duration", null, "mdi:timer-outline",
      new { min = 1, max = 16, step = 0.5, mode = "box", unit_of_measurement = "h" },
      _config.DefaultDurationHours.ToString(CultureInfo.InvariantCulture));
    _status = await RegisterAsync("sensor.heating_test_status", "Heating Test - Status", null, "mdi:flask", null, "Idle");
    var version = await RegisterAsync("sensor.heating_test_version", "Heating Test - Version", null, "mdi:tag-outline", null, AppVersion.Current, republish: true);
    await _mqtt.SetStateAsync(version.EntityId, AppVersion.Current).ConfigureAwait(false);
  }

  async Task CommandAsync(Entity entity, string state)
  {
    string echo = state;
    if (entity.EntityId == _armed.EntityId)
    {
      echo = state.Equals("on", StringComparison.OrdinalIgnoreCase) ? "ON" : "OFF";
      _logger.LogInformation("Heating test {state}", echo == "ON" ? "armed" : "disarmed");
    }
    else if (entity.EntityId == _startTime.EntityId)
    {
      if (!TryTime(state, out var t)) return;
      echo = t.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
    }
    else if (!double.TryParse(state, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
      return;
    await _mqtt.SetStateAsync(entity.EntityId, echo);
    await CycleAsync(); // a disarm restores the level right away
  }

  async Task PublishAsync(DateTime now)
  {
    string state = _active is { } run ? run.Kind.ToString()
      : _restore is not null ? "Restoring"
      : Settings().Armed ? "Armed" : "Idle";
    await _mqtt.SetStateAsync(_status.EntityId, state);
    var recent = await _db.RecentRunsAsync(6);
    await _mqtt.SetAttributesAsync(_status.EntityId, new
    {
      decision = _lastDecision,
      run_id = _active?.Id,
      run_end = _active?.PlannedEnd.ToISO8601(),
      original_level = _active?.OriginalLevel ?? _restore?.OriginalLevel,
      level = Number(_config.LevelEntity),
      next_kind = (_lastKind == RunKind.Test ? RunKind.Reference : RunKind.Test).ToString(),
      db = _config.DBLocation,
      recent_runs = recent.Select(x => new
      {
        id = x.Run.Id, kind = x.Run.Kind.ToString(),
        start = HeatingTestDb.Ts(x.Run.Start), end = x.End is { } e ? HeatingTestDb.Ts(e) : null,
        offset_k = x.Run.OffsetK, reason = x.Reason,
      }).ToList(),
    });
  }
}
