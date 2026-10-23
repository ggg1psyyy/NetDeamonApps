using System;
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
  /// <summary>Price of a time range.</summary>
  /// <param name="startTime">Start (inclusive).</param>
  /// <param name="endTime">End (exclusive).</param>
  /// <param name="price">Price in €/kWh.</param>
  public struct PriceTableEntry(DateTime startTime, DateTime endTime, float price)
  {
    [JsonPropertyName("start_time")]
    public DateTime StartTime { get; set; } = startTime;
    [JsonPropertyName("end_time")]
    public DateTime EndTime { get; set; } = endTime;
    [JsonPropertyName("price_per_kwh")]
    public float Price { get; set; } = price;

    /// <summary>Whether a time lies in this entry.</summary>
    /// <param name="t">Time to test.</param>
    /// <returns>True if StartTime ≤ <paramref name="t"/> &lt; EndTime.</returns>
    public readonly bool Covers(DateTime t) => TimeRange.Contains(StartTime, EndTime, t);
  }
  public enum InverterModes
  {
    automatic,
    normal,
    force_charge,
    force_charge_grid_only,
    grid_only,
    force_discharge,
    feedin_priority,
    reset,
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
    NextHourCheaper,
    BugFixMode,
    UserMode,
  }

  public enum PriceResolution
  {
    QuarterHourly,
    Hourly,
  }

  public enum PVPeriods
  {
    BeforePV,
    InPVPeriod,
    AfterPV,
  }
  // Optimal/Priority/PriorityPlus form a numbered ladder, climbed from the bottom in
  // HouseEnergy.FindLoadWindow: each mode's ruleset runs in full, and if it doesn't allow
  // charging, the next-higher-numbered ruleset gets a turn. The gaps between 10/20/30 are
  // deliberate room for a future mode to slot in between without renumbering anything else.
  // Off/Emergency are unrelated, independent gates (handled before the ladder even starts),
  // so their own values don't need to fit the numbering scheme.
  public enum LoadSchedulingMode
  {
    Off = 0,
    Emergency = 1,
    Optimal = 10,
    Priority = 20,
    PriorityPlus = 30,
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
    /// <summary>Reads an entity's state history from the HA REST API (history/period).</summary>
    /// <param name="apiManager">HA API.</param>
    /// <param name="entity">Entity to read.</param>
    /// <param name="startDateTime">Start of the period.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <param name="getMinimal">Minimal response: only state and last_changed after the first entry.</param>
    /// <param name="getAttributes">Include the attributes.</param>
    /// <param name="endDateTime">End of the period; null = now.</param>
    /// <returns>(true, entries in time order), or (false, empty) on error or no data.</returns>
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
    /// <summary>Formats a time as ISO 8601 with offset (yyyy-MM-ddTHH:mm:ss±zz:zz).</summary>
    /// <param name="date">Time to format.</param>
    /// <returns>The formatted string.</returns>
    public static string ToISO8601(this DateTime date) =>
      date.ToString("yyyy'-'MM'-'dd'T'HH':'mm':'sszzz", CultureInfo.InvariantCulture);

    /// <summary>Reads an ISO 8601 time with offset (e.g. "2026-06-27T12:59:59+02:00", DB and HA) as local time.</summary>
    /// <param name="text">The time text.</param>
    /// <returns>Local time (not UTC, as SqliteDataReader.GetDateTime returns for offsets).</returns>
    public static DateTime FromISO8601(string text) => DateTimeOffset.Parse(text, CultureInfo.InvariantCulture).LocalDateTime;
    /// <summary>Factor from an entity's unit_of_measurement to the base unit (W, Wh, €).</summary>
    /// <param name="entity">Entity with the unit attribute.</param>
    /// <returns>The factor; 1 without a unit.</returns>
    public static float GetUnitMultiplicator(this Entity entity)
    {
      if (!entity.TryGetJsonAttribute("unit_of_measurement", out JsonElement unitAttr))
        return 1f;
      return GetUnitMultiplicator(unitAttr.ToString());
    }
    /// <summary>Factor from a unit to its base unit: k/M/m before W/Wh, ct → €.</summary>
    /// <param name="unit">Unit string, e.g. "kWh", "ct/kWh".</param>
    /// <returns>The factor; 1 for base or unknown units.</returns>
    public static float GetUnitMultiplicator(string? unit)
    {
      if (unit is not null && unit.Length > 1)
      {
        string lowerUnit = unit.ToLower();
        if (lowerUnit.StartsWith("ct"))
          return 0.01f;  // ct → € (base unit is €)
        if (lowerUnit.StartsWith('€') || lowerUnit.StartsWith("eur"))
          return 1f;
        // Only apply SI prefix scaling to the known power/energy base units (W, Wh) — HA has
        // plenty of legitimate units that merely start with k/M/m without being SI-prefixed
        // (e.g. "min", "mbar", "Mbit/s"), so match the prefix + exact base unit, not just the
        // first letter. SI prefixes are case-sensitive: k = kilo, M = mega, m = milli.
        string baseUnit = unit[1..];
        if (baseUnit is "W" or "Wh")
        {
          if (unit[0] == 'k') return 1000f;
          if (unit[0] == 'M') return 1000000f;
          if (unit[0] == 'm') return 0.001f;
        }
      }
      return 1f;
    }
    /// <summary>Reads an entity's state as a typed value.</summary>
    /// <typeparam name="T">float, int, string, DateTime, bool ("on"), <see cref="InverterModes"/> or <see cref="LoadSchedulingMode"/>.</typeparam>
    /// <param name="entity">Entity to read.</param>
    /// <param name="resultValue">The value; default on failure.</param>
    /// <param name="numericalGetBaseValue">Numbers: convert to the base unit (<see cref="GetUnitMultiplicator(Entity)"/>).</param>
    /// <returns>False if the entity is missing, unavailable/unknown, or the state doesn't parse.</returns>
    public static bool TryGetStateValue<T>(this Entity entity, out T resultValue, bool numericalGetBaseValue = true)
    {
      resultValue = default!;
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
      else if (typeof(T) == typeof(LoadSchedulingMode))
      {
        dynamic result = LoadSchedulingMode.Off;
        if (Enum.TryParse(entity.State, out LoadSchedulingMode modeselect))
          result = modeselect;
        else
          return false;
        resultValue = (T)result;
        return true;
      }
      else
        return false;
    }
    /// <summary>Reads an attribute as raw JSON.</summary>
    /// <param name="entity">Entity to read.</param>
    /// <param name="attributeName">Attribute name.</param>
    /// <param name="attribute">The attribute; default on failure.</param>
    /// <returns>False if the entity is missing or unavailable, or has no such attribute.</returns>
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
    /// <summary>Reads an attribute as a string.</summary>
    /// <param name="entity">Entity to read.</param>
    /// <param name="attributeName">Attribute name.</param>
    /// <param name="attribute">The attribute's string value; empty on failure.</param>
    /// <returns>False if the entity is missing or has no such attribute.</returns>
    public static bool TryGetAttribute(this Entity entity, string attributeName, out string attribute)
    {
      attribute = string.Empty;
      if (entity is null)
        return false;
      else if (entity.EntityState is null || entity.EntityState.Attributes is null)
      {
        return false;
      }

      if (entity.Attributes!.ContainsKey(attributeName))
      {
        attribute = entity.Attributes[attributeName]?.ToString() ?? string.Empty;
        return attribute != null;
      }
      return false;
    }
    /// <summary>Reads a cover's current_position.</summary>
    /// <param name="entity">Cover entity.</param>
    /// <param name="position">Position 0…100; 0 on failure.</param>
    /// <returns>False if there is no readable position.</returns>
    public static bool TryGetCoverPosition(this Entity entity, out int position)
    {
      position = 0;
      if (entity is null)
        return false;
      else if (entity.EntityState is null || entity.EntityState.Attributes is null)
      {
        return false;
      }

      if (entity.Attributes!.ContainsKey("current_position"))
      {
        return int.TryParse(entity?.Attributes?["current_position"]?.ToString(), out position);
      }
      return false;
    }
    /// <summary>Calls cover.set_cover_position.</summary>
    /// <param name="entity">Cover entity.</param>
    /// <param name="position">Target position 0…100.</param>
    /// <returns>False if the entity is not a cover.</returns>
    public static bool SetCoverPosition(this Entity entity, int position)
    {
      if (entity is null || entity.GetEntityPlatform().ToLowerInvariant() != "cover")
        return false;
      entity.HaContext.CallService("cover", "set_cover_position", data: new { entity_id = entity.EntityId , position = position });
      return true;
    }
    /// <summary>Domain of an entity id ("light" for light.kitchen).</summary>
    /// <param name="entity">Entity.</param>
    /// <returns>The domain; empty without an id.</returns>
    public static string GetEntityPlatform(this Entity entity)
    {
      if (entity is null || string.IsNullOrWhiteSpace(entity.EntityId))
        return string.Empty;
      return entity.EntityId.Split('.').FirstOrDefault() ?? string.Empty;
    }
    /// <summary>Switches a light to the opposite state.</summary>
    /// <param name="entity">Light entity.</param>
    /// <returns>True if the light has the new state afterwards.</returns>
    public static bool ToggleLight(this Entity entity)
    {
      if (entity is null || entity.GetEntityPlatform().ToLowerInvariant() != "light")
        return false;
      entity.TryGetStateValue(out bool curStatus);
      return entity.TurnOnOff(!curStatus);
    }
    /// <summary>Calls turn_on/turn_off and checks the state 100 ms later.</summary>
    /// <param name="entity">Switchable entity.</param>
    /// <param name="On">True = turn on.</param>
    /// <returns>True if the entity has the requested state; false if its state can't be read.</returns>
    public static bool TurnOnOff(this Entity entity, bool On)
    {
      if (entity.TryGetStateValue(out bool curStatus))
      {
        entity.CallService(On ? "turn_on" : "turn_off");
        System.Threading.Thread.Sleep(100);
        entity.TryGetStateValue(out curStatus);
        return curStatus == On;
      }
      else
        return false;
    }
    /// <summary>Turns an entity on (<see cref="TurnOnOff"/>).</summary>
    /// <param name="entity">Switchable entity.</param>
    /// <returns>True if it is on afterwards.</returns>
    public static bool TurnOn(this Entity entity)
    {
      return TurnOnOff(entity, true);
    }
    /// <summary>Turns an entity off (<see cref="TurnOnOff"/>).</summary>
    /// <param name="entity">Switchable entity.</param>
    /// <returns>True if it is off afterwards.</returns>
    public static bool TurnOff(this Entity entity)
    {
      return TurnOnOff(entity, false);
    }
    /// <summary>The entity's icon attribute, else a default for its domain (cover, light on/off).</summary>
    /// <param name="entity">Entity.</param>
    /// <param name="icon">mdi icon name; "mdi:crosshairs-question" if none applies.</param>
    /// <returns>False if the entity or its attributes are missing.</returns>
    public static bool TryGetIconString(this Entity entity, out string icon)
    {
      icon = "mdi:crosshairs-question";
      if (entity is null)
        return false;
      else if (entity.EntityState is null || entity.EntityState.Attributes is null)
      {
        return false;
      }

      if (entity.TryGetAttribute("icon", out string iconString))
        icon = iconString;
      else
      {
        if (!entity.TryGetStateValue(out string state))
          state = string.Empty;
        
        icon = entity.GetEntityPlatform().ToLowerInvariant() switch
        {
          "cover" => "mdi:roller-shade",
          "light" => state == "on" ? "mdi:lightbulb-on" : "mdi:lightbulb-off",
          _ => icon
        };
      }
      return true;
    }
    /// <summary>Reads a light's brightness.</summary>
    /// <param name="entity">Light entity.</param>
    /// <param name="brightness">Brightness 0…255; −1 on failure.</param>
    /// <returns>False if not a light or no brightness attribute.</returns>
    public static bool TryGetBrightness(this Entity entity, out int brightness)
    {
      brightness = -1;
      if (entity is null || entity.GetEntityPlatform().ToLowerInvariant() != "light")
        return false;
      
      if (entity.Attributes!.ContainsKey("brightness"))
      {
        return int.TryParse(entity?.Attributes?["brightness"]?.ToString(), out brightness);
      }
      return false;
    }

    /// <summary>Turns a light on with a brightness.</summary>
    /// <param name="entity">Light entity.</param>
    /// <param name="brightness">Brightness 0…255.</param>
    /// <returns>The brightness read back; −1 on failure.</returns>
    public static int SetBrightness(this Entity entity, int brightness)
    {
      if (entity is null || entity.GetEntityPlatform().ToLowerInvariant() != "light")
        return -1;
      
      entity.HaContext.CallService("light", "turn_on", data: new { entity_id = entity.EntityId , brightness = brightness });
      if (entity.TryGetBrightness(out int newBrightness))
        return newBrightness;
      
      return -1;
    }
    /// <summary>Reads a light's supported_color_modes.</summary>
    /// <param name="entity">Light entity.</param>
    /// <param name="supportedColorModes">The modes; empty if none.</param>
    /// <returns>False if not a light.</returns>
    public static bool TryGetSupportedColorModes(this Entity entity, out List<string> supportedColorModes)
    {
      supportedColorModes = [];
      if (entity is null || entity.GetEntityPlatform().ToLowerInvariant() != "light")
        return false;
      if (entity.TryGetAttribute("supported_color_modes", out string modes))
      {
        supportedColorModes = modes.Split(' ').ToList();
      }
      return true;
    }
    /// <summary>Adds a second forecast to the first per time (e.g. two PV roof sides).</summary>
    /// <param name="list1">First forecast; modified in place.</param>
    /// <param name="list2">Forecast to add; missing times are added.</param>
    /// <returns>The sum, ordered by time.</returns>
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
    // The three helpers below run inside the simulator's per-slot loop (thousands of times per
    // planning cycle), so they're single passes without allocation. "First" = first in the
    // dictionary's enumeration order among entries with start <= key <= end (default = unbounded).
    /// <summary>Whether a time lies in [start, end]; default bounds are open.</summary>
    /// <param name="key">Time to test.</param>
    /// <param name="start">Start (inclusive); default = unbounded.</param>
    /// <param name="end">End (inclusive); default = unbounded.</param>
    private static bool InRange(DateTime key, DateTime start, DateTime end) =>
      (start == default || key >= start) && (end == default || key <= end);

    /// <summary>First entry with the lowest value in a time range.</summary>
    /// <param name="list">Values by time.</param>
    /// <param name="start">Range start (inclusive); default = unbounded.</param>
    /// <param name="end">Range end (inclusive); default = unbounded.</param>
    /// <returns>The entry; default if the range is empty.</returns>
    public static KeyValuePair<DateTime, int> FirstMinOrDefault(this Dictionary<DateTime, int> list, DateTime start = default, DateTime end = default) =>
      FirstExtremeOrDefault(list, -1, start, end);
    /// <summary>First entry with the highest value in a time range.</summary>
    /// <param name="list">Values by time.</param>
    /// <param name="start">Range start (inclusive); default = unbounded.</param>
    /// <param name="end">Range end (inclusive); default = unbounded.</param>
    /// <returns>The entry; default if the range is empty.</returns>
    public static KeyValuePair<DateTime, int> FirstMaxOrDefault(this Dictionary<DateTime, int> list, DateTime start = default, DateTime end = default) =>
      FirstExtremeOrDefault(list, 1, start, end);
    /// <summary>First entry with the lowest or highest value in a time range.</summary>
    /// <param name="list">Values by time.</param>
    /// <param name="sign">−1 = lowest, +1 = highest.</param>
    /// <param name="start">Range start (inclusive); default = unbounded.</param>
    /// <param name="end">Range end (inclusive); default = unbounded.</param>
    /// <returns>The entry; default if the range is empty.</returns>
    private static KeyValuePair<DateTime, int> FirstExtremeOrDefault(Dictionary<DateTime, int> list, int sign, DateTime start, DateTime end)
    {
      KeyValuePair<DateTime, int> best = default;
      bool found = false;
      foreach (var kv in list)
        if (InRange(kv.Key, start, end) && (!found || sign * kv.Value > sign * best.Value)) { best = kv; found = true; }
      return best;
    }
    /// <summary>First entry at or below a value in a time range.</summary>
    /// <param name="list">Values by time.</param>
    /// <param name="underValue">Threshold (inclusive).</param>
    /// <param name="start">Range start (inclusive); default = unbounded.</param>
    /// <param name="end">Range end (inclusive); default = unbounded.</param>
    /// <returns>The entry; default if there is none.</returns>
    public static KeyValuePair<DateTime, int> FirstUnderOrDefault(this Dictionary<DateTime, int> list, int underValue, DateTime start = default, DateTime end = default)
    {
      foreach (var kv in list)
        if (InRange(kv.Key, start, end) && kv.Value <= underValue) return kv;
      return default;
    }
    /// <summary>Entry of the quarter-hour a time lies in.</summary>
    /// <param name="list">Values by quarter-hour start.</param>
    /// <param name="time">Any time within the quarter.</param>
    /// <returns>The entry; (default, 0) if missing.</returns>
    public static KeyValuePair<DateTime, int> GetEntryAtTime(this Dictionary<DateTime, int> list, DateTime time)
    {
      if (list.TryGetValue(time.RoundToNearestQuarterHour(), out var entry))
        return new KeyValuePair<DateTime, int>(time.RoundToNearestQuarterHour(), entry);
      else
        return new KeyValuePair<DateTime, int>(default, 0);
    }
    /// <summary>Running sum per day (restarts at each new date).</summary>
    /// <param name="list">Values by time, in time order.</param>
    /// <returns>Cumulative values by time.</returns>
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
    /// <summary>Sum of the values in a time range.</summary>
    /// <param name="list">Values by time.</param>
    /// <param name="start">Range start (inclusive); default = unbounded.</param>
    /// <param name="end">Range end (inclusive); default = unbounded.</param>
    /// <returns>The sum.</returns>
    public static int GetSum(this Dictionary<DateTime, int> list, DateTime start = default, DateTime end = default) =>
      list.Where(t => InRange(t.Key, start, end)).Sum(s => s.Value);
    /// <summary>Mean of the values in a time range, rounded.</summary>
    /// <param name="list">Values by time.</param>
    /// <param name="start">Range start (inclusive); default = unbounded.</param>
    /// <param name="end">Range end (inclusive); default = unbounded.</param>
    /// <returns>The rounded mean; throws if the range is empty.</returns>
    public static int GetAverage(this Dictionary<DateTime, int> list, DateTime start = default, DateTime end = default) =>
      (int)Math.Round(list.Where(t => InRange(t.Key, start, end)).Average(s => s.Value), 0);
    /// <summary>Start of the quarter-hour a time lies in (floors, despite the name).</summary>
    /// <param name="time">Any time.</param>
    /// <returns>The quarter start; seconds dropped.</returns>
    public static DateTime RoundToNearestQuarterHour(this DateTime time) => time.Floor(TimeRange.SlotMinutes);

    /// <summary>A UTC time as local time; other kinds unchanged (DB timestamps).</summary>
    /// <param name="time">Time of any kind.</param>
    /// <returns>Local time.</returns>
    public static DateTime AsLocal(this DateTime time) => time.Kind == DateTimeKind.Utc ? time.ToLocalTime() : time;

    /// <summary>Next quarter-hour strictly after a time.</summary>
    /// <param name="time">Any time.</param>
    /// <returns>The next quarter start.</returns>
    public static DateTime GetNextQuarterHour(this DateTime time) => time.RoundToNearestQuarterHour() + TimeRange.Slot;
    /// <summary>Fills a prediction window with zeros: today 00:00 → tomorrow 23:45 in 15-min slots.</summary>
    /// <param name="data">Window to clear and fill.</param>
    /// <param name="now">Any time of today.</param>
    public static void ClearAndCreateEmptyPredictionData(this Dictionary<DateTime, int> data, DateTime now)
    {
      data.Clear();
      for (var time = now.Date; time < now.Date.AddDays(2); time = time.AddMinutes(15))
      {
        data.Add(time, 0);
      }
    }
    
    #region General Things
    /// <summary>Parses numbers and ranges ("3", "1-5") into a list of ints.</summary>
    /// <param name="list">Items to parse; unparsable ones are skipped.</param>
    /// <param name="numbers">The numbers, ranges expanded.</param>
    /// <returns>False if <paramref name="list"/> is null or empty.</returns>
    public static bool TryParseToIntList(this List<string> list, out List<int> numbers)
    {
      numbers = [];
      if (list != null && list.Count > 0)
      {
        foreach (var item in list)
        {
          if (item.Contains('-'))
          {
            // Parse range like "1-5"
            var parts = item.Split('-');
            if (parts.Length == 2 && 
                int.TryParse(parts[0], out int start) && 
                int.TryParse(parts[1], out int end))
            {
              for (int i = start; i <= end; i++)
              {
                numbers.Add(i);
              }
            }
          }
          else if (int.TryParse(item, out int value))
          {
            // Single value
            numbers.Add(value);
          }
        }

        return true;
      }
      return false;
    }

    /// <summary>HA entity-id part of a name: lower case, spaces as underscores.</summary>
    /// <param name="name">Display name, e.g. "EV Charger".</param>
    /// <returns>e.g. "ev_charger".</returns>
    public static string ToSlug(this string name) => name.ToLowerInvariant().Replace(' ', '_');

    /// <summary>Cuts a string to a maximum length.</summary>
    /// <param name="str">String to cut.</param>
    /// <param name="maxLength">Maximum length; ≤ 0 gives "".</param>
    /// <returns>The first <paramref name="maxLength"/> characters.</returns>
    public static string Truncate(this string str, int maxLength)
    {
      if (maxLength <= 0)
        return "";
      return str.Substring(0,  Math.Min(str.Length, maxLength));
    }
    #endregion
  }
  /// <summary>One entry per day for the last days, oldest first (price publications, scored nights).</summary>
  /// <typeparam name="T">Entry type.</typeparam>
  /// <param name="keep">Days kept; older entries are dropped.</param>
  /// <param name="dayOf">The day an entry belongs to.</param>
  public sealed class DayLog<T>(int keep, Func<T, DateTime> dayOf)
  {
    private readonly List<T> _items = [];

    /// <summary>The entries, oldest first.</summary>
    public IReadOnlyList<T> Items => _items;

    /// <summary>Replaces the entries (after a restart), keeping the last <paramref name="keep"/> days.</summary>
    /// <param name="items">Entries in any order.</param>
    public void Restore(IEnumerable<T> items)
    {
      _items.Clear();
      _items.AddRange(items.OrderBy(dayOf).TakeLast(keep));
    }

    /// <summary>Whether there is an entry for a day.</summary>
    /// <param name="day">The day (date).</param>
    public bool Has(DateTime day) => _items.Any(i => dayOf(i) == day);

    /// <summary>Adds an entry, dropping the oldest beyond <paramref name="keep"/>.</summary>
    /// <param name="item">The new entry (the newest day).</param>
    public void Add(T item)
    {
      _items.Add(item);
      if (_items.Count > keep) _items.RemoveAt(0);
    }

    /// <summary>Replaces an entry.</summary>
    /// <param name="index">Index in <see cref="Items"/>.</param>
    /// <param name="item">The new entry.</param>
    public void Set(int index, T item) => _items[index] = item;
  }

  /// <summary>Queue that drops its oldest item when full.</summary>
  /// <typeparam name="T">Item type.</typeparam>
  /// <param name="capacity">Maximum number of items.</param>
  public class FixedSizeQueue<T>(int capacity) : Queue<T> where T : struct
  {
    private readonly int _capacity = capacity;

    /// <summary>Adds an item, dropping the oldest one when full.</summary>
    /// <param name="item">Item to add.</param>
    public new void Enqueue(T item)
    {
      if (Count >= _capacity)
      {
        Dequeue();
      }
      base.Enqueue(item);
    }
  }
  /// <summary>
  /// An energy counter with coarse steps made continuous: the last counter value plus the power
  /// integrated since it last changed, at most one step.
  /// </summary>
  /// <param name="resolutionWh">Step of the counter in Wh (inverter's PV energy today: 100).</param>
  public sealed class CounterInterpolator(int resolutionWh)
  {
    private int? _counter;
    private double _sinceStepWh;
    private DateTime? _powerAt;
    private int _powerW;

    /// <summary>Adds a power reading.</summary>
    /// <param name="at">Time of the reading.</param>
    /// <param name="watts">Power in W; negative counts as 0.</param>
    public void AddPower(DateTime at, int watts)
    {
      Accumulate(at);
      _powerW = Math.Max(0, watts);
      _powerAt = at;
    }

    /// <summary>The counter made continuous.</summary>
    /// <param name="now">Current time.</param>
    /// <param name="counterWh">Counter as read now, in Wh.</param>
    /// <returns>Counter + energy since its last step (at most one step), in Wh.</returns>
    public int Value(DateTime now, int counterWh)
    {
      if (counterWh != _counter)
      {
        _counter = counterWh;
        _sinceStepWh = 0;
        if (_powerAt is not null) _powerAt = now;
      }
      Accumulate(now);
      return counterWh + (int)Math.Min(_sinceStepWh, resolutionWh);
    }

    /// <summary>Integrates the last power reading up to a time.</summary>
    /// <param name="at">Integrate until here.</param>
    private void Accumulate(DateTime at)
    {
      if (_powerAt is { } p && at > p)
      {
        _sinceStepWh += Energy.Wh(_powerW, at - p);
        _powerAt = at;
      }
    }
  }

  /// <summary>
  /// Average of the values of a time window. Values also expire on read; the latest one is always
  /// kept (sensors that report only on change still have their current state).
  /// </summary>
  /// <param name="window">Window length.</param>
  /// <param name="utcNow">Clock (UTC); null = system clock. For tests.</param>
  public class RunningIntAverage(TimeSpan window, Func<DateTime>? utcNow = null)
  {
    private Queue<(DateTime timestamp, int value)> _Values = [];
    private TimeSpan _Window = window;
    private readonly Func<DateTime> _utcNow = utcNow ?? (() => DateTime.UtcNow);

    /// <summary>Adds a value with the current time.</summary>
    /// <param name="value">Value to add.</param>
    public void AddValue(int value)
    {
      _Values.Enqueue((_utcNow(), value));
      Prune();
    }

    /// <summary>Drops values older than the window, keeping at least the latest one.</summary>
    private void Prune()
    {
      DateTime now = _utcNow();
      while (_Values.Count > 1 && now - _Values.Peek().timestamp > _Window)
        _Values.Dequeue();
    }
    /// <summary>Number of values in the window.</summary>
    public int Count
    {
      get { return _Values.Count; }
    }
    /// <summary>Average of the values in the window.</summary>
    /// <returns>Rounded average; int.MinValue without any value.</returns>
    public int GetAverage()
    {
      if (_Values.Count == 0)
        return int.MinValue;
      Prune();
      return (int)Math.Round(_Values.Average(v => v.value), 0);
    }

    /// <summary>Same as <see cref="GetAverage"/>.</summary>
    public int CurrentAverage
    {
      get => GetAverage();
    }
    /// <summary>Drops all values and starts again with 0.</summary>
    public void Reset()
    {
      _Values = [];
      AddValue(0);
    }
  }
}
