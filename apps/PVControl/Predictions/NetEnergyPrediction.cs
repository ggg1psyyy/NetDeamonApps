using System.Collections.Generic;
using System.Linq;
using static NetDeamon.apps.PVControl.PVControlCommon;

namespace NetDeamon.apps.PVControl.Predictions
{
  public class NetEnergyPrediction : Prediction
  {
    private readonly Prediction _SolarForecast;
    private readonly Prediction _LoadPrediction;
    private readonly RunningIntAverage _CurrentLoad;
    private readonly RunningIntAverage _CurrentPV;
    private readonly bool _AdjustToRunningAverage;

    public NetEnergyPrediction(Prediction solarForecast, Prediction loadPrediction, RunningIntAverage currentLoad, RunningIntAverage currentPV, bool adjustToRunningAverage = true)
    {
      _SolarForecast = solarForecast;
      _LoadPrediction = loadPrediction;
      _CurrentLoad = currentLoad;
      _CurrentPV = currentPV;
      _AdjustToRunningAverage = adjustToRunningAverage;
      if (currentLoad is null || currentPV is null)
        _AdjustToRunningAverage = false;
      Initialize("NetEnergy Prediction");
    }

    protected override Dictionary<DateTime, int> PopulateData()
    {
      Dictionary<DateTime, int> result = [];
      var now = DateTime.Now;
      int diffLoad = 0;
      int diffPV = 0;
      int quarterHourCount = 4;
      foreach (var item in _LoadPrediction.TodayAndTomorrow)
      {
        if (!_SolarForecast.TodayAndTomorrow.TryGetValue(item.Key, out int value))
        { 
          value = 0;
          PVCC_Logger.LogError("Could not find SolarForeCast for {date}", item.Key);
        }
        // adjust for actual values and revert to the original prediction over an hour
        int predictedLoad = item.Value;
        int predictedPV = value;
        if (item.Key >= now && _AdjustToRunningAverage && quarterHourCount > 0)
        {
          int avgLoad = _CurrentLoad.GetAverage() / 4;
          int avgPV = _CurrentPV.GetAverage() / 4;

          diffLoad = (avgLoad - predictedLoad) * 1/4 * quarterHourCount;
          diffPV = (avgPV - predictedPV) * 1/4 * quarterHourCount;

          quarterHourCount--;
          predictedLoad += diffLoad;
          predictedPV += diffPV;
        }
        result.Add(item.Key, predictedPV - predictedLoad);
      }
      return result.OrderBy(o => o.Key).ToDictionary();
    }
  }
}
