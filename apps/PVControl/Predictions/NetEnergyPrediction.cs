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
      float multLoad = 1f;
      float multPV = 1f;
      float scaling = 0.95f;
      float threshhold = 0.1f;
      bool needRecalc = true;
      foreach (var item in _LoadPrediction.TodayAndTomorrow)
      {
        if (!_SolarForecast.TodayAndTomorrow.TryGetValue(item.Key, out int value))
        { 
          value = 0;
          PVCC_Logger.LogError("Could not find SolarForeCast for {date}", item.Key);
        }
        // adjust for actual values and slowly revert to original prediction
        int predictedLoad = item.Value;
        int predictedPV = value;
        if (item.Key >= now && _AdjustToRunningAverage)
        {
          if (needRecalc)
          {
            float avgLoad = _CurrentLoad.GetAverage() / 4;
            float avgPV = _CurrentPV.GetAverage() / 4;

            multLoad = predictedLoad != 0 ? (float)avgLoad / predictedLoad : 1;
            multPV = predictedPV != 0 ? (float)avgPV / predictedPV : 1;
            needRecalc = false;
          }

          predictedLoad = (int)Math.Round(predictedLoad * multLoad, 0);
          predictedPV = (int)Math.Round(predictedPV * multPV, 0);
          if (multLoad != 1.0f)
            multLoad = multLoad > 1f ? multLoad * scaling : multLoad / scaling;
          if (multPV != 1.0f)
            multPV = multPV > 1f ? multPV * scaling : multPV / scaling;
          if (Math.Abs(multLoad - 1) < threshhold)
            multLoad = 1.0f;
          if (Math.Abs(multPV - 1) < threshhold)
            multPV = 1.0f;
        }
        result.Add(item.Key, predictedPV - predictedLoad);
      }
      return result.OrderBy(o => o.Key).ToDictionary();
    }
  }
}
