using NetDaemon.Client;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using System.Threading;
using NetDaemon.HassModel.Entities;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Reactive.Concurrency;
using NetDaemon.Extensions.Scheduler;
using NetDeamon.apps;

#pragma warning disable CS1998

namespace PVControl
{
  public class DataLoggerConfig
  {
    public String? DBLocation { get; set; }
    public Entity? TemperatureOutsideEntity { get; set; }
    public Entity? TemperatureInsideEntity { get; set; }
    public Entity? AtmoPressureEntity { get; set; }
    public Entity? TemperatureWarmwaterEntity { get; set; }
    public Entity? CloudCoverEntity { get; set; }
    public Entity? HumidityEntity { get; set; }
    public Entity? BatterySoCEntity { get; set; }
    public Entity? PVYieldEntity { get; set; }

    /// <summary>Priority-ordered fallback list — the EPEX provider occasionally stops
    /// reporting for hours/days, so we fall through to the next source that has a valid state.</summary>
    public List<Entity>? PriceEnergyImportEntities { get; set; }

    /// <summary>First entity in <see cref="PriceEnergyImportEntities"/> with a currently
    /// parseable numeric state, or null if none are available.</summary>
    public Entity? PriceEnergyImportEntity
    {
      get
      {
        if (PriceEnergyImportEntities is null) return null;
        foreach (var entity in PriceEnergyImportEntities)
          if (float.TryParse(entity.State, NumberStyles.Any, CultureInfo.InvariantCulture, out _))
            return entity;
        return null;
      }
    }

    /// <summary>How to determine which entity's value to log as priceenergyexport, checked in order:
    /// 1. PriceEnergyExportSameAsImport: true -> reuse the resolved PriceEnergyImportEntity
    /// 2. PriceEnergyExportFixed: true        -> PriceEnergyExportFixedEntity (falls back to
    ///    PriceEnergyExportFixedValue in PopulateDB if the entity has no value)
    /// 3. otherwise                           -> first available entity in PriceEnergyExportEntities</summary>
    public bool PriceEnergyExportSameAsImport { get; set; }
    public bool PriceEnergyExportFixed { get; set; }
    public Entity? PriceEnergyExportFixedEntity { get; set; }
    public float PriceEnergyExportFixedValue { get; set; }
    public List<Entity>? PriceEnergyExportEntities { get; set; }

    public Entity? PriceEnergyExportEntity
    {
      get
      {
        if (PriceEnergyExportSameAsImport) return PriceEnergyImportEntity;
        if (PriceEnergyExportFixed) return PriceEnergyExportFixedEntity;
        if (PriceEnergyExportEntities is null) return null;
        foreach (var entity in PriceEnergyExportEntities)
          if (float.TryParse(entity.State, NumberStyles.Any, CultureInfo.InvariantCulture, out _))
            return entity;
        return null;
      }
    }

    public Entity? ImportEnergyEntity { get; set; }
    /// <summary>Grid import counters (kWh), in priority order: the first with readings in a period gives its highest 15-min import (schema v8).</summary>
    public List<Entity> ImportTotalEntities { get; set; } = [];
    public Entity? ExportEnergyEntity { get; set; }
    public Entity? HouseEnergyEntity { get; set; }
    public Entity? BatteryChargeEntity { get; set; }
    public Entity? BatteryDischargeEntity { get; set; }
    public Entity? CarChargeEntity { get; set; }
    public Entity? CarDischargeEntity { get; set; }
    public Entity? HeatPumpEnergyEntity { get; set; }
    public Entity? WarmwaterEnergyEntity { get; set; }
    public Entity? CarSoCEntity { get; set; }
    // Heat pump operation and the house's state (schema v6, since 2026-10-22) — the load and
    // heating models need what history can't give back: mode, timing, valves, presence.
    public Entity? HeatPumpModeEntity { get; set; }
    public Entity? HeatingActiveEntity { get; set; }
    public Entity? CompressorActiveEntity { get; set; }
    public Entity? EcoModeEntity { get; set; }
    public Entity? TargetTemperatureEntity { get; set; }
    public Entity? FlowTemperatureEntity { get; set; }
    public Entity? ReturnTemperatureEntity { get; set; }
    public Entity? WindSpeedEntity { get; set; }
    public Entity? PresenceEntity { get; set; }
    public List<Entity> RoomClimateEntities { get; set; } = [];
    // Heat pump heat output and its electricity by mode (schema v7, since 2026-10-24): COP per
    // mode over any period = heat / electricity, counted under identical conditions in HA.
    public Entity? HeatEnergyHeatingEntity { get; set; }
    public Entity? HeatEnergyWarmwaterEntity { get; set; }
    public Entity? ElecEnergyHeatingEntity { get; set; }
    public Entity? ElecEnergyWarmwaterEntity { get; set; }
    // PVControl's mode and the inverter's EEPROM writes (schema v9): how often each mode runs,
    // and how many writes the inverter's non-volatile registers get.
    public Entity? PVControlModeEntity { get; set; }
    public Entity? BatteryChargingEnabledEntity { get; set; }
    /// <summary>Inverter settings stored in EEPROM: every state change is one write.</summary>
    public List<Entity> InverterEepromEntities { get; set; } = [];
  }

  [NetDaemonApp]
//#if DEBUG
//  [Focus]
//#endif
  /// <summary>Logs hourly and daily energy, temperatures and heat pump operation from the HA history into SQLite (training data for the load models).</summary>
  public partial class DataLogger : IAsyncInitializable
  {
    private readonly IHomeAssistantApiManager _apiManager;
    private readonly String _connectionString = "Data Source=";
    private readonly ILogger _logger;
    private readonly DataLoggerConfig _config;
    private readonly IScheduler _scheduler;
    private CancellationToken _cancelToken;
    // A catch-up run can outlast the 15-min cron; overlapping runs inserted every hour twice.
    private readonly SemaphoreSlim _runLock = new(1, 1);

    /// <summary>Creates the app.</summary>
    /// <param name="apiManager">HA REST API (history).</param>
    /// <param name="logger">Logger.</param>
    /// <param name="config">App configuration (DataLogger.yaml).</param>
    /// <param name="scheduler">Scheduler for the 15-min run.</param>
    public DataLogger(IHomeAssistantApiManager apiManager, ILogger<DataLogger> logger, IAppConfig<DataLoggerConfig> config, IScheduler scheduler)
    {
      _apiManager = apiManager;
      _logger = logger;
      _config = config.Value;
      _scheduler = scheduler;
      if (String.IsNullOrWhiteSpace(_config.DBLocation))
        _config.DBLocation = "apps/DataLogger/energy_history.db";
      _connectionString += _config.DBLocation;
    }

