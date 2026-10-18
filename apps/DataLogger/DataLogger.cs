using NetDaemon.Client;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using System.Threading;
using NetDaemon.HassModel.Entities;
using System.Globalization;
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
    public Entity? ExportEnergyEntity { get; set; }
    public Entity? HouseEnergyEntity { get; set; }
    public Entity? BatteryChargeEntity { get; set; }
    public Entity? BatteryDischargeEntity { get; set; }
    public Entity? CarChargeEntity { get; set; }
    public Entity? CarDischargeEntity { get; set; }
    public Entity? HeatPumpEnergyEntity { get; set; }
    public Entity? WarmwaterEnergyEntity { get; set; }
    public Entity? CarSoCEntity { get; set; }
  }

  [NetDaemonApp]
//#if DEBUG
//  [Focus]
//#endif
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
        (nameof(_config.WarmwaterEnergyEntity), _config.WarmwaterEnergyEntity),
      ];

      foreach (var (name, entity) in entities)
      {
        if (entity is null) continue; // not configured — expected, not a failure
        if (entity.State is null
            || entity.State.Equals("unavailable", StringComparison.OrdinalIgnoreCase)
            || entity.State.Equals("unknown", StringComparison.OrdinalIgnoreCase))
          _logger.LogWarning("DataLogger: {name} ({entityId}) is not available at startup", name, entity.EntityId);
      }
    }
    /// <summary>
    /// Run this at least every 15-30 minutes to make sure every period is correctly logged even if netDeamon or HA restarts
    /// </summary>
    /// <returns></returns>
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

    private async Task ScheduleRunCore()
    {
      _logger.LogTrace("Entered ScheduleRun");
      DateTime now = DateTime.Now;
      DateTime lastRunDaily = await GetLastLogTimeStamp("daily");
      DateTime lastRunHourly = await GetLastLogTimeStamp("hourly");

      if (lastRunHourly == DateTime.MinValue)
      {
        // never run, so go back as far as HA history allows (normally 10 days, but we restrict it to 7 days to be sure we get values)
        DateTime endTime = DateTime.Now.AddDays(-7).Date.AddHours(1).AddMinutes(0);
        while (endTime < DateTime.Now)
        {
          await InsertHourly(endTime);
          endTime = endTime.AddHours(1);
        }
        lastRunHourly = await GetLastLogTimeStamp("hourly");
      }
      if (lastRunDaily == DateTime.MinValue)
      {
        // never run, so go back as far as HA history allows (normally 10 days, but we restrict it to 7 days to be sure we get values)
        DateTime endTime = DateTime.Now.AddDays(-6).Date.AddHours(0).AddMinutes(0);
        while (endTime < DateTime.Now)
        {
          await InsertDaily(endTime);
          endTime = endTime.AddDays(1);
        }
        lastRunDaily = await GetLastLogTimeStamp("daily");
      }

      lastRunHourly = lastRunHourly.AddHours(1);
      while (lastRunHourly < now)
      {
        // make sure we don't have some timedrift
        lastRunHourly = lastRunHourly.Date.AddHours(lastRunHourly.Hour).AddMinutes(59).AddSeconds(59);
        await InsertHourly(lastRunHourly);
        lastRunHourly = lastRunHourly.AddHours(1);
      }

      lastRunDaily = lastRunDaily.AddDays(1);
      while (lastRunDaily < now)
      {
        // make sure we don't have some timedrift
        lastRunDaily = lastRunDaily.Date.AddHours(23).AddMinutes(59).AddSeconds(59);
        await InsertDaily(lastRunDaily);
        lastRunDaily = lastRunDaily.AddDays(1);
      }
      _logger.LogTrace("Finished ScheduleRun");
    }
    private async Task<DateTime> GetLastLogTimeStamp(string tableName)
    {
      DateTime timeStamp = DateTime.MinValue;

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
            timeStamp = ParseDbTimestamp(queryResult.GetString(0));
          }
        }
        await con.CloseAsync();
      }

      return timeStamp;
    }
    /// <summary>
    /// Reads a stored timestamp ("2026-06-27T12:59:59+02:00") as local time. Don't use
    /// SqliteDataReader.GetDateTime for this: since Microsoft.Data.Sqlite 10 it returns UTC for
    /// offset strings, and all period arithmetic here (whole local hours/days) then ran on the
    /// UTC clock — hourly windows ended up in the future (all NULL), daily ones shifted by the
    /// UTC offset.
    /// </summary>
    public static DateTime ParseDbTimestamp(string text) =>
      DateTimeOffset.Parse(text, CultureInfo.InvariantCulture).LocalDateTime;

    /// <summary>
    /// Energy counted by a meter over a series of readings: the sum of its increases. A drop
    /// below 90 % of the last counted reading is a reset (daily counters at midnight, whenever it
    /// happens) and the new reading counts from 0 — unless the meter is back near its level
    /// within the next <see cref="GlitchLookahead"/> readings: then it was a glitch (e.g. a
    /// template sum briefly reading 0 while one source is unavailable) and is skipped. Smaller
    /// drops are noise and skipped too. Works for daily-reset and running-total counters alike.
    /// (It replaced "last − first, after skipping to the first exact 0", which wrote 0 for every
    /// day of running totals like the heat pump and carport meters, and for daily counters whose
    /// reset never showed an exact 0. HA's own statistics count such glitches as resets — e.g.
    /// 125 kWh of heat pump energy on 2026-10-12 instead of ~5.)
    /// </summary>
    public static float CounterIncrease(IReadOnlyList<float> readings)
    {
      if (readings.Count == 0) return 0;
      float total = 0;
      float reference = readings[0]; // last reading that counted — skipped readings never become the base
      for (int i = 1; i < readings.Count; i++)
      {
        float current = readings[i];
        if (current >= reference) { total += current - reference; reference = current; }
        else if (current < reference * 0.9f && !ReturnsTo(readings, i + 1, reference * 0.9f))
        { total += current; reference = current; }
      }
      return total;
    }

    /// <summary>Readings after a big drop checked for a return to the old level (glitch, not reset).</summary>
    public const int GlitchLookahead = 5;

    private static bool ReturnsTo(IReadOnlyList<float> readings, int from, float level)
    {
      for (int i = from; i < Math.Min(readings.Count, from + GlitchLookahead); i++)
        if (readings[i] >= level) return true;
      return false;
    }

    private async Task InsertHourly(DateTime endPeriod)
    {
      await PopulateDB(endPeriod, false);
    }
    private async Task InsertDaily(DateTime endPeriod)
    {
      await PopulateDB(endPeriod, true);
    }
    private async Task PopulateDB(DateTime historyEnd, bool daily=false)
    {
      int hours = daily ? 24 : 1;
      DateTime historyStart = historyEnd.AddHours(-hours);
      if (historyEnd.Minute >= 58 && hours == 1)
      {
        historyStart = historyEnd.Date.AddHours(historyEnd.Hour).AddMinutes(0).AddSeconds(0);
      }
      else if (historyEnd.Minute <= 2 && hours == 1)
      {
        historyEnd = historyEnd.AddHours(-1);
        historyEnd = historyEnd.Date.AddHours(historyEnd.Hour).AddMinutes(59).AddSeconds(59);
        historyStart = historyEnd.Date.AddHours(historyEnd.Hour).AddMinutes(0).AddSeconds(0);
      }
      else if (historyEnd.Minute >= 58 && historyEnd.Hour == 23 && hours == 24)
      {
        historyStart = historyEnd.Date.AddHours(0).AddMinutes(0).AddSeconds(0);
      }
      else if (historyEnd.Minute <= 2 && historyEnd.Hour == 0 && hours == 24)
      {
        historyEnd = historyEnd.Date.AddDays(-1).AddHours(23).AddMinutes(59).AddSeconds(59);
        historyStart = historyEnd.Date.AddHours(0).AddMinutes(0).AddSeconds(0);
      }

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
        await Task.WhenAll(averageTasks);
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


        var insertCommand = con.CreateCommand();
        insertCommand.CommandText = String.Format(
          @"
          INSERT OR IGNORE INTO {0} 
          (timestamp, temperatureoutside, temperatureinside, atmopressure, temperaturewarmwater, cloudcover, humidity, batterysoc, priceenergyimport, priceenergyexport, pvyield, houseenergy, importenergy, exportenergy, batterycharge, batterydischarge, carsoc, carcharge, cardischarge, heatpumpenergy, warmwaterenergy) 
          Values ('{1}',{2},{3},{4},{5},{6},{7},{8},{9},{10},{11},{12},{13},{14},{15},{16},{17},{18},{19}, {20}, {21});
          ",
          hours == 1 ? "hourly" : "daily",
          historyEnd.ToISO8601(),
          (tempOutside is not null) ? tempOutside?.ToString(CultureInfo.InvariantCulture) : "NULL",
          (tempInside is not null) ? tempInside?.ToString(CultureInfo.InvariantCulture) : "NULL",
          (atmopressure is not null) ? atmopressure?.ToString(CultureInfo.InvariantCulture) : "NULL",
          (tempWarmwater is not null) ? tempWarmwater?.ToString(CultureInfo.InvariantCulture) : "NULL",
          (cloudCover is not null) ? cloudCover?.ToString(CultureInfo.InvariantCulture) : "NULL",
          (humidity is not null) ? humidity?.ToString(CultureInfo.InvariantCulture) : "NULL",
          (batterySoC is not null) ? batterySoC?.ToString(CultureInfo.InvariantCulture) : "NULL",
          (priceEnergyImport is not null) ? priceEnergyImport?.ToString(CultureInfo.InvariantCulture) : "NULL",
          (priceEnergyExport is not null) ? priceEnergyExport?.ToString(CultureInfo.InvariantCulture) : "NULL",
          (pvYield is not null) ? pvYield?.ToString(CultureInfo.InvariantCulture) : "NULL",
          (houseEnergyUsed is not null) ? houseEnergyUsed?.ToString(CultureInfo.InvariantCulture) : "NULL",
          (energyImport is not null) ? energyImport?.ToString(CultureInfo.InvariantCulture) : "NULL",
          (energyExport is not null) ? energyExport?.ToString(CultureInfo.InvariantCulture) : "NULL",
          (batteryCharge is not null) ? batteryCharge?.ToString(CultureInfo.InvariantCulture) : "NULL",
          (batteryDischarge is not null) ? batteryDischarge?.ToString(CultureInfo.InvariantCulture) : "NULL",
          (carSoC is not null) ? carSoC?.ToString(CultureInfo.InvariantCulture) : "NULL",
          (carCharge is not null) ? carCharge?.ToString(CultureInfo.InvariantCulture) : "NULL",
          (carDischarge is not null) ? carDischarge?.ToString(CultureInfo.InvariantCulture) : "NULL",
          (heatpumpEnergy is not null) ? heatpumpEnergy?.ToString(CultureInfo.InvariantCulture) : "NULL",
          (warmwaterEnergy is not null) ? warmwaterEnergy?.ToString(CultureInfo.InvariantCulture) : "NULL"
          );
        _ = await insertCommand.ExecuteNonQueryAsync();
        await con.CloseAsync();
      }
      _logger.LogTrace("Finished storing values");
    }
    // Configured entities are always queried, whatever their state right now: a sensor that's
    // briefly unavailable at logging time still has history for the period.
    private static void AddFloatTask(List<Task<Tuple<string, float?>>> averageTasks, Entity? entity, FloatTask op, DateTime historyStart, DateTime historyEnd, Func<Entity, FloatTask, DateTime, DateTime, Task<Tuple<string, float?>>> query)
    {
      if (entity is not null)
        averageTasks.Add(Task.Run(() => query(entity, op, historyStart, historyEnd)));
    }
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
    private enum FloatTask
    {
      average, sum, min, max, diff, first, last
    }
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
          FloatTask.diff    => CounterIncrease(list),
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
    private async Task InitializeDB()
    {
      using SqliteConnection con = new(_connectionString);
      await con.OpenAsync();
      int actualDBVersion = await GetDBVersion(con);
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
        await SetDbVersion(con, 4);
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
        await SetDbVersion(con, 5);
      }
      await con.CloseAsync();
    }

    /// <summary>Removes duplicate timestamps (keeping the first row) and adds unique indexes.</summary>
    public const string DedupeAndIndexSql = @"
      DELETE FROM hourly WHERE rowid NOT IN (SELECT MIN(rowid) FROM hourly GROUP BY timestamp);
      DELETE FROM daily  WHERE rowid NOT IN (SELECT MIN(rowid) FROM daily  GROUP BY timestamp);
      CREATE UNIQUE INDEX IF NOT EXISTS ux_hourly_timestamp ON hourly(timestamp);
      CREATE UNIQUE INDEX IF NOT EXISTS ux_daily_timestamp  ON daily(timestamp);";
    private static async Task SetDbVersion(SqliteConnection con, int version)
    {
      var pragmaCommand = con.CreateCommand();
      pragmaCommand.CommandText = String.Format("PRAGMA user_version={0};", version);
      await pragmaCommand.ExecuteNonQueryAsync();
    }
    private static async Task<int> GetDBVersion(SqliteConnection con)
    {
      var pragmaCommand = con.CreateCommand();
      pragmaCommand.CommandText = "PRAGMA user_version;";
      using var result = await pragmaCommand.ExecuteReaderAsync();
      if (await result.ReadAsync())
        return int.TryParse(result.GetString(0), out int version) ? version : 0;

      return 0;
    }

    [GeneratedRegex(@"^-?\d+\.?\d*$")]
    private static partial Regex RegexNumeric();
  }
}
