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
    private readonly Func<int>? _actualPVWh;

    /// <param name="actualPVWh">Today's PV energy so far (Wh) — the same smoothed value the
    /// simulation uses; without it the raw TodayPVEnergyEntity.</param>
    public NetEnergyPrediction(Prediction solarForecast, Prediction loadPrediction,
      RunningIntAverage currentLoad, RunningIntAverage currentPV,
      bool adjustToRunningAverage = true, Func<int>? actualPVWh = null)
    {
      _SolarForecast = solarForecast;
      _LoadPrediction = loadPrediction;
      _CurrentLoad = currentLoad;
      _CurrentPV = currentPV;
      _actualPVWh = actualPVWh;
      _AdjustToRunningAverage = adjustToRunningAverage;
      if (currentLoad is null || currentPV is null)
        _AdjustToRunningAverage = false;
      Initialize("NetEnergy Prediction");
    }

    protected override Dictionary<DateTime, int> PopulateData(DateTime now)
    {
      Dictionary<DateTime, int> result = [];

      int actualPVWh = 0;
      if (_actualPVWh is not null)
        actualPVWh = _actualPVWh();
      else if (PVCC_Config.TodayPVEnergyEntity.TryGetStateValue(out float pvWh))
        actualPVWh = (int)pvWh;

      var correctedPV = _AdjustToRunningAverage
        ? WithRunningAvgCorrection(_SolarForecast.TodayAndTomorrow, _CurrentPV.GetAverage(), actualPVWh, now)
        : _SolarForecast.TodayAndTomorrow;
      var correctedLoad = _AdjustToRunningAverage
        ? WithRunningAvgCorrection(_LoadPrediction.TodayAndTomorrow, _CurrentLoad.GetAverage(), now)
        : _LoadPrediction.TodayAndTomorrow;

      var missingPV = new List<DateTime>();
      foreach (var item in correctedLoad)
      {
        if (!correctedPV.TryGetValue(item.Key, out int pv))
        {
          pv = 0;
          missingPV.Add(item.Key);
        }
        result.Add(item.Key, pv - item.Value);
      }
      // One line instead of one per slot (2026-10-20 00:00: 96 lines when the load and PV windows
      // were built for different days).
      if (missingPV.Count > 0)
        PVCC_Logger.LogError("PV forecast missing for {Count} slots {First:dd.MM. HH:mm} → {Last:dd.MM. HH:mm}, counted as 0",
          missingPV.Count, missingPV.Min(), missingPV.Max());
      return result.OrderBy(o => o.Key).ToDictionary();
    }

    /// <summary>Forecast energy (Wh) in the last 15 min below which the near-term correction uses
    /// the absolute live average instead of the ratio live/forecast (dawn: the ratio is noise).
    /// 150 Wh ≈ 600 W forecast; picked by the 10-day backtest (50: morning error 130 Wh, 150 with
    /// the absolute fallback: 106, absolute only: 111).</summary>
    public const double NearTermRatioMinWh = 150;

    private static double OverlapFraction(DateTime slotStart, DateTime from, DateTime to) =>
      Math.Max(0, (Math.Min(to.Ticks, slotStart.AddMinutes(15).Ticks) - Math.Max(from.Ticks, slotStart.Ticks)) / (double)TimeSpan.FromMinutes(15).Ticks);

    /// <summary>
    /// PV overload: applies a day-scale ratio (actual PV energy today / sum of forecast slots
    /// from midnight to now) to all remaining today slots, then ramps the near-term 4 slots
    /// from the live 15-min average (as a ratio to the forecast, see below) down to the
    /// day-scaled values.  Tomorrow is untouched.
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
      // The simulation starts with the running quarter (EnergySimulator, RoundToNearestQuarterHour
      // floors) — from that slot on everything is "ahead", also when now is already into it.
      // Before, the running slot got the raw forecast and the live average went to the slot after.
      var start      = now.RoundToNearestQuarterHour();

      // Forecast from midnight to now — same window as the actual energy, the running slot pro
      // rata. Counting it in full as soon as it began (actual ≈ 0 for it yet) dropped the ratio at
      // every quarter boundary: 2026-10-22 14:15:15 0.90 → 0.85, −163 Wh PV in the simulation
      // (part of the jump that restarted a stopped grid charge, see SimulationInput.SlotFraction).
      int forecastSumWh = (int)raw
        .Where(kvp => kvp.Key >= today && kvp.Key < now)
        .Sum(kvp => kvp.Value * Math.Clamp((now - kvp.Key).TotalMinutes / 15.0, 0.0, 1.0));

      // Compute raw day-scale ratio; skip before meaningful generation has been forecast
      double ratio = 1.0;
      if (forecastSumWh >= 50)
        ratio = Math.Clamp((double)actualPVEnergyWh / forecastSumWh, 0.0, 2.0);

      // Confidence: count how many slots before now had meaningful forecast (>= 100 Wh = ~400 W).
      // (A just-started slot counts — confidence is full after ~1.5 h of sun, so that's irrelevant.)
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
        if (key < start) continue;
        if (key >= endOfToday) break;
        result[key] = (int)(result[key] * effectiveRatio);
      }

      // Near term (the first 4 slots, ramping to the day-scaled values): the live 15-min average
      // as a ratio to what the forecast expected in those same 15 min, applied to each slot's
      // forecast — the forecast keeps its slope. Ramping to the absolute live power instead lags
      // the steep October mornings/evenings (from full PV to 0 in ~2 h): over 10 days
      // (2026-10-12…22, minute by minute) the ratio halved the evening error of the next hour's
      // PV (154 → 77 Wh) and cut it overall by 15 % (198 → 168 Wh). Under NearTermRatioMinWh of
      // forecast in the window (dawn) the ratio is noise — then the absolute average as before.
      double windowForecastWh = raw
        .Where(kvp => kvp.Key < now && kvp.Key.AddMinutes(15) > now.AddMinutes(-15))
        .Sum(kvp => kvp.Value * OverlapFraction(kvp.Key, now.AddMinutes(-15), now));
      double shortWh = shortAvgW / 4.0; // the 15-min average = energy of the last 15 min
      bool useRatio = shortAvgW >= 0 && windowForecastWh >= NearTermRatioMinWh;
      double shortRatio = useRatio ? Math.Clamp(shortWh / windowForecastWh, 0.0, 3.0) : 0;

      // Absolute fallback: skip slots the forecast says have zero PV potential at all (e.g. after
      // sunset) — the live running average lingering above zero there (sensor noise, residual
      // self-consumption reporting, slow decay) is not real PV and must never be blended in;
      // doing so fabricates a false "net PV" slot that corrupts FirstRelevantPVEnergyToday/
      // Tomorrow (and CurrentPVPeriod) for everything downstream that relies on them. A skipped
      // slot still counts toward the 4 — it's already correct, there's nothing to ramp. (The ratio
      // keeps a zero forecast at zero by itself.)
      int shortAvgPerSlot = shortAvgW / 4;
      int slotsToCorrect  = 4;
      int remaining       = slotsToCorrect;
      foreach (var key in result.Keys.OrderBy(k => k).ToList())
      {
        if (remaining <= 0) break;
        if (key < start) continue;
        int scaled = result[key];
        if (useRatio)
          result[key] = (int)(raw[key] * (effectiveRatio + (shortRatio - effectiveRatio) * remaining / slotsToCorrect));
        else if (scaled > 0)
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
      var start       = now.RoundToNearestQuarterHour(); // same first slot as the PV overload
      foreach (var key in result.Keys.OrderBy(k => k).ToList())
      {
        if (remaining <= 0) break;
        if (key < start) continue;
        int predicted = result[key];
        result[key] = predicted + (avgPerSlot - predicted) * remaining / slotsToCorrect;
        remaining--;
      }
      return result;
    }
  }
}
