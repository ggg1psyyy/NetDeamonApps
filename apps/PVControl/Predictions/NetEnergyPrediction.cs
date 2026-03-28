using System;
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
    private readonly RunningIntAverage _CurrentPVLong;
    private readonly bool _AdjustToRunningAverage;

    public NetEnergyPrediction(Prediction solarForecast, Prediction loadPrediction,
      RunningIntAverage currentLoad, RunningIntAverage currentPV, RunningIntAverage currentPVLong,
      bool adjustToRunningAverage = true)
    {
      _SolarForecast = solarForecast;
      _LoadPrediction = loadPrediction;
      _CurrentLoad = currentLoad;
      _CurrentPV = currentPV;
      _CurrentPVLong = currentPVLong;
      _AdjustToRunningAverage = adjustToRunningAverage;
      if (currentLoad is null || currentPV is null || currentPVLong is null)
        _AdjustToRunningAverage = false;
      Initialize("NetEnergy Prediction");
    }

    protected override Dictionary<DateTime, int> PopulateData()
    {
      Dictionary<DateTime, int> result = [];
      var now = DateTime.Now;
      var correctedPV = _AdjustToRunningAverage
        ? WithRunningAvgCorrection(_SolarForecast.TodayAndTomorrow, _CurrentPV.GetAverage(), _CurrentPVLong.GetAverage(), now)
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
    /// PV overload: applies a day-scale ratio (derived from the 45-min long average vs the current
    /// slot forecast) to all remaining today slots, then ramps the near-term 4 slots from the
    /// 5-min short average down to the day-scaled values.  Tomorrow is untouched.
    /// Day-scale is skipped when the current slot forecast is below 50 Wh (night/dawn/dusk).
    /// </summary>
    public static Dictionary<DateTime, int> WithRunningAvgCorrection(
      Dictionary<DateTime, int> raw, int shortAvgW, int longAvgW, DateTime now)
    {
      var result = new Dictionary<DateTime, int>(raw);
      var currentSlot = now.RoundToNearestQuarterHour();
      var endOfToday  = now.Date.AddDays(1);

      // Compute day-scale ratio from long average; skip at night
      double ratio = 1.0;
      if (raw.TryGetValue(currentSlot, out int currentForecast) && currentForecast >= 50)
      {
        int longAvgPerSlot = longAvgW / 4;
        ratio = Math.Clamp((double)longAvgPerSlot / currentForecast, 0.0, 2.0);
      }

      // Apply day-scale to all remaining today slots
      foreach (var key in result.Keys.OrderBy(k => k).ToList())
      {
        if (key < now) continue;
        if (key >= endOfToday) break;
        result[key] = (int)(result[key] * ratio);
      }

      // Ramp the first 4 slots from the 5-min average down to the already-scaled values
      int shortAvgPerSlot = shortAvgW / 4;
      int slotsToCorrect  = 4;
      int remaining       = slotsToCorrect;
      foreach (var key in result.Keys.OrderBy(k => k).ToList())
      {
        if (remaining <= 0) break;
        if (key < now) continue;
        int scaled = result[key]; // already day-scaled above
        result[key] = scaled + (shortAvgPerSlot - scaled) * remaining / slotsToCorrect;
        remaining--;
      }

      return result;
    }

    /// <summary>
    /// Load overload: ramps the next 4 slots from the 5-min running average down to the raw
    /// prediction.  No day-scale applied (load follows historical patterns, not weather).
    /// </summary>
    public static Dictionary<DateTime, int> WithRunningAvgCorrection(
      Dictionary<DateTime, int> raw, int avgW, DateTime now, int slotsToCorrect = 4)
    {
      var result      = new Dictionary<DateTime, int>(raw);
      int avgPerSlot  = avgW / 4;
      int remaining   = slotsToCorrect;
      foreach (var key in result.Keys.OrderBy(k => k).ToList())
      {
        if (remaining <= 0) break;
        if (key < now) continue;
        int predicted = result[key];
        result[key] = predicted + (avgPerSlot - predicted) * remaining / slotsToCorrect;
        remaining--;
      }
      return result;
    }
  }
}
