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
      var correctedPV   = _AdjustToRunningAverage
        ? WithRunningAvgCorrection(_SolarForecast.TodayAndTomorrow, _CurrentPV.GetAverage(), now)
        : _SolarForecast.TodayAndTomorrow;
      var correctedLoad = _AdjustToRunningAverage
        ? WithRunningAvgCorrection(_LoadPrediction.TodayAndTomorrow, _CurrentLoad.GetAverage(), now)
        : _LoadPrediction.TodayAndTomorrow;

      foreach (var item in correctedLoad)
      {
        if (!correctedPV.TryGetValue(item.Key, out int pv))
        {
          pv = 0;
          PVCC_Logger.LogError("Could not find SolarForeCast for {date}", item.Key);
        }
        result.Add(item.Key, pv - item.Value);
      }
      return result.OrderBy(o => o.Key).ToDictionary();
    }

    /// <summary>
    /// Returns a copy of <paramref name="raw"/> with the next <paramref name="slotsToCorrect"/>
    /// future slots blended toward <paramref name="runningAvgW"/> (in watts).
    /// Slot 0 gets a full correction, slot 1 gets 75%, …, slot N-1 gets 1/N — then raw prediction resumes.
    /// </summary>
    public static Dictionary<DateTime, int> WithRunningAvgCorrection(
      Dictionary<DateTime, int> raw, int runningAvgW, DateTime now, int slotsToCorrect = 4)
    {
      var result = new Dictionary<DateTime, int>(raw);
      int avgPerSlot = runningAvgW / 4;  // W → Wh per 15-min slot
      int remaining = slotsToCorrect;
      foreach (var key in result.Keys.OrderBy(k => k).ToList())
      {
        if (key < now || remaining <= 0) break;
        int predicted = result[key];
        result[key] = predicted + (avgPerSlot - predicted) * remaining / slotsToCorrect;
        remaining--;
      }
      return result;
    }
  }
}