    async Task IAsyncInitializable.InitializeAsync(CancellationToken cancellationToken)
    {     
      _cancelToken = cancellationToken;
      using (SqliteConnection con = new(_connectionString))
      {
        await con.OpenAsync(cancellationToken);
        if (con.State != System.Data.ConnectionState.Open)
        {
          _logger.LogError("Error opening DB: {DataSource}", con.DataSource);
          return;
        }
        await con.CloseAsync();
      }
      await InitializeDB();
      LogUnavailableSensors();
#if DEBUG
      //await PopulateDB();
      //await PopulateDB(true);
      await ScheduleRun();
#endif
      _scheduler.ScheduleCron("*/15 * * * *", async () => await ScheduleRun());
      //_scheduler.ScheduleCron("59 23 * * *", async () => await PopulateDB(true));
    }
    /// <summary>
    /// Logs a warning for every configured sensor that's unavailable/unknown/missing at startup.
    /// Runs once (InitializeAsync only runs once per app lifetime) rather than on every
    /// ScheduleRun tick, so a sensor that's been down for hours doesn't flood the log.
    /// </summary>
    private void LogUnavailableSensors()
    {
      (string Name, Entity? Entity)[] entities =
      [
        (nameof(_config.TemperatureOutsideEntity), _config.TemperatureOutsideEntity),
        (nameof(_config.TemperatureInsideEntity), _config.TemperatureInsideEntity),
        (nameof(_config.AtmoPressureEntity), _config.AtmoPressureEntity),
        (nameof(_config.TemperatureWarmwaterEntity), _config.TemperatureWarmwaterEntity),
        (nameof(_config.CloudCoverEntity), _config.CloudCoverEntity),
        (nameof(_config.HumidityEntity), _config.HumidityEntity),
        (nameof(_config.BatterySoCEntity), _config.BatterySoCEntity),
        (nameof(_config.PVYieldEntity), _config.PVYieldEntity),
        ($"{nameof(_config.PriceEnergyImportEntity)} (resolved)", _config.PriceEnergyImportEntity),
        ($"{nameof(_config.PriceEnergyExportEntity)} (resolved)", _config.PriceEnergyExportEntity),
        (nameof(_config.HouseEnergyEntity), _config.HouseEnergyEntity),
        (nameof(_config.ImportEnergyEntity), _config.ImportEnergyEntity),
        (nameof(_config.ExportEnergyEntity), _config.ExportEnergyEntity),
        (nameof(_config.BatteryChargeEntity), _config.BatteryChargeEntity),
        (nameof(_config.BatteryDischargeEntity), _config.BatteryDischargeEntity),
        (nameof(_config.CarSoCEntity), _config.CarSoCEntity),
        (nameof(_config.CarChargeEntity), _config.CarChargeEntity),
        (nameof(_config.CarDischargeEntity), _config.CarDischargeEntity),
        (nameof(_config.HeatPumpEnergyEntity), _config.HeatPumpEnergyEntity),
        (nameof(_config.HeatEnergyHeatingEntity), _config.HeatEnergyHeatingEntity), (nameof(_config.HeatEnergyWarmwaterEntity), _config.HeatEnergyWarmwaterEntity),
        (nameof(_config.ElecEnergyHeatingEntity), _config.ElecEnergyHeatingEntity), (nameof(_config.ElecEnergyWarmwaterEntity), _config.ElecEnergyWarmwaterEntity),
        (nameof(_config.WarmwaterEnergyEntity), _config.WarmwaterEnergyEntity),
        (nameof(_config.HeatPumpModeEntity), _config.HeatPumpModeEntity),
        (nameof(_config.HeatingActiveEntity), _config.HeatingActiveEntity),
        (nameof(_config.CompressorActiveEntity), _config.CompressorActiveEntity),
        (nameof(_config.EcoModeEntity), _config.EcoModeEntity),
        (nameof(_config.TargetTemperatureEntity), _config.TargetTemperatureEntity),
        (nameof(_config.FlowTemperatureEntity), _config.FlowTemperatureEntity),
        (nameof(_config.ReturnTemperatureEntity), _config.ReturnTemperatureEntity),
        (nameof(_config.WindSpeedEntity), _config.WindSpeedEntity),
        (nameof(_config.PresenceEntity), _config.PresenceEntity),
        .. _config.RoomClimateEntities.Select(e => ((string, Entity?))(nameof(_config.RoomClimateEntities), e)),
      ];

      // Fallback lists resolve to null when none of their entities has a value — that's a
      // failure, not "not configured", and the price column would silently stay empty.
      if (_config.PriceEnergyImportEntities is { Count: > 0 } imports && _config.PriceEnergyImportEntity is null)
        _logger.LogWarning("DataLogger: none of the import price entities has a value: {ids}", string.Join(", ", imports.Select(e => e.EntityId)));
      if (!_config.PriceEnergyExportSameAsImport && !_config.PriceEnergyExportFixed
          && _config.PriceEnergyExportEntities is { Count: > 0 } exports && _config.PriceEnergyExportEntity is null)
        _logger.LogWarning("DataLogger: none of the export price entities has a value: {ids}", string.Join(", ", exports.Select(e => e.EntityId)));

      foreach (var (name, entity) in entities)
      {
        if (entity is null) continue; // not configured — expected, not a failure
        if (entity.State is null
            || entity.State.Equals("unavailable", StringComparison.OrdinalIgnoreCase)
            || entity.State.Equals("unknown", StringComparison.OrdinalIgnoreCase))
          _logger.LogWarning("DataLogger: {name} ({entityId}) is not available at startup", name, entity.EntityId);
      }
    }
    /// <summary>Runs <see cref="ScheduleRunCore"/> (every 15 min, catching up after restarts); a run still going skips this one.</summary>
    private async Task ScheduleRun()
    {
      if (!await _runLock.WaitAsync(0))
      {
        _logger.LogDebug("DataLogger: previous run still busy, skipping this tick");
        return;
      }
      try { await ScheduleRunCore(); }
      finally { _runLock.Release(); }
    }

