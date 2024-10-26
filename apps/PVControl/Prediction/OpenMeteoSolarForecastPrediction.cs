using NetDaemon.HassModel.Entities;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace PVControl
{
  public class OpenMeteoSolarForecastPrediction : Prediction
  {
    private readonly List<Entity> _todayForecastEntities;
    private readonly List<Entity> _tomorrowForeCastEntities;
    public OpenMeteoSolarForecastPrediction(List<Entity> todayForecastEntities, List<Entity> tomorrowForeCastEntities)
    {
      _todayForecastEntities = todayForecastEntities;
      _tomorrowForeCastEntities = tomorrowForeCastEntities;
      base.Initialize("PV Forecast");
    }
    protected override Dictionary<DateTime, int> PopulateData()
    {
      Dictionary<DateTime, int> completeForecast = [];
      completeForecast = completeForecast.CombineForecastLists(GetPVForecastFromEntities(_todayForecastEntities));
      completeForecast = completeForecast.CombineForecastLists(GetPVForecastFromEntities(_tomorrowForeCastEntities));
      return completeForecast;
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
