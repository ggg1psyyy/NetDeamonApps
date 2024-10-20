using System.Collections.Generic;
using System.Linq;

namespace PVControl
{
  public class NetEnergyPrediction : Prediction
  {
    private readonly Prediction _SolarForecast;
    private readonly Prediction _LoadPrediction;

    public NetEnergyPrediction(Prediction solarForecast, Prediction loadPrediction)
    {
      _SolarForecast = solarForecast;
      _LoadPrediction = loadPrediction;
      base.Initialize("NetEnergy Prediction");
    }

    protected override Dictionary<DateTime, int> PopulateData()
    {
      Dictionary<DateTime, int> result = [];
      foreach (var item in _LoadPrediction.TodayAndTomorrow)
      {
        if (_SolarForecast.TodayAndTomorrow.TryGetValue(item.Key, out int value))
          result.Add(item.Key, value - item.Value);
        else
          throw new ArgumentException("one of the predictions is not valid");
      }
      return result.OrderBy(o => o.Key).ToDictionary();
    }
  }
}
