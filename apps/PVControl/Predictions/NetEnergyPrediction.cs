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
    private readonly bool _AdjustToRunningAverage;

    public NetEnergyPrediction(Prediction solarForecast, Prediction loadPrediction,
      RunningIntAverage currentLoad, RunningIntAverage currentPV,
      bool adjustToRunningAverage = true)
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

      int actualPVWh = 0;
      if (PVCC_Config.TodayPVEnergyEntity.TryGetStateValue(out float pvWh))
        actualPVWh = (int)pvWh;

      var correctedPV = _AdjustToRunningAverage
        ? WithRunningAvgCorrection(_SolarForecast.TodayAndTomorrow, _CurrentPV.GetAverage(), actualPVWh, now)
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
    /// PV overload: applies a day-scale ratio (actual PV energy today / sum of forecast slots
    /// from midnight to now) to all remaining today slots, then ramps the near-term 4 slots
    /// from the 5-min short average down to the day-scaled values.  Tomorrow is untouched.
    /// The ratio is blended with 1.0 (= no correction) based on how many forecast-positive
    /// slots have been observed today (confidence 0→1 over the first 6 solar slots ≈ 1.5 h
    /// after sunrise).  This prevents early-morning noise from scaling the whole day.
    /// </summary>
    public static Dictionary<DateTime, int> WithRunningAvgCorrection(
      Dictionary<DateTime, int> raw, int shortAvgW, int actualPVEnergyWh, DateTime now)
    {
      var result     = new Dictionary<DateTime, int>(raw);
      var today      = now.Date;
      var endOfToday = today.AddDays(1);

      // Sum forecast slots from midnight up to (not including) now — same window as actual energy
      int forecastSumWh = raw
        .Where(kvp => kvp.Key >= today && kvp.Key < now)
        .Sum(kvp => kvp.Value);

      // Compute raw day-scale ratio; skip before meaningful generation has been forecast
      double ratio = 1.0;
      if (forecastSumWh >= 50)
        ratio = Math.Clamp((double)actualPVEnergyWh / forecastSumWh, 0.0, 2.0);

      // Confidence: count how many slots before now had meaningful forecast (>= 100 Wh = ~400 W).
      // 100 Wh/slot filters out the low-irradiance dawn/dusk fringe where the ratio is unreliable.
      // Self-seasonal: threshold is met earlier in summer, later in winter/spring.
      // Full confidence is reached after 6 such slots (≈ 1.5 h of meaningful solar activity).
      int solarSlotCount = raw.Count(kvp => kvp.Key >= today && kvp.Key < now && kvp.Value >= 100);
      double confidence  = Math.Clamp(solarSlotCount / 6.0, 0.0, 1.0);

      // Blend: near sunrise use raw forecast (ratio→1.0); full correction only after 6 solar slots.
      // Skip blend arithmetic when confidence is already 1.0 to avoid floating-point truncation drift.
      double effectiveRatio = confidence >= 1.0 ? ratio : 1.0 + (ratio - 1.0) * confidence;

      // Apply effective day-scale to all remaining today slots
      foreach (var key in result.Keys.OrderBy(k => k).ToList())
      {
        if (key < now) continue;
        if (key >= endOfToday) break;
        result[key] = (int)(result[key] * effectiveRatio);
      }

      // Ramp the first 4 slots from the 5-min average down to the already-scaled values
      int shortAvgPerSlot = shortAvgW / 4;
      int slotsToCorrect  = 4;
      int remaining       = slotsToCorrect;
      foreach (var key in result.Keys.OrderBy(k => k).ToList())
      {
        if (remaining <= 0) break;
        if (key < now) continue;
        int scaled = result[key];
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