    /// <summary>Writes every hourly and daily row that is due (backfills an empty table first).</summary>
    private async Task ScheduleRunCore()
    {
      _logger.LogTrace("Entered ScheduleRun");
      DateTime nowUtc = DateTime.UtcNow;
      foreach (bool daily in new[] { false, true })
      {
        string table = daily ? "daily" : "hourly";
        foreach (var (start, end) in DuePeriods(await GetLastLogTimeStamp(table), nowUtc, daily, TimeZoneInfo.Local))
          await PopulateDB(start, end, daily);
      }
      _logger.LogTrace("Finished ScheduleRun");
    }

    /// <summary>Days an empty table is backfilled (HA history keeps ~10 days).</summary>
    public const int BackfillDays = 7;

    // Periods are UTC instants: local clock arithmetic skips or doubles the repeated hour when
    // summer time ends (2026-10-25: 02:00–02:59 summer time was never logged).
    /// <summary>The periods to write: every hour/local day after the last row that has ended by now.</summary>
    /// <param name="lastEndUtc">End of the table's last row; null = empty table, backfilled from <see cref="BackfillDays"/> local days ago.</param>
    /// <param name="nowUtc">Current time; periods ending before it are due.</param>
    /// <param name="daily">True = local days (23/25 h at the clock change), false = hours.</param>
    /// <param name="tz">Time zone of the local days.</param>
    /// <returns>(start, end) in UTC; end = the period's last second, the row's timestamp.</returns>
    public static IEnumerable<(DateTime Start, DateTime End)> DuePeriods(DateTime? lastEndUtc, DateTime nowUtc, bool daily, TimeZoneInfo tz)
    {
      DateTime PeriodEnd(DateTime t) => daily ? DayStart(DayStart(t, tz).AddHours(30), tz).AddSeconds(-1) : HourStart(t).AddHours(1).AddSeconds(-1);
      DateTime PeriodStart(DateTime end) => daily ? DayStart(end, tz) : HourStart(end);
      var end = lastEndUtc is { } last
        ? PeriodEnd(last.AddSeconds(1))
        : PeriodEnd(DayStart(nowUtc.AddDays(daily ? 1 - BackfillDays : -BackfillDays), tz));
      for (; end < nowUtc; end = PeriodEnd(end.AddSeconds(1)))
        yield return (PeriodStart(end), end);
    }

    /// <summary>Start of the UTC hour an instant lies in (= a local hour; whole-hour offsets).</summary>
    /// <param name="utc">Instant (UTC).</param>
    private static DateTime HourStart(DateTime utc) => new(utc.Year, utc.Month, utc.Day, utc.Hour, 0, 0, DateTimeKind.Utc);

    /// <summary>Local midnight starting the day an instant lies in.</summary>
    /// <param name="utc">Instant (UTC).</param>
    /// <param name="tz">Time zone of the day.</param>
    /// <returns>That midnight in UTC.</returns>
    private static DateTime DayStart(DateTime utc, TimeZoneInfo tz) =>
      TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(TimeZoneInfo.ConvertTimeFromUtc(utc, tz).Date, DateTimeKind.Unspecified), tz);

    /// <summary>Timestamp of a table's last row.</summary>
    /// <param name="tableName">"hourly" or "daily".</param>
    /// <returns>UTC; null for an empty table.</returns>
    private async Task<DateTime?> GetLastLogTimeStamp(string tableName)
    {
      DateTime? timeStamp = null;

      using (SqliteConnection con = new(_connectionString))
      {
        await con.OpenAsync();
        var queryCommand = con.CreateCommand();
        queryCommand.CommandText = String.Format(
          @"SELECT timestamp FROM {0} ORDER BY rowid DESC LIMIT 1",
          tableName
        );
        using (var queryResult = await queryCommand.ExecuteReaderAsync())
        {
          if (queryResult.Read())
          {
            timeStamp = DateTimeOffset.Parse(queryResult.GetString(0), CultureInfo.InvariantCulture).UtcDateTime;
          }
        }
        await con.CloseAsync();
      }

      return timeStamp;
    }

    /// <summary>Minutes in a period during which the state matched.</summary>
    /// <param name="changes">(time, state) in time order; the first one is the state at the period start.</param>
    /// <param name="from">Period start.</param>
    /// <param name="to">Period end.</param>
    /// <param name="match">Which states count.</param>
    /// <returns>Minutes.</returns>
    public static double MinutesInState(IReadOnlyList<(DateTime Time, string State)> changes, DateTime from, DateTime to, Func<string, bool> match) =>
      StepFunction.Pieces(changes, from, to).Where(p => match(p.Value)).Sum(p => p.Held.TotalMinutes);

    /// <summary>Changes of state within a period (unknown/unavailable skipped: a dropout and back is no change).</summary>
    /// <param name="changes">(time, state) in time order; the first one may be the state at the period start.</param>
    /// <param name="from">Period start (a change exactly then belongs to the period before).</param>
    /// <param name="to">Period end.</param>
    /// <returns>Count.</returns>
    public static int StateChanges(IReadOnlyList<(DateTime Time, string State)> changes, DateTime from, DateTime to)
    {
      int count = 0;
      string? previous = null;
      foreach (var (time, state) in changes)
      {
        if (state is "unknown" or "unavailable" or "") continue;
        if (previous is not null && state != previous && time > from && time <= to) count++;
        previous = state;
      }
      return count;
    }

    /// <summary>The state held longest in a period (unknown/unavailable ignored).</summary>
    /// <param name="changes">(time, state) in time order.</param>
    /// <param name="from">Period start.</param>
    /// <param name="to">Period end.</param>
    /// <returns>The state; null if none.</returns>
    public static string? PrevailingState(IReadOnlyList<(DateTime Time, string State)> changes, DateTime from, DateTime to) =>
      changes.Select(c => c.State).Where(s => s is not "unknown" and not "unavailable").Distinct()
        .Select(s => (State: s, Minutes: MinutesInState(changes, from, to, x => x == s)))
        .Where(x => x.Minutes > 0)
        .OrderByDescending(x => x.Minutes).Select(x => x.State).FirstOrDefault();

