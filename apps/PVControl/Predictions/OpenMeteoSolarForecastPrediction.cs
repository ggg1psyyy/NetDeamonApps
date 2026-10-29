using NetDaemon.HassModel.Entities;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using static NetDeamon.apps.PVControl.PVControlCommon;

namespace NetDeamon.apps.PVControl.Predictions
{
  /// <summary>PV forecast per slot, Wh: the sum of the Open-Meteo Solar Forecast entities (e.g. two roof sides).</summary>
  public class OpenMeteoSolarForecastPrediction : Prediction
  {
    private readonly List<Entity> _todayForecastEntities;
    private readonly List<Entity> _tomorrowForeCastEntities;
    /// <summary>Creates and fills the forecast.</summary>
    /// <param name="todayForecastEntities">Forecast entities for today.</param>
    /// <param name="tomorrowForeCastEntities">Forecast entities for tomorrow.</param>
    public OpenMeteoSolarForecastPrediction(List<Entity> todayForecastEntities, List<Entity> tomorrowForeCastEntities)
    {
      _todayForecastEntities = todayForecastEntities;
      _tomorrowForeCastEntities = tomorrowForeCastEntities;
      Initialize("PV Forecast");
    }
    /// <summary>The entities' forecasts summed into the day's window; missing slots are 0.</summary>
    /// <param name="now">Day of the window.</param>
    /// <returns>Wh by slot start.</returns>
    protected override Dictionary<DateTime, int> PopulateData(DateTime now)
    {
      // Start with a full 192-slot zero template so the result always covers the required window,
      // even when HA entities haven't refreshed yet for the new day (e.g. right after midnight).
      // Entity data is overlaid on top; any out-of-window entries are stripped so the count stays at 192.
      Dictionary<DateTime, int> completeForecast = [];
      completeForecast.ClearAndCreateEmptyPredictionData(now);
      completeForecast = completeForecast.CombineForecastLists(GetPVForecastFromEntities(_todayForecastEntities));
      completeForecast = completeForecast.CombineForecastLists(GetPVForecastFromEntities(_tomorrowForeCastEntities));
      var windowStart = now.Date;
      var windowEnd = now.Date.AddDays(2);
      return completeForecast.Where(kv => kv.Key >= windowStart && kv.Key < windowEnd).ToDictionary();
    }
    /// <summary>Sum of several entities' forecasts.</summary>
    /// <param name="entities">Forecast entities; null = none.</param>
    /// <returns>Wh by time.</returns>
    private static Dictionary<DateTime, int> GetPVForecastFromEntities(List<Entity>? entities)
    {
      Dictionary<DateTime, int> completeForecast = [];
      if (entities != null && entities.Count > 0)
      {
        foreach (Entity entity in entities)
        {
          completeForecast = completeForecast.CombineForecastLists(GetForecastDetailsFromPower(entity));
        }
      }
      return completeForecast;
    }
    /// <summary>Energy per interval from an entity's "watts" attribute (power × time until the next value).</summary>
    /// <param name="entity">Forecast entity.</param>
    /// <returns>Wh by time; empty on error.</returns>
    private static Dictionary<DateTime, int> GetForecastDetailsFromPower(Entity entity)
    {
      try
      {
        var resultDTO = entity.EntityState?.AttributesJson?.GetProperty("watts").Deserialize<Dictionary<DateTimeOffset, int>>()?.OrderBy(t => t.Key).ToDictionary();
        Dictionary<DateTime, int> result = [];
        if (resultDTO is null)
        {
          PVCC_Logger.LogError("Could not get PV forecast values");
          return [];
        }
        foreach (var item in resultDTO)
          result.Add(item.Key.DateTime, item.Value);

        // Power at each timestamp holds until the next one (the last: the interval before).
        var interval = TimeRange.Slot;
        for (int i = 0; i < result.Count; i++)
        {
          var r = result.ElementAt(i).Key;
          if (result[r] == 0)
            continue;
          if (i < result.Count - 1)
            interval = result.ElementAt(i + 1).Key - r;
          result[r] = (int)Energy.Wh(result[r], interval);
        }
        return result;
      }
      catch (Exception ex)
      {
        PVCC_Logger.LogError(ex, "Could not read the PV forecast of {Entity}", entity.EntityId);
      }
      return [];
    }
  }
}
