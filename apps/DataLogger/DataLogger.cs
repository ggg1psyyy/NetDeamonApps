using NetDaemon.Client;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using System.Threading;
using NetDaemon.HassModel.Entities;
using PVControl;
using System.Globalization;
using YamlDotNet.Core;
using System.Text.RegularExpressions;
using System.Reactive.Concurrency;
using NetDaemon.Extensions.Scheduler;

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
    public Entity? PriceEnergyImportEntity { get; set; }
    public Entity? PriceEnergyExportEntity { get; set; }
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
    private readonly List<String> _resetDailyEntities;

    public DataLogger(IHomeAssistantApiManager apiManager, ILogger<DataLogger> logger, IAppConfig<DataLoggerConfig> config, IScheduler scheduler)
    {
      _apiManager = apiManager;
      _logger = logger;
      _config = config.Value;
      _scheduler = scheduler;
      _resetDailyEntities = [];
      if (String.IsNullOrWhiteSpace(_config.DBLocation))
        _config.DBLocation = "apps/DataLogger/energy_history.db";
      _connectionString += _config.DBLocation;
      if (_config.PVYieldEntity != null) _resetDailyEntities.Add(_config.PVYieldEntity.EntityId);
      if (_config.ImportEnergyEntity != null) _resetDailyEntities.Add(_config.ImportEnergyEntity.EntityId);
      if (_config.ExportEnergyEntity != null) _resetDailyEntities.Add(_config.ExportEnergyEntity.EntityId);
      if (_config.HouseEnergyEntity != null) _resetDailyEntities.Add(_config.HouseEnergyEntity.EntityId);
      if (_config.BatteryChargeEntity != null) _resetDailyEntities.Add(_config.BatteryChargeEntity.EntityId);
      if (_config.BatteryDischargeEntity != null) _resetDailyEntities.Add(_config.BatteryDischargeEntity.EntityId);
      if (_config.CarChargeEntity != null) _resetDailyEntities.Add(_config.CarChargeEntity.EntityId);
      if (_config.CarDischargeEntity != null) _resetDailyEntities.Add(_config.CarDischargeEntity.EntityId);
      if (_config.HeatPumpEnergyEntity != null) _resetDailyEntities.Add(_config.HeatPumpEnergyEntity.EntityId);
      if (_config.WarmwaterEnergyEntity != null) _resetDailyEntities.Add(_config.WarmwaterEnergyEntity.EntityId);
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
#if DEBUG
      //await PopulateDB();
      //await PopulateDB(true);
      await ScheduleRun();
#endif
      _scheduler.ScheduleCron("*/15 * * * *", async () => await ScheduleRun());
      //_scheduler.ScheduleCron("59 23 * * *", async () => await PopulateDB(true));
    }
    /// <summary>
    /// Run this at least every 15-30 minutes to make sure every period is correctly logged even if netDeamon or HA restarts
    /// </summary>
    /// <returns></returns>
    private async Task ScheduleRun()
    {
      _logger.LogDebug("Entered ScheduleRun");
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
      _logger.LogDebug("Finished ScheduleRun");
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
            timeStamp = queryResult.GetDateTime(0);
          }
        }
        await con.CloseAsync();
      }

      return timeStamp;
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
        AddFloatTask(averageTasks, _config.TemperatureOutsideEntity, FloatTask.average, historyStart, historyEnd);
        AddFloatTask(averageTasks, _config.TemperatureInsideEntity, FloatTask.average, historyStart, historyEnd);
        AddFloatTask(averageTasks, _config.AtmoPressureEntity, FloatTask.average, historyStart, historyEnd);
        AddFloatTask(averageTasks, _config.TemperatureWarmwaterEntity, FloatTask.average, historyStart, historyEnd);
        AddFloatTask(averageTasks, _config.CloudCoverEntity, FloatTask.average, historyStart, historyEnd);
        AddFloatTask(averageTasks, _config.HumidityEntity, FloatTask.average, historyStart, historyEnd);
        AddFloatTask(averageTasks, _config.BatterySoCEntity, FloatTask.average, historyStart, historyEnd);
        AddFloatTask(averageTasks, _config.PriceEnergyExportEntity, FloatTask.last, historyStart, historyEnd);
        AddFloatTask(averageTasks, _config.PriceEnergyImportEntity, FloatTask.last, historyStart, historyEnd);
        AddFloatTask(averageTasks, _config.PVYieldEntity, FloatTask.diff, historyStart, historyEnd);
        AddFloatTask(averageTasks, _config.HouseEnergyEntity, FloatTask.diff, historyStart, historyEnd);
        AddFloatTask(averageTasks, _config.ImportEnergyEntity, FloatTask.diff, historyStart, historyEnd);
        AddFloatTask(averageTasks, _config.ExportEnergyEntity, FloatTask.diff, historyStart, historyEnd);
        AddFloatTask(averageTasks, _config.BatteryChargeEntity, FloatTask.diff, historyStart, historyEnd);
        AddFloatTask(averageTasks, _config.BatteryDischargeEntity, FloatTask.diff, historyStart, historyEnd);
        AddFloatTask(averageTasks, _config.CarSoCEntity, FloatTask.average, historyStart, historyEnd);
        AddFloatTask(averageTasks, _config.CarChargeEntity, FloatTask.diff, historyStart, historyEnd);
        AddFloatTask(averageTasks, _config.CarDischargeEntity, FloatTask.diff, historyStart, historyEnd);
        AddFloatTask(averageTasks, _config.HeatPumpEnergyEntity, FloatTask.diff, historyStart, historyEnd);
        AddFloatTask(averageTasks, _config.WarmwaterEnergyEntity, FloatTask.diff, historyStart, historyEnd);
        await Task.WhenAll(averageTasks);
        float? tempOutside = GetFloatTaskResult(averageTasks, _config.TemperatureOutsideEntity);
        float? tempInside = GetFloatTaskResult(averageTasks, _config.TemperatureInsideEntity);
        float? atmopressure = GetFloatTaskResult(averageTasks, _config.AtmoPressureEntity);
        float? tempWarmwater = GetFloatTaskResult(averageTasks, _config.TemperatureWarmwaterEntity);
        int? cloudCover = (int?)GetFloatTaskResult(averageTasks, _config.CloudCoverEntity);
        int? humidity = (int?)GetFloatTaskResult(averageTasks, _config.HumidityEntity);
        int? batterySoC = (int?)GetFloatTaskResult(averageTasks, _config.BatterySoCEntity);
        float? priceEnergyImport = GetFloatTaskResult(averageTasks, _config.PriceEnergyImportEntity);
        float? priceEnergyExport = GetFloatTaskResult(averageTasks, _config.PriceEnergyExportEntity) * 100;
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
          INSERT INTO {0} 
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
      _logger.LogInformation("Finished storing values");
    }
    private async void AddFloatTask(List<Task<Tuple<string, float?>>> averageTasks, Entity? entity, FloatTask op, DateTime historyStart, DateTime historyEnd)
    {
      if (entity?.State is not null)
      {
        averageTasks.Add(Task.Run(() => GetFloatStateHistory(entity, op, historyStart, historyEnd)));
      }
    }
    private float? GetFloatTaskResult(List<Task<Tuple<string, float?>>> averageTasks, Entity? entity)
    {
      if (entity?.State is not null)
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
      if (float.TryParse(entity.State, NumberStyles.Any, CultureInfo.InvariantCulture, out float _))
      {
        try
        {
          var history = await _apiManager.GetEntityHistoryAsync(entity, historyStart, _cancelToken, endDateTime: historyEnd);
          var floatRegex = RegexNumeric();
          float stateValueFloat = 0;
          if (history.Item1 && history.Item2.Count > 0)
          {
            List<float> list = history.Item2.Where(w => floatRegex.IsMatch(w.State)).Select(s => float.Parse(s.State, NumberStyles.Any, CultureInfo.InvariantCulture)).ToList();
            // some daily sensors don't reset exactly at 00:00, so we have to remove all values != 0 from the beginning of the list
            int listCount = list.Count;
            if (_resetDailyEntities.Contains(entity.EntityId) && historyStart.Hour == 0 && historyStart.Minute == 0)
            {
              list = list.SkipWhile(x => x != 0).ToList();
            }
            if (list.Count < listCount)
            {
              _logger.LogDebug("History of Entity \"{entity}\" had to remove {count} entries where reset seems to be failing", entity.EntityId, listCount - list.Count);
              if (list.Count == 0)
              {
                _logger.LogDebug("History is now empty, so we asume it's simply 0");
                return new Tuple<string, float?>(entity.EntityId, 0f);
              }
            }
            //if (list.Count == 0)
            //{
            //  _logger.LogError(String.Format("History of Entity \"{0}\" didn't contain any entries", entity.EntityId));
            //  return new Tuple<string, float?>(entity.EntityId, null);
            //}
            switch (op)
            {
              case FloatTask.average:
                stateValueFloat = list.Count > 0 ? list.Average() : 0;
                break;
              case FloatTask.sum:
                stateValueFloat = list.Count > 0 ? list.Sum() : 0;
                break;
              case FloatTask.min:
                stateValueFloat = list.Count > 0 ? list.Min() : 0;
                break;
              case FloatTask.max:
                stateValueFloat = list.Count > 0 ? list.Max() : 0;
                break;
              case FloatTask.diff:
                stateValueFloat = list.Count > 0 ? list.Last() - list.First() : 0;
                break;
              case FloatTask.first:
                stateValueFloat = list.Count > 0 ? list.First() : 0;
                break;
              case FloatTask.last:
                stateValueFloat = list.Count > 0 ? list.Last() : 0;
                break;
            }

            return new Tuple<string, float?>(entity.EntityId, stateValueFloat);
          }
        }
        catch (Exception ex)
        {
          _logger.LogError("Error getting floatstatehistory ({entity}) \n {message}", entity.EntityId, ex.Message);
          return new Tuple<string, float?>(entity.EntityId, null);
        }
      }
      return new Tuple<string, float?>(entity.EntityId, null); 
    }
    private async Task InitializeDB()
    {
      using SqliteConnection con = new(_connectionString);
      await con.OpenAsync();
      int currentDBVersion = 4;
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
        await SetDbVersion(con, currentDBVersion);
      }
      //if (actualDBVersion < 4)
      //{
      //  var alterTableCommand = con.CreateCommand();
      //  alterTableCommand.CommandText =
      //    @"
      //    ALTER TABLE hourly ADD COLUMN carsoc INTEGER;
      //    ALTER TABLE daily ADD COLUMN carsoc INTEGER;
      //  ";
      //  _ = await alterTableCommand.ExecuteNonQueryAsync();
      //  await SetDbVersion(con, 4);
      //}
      await con.CloseAsync();
    }
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
