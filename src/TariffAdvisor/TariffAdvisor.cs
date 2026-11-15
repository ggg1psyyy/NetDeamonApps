using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Globalization;
using System.Linq;
using System.Reactive.Concurrency;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NetDaemon.AppModel;
using NetDaemon.Extensions.MqttEntityManager;
using NetDaemon.Extensions.Scheduler;
using NetDaemon.HassModel;
using NetDaemon.HassModel.Entities;
using NetDeamon.apps.PVControl.Optimizer;

namespace NetDeamon.apps.TariffAdvisor;

/// <summary>
/// Tariff advisor: is the other import tariff (Market/Fixed) cheaper for the coming day than today's? From
/// <see cref="AdviceSettings.StartTime"/> on it judges PVControl's latest snapshot once per quarter
/// (<see cref="TariffAdvice.Decide"/>) and shows the result in <c>sensor.tariff_advisor_advice</c>. It only advises:
/// the switch itself is done at the supplier (and in PVControl's select).
///
/// Independent of PVControl's code and state: it reads PVControl's dp_shadow.db (read only) and plans with the shared
/// planner (src/Planning). The decision is unit tested; this class is the HA side.
/// </summary>
[NetDaemonApp]
public sealed class TariffAdvisor : IAsyncInitializable, IAsyncDisposable
{
  /// <summary>The advice is judged again after this long even without a new snapshot (it may have gone stale).</summary>
  static readonly TimeSpan RecheckInterval = TimeSpan.FromMinutes(15);

  readonly IHaContext _ha;
  readonly IMqttEntityManager _mqtt;
  readonly ILogger<TariffAdvisor> _logger;
  readonly IScheduler _scheduler;
  readonly TariffAdvisorConfig _config;
  readonly string _dbPath;
  readonly SemaphoreSlim _lock = new(1, 1);
  IDisposable? _timer;

  Entity _advice = null!;
  DateTime? _judgedQuarter;
  DateTime _judgedAt = DateTime.MinValue;
  string? _lastState;

  /// <summary>Creates the app.</summary>
  /// <param name="ha">HA context.</param>
  /// <param name="mqtt">MQTT entity manager.</param>
  /// <param name="config">App configuration (TariffAdvisor.yaml).</param>
  /// <param name="scheduler">Scheduler for the cycle.</param>
  /// <param name="logger">Logger.</param>
  public TariffAdvisor(IHaContext ha, IMqttEntityManager mqtt, IAppConfig<TariffAdvisorConfig> config, IScheduler scheduler, ILogger<TariffAdvisor> logger)
  {
    _ha = ha;
    _mqtt = mqtt;
    _logger = logger;
    _scheduler = scheduler;
    _config = config.Value;
    _dbPath = AppFiles.PathOf(_config.SnapshotDb, GetType().Assembly);
  }

  async Task IAsyncInitializable.InitializeAsync(CancellationToken cancellationToken)
  {
    if (!_config.Enabled)
    {
      _logger.LogInformation("Tariff advisor disabled (TariffAdvisor.yaml: Enabled: false) — nothing runs");
      return;
    }
    _advice = await RegisterAsync("sensor.tariff_advisor_advice", "Tariff Advisor - Advice", "mdi:scale-balance", "pending");
    var version = await RegisterAsync("sensor.tariff_advisor_version", "Tariff Advisor - Version", "mdi:tag-outline",
      AppVersion.Of(GetType().Assembly), republish: true);
    await _mqtt.SetStateAsync(version.EntityId, AppVersion.Of(GetType().Assembly)).ConfigureAwait(false);
    _timer = _scheduler.SchedulePeriodic(TimeSpan.FromMinutes(1), async () => await CycleAsync());
    await CycleAsync();
    _logger.LogInformation("Tariff advisor ready: judging from {start} on, snapshots from {db}", _config.StartTime, _dbPath);
  }

  /// <summary>Ends the timer (NetDaemon disposes the app on a reload).</summary>
  public ValueTask DisposeAsync()
  {
    _timer?.Dispose();
    return ValueTask.CompletedTask;
  }

  // ── Cycle ──────────────────────────────────────────────────────────────────────────────────
  /// <summary>Every minute: from the start time on, judge each new snapshot (and again after <see cref="RecheckInterval"/>).</summary>
  async Task CycleAsync()
  {
    if (!await _lock.WaitAsync(0)) return;
    try
    {
      var now = DateTime.Now;
      var settings = _config.Settings;
      if (!TariffAdvice.Due(now, settings)) return;
      var latest = await ForecastSnapshotStore.ReadLatestAsync(_dbPath);
      if (latest?.Quarter == _judgedQuarter && now - _judgedAt < RecheckInterval) return;
      var advice = await Task.Run(() => TariffAdvice.Decide(latest, now, settings));   // two DP runs, ~1 s each
      _judgedQuarter = latest?.Quarter;
      _judgedAt = now;
      await PublishAsync(advice, now);
    }
    catch (Exception ex)
    {
      // Never leave an old advice standing: unavailable with the error, judged again after the recheck interval.
      _logger.LogError(ex, "Tariff advisor cycle failed");
      _judgedAt = DateTime.Now;
      try { await PublishAsync(TariffAdvice.Failed(ex), _judgedAt); }
      catch (Exception publish) { _logger.LogError(publish, "Tariff advisor: publishing the failure failed"); }
    }
    finally
    {
      _lock.Release();
    }
  }

  async Task PublishAsync(Advice a, DateTime now)
  {
    if (a.State != _lastState)
      _logger.LogInformation("Tariff advice: {state} — {reason}", a.State, a.Reason);
    _lastState = a.State;
    var attributes = new Dictionary<string, object?>
    {
      ["for_day"] = a.ForDay?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
      ["current_tariff"] = a.CurrentTariff,
      ["saving_eur"] = a.SavingEur is { } s ? Math.Round(s, 2) : null,
      ["min_saving_eur"] = _config.MinSavingEur,
      ["reason"] = a.Reason,
      ["snapshot_at"] = a.SnapshotAt?.ToISO8601(),
      ["computed_at"] = now.ToISO8601(),
    };
    foreach (var (tariff, cost) in a.CostEur ?? new Dictionary<string, float>())
      attributes[$"cost_{tariff.ToLowerInvariant()}_eur"] = Math.Round(cost, 2);
    await _mqtt.SetAttributesAsync(_advice.EntityId, attributes).ConfigureAwait(false);
    await _mqtt.SetStateAsync(_advice.EntityId, a.State).ConfigureAwait(false);
  }

  // ── HA entities ───────────────────────────────────────────────────────────────────────────
  // republish: publish the config even if the entity exists (state kept) — HA updates the device's
  // firmware version only from a published config.
  async Task<Entity> RegisterAsync(string id, string name, string icon, string defaultValue, bool republish = false)
  {
    var entity = new Entity(_ha, id);
    bool isNew = entity.State is null;
    if (isNew || republish)
    {
      dynamic cfg = new ExpandoObject();
      var dict = (IDictionary<string, object>)cfg;
      dict["device"] = new { identifiers = new[] { "tariff_advisor" }, name = "Tariff Advisor", model = "Tariff Advisor", manufacturer = "AH", sw_version = AppVersion.Of(GetType().Assembly) };
      dict["icon"] = icon;
      await _mqtt.CreateAsync(id, new EntityCreationOptions { Name = name }, cfg).ConfigureAwait(false);
      if (isNew)
        await _mqtt.SetStateAsync(id, defaultValue).ConfigureAwait(false);
    }
    return entity;
  }
}
