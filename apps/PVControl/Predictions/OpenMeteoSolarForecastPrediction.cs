using NetDaemon.HassModel.Entities;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using static NetDeamon.apps.PVControl.PVControlCommon;

namespace NetDeamon.apps.PVControl.Predictions
{
  public class OpenMeteoSolarForecastPrediction : Prediction
  {
    private readonly List<Entity> _todayForecastEntities;
    private readonly List<Entity> _tomorrowForeCastEntities;
    public OpenMeteoSolarForecastPrediction(List<Entity> todayForecastEntities, List<Entity> tomorrowForeCastEntities)
    {
      _todayForecastEntities = todayForecastEntities;
      _tomorrowForeCastEntities = tomorrowForeCastEntities;
      Initialize("PV Forecast");
    }
    protected override Dictionary<DateTime, int> PopulateData()
    {
      // Start with a full 192-slot zero template so the result always covers the required window,
      // even when HA entities haven't refreshed yet for the new day (e.g. right after midnight).
      // Entity data is overlaid on top; any out-of-window entries are stripped so the count stays at 192.
      Dictionary<DateTime, int> completeForecast = [];
      completeForecast.ClearAndCreateEmptyPredictionData();
      completeForecast = completeForecast.CombineForecastLists(GetPVForecastFromEntities(_todayForecastEntities));
      completeForecast = completeForecast.CombineForecastLists(GetPVForecastFromEntities(_tomorrowForeCastEntities));
      var windowStart = DateTime.Now.Date;
      var windowEnd = DateTime.Now.AddDays(2).Date;
      return completeForecast.Where(kv => kv.Key >= windowStart && kv.Key < windowEnd).ToDictionary();
    }
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

        if (result != null)
        {
          float interval = 0.25f;
          for (int i = 0; i < result.Count; i++)
          {
            var r = result.ElementAt(i).Key;
            if (result[r] == 0)
              continue;
            if (i < result.Count - 1)
            {
              var r_next = result.ElementAt(i + 1).Key;
              interval = (float)(r_next - r).TotalHours;
            }
            result[r] = (int)(result[r] * interval);
          }
          return result;
        }
      }
      catch { }
      return [];
    }
  }
}
