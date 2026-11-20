using System;
using System.Collections.Generic;
using NetDeamon.apps.PVControl.PvForecast;

namespace NetDeamon.apps.PVControl.Predictions
{
  /// <summary>
  /// The PV forecast the planners use: the raw one (Open-Meteo) with every quarter the steering forecast has replaced by
  /// it (<see cref="PvForecastMode.Steer"/>: the first usable source, corrected). Without a steering forecast (Shadow,
  /// before the first run, no usable source) the raw forecast as it is.
  /// </summary>
  public class SteeredPvPrediction : Prediction
  {
    private readonly Prediction _raw;
    private readonly Func<SteeringForecast?> _steering;

    /// <summary>Creates and fills the forecast.</summary>
    /// <param name="raw">The raw forecast (also the shadow's Open-Meteo source — never the steered one).</param>
    /// <param name="steering">The current steering forecast; null: none.</param>
    public SteeredPvPrediction(Prediction raw, Func<SteeringForecast?> steering)
    {
      _raw = raw;
      _steering = steering;
      Initialize("PV Forecast");
    }

    /// <summary>The raw window, updated, with the steering forecast's quarters laid over it.</summary>
    /// <param name="now">Time of this cycle.</param>
    protected override Dictionary<DateTime, int> PopulateData(DateTime now)
    {
      _raw.UpdateData(now);
      return Overlay(_raw.TodayAndTomorrow, _steering()?.Wh);
    }

    /// <summary>The raw window with the steering forecast's quarters laid over it (only the window's quarters: the
    /// window stays the raw one's 192).</summary>
    /// <param name="raw">The raw window.</param>
    /// <param name="steering">The steering forecast's Wh by quarter; null: none.</param>
    public static Dictionary<DateTime, int> Overlay(IReadOnlyDictionary<DateTime, int> raw, IReadOnlyDictionary<DateTime, int>? steering)
    {
      var result = new Dictionary<DateTime, int>(raw);
      if (steering is null) return result;
      foreach (var t in raw.Keys)
        if (steering.TryGetValue(t, out int wh)) result[t] = wh;
      return result;
    }
  }
}
