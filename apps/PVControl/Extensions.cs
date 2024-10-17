﻿using NetDaemon.Client;
using NetDaemon.HassModel.Entities;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Web;

namespace PVControl
{
  public struct SensorData
  {
    [JsonPropertyName("entity_id")]
    public string EntityId { get; set; }
    [JsonPropertyName("state")]
    public string State { get; set; }
    [JsonPropertyName("attributes")]
    public object Attributes { get; set; }
    [JsonPropertyName("last_changed")]
    public DateTime LastChanged { get; set; }
    [JsonPropertyName("last_reported")]
    public DateTime LastReported { get; set; }
    [JsonPropertyName("last_updated")]
    public DateTime LastUpdated { get; set; }
    [JsonPropertyName("context")]
    public Context Context { get; set; }
  }
  public struct Context
  {
    [JsonPropertyName("id")]
    public string Id { get; set; }
    [JsonPropertyName("parent_id")]
    public object ParentId { get; set; }
    [JsonPropertyName("user_id")]
    public object UserId { get; set; }
  }
  public static class Extensions
  {
    public static async Task<Tuple<bool, List<SensorData>>> GetEntityHistoryAsync(this IHomeAssistantApiManager apiManager, Entity entity, DateTime startDateTime, CancellationToken cancellationToken, bool getMinimal=false, bool getAttributes = false, DateTime? endDateTime = null)
    {
      if (entity is null)
        throw new ArgumentNullException(nameof(entity));

      string apiPath = String.Format("history/period/{1}?filter_entity_id={0}{2}{3}{4}", 
        entity.EntityId, 
        startDateTime.ToISO8601(),
        getAttributes ? "": "&no_attributes",
        getMinimal ? "&minimal_response" : "",
        endDateTime != null ? "&end_time=" + HttpUtility.UrlEncode(endDateTime?.ToISO8601()) : ""
        );
      try
      {
        var result = await apiManager.GetApiCallAsync<JsonElement>(apiPath, cancellationToken);
        var list = JsonSerializer.Deserialize<List<List<SensorData>>>(result);
        if (list is not null  && list.Count > 0) 
          return new Tuple<bool, List<SensorData>>(true, list.First());
        else
          return new Tuple<bool, List<SensorData>>(false, []);
      }
      catch 
      {
        return new Tuple<bool, List<SensorData>>(false, []);
      }
    }
    public static string ToISO8601(this DateTime date)
    {
      return date.ToString("yyyy'-'MM'-'dd'T'HH':'mm':'sszzz");
    }
    public static int GetUnitMultiplicator(this Entity entity)
    {
      string? unit = entity?.EntityState?.AttributesJson?.GetProperty("unit_of_measurement").ToString().ToLower();
      if (unit is not null && unit.Length > 1)
      {
        if (unit.StartsWith("ct"))
          return 1;
        if (unit.StartsWith('€') || unit.StartsWith("eur"))
          return 100;
        if (unit.StartsWith('k'))
          return 1000;
        if (unit.StartsWith('m'))
          return 1000000;
      }
      return 1;
    }
    public static bool TryGetStateValue<T>(this Entity entity, out T resultValue, bool numericalGetBaseValue = true) where T : struct
    {
      resultValue = default;
      if (entity.State is null)
      {
        return false;
      }
      else if (typeof(T) == typeof(float))
      {
        if (float.TryParse(entity.State, NumberStyles.Any, CultureInfo.InvariantCulture, out float value))
        {
          dynamic result = numericalGetBaseValue ? value * entity.GetUnitMultiplicator() : value;
          resultValue = (T)result;
          return true;
        }
        else
          return false;
      }
      else if (typeof(T) == typeof(int))
      {
        if (int.TryParse(entity.State, NumberStyles.Any, CultureInfo.InvariantCulture, out int value))
        {
          dynamic result = numericalGetBaseValue ? value * entity.GetUnitMultiplicator() : value;
          resultValue = (T)result;
          return true;
        }
        else
          return false;
      }
      else if (typeof(T) == typeof(String))
      {
        dynamic result = entity.State.ToString();
        resultValue = (T)result;
        return true;
      }
      else if (typeof(T) == typeof(DateTime))
      {
        if (DateTime.TryParse(entity.State, CultureInfo.InvariantCulture, out DateTime value))
        {
          dynamic result = value;
          resultValue = (T)result;
          return true;
        }
        else
          return false;
      }
      else
        return false;
    }
    public static Dictionary<DateTime, int> CombineForecastLists(this Dictionary<DateTime, int> list1, Dictionary<DateTime, int> list2)
    {
      foreach (var item in list2)
      {
        if (list1.ContainsKey(item.Key))
          list1[item.Key] += item.Value;
        else
          list1.Add(item.Key, item.Value);
      }
      return list1.OrderBy(o => o.Key).ToDictionary();
    }
    public static KeyValuePair<DateTime, int> FirstMinOrDefault(this Dictionary<DateTime, int> list, DateTime start = default, DateTime end = default)
    {
      var tempList = list;
      if (start != default)
        tempList = tempList.Where(l => l.Key >= start).ToDictionary();
      if (end != default)
        tempList = tempList.Where(l => l.Key <= end).ToDictionary();
      return tempList.Where(l => l.Value == tempList.Min(t => t.Value)).FirstOrDefault();
    }
    public static KeyValuePair<DateTime, int> FirstMaxOrDefault(this Dictionary<DateTime, int> list, DateTime start = default, DateTime end = default)
    {
      var tempList = list;
      if (start != default)
        tempList = tempList.Where(l => l.Key >= start).ToDictionary();
      if (end != default)
        tempList = tempList.Where(l => l.Key <= end).ToDictionary();
      return tempList.Where(l => l.Value == tempList.Max(t => t.Value)).FirstOrDefault();
    }
    public static KeyValuePair<DateTime, int> FirstUnderOrDefault(this Dictionary<DateTime, int> list, int underValue, DateTime start = default, DateTime end = default)
    {
      var tempList = list;
      if (start != default)
        tempList = tempList.Where(l => l.Key >= start).ToDictionary();
      if (end != default)
        tempList = tempList.Where(l => l.Key <= end).ToDictionary();
      return tempList.Where(l => l.Value <= underValue).FirstOrDefault();
    }
    public static KeyValuePair<DateTime, int> GetEntryAtTime(this Dictionary<DateTime, int> list, DateTime time)
    {
      if (list.TryGetValue(time.RoundToNearestQuarterHour(), out var entry))
        return new KeyValuePair<DateTime, int>(time.RoundToNearestQuarterHour(), entry);
      else
        return new KeyValuePair<DateTime, int>(default, 0);
    }
    public static DateTime RoundToNearestQuarterHour(this DateTime time)
    {
      int minutes = time.Minute;
      int remainder = minutes % 15;

      // Round to the nearest 15 minutes
      if (remainder < 8)
      {
        minutes -= remainder; // Round down
      }
      else
      {
        minutes += (15 - remainder); // Round up
      }

      if (minutes >= 60)
      {
        minutes -= 60;
        return new DateTime(time.Year, time.Month, time.Day, time.Hour + 1, minutes, 0);
      }
      else
        return new DateTime(time.Year, time.Month, time.Day, time.Hour, minutes, 0);
    }
  }
  public class FixedSizeQueue<T>(int capacity) : Queue<T> where T : struct
  {
    private readonly int _capacity = capacity;

    public new void Enqueue(T item)
    {
      if (base.Count >= _capacity)
      {
        base.Dequeue();
      }
      base.Enqueue(item);
    }
  }

}
