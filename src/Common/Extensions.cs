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
    /// <summary>Formats a time as local ISO 8601 with the offset valid at that instant (yyyy-MM-ddTHH:mm:ss±zz:zz).</summary>
    /// <param name="date">Time to format; UTC, or local (also the repeated hour when read from HA/DB; a naive time there counts as winter time).</param>
    /// <returns>The formatted string.</returns>
    public static string ToISO8601(this DateTime date) =>
      TimeZoneInfo.ConvertTime(new DateTimeOffset(date.ToUniversalTime()), TimeZoneInfo.Local)
        .ToString("yyyy'-'MM'-'dd'T'HH':'mm':'sszzz", CultureInfo.InvariantCulture);

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
    /// <typeparam name="T">float, int, string, DateTime, bool ("on") or an enum (by name).</typeparam>
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
      else if (typeof(T).IsEnum)
        return TryParseEnum(entity.State, out resultValue);
      else
        return false;
    }
    /// <summary>A state as a value of an enum, by its name (case-sensitive): <c>InverterModes</c>, <c>LoadSchedulingMode</c>, …</summary>
    /// <typeparam name="T">The enum.</typeparam>
    /// <param name="state">The state.</param>
    /// <param name="value">The value; default on failure.</param>
    /// <returns>False if the state isn't one of the names.</returns>
    public static bool TryParseEnum<T>(string state, out T value)
    {
      value = default!;
      if (!typeof(T).IsEnum || !Enum.TryParse(typeof(T), state, out object? parsed) || !Enum.IsDefined(typeof(T), parsed!))
        return false;
      value = (T)parsed!;
      return true;
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

}