    /// <summary>Reads the period's values from the HA history and writes one hourly or daily row.</summary>
    /// <param name="historyStart">Period start (UTC).</param>
    /// <param name="historyEnd">Period end (UTC, its last second); the row's timestamp, written as local time with offset.</param>
    /// <param name="daily">True = daily row, false = hourly.</param>
    private async Task PopulateDB(DateTime historyStart, DateTime historyEnd, bool daily)
    {

      _logger.LogInformation("Calculating and storing {daily} values from {start} to {end}", daily ? "daily" : "hourly", historyStart.ToISO8601(), historyEnd.ToISO8601());

      using (SqliteConnection con = new(_connectionString))
      {
        await con.OpenAsync();
        List<Task<Tuple<string, float?>>> averageTasks = [];
        AddFloatTask(averageTasks, _config.TemperatureOutsideEntity, FloatTask.average, historyStart, historyEnd, GetFloatStateHistory);
        AddFloatTask(averageTasks, _config.TemperatureInsideEntity, FloatTask.average, historyStart, historyEnd, GetFloatStateHistory);
        AddFloatTask(averageTasks, _config.AtmoPressureEntity, FloatTask.average, historyStart, historyEnd, GetFloatStateHistory);
        AddFloatTask(averageTasks, _config.TemperatureWarmwaterEntity, FloatTask.average, historyStart, historyEnd, GetFloatStateHistory);
        AddFloatTask(averageTasks, _config.CloudCoverEntity, FloatTask.average, historyStart, historyEnd, GetFloatStateHistory);
        AddFloatTask(averageTasks, _config.HumidityEntity, FloatTask.average, historyStart, historyEnd, GetFloatStateHistory);
        AddFloatTask(averageTasks, _config.BatterySoCEntity, FloatTask.average, historyStart, historyEnd, GetFloatStateHistory);
        // Price entities may update every 15 min while this window can span an hour (or a full
        // day) — average rather than "last", so a logged row always represents the true mean
        // price over its window regardless of the source sensor's native update granularity.
        AddFloatTask(averageTasks, _config.PriceEnergyExportEntity, FloatTask.average, historyStart, historyEnd, GetFloatStateHistory);
        AddFloatTask(averageTasks, _config.PriceEnergyImportEntity, FloatTask.average, historyStart, historyEnd, GetFloatStateHistory);
        AddFloatTask(averageTasks, _config.PVYieldEntity, FloatTask.diff, historyStart, historyEnd, GetFloatStateHistory);
        AddFloatTask(averageTasks, _config.HouseEnergyEntity, FloatTask.diff, historyStart, historyEnd, GetFloatStateHistory);
        AddFloatTask(averageTasks, _config.ImportEnergyEntity, FloatTask.diff, historyStart, historyEnd, GetFloatStateHistory);
        AddFloatTask(averageTasks, _config.ExportEnergyEntity, FloatTask.diff, historyStart, historyEnd, GetFloatStateHistory);
        AddFloatTask(averageTasks, _config.BatteryChargeEntity, FloatTask.diff, historyStart, historyEnd, GetFloatStateHistory);
        AddFloatTask(averageTasks, _config.BatteryDischargeEntity, FloatTask.diff, historyStart, historyEnd, GetFloatStateHistory);
        AddFloatTask(averageTasks, _config.CarSoCEntity, FloatTask.average, historyStart, historyEnd, GetFloatStateHistory);
        AddFloatTask(averageTasks, _config.CarChargeEntity, FloatTask.diff, historyStart, historyEnd, GetFloatStateHistory);
        AddFloatTask(averageTasks, _config.CarDischargeEntity, FloatTask.diff, historyStart, historyEnd, GetFloatStateHistory);
        AddFloatTask(averageTasks, _config.HeatPumpEnergyEntity, FloatTask.diff, historyStart, historyEnd, GetFloatStateHistory);
        AddFloatTask(averageTasks, _config.WarmwaterEnergyEntity, FloatTask.diff, historyStart, historyEnd, GetFloatStateHistory);
        AddFloatTask(averageTasks, _config.HeatEnergyHeatingEntity, FloatTask.diff, historyStart, historyEnd, GetFloatStateHistory);
        AddFloatTask(averageTasks, _config.HeatEnergyWarmwaterEntity, FloatTask.diff, historyStart, historyEnd, GetFloatStateHistory);
        AddFloatTask(averageTasks, _config.ElecEnergyHeatingEntity, FloatTask.diff, historyStart, historyEnd, GetFloatStateHistory);
        AddFloatTask(averageTasks, _config.ElecEnergyWarmwaterEntity, FloatTask.diff, historyStart, historyEnd, GetFloatStateHistory);
        var operation = GetOperationValuesAsync(historyStart, historyEnd);
        var maxQuarter = GetMaxQuarterImportAsync(historyStart, historyEnd);
        var control = GetControlValuesAsync(historyStart, historyEnd);
        await Task.WhenAll(averageTasks);
        var op = await operation;
        var ctl = await control;
        float? tempOutside = GetFloatTaskResult(averageTasks, _config.TemperatureOutsideEntity);
        float? tempInside = GetFloatTaskResult(averageTasks, _config.TemperatureInsideEntity);
        float? atmopressure = GetFloatTaskResult(averageTasks, _config.AtmoPressureEntity);
        float? tempWarmwater = GetFloatTaskResult(averageTasks, _config.TemperatureWarmwaterEntity);
        int? cloudCover = (int?)GetFloatTaskResult(averageTasks, _config.CloudCoverEntity);
        int? humidity = (int?)GetFloatTaskResult(averageTasks, _config.HumidityEntity);
        int? batterySoC = (int?)GetFloatTaskResult(averageTasks, _config.BatterySoCEntity);
        float? priceEnergyImport = GetFloatTaskResult(averageTasks, _config.PriceEnergyImportEntity);
        float? priceEnergyExport = GetFloatTaskResult(averageTasks, _config.PriceEnergyExportEntity)
          ?? (_config.PriceEnergyExportFixed ? _config.PriceEnergyExportFixedValue : null);
        int? pvYield = (int?)(GetFloatTaskResult(averageTasks, _config.PVYieldEntity) * 1000);
        int? houseEnergyUsed = (int?)(GetFloatTaskResult(averageTasks, _config.HouseEnergyEntity));
        int? energyImport = (int?)(GetFloatTaskResult(averageTasks, _config.ImportEnergyEntity) * 1000);
        int? energyExport = (int?)(GetFloatTaskResult(averageTasks, _config.ExportEnergyEntity) * 1000);
        int? carSoC = (int?)(GetFloatTaskResult(averageTasks, _config.CarSoCEntity));
        int? batteryCharge = (int?)(GetFloatTaskResult(averageTasks, _config.BatteryChargeEntity) * 1000);
        int? batteryDischarge = (int?)(GetFloatTaskResult(averageTasks, _config.BatteryDischargeEntity) * 1000);
        int? carCharge = (int?)(GetFloatTaskResult(averageTasks, _config.CarChargeEntity) * 1000);
        int? carDischarge = (int?)(GetFloatTaskResult(averageTasks, _config.CarDischargeEntity) * 1000);
        int? heatpumpEnergy = (int?)(GetFloatTaskResult(averageTasks, _config.HeatPumpEnergyEntity) * 1000);
        int? warmwaterEnergy = (int?)(GetFloatTaskResult(averageTasks, _config.WarmwaterEnergyEntity) * 1000);
        int? heatHeating = (int?)(GetFloatTaskResult(averageTasks, _config.HeatEnergyHeatingEntity) * 1000);
        int? heatWarmwater = (int?)(GetFloatTaskResult(averageTasks, _config.HeatEnergyWarmwaterEntity) * 1000);
        int? elecHeating = (int?)(GetFloatTaskResult(averageTasks, _config.ElecEnergyHeatingEntity) * 1000);
        int? elecWarmwater = (int?)(GetFloatTaskResult(averageTasks, _config.ElecEnergyWarmwaterEntity) * 1000);


        (string Column, object? Value)[] values =
        [
          ("timestamp", historyEnd.ToISO8601()),
          ("temperatureoutside", tempOutside), ("temperatureinside", tempInside), ("atmopressure", atmopressure),
          ("temperaturewarmwater", tempWarmwater), ("cloudcover", cloudCover), ("humidity", humidity), ("batterysoc", batterySoC),
          ("priceenergyimport", priceEnergyImport), ("priceenergyexport", priceEnergyExport), ("pvyield", pvYield),
          ("houseenergy", houseEnergyUsed), ("importenergy", energyImport), ("exportenergy", energyExport),
          ("batterycharge", batteryCharge), ("batterydischarge", batteryDischarge), ("carsoc", carSoC), ("carcharge", carCharge),
          ("cardischarge", carDischarge), ("heatpumpenergy", heatpumpEnergy), ("warmwaterenergy", warmwaterEnergy),
          ("heatingmode", op.HeatingMode), ("heatingminutes", op.HeatingMinutes), ("compressorminutes", op.CompressorMinutes),
          ("ecominutes", op.EcoMinutes), ("hptargettemperature", op.TargetTemperature), ("flowtemperature", op.FlowTemperature), ("returntemperature", op.ReturnTemperature),
          ("windspeed", op.WindSpeed), ("athomeminutes", op.AtHomeMinutes), ("roomsheating", op.RoomsHeating), ("roomsetpoint", op.RoomSetpoint),
          ("heatenergyheating", heatHeating), ("heatenergywarmwater", heatWarmwater), ("heatpumpelecheating", elecHeating), ("heatpumpelecwarmwater", elecWarmwater),
          ("maxquarterimport", await maxQuarter),
          ("pvcontrolmode", ctl.Mode), ("pvmodechanges", ctl.ModeChanges), ("feedinminutes", ctl.FeedinMinutes),
          ("forcechargeminutes", ctl.ForceChargeMinutes), ("chargedisabledminutes", ctl.ChargeDisabledMinutes), ("invertereepromwrites", ctl.EepromWrites),
        ];
        var insertCommand = con.CreateCommand();
        insertCommand.CommandText = $"INSERT OR IGNORE INTO {(daily ? "daily" : "hourly")} ({string.Join(", ", values.Select(v => v.Column))}) " +
                                    $"VALUES ({string.Join(", ", values.Select(v => "$" + v.Column))});";
        foreach (var (column, value) in values)
          insertCommand.Parameters.AddWithValue("$" + column, value ?? DBNull.Value);
        _ = await insertCommand.ExecuteNonQueryAsync();
        await con.CloseAsync();
      }
      _logger.LogTrace("Finished storing values");
    }
    /// <summary>Highest 15-min grid import of a period from the first import counter with readings in it (v8).</summary>
    /// <param name="from">Period start (UTC).</param>
    /// <param name="to">Period end (UTC, its last second).</param>
    /// <returns>kW; null without a counter or readings.</returns>
    private async Task<double?> GetMaxQuarterImportAsync(DateTime from, DateTime to)
    {
      // All sources, then the first that worked over the period: a frozen receiver (online, no
      // telegrams) keeps its last value and would log 0 kW — the same rule as PVControl's live counter.
      var sources = new List<List<(DateTime, double)>?>();
      foreach (var entity in _config.ImportTotalEntities)
      {
        try
        {
          var history = await _apiManager.GetEntityHistoryAsync(entity, from, _cancelToken, endDateTime: to);
          sources.Add(!history.Item1 ? null : history.Item2
            .Where(s => double.TryParse(s.State, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            .Select(s => (s.LastChanged.ToUniversalTime(), double.Parse(s.State!, CultureInfo.InvariantCulture))).ToList());
        }
        catch (Exception ex)
        {
          _logger.LogError("Error getting history ({entity}) \n {message}", entity.EntityId, ex.Message);
          sources.Add(null);
        }
      }
      int used = NetDeamon.apps.PVControl.ImportCap.PriorityCounter.FirstWorking(sources
        .Select(r => r is { Count: > 0 } ? (double?)Energy.CounterIncrease(r.Select(x => (float)x.Item2).ToList()) : null).ToList());
      return used >= 0 && MaxQuarterKw(sources[used]!, from, to.AddSeconds(1)) is { } kw ? Math.Round(kw, 3) : null;
    }

    /// <summary>Highest mean import of the 15-min blocks of a period from a kWh counter (resets and glitches as in <see cref="Energy.CounterIncrease"/>).</summary>
    /// <param name="readings">(time, kWh) in time order; the first one may lie before the period.</param>
    /// <param name="from">Period start, on a quarter.</param>
    /// <param name="to">Period end (exclusive).</param>
    /// <returns>kW; null without readings.</returns>
    public static double? MaxQuarterKw(IReadOnlyList<(DateTime Time, double Kwh)> readings, DateTime from, DateTime to)
    {
      if (readings.Count == 0) return null;
      double? max = null;
      for (var q = from; q < to; q = q.AddMinutes(TimeRange.SlotMinutes))
      {
        var end = q.AddMinutes(TimeRange.SlotMinutes);
        var values = new List<float> { (float)StepFunction.ValueAt(readings, q) };
        values.AddRange(readings.Where(r => r.Time > q && r.Time < end).Select(r => (float)r.Kwh));
        values.Add((float)StepFunction.ValueAt(readings, end));
        double kw = Energy.SlotW(Energy.CounterIncrease(values));
        max = Math.Max(max ?? 0, kw);
      }
      return max;
    }

    /// <summary>Heat pump operation and the house's state over a period (schema v6 columns).</summary>
    private sealed record OperationValues(string? HeatingMode, double? HeatingMinutes, double? CompressorMinutes, double? EcoMinutes,
      double? TargetTemperature, double? FlowTemperature, double? ReturnTemperature, double? WindSpeed, double? AtHomeMinutes, double? RoomsHeating, double? RoomSetpoint);

    /// <summary>Heat pump operation and the house's state over a period (v6 columns) from the HA history.</summary>
    /// <param name="from">Period start (UTC).</param>
    /// <param name="to">Period end (UTC).</param>
    /// <returns>The values; null where the entity isn't configured or has no history.</returns>
    private async Task<OperationValues> GetOperationValuesAsync(DateTime from, DateTime to)
    {
      Task<List<SensorData>?> History(Entity? entity, bool attributes = false) => GetStateHistoryAsync(entity, from, to, attributes);
      static List<(DateTime, double?)> Numbers(List<SensorData> h) => h.Select(s => (Utc(s),
        double.TryParse(s.State, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : (double?)null)).ToList();
      static string? Attribute(SensorData s, string name) =>
        s.Attributes is JsonElement { ValueKind: JsonValueKind.Object } a && a.TryGetProperty(name, out var p)
          ? p.ValueKind == JsonValueKind.String ? p.GetString() : p.ToString()
          : null;
      double? Minutes(List<SensorData>? h, string on) => h is null ? null : MinutesInState(States(h), from, to, s => s == on);
      double? Average(List<SensorData>? h) => h is null ? null : StepFunction.TimeWeightedAverage(Numbers(h), from, to);

      var mode = History(_config.HeatPumpModeEntity);
      var heating = History(_config.HeatingActiveEntity);
      var compressor = History(_config.CompressorActiveEntity);
      var eco = History(_config.EcoModeEntity);
      var target = History(_config.TargetTemperatureEntity);
      var flow = History(_config.FlowTemperatureEntity);
      var ret = History(_config.ReturnTemperatureEntity);
      var wind = History(_config.WindSpeedEntity);
      var presence = History(_config.PresenceEntity);
      var rooms = _config.RoomClimateEntities.Select(e => History(e, attributes: true)).ToList();
      await Task.WhenAll(new Task[] { mode, heating, compressor, eco, target, flow, ret, wind, presence }.Concat(rooms));

      double? roomsHeating = null, roomSetpoint = null;
      var roomHistories = rooms.Select(r => r.Result).Where(h => h is not null).Select(h => h!).ToList();
      if (roomHistories.Count > 0)
      {
        double periodMinutes = (to - from).TotalMinutes;
        roomsHeating = roomHistories.Sum(h => MinutesInState(h.Select(s => (Utc(s), Attribute(s, "hvac_action") ?? "")).ToList(), from, to, a => a == "heating")) / periodMinutes;
        var setpoints = roomHistories
          .Select(h => StepFunction.TimeWeightedAverage(h.Select(s => (Utc(s),
            double.TryParse(Attribute(s, "temperature"), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : (double?)null)).ToList(), from, to))
          .Where(v => v is not null).Select(v => v!.Value).ToList();
        roomSetpoint = setpoints.Count > 0 ? setpoints.Average() : null;
      }
      return new OperationValues(
        mode.Result is { } m ? PrevailingState(States(m), from, to) : null,
        Minutes(heating.Result, "on"), Minutes(compressor.Result, "on"), Minutes(eco.Result, "on"), Average(target.Result),
        Average(flow.Result), Average(ret.Result), Average(wind.Result), Minutes(presence.Result, "home"),
        roomsHeating, roomSetpoint);
    }

    /// <summary>An entity's state history over a period.</summary>
    /// <param name="entity">Entity; null = none.</param>
    /// <param name="from">Period start (UTC).</param>
    /// <param name="to">Period end (UTC).</param>
    /// <param name="attributes">With attributes.</param>
    /// <returns>The history; null if not configured or failed.</returns>
    private async Task<List<SensorData>?> GetStateHistoryAsync(Entity? entity, DateTime from, DateTime to, bool attributes = false)
    {
      if (entity is null) return null;
      try
      {
        var history = await _apiManager.GetEntityHistoryAsync(entity, from, _cancelToken, getAttributes: attributes, endDateTime: to);
        return history.Item1 ? history.Item2 : null;
      }
      catch (Exception ex)
      {
        _logger.LogError("Error getting history ({entity}) \n {message}", entity.EntityId, ex.Message);
        return null;
      }
    }

    // LastChanged arrives as local time; compared with the UTC period only as UTC (DateTime
    // comparisons ignore Kind).
    private static DateTime Utc(SensorData s) => s.LastChanged.ToUniversalTime();
    private static List<(DateTime, string)> States(List<SensorData> h) => h.Select(s => (Utc(s), s.State ?? "")).ToList();

    /// <summary>PVControl's mode and the inverter's EEPROM writes over a period (schema v9 columns).</summary>
    private sealed record ControlValues(string? Mode, int? ModeChanges, double? FeedinMinutes, double? ForceChargeMinutes,
      double? ChargeDisabledMinutes, int? EepromWrites);

    /// <summary>PVControl's mode and the inverter's EEPROM writes over a period (v9 columns) from the HA history.</summary>
    /// <param name="from">Period start (UTC).</param>
    /// <param name="to">Period end (UTC).</param>
    /// <returns>The values; null where the entity isn't configured or has no history.</returns>
    private async Task<ControlValues> GetControlValuesAsync(DateTime from, DateTime to)
    {
      var mode = GetStateHistoryAsync(_config.PVControlModeEntity, from, to);
      var charging = GetStateHistoryAsync(_config.BatteryChargingEnabledEntity, from, to);
      var eeprom = _config.InverterEepromEntities.Select(e => GetStateHistoryAsync(e, from, to)).ToList();
      await Task.WhenAll(new[] { mode, charging }.Concat(eeprom));

      var modes = mode.Result is { } m ? States(m) : null;
      var eepromHistories = eeprom.Select(e => e.Result).Where(h => h is not null).ToList();
      return new ControlValues(
        modes is null ? null : PrevailingState(modes, from, to),
        modes is null ? null : StateChanges(modes, from, to),
        modes is null ? null : MinutesInState(modes, from, to, s => s == "feedin_priority"),
        modes is null ? null : MinutesInState(modes, from, to, s => s is "force_charge" or "force_charge_grid_only"),
        charging.Result is { } c ? MinutesInState(States(c), from, to, s => s == "off") : null,
        eepromHistories.Count > 0 ? eepromHistories.Sum(h => StateChanges(States(h!), from, to)) : null);
    }

    // Configured entities are always queried, whatever their state right now: a sensor that's
    // briefly unavailable at logging time still has history for the period.
    /// <summary>Starts a history query for an entity, if configured.</summary>
    /// <param name="averageTasks">Running queries.</param>
    /// <param name="entity">Entity; null = skipped.</param>
    /// <param name="op">Aggregation.</param>
    /// <param name="historyStart">Period start.</param>
    /// <param name="historyEnd">Period end.</param>
    /// <param name="query">The query (<see cref="GetFloatStateHistory"/>).</param>
    private static void AddFloatTask(List<Task<Tuple<string, float?>>> averageTasks, Entity? entity, FloatTask op, DateTime historyStart, DateTime historyEnd, Func<Entity, FloatTask, DateTime, DateTime, Task<Tuple<string, float?>>> query)
    {
      if (entity is not null)
        averageTasks.Add(Task.Run(() => query(entity, op, historyStart, historyEnd)));
    }
    /// <summary>Result of an entity's history query.</summary>
    /// <param name="averageTasks">Finished queries.</param>
    /// <param name="entity">Entity; null = none.</param>
    /// <returns>The value; null if missing or failed.</returns>
    private float? GetFloatTaskResult(List<Task<Tuple<string, float?>>> averageTasks, Entity? entity)
    {
      if (entity is not null)
      {
        try
        {
          return (float?)averageTasks.Where(t => t.Result.Item1 == entity.EntityId).FirstOrDefault()?.Result.Item2;
        }
        catch(Exception ex) 
        {
          _logger.LogError("Error getting historyvalues ({entityID}) \n {message}", entity.EntityId, ex.Message);
          return null;
        }
      }
      return null;
    }
    /// <summary>Aggregation of a period's numeric readings (diff = <see cref="Energy.CounterIncrease"/>).</summary>
    private enum FloatTask
    {
      average, sum, min, max, diff, first, last
    }
    /// <summary>Aggregates an entity's numeric readings over a period.</summary>
    /// <param name="entity">Entity.</param>
    /// <param name="op">Aggregation.</param>
    /// <param name="historyStart">Period start.</param>
    /// <param name="historyEnd">Period end.</param>
    /// <returns>(entity id, value); value null without readings or on error.</returns>
    private async Task <Tuple<string,float?>> GetFloatStateHistory(Entity entity, FloatTask op, DateTime historyStart, DateTime historyEnd)
    {
      try
      {
        var history = await _apiManager.GetEntityHistoryAsync(entity, historyStart, _cancelToken, endDateTime: historyEnd);
        if (!history.Item1)
        {
          _logger.LogWarning("History request failed for {entity} ({start} – {end})", entity.EntityId, historyStart.ToISO8601(), historyEnd.ToISO8601());
          return new Tuple<string, float?>(entity.EntityId, null);
        }
        var floatRegex = RegexNumeric();
        List<float> list = history.Item2.Where(w => floatRegex.IsMatch(w.State)).Select(s => float.Parse(s.State, NumberStyles.Any, CultureInfo.InvariantCulture)).ToList();
        // No numeric reading in the period: unknown, not 0 (0 °C / 0 % would be wrong data).
        if (list.Count == 0)
          return new Tuple<string, float?>(entity.EntityId, null);

        float value = op switch
        {
          FloatTask.average => list.Average(),
          FloatTask.sum     => list.Sum(),
          FloatTask.min     => list.Min(),
          FloatTask.max     => list.Max(),
          FloatTask.diff    => Energy.CounterIncrease(list),
          FloatTask.first   => list.First(),
          _                 => list.Last(),
        };
        return new Tuple<string, float?>(entity.EntityId, value);
      }
      catch (Exception ex)
      {
        _logger.LogError("Error getting floatstatehistory ({entity}) \n {message}", entity.EntityId, ex.Message);
        return new Tuple<string, float?>(entity.EntityId, null);
      }
    }
    /// <summary>Creates the tables or migrates them to the current schema version.</summary>
    private async Task InitializeDB()
    {
      using SqliteConnection con = new(_connectionString);
      await con.OpenAsync();
      int actualDBVersion = await SqliteSchema.GetVersionAsync(con);
      if (actualDBVersion <= 0)
      {
        var createTableCommand = con.CreateCommand();
        createTableCommand.CommandText =
          @"
          CREATE TABLE IF NOT EXISTS hourly
          (timestamp TEXT, temperatureoutside REAL, atmopressure REAL, temperatureinside REAL, temperaturewarmwater REAL, cloudcover INTEGER, humidity INTEGER, pvyield INTEGER, importenergy INTEGER, exportenergy INTEGER, houseenergy INTEGER, batterycharge INTEGER, batterydischarge INTEGER, batterysoc INTEGER, carcharge INTEGER, cardischarge INTGER, heatpumpenergy INTEGER, warmwaterenergy INTEGER, priceenergyimport REAL, priceenergyexport REAL, carsoc INTEGER);
          CREATE TABLE IF NOT EXISTS daily
          (timestamp TEXT, temperatureoutside REAL, atmopressure REAL, temperatureinside REAL, temperaturewarmwater REAL, cloudcover INTEGER, humidity INTEGER, pvyield INTEGER, importenergy INTEGER, exportenergy INTEGER, houseenergy INTEGER, batterycharge INTEGER, batterydischarge INTEGER, batterysoc INTEGER, carcharge INTEGER, cardischarge INTGER, heatpumpenergy INTEGER, warmwaterenergy INTEGER, priceenergyimport REAL, priceenergyexport REAL, carsoc INTEGER);
        ";
        _ = await createTableCommand.ExecuteNonQueryAsync();
        await SqliteSchema.SetVersionAsync(con, 4);
        actualDBVersion = 4;
      }
      if (actualDBVersion < 5)
      {
        // v5: one row per timestamp. Overlapping runs had inserted ~480 hourly and 20 daily
        // duplicates (counted twice by the load forecast). Keep the first row of each.
        var migrate = con.CreateCommand();
        migrate.CommandText = DedupeAndIndexSql;
        int removed = await migrate.ExecuteNonQueryAsync();
        _logger.LogInformation("DataLogger DB migrated to v5: duplicates removed, unique timestamp index added ({rows} rows changed)", removed);
        await SqliteSchema.SetVersionAsync(con, 5);
      }
      if (actualDBVersion < 6)
      {
        // Only the missing columns: an interrupted migration (columns added, version not saved)
        // must not fail with "duplicate column" on the next start and stop all logging.
        foreach (var table in new[] { "hourly", "daily" })
        {
          string sql = AddColumnsSql(table, OperationColumns, await SqliteSchema.ColumnsAsync(con, table));
          if (sql.Length > 0)
            await SqliteSchema.ExecuteAsync(con, sql);
        }
        _logger.LogInformation("DataLogger DB migrated to v6: heat pump operation and house state columns added");
        await SqliteSchema.SetVersionAsync(con, 6);
      }
      if (actualDBVersion < 7)
      {
        foreach (var table in new[] { "hourly", "daily" })
        {
          string sql = AddColumnsSql(table, HeatColumns, await SqliteSchema.ColumnsAsync(con, table));
          if (sql.Length > 0)
            await SqliteSchema.ExecuteAsync(con, sql);
        }
        _logger.LogInformation("DataLogger DB migrated to v7: heat pump heat and electricity per mode added");
        await SqliteSchema.SetVersionAsync(con, 7);
      }
      if (actualDBVersion < 8)
      {
        foreach (var table in new[] { "hourly", "daily" })
        {
          string sql = AddColumnsSql(table, ImportPeakColumns, await SqliteSchema.ColumnsAsync(con, table));
          if (sql.Length > 0)
            await SqliteSchema.ExecuteAsync(con, sql);
        }
        _logger.LogInformation("DataLogger DB migrated to v8: highest 15-min import added");
        await SqliteSchema.SetVersionAsync(con, 8);
      }
      if (actualDBVersion < 9)
      {
        foreach (var table in new[] { "hourly", "daily" })
        {
          string sql = AddColumnsSql(table, ControlColumns, await SqliteSchema.ColumnsAsync(con, table));
          if (sql.Length > 0)
            await SqliteSchema.ExecuteAsync(con, sql);
        }
        _logger.LogInformation("DataLogger DB migrated to v9: PVControl mode and inverter EEPROM writes added");
        await SqliteSchema.SetVersionAsync(con, 9);
      }
      await con.CloseAsync();
    }

    /// <summary>v6: heat pump operation and the house's state per hour/day — mode, minutes of
    /// heating/compressor/eco, the heat pump's current target temperature (lowered in eco mode),
    /// flow and return temperature, wind, minutes at home, rooms with an open valve (mean count),
    /// mean room setpoint.</summary>
    public static readonly string[] OperationColumns =
    [
      "heatingmode TEXT", "heatingminutes REAL", "compressorminutes REAL", "ecominutes REAL", "hptargettemperature REAL", "flowtemperature REAL",
      "returntemperature REAL", "windspeed REAL", "athomeminutes REAL", "roomsheating REAL", "roomsetpoint REAL",
    ];

    /// <summary>v7: heat pump heat output and electricity for heating and warm water, Wh (COP = heat / electricity).</summary>
    public static readonly string[] HeatColumns =
      ["heatenergyheating INTEGER", "heatenergywarmwater INTEGER", "heatpumpelecheating INTEGER", "heatpumpelecwarmwater INTEGER"];

    /// <summary>v8: highest 15-min grid import of the hour/day, kW (the network power price counts the month's highest).</summary>
    public static readonly string[] ImportPeakColumns = ["maxquarterimport REAL"];

    /// <summary>v9: PVControl's prevailing mode, its mode changes, minutes in feed-in priority / grid
    /// charging / with battery charging off, and writes to the inverter's EEPROM settings.</summary>
    public static readonly string[] ControlColumns =
    [
      "pvcontrolmode TEXT", "pvmodechanges INTEGER", "feedinminutes REAL", "forcechargeminutes REAL",
      "chargedisabledminutes REAL", "invertereepromwrites INTEGER",
    ];

    /// <summary>ALTER TABLE statements for the columns a table doesn't have yet (a migration step may run again after an interruption).</summary>
    /// <param name="table">Table name.</param>
    /// <param name="columns">Column definitions ("name TYPE").</param>
    /// <param name="existing">Columns it has.</param>
    /// <returns>SQL; empty if none are missing.</returns>
    public static string AddColumnsSql(string table, IEnumerable<string> columns, IReadOnlySet<string> existing) =>
      string.Concat(columns.Where(c => !existing.Contains(c.Split(' ')[0]))
        .Select(c => $"ALTER TABLE {table} ADD COLUMN {c};\n"));

    /// <summary>Removes duplicate timestamps (keeping the first row) and adds unique indexes.</summary>
    public const string DedupeAndIndexSql = @"
      DELETE FROM hourly WHERE rowid NOT IN (SELECT MIN(rowid) FROM hourly GROUP BY timestamp);
      DELETE FROM daily  WHERE rowid NOT IN (SELECT MIN(rowid) FROM daily  GROUP BY timestamp);
      CREATE UNIQUE INDEX IF NOT EXISTS ux_hourly_timestamp ON hourly(timestamp);
      CREATE UNIQUE INDEX IF NOT EXISTS ux_daily_timestamp  ON daily(timestamp);";

    [GeneratedRegex(@"^-?\d+\.?\d*$")]
    private static partial Regex RegexNumeric();
  }
}
