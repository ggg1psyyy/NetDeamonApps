using NetDaemon.Client;
using NetDaemon.HassModel.Entities;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Web;

namespace NetDeamon.apps
{
  public struct PriceTableEntry(DateTime startTime, DateTime endTime, float price)
  {
    [JsonPropertyName("start_time")]
    public DateTime StartTime { get; set; } = startTime;
    [JsonPropertyName("end_time")]
    public DateTime EndTime { get; set; } = endTime;
    [JsonPropertyName("price_per_kwh")]
    public float Price { get; set; } = price;
  }
  public enum InverterModes
  {
    automatic,
    normal,
    force_charge,
    grid_only,
    force_discharge,
    feedin_priority,
    house_only,
  }
  public enum BatteryStatuses
  {
    idle,
    charging,
    discharging,
    unknown,
  }
  public enum ForceChargeReasons
  {
    None,
    GoingUnderPreferredMinima,
    GoingUnderAbsoluteMinima,
    ForcedChargeAtMinimumPrice,
    ImportPriceNegative,
    ExportPriceNegative,
    OpportunisticDischarge,
    UserMode,
  }
  public enum RunHeavyLoadsStatus
  {
    Yes,
    No,
    IfNecessary,
    Prevent,
  }
  public enum RunHeavyLoadReasons
  {
    WillReach100,
    ChargingAtCheapestPrice,
    Charging,
    WillStayOverPreferredMinima,
    WillStayOverAbsoluteMinima,
    WillGoUnderAbsoluteMinima,
    CurrentlyOverPreferredMinima,
    CurrentlyOverAbsoluteMinima,
  }
  public enum PVPeriods
  {
    BeforePV,
    InPVPeriod,
    AfterPV,
  }
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
    public static async Task<Tuple<bool, List<SensorData>>> GetEntityHistoryAsync(this IHomeAssistantApiManager apiManager, Entity entity, DateTime startDateTime, CancellationToken cancellationToken, bool getMinimal = false, bool getAttributes = false, DateTime? endDateTime = null)
    {
      ArgumentNullException.ThrowIfNull(entity);

      string apiPath = string.Format("history/period/{1}?filter_entity_id={0}{2}{3}{4}",
        entity.EntityId,
        startDateTime.ToISO8601(),
        getAttributes ? "" : "&no_attributes",
        getMinimal ? "&minimal_response" : "",
        endDateTime != null ? "&end_time=" + HttpUtility.UrlEncode(endDateTime?.ToISO8601()) : ""
        );
      try
      {
        var result = await apiManager.GetApiCallAsync<JsonElement>(apiPath, cancellationToken);
        var list = result.Deserialize<List<List<SensorData>>>();
        if (list is not null && list.Count > 0)
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
      if (!entity.TryGetJsonAttribute("unit_of_measurement", out JsonElement unitAttr))
        return 1;
      string unit = unitAttr.ToString().ToLower();
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
      if (entity is null || entity.State is null)
      {
        return false;
      }
      else if (entity.State.Equals("unavailable", StringComparison.InvariantCultureIgnoreCase) || entity.State.Equals("unknown", StringComparison.InvariantCultureIgnoreCase))
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
      else if (typeof(T) == typeof(string))
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
      else if (typeof(T) == typeof(bool))
      {
        dynamic result = entity.State.Equals("on", StringComparison.InvariantCultureIgnoreCase);
        resultValue = (T)result;
        return true;
      }
      else if (typeof(T) == typeof(InverterModes))
      {
        dynamic result = InverterModes.automatic;
        if (Enum.TryParse(entity.State, out InverterModes modeselect))
          result = modeselect;
        else
          return false;
        resultValue = (T)result;
        return true;
      }
      else
        return false;
    }
    public static bool TryGetJsonAttribute(this Entity entity, string attributeName, out JsonElement attribute)
    {
      attribute = default!;
      if (entity is null)
        return false;
      else if (entity.EntityState is null || entity.EntityState.AttributesJson is null || entity.State?.ToLowerInvariant() == "unavailable")
      {
        return false;
      }
      JsonElement attr = (JsonElement)entity.EntityState.AttributesJson;
      if (attr.TryGetProperty(attributeName, out attribute))
        return true;

      return false;
    }
    public static bool TurnOnOff(this Entity entity, bool On)
    {
      if (entity.TryGetStateValue(out bool curStatus))
      {
        entity.CallService(On ? "turn_on" : "turn_off");
        //await PVCC_EntityManager.SetStateAsync(entity.EntityId, On ? "ON": "OFF");
        System.Threading.Thread.Sleep(100);
        entity.TryGetStateValue(out curStatus);
        return curStatus == On;
      }
      else
        return false;
    }
    public static bool TurnOn(this Entity entity)
    {
      return TurnOnOff(entity, true);
    }
    public static bool TurnOff(this Entity entity)
    {
      return TurnOnOff(entity, false);
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
    public static Dictionary<DateTime, int> GetRunningSumsDaily(this Dictionary<DateTime, int> list)
    {
      return list.Aggregate(new Dictionary<DateTime, int>(), (acc, x) =>
      {
        if (acc.Count == 0 || x.Key.Date != acc.Last().Key.Date)
          acc.Add(x.Key, x.Value);
        else
          acc.Add(x.Key, acc.Last().Value + x.Value);
        return acc;
      });
    }
    public static int GetSum(this Dictionary<DateTime, int> list, DateTime start = default, DateTime end = default)
    {
      if (start == default) start = DateTime.MinValue;
      if (end == default) end = DateTime.MaxValue;
      return list.Where(t => t.Key >= start && t.Key <= end).Sum(s => s.Value);
    }
    public static int GetAverage(this Dictionary<DateTime, int> list, DateTime start = default, DateTime end = default)
    {
      if (start == default) start = DateTime.MinValue;
      if (end == default) end = DateTime.MaxValue;
      return (int)Math.Round(list.Where(t => t.Key >= start && t.Key <= end).Average(s => s.Value), 0);
    }
    public static List<PriceTableEntry> GetLocalMaxima(this List<PriceTableEntry> list, DateTime start = default, DateTime end = default)
    {
      if (start == default) start = DateTime.MinValue;
      if (end == default) end = DateTime.MaxValue;
      List<PriceTableEntry> maxima = [];
      List<PriceTableEntry> actList = list.Where(t => t.StartTime >= start && t.EndTime <= end).OrderBy(t => t.StartTime).ToList();
      if (actList.Count > 2)
      {
        for (int i = 1; i < actList.Count - 1; i++)
        {
          if (actList[i].Price > actList[i - 1].Price && actList[i].Price > actList[i + 1].Price)
          {
            maxima.Add(actList[i]);
          }
        }
      }
      return maxima;
    }
    public static DateTime RoundToNearestQuarterHour(this DateTime time)
    {
      int minutes = time.Minute;
      int remainder = minutes % 15;
      return new DateTime(time.Year, time.Month, time.Day, time.Hour, minutes - remainder, 0);
    }
    public static void ClearAndCreateEmptyPredictionData(this Dictionary<DateTime, int> data)
    {
      data.Clear();
      for (var time = DateTime.Now.Date; time < DateTime.Now.AddDays(2).Date; time = time.AddMinutes(15))
      {
        data.Add(time, 0);
      }
    }
  }
  public class FixedSizeQueue<T>(int capacity) : Queue<T> where T : struct
  {
    private readonly int _capacity = capacity;

    public new void Enqueue(T item)
    {
      if (Count >= _capacity)
      {
        Dequeue();
      }
      base.Enqueue(item);
    }
  }
  public class RunningIntAverage(TimeSpan window)
  {
    private Queue<(DateTime timestamp, int value)> _Values = [];
    private TimeSpan _Window = window;

    public void AddValue(int value)
    {
      DateTime now = DateTime.UtcNow;
      _Values.Enqueue((now, value));

      while (_Values.Count > 0 && now - _Values.Peek().timestamp > _Window)
      {
        _Values.Dequeue();
      }
    }
    public int Count
    {
      get { return _Values.Count; }
    }
    public int GetAverage()
    {
      if (_Values.Count == 0)
        return int.MinValue;
      return (int)Math.Round(_Values.Average(v => v.value), 0);
    }

    public void Reset()
    {
      _Values = [];
      AddValue(0);
    }
  }
}
