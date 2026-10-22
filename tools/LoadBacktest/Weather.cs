namespace LoadBacktest;

/// <summary>
/// Outside temperature as a forecast made at <see cref="OriginUtc"/> would see it: measured
/// (DataLogger, roof sensor) up to the origin, afterwards the measured value plus an optional
/// forecast error (a per-forecast bias plus noise growing with the lead time). With
/// <c>errorScale = 0</c> it's a perfect forecast.
/// </summary>
public sealed class Weather
{
  readonly History _history;
  readonly double[] _measured; // per history index, gaps filled with the previous value
  readonly double _bias;
  readonly Random _random;
  readonly double _errorScale;
  readonly Dictionary<DateTime, double> _forecast = [];

  public DateTime OriginUtc { get; }

  public Weather(History history, double[] measured, DateTime originUtc, double errorScale, int seed)
  {
    _history = history;
    _measured = measured;
    OriginUtc = originUtc;
    _errorScale = errorScale;
    _random = new Random(seed);
    _bias = errorScale * Gaussian(_random) * 1.0;
  }

  /// <summary>Measured temperatures per history index, gaps filled with the last known value.</summary>
  public static double[] FillMeasured(History history)
  {
    var result = new double[history.Hours.Count];
    double last = 10;
    for (int i = 0; i < result.Length; i++)
    {
      if (history.Hours[i].Temp is { } t) last = t;
      result[i] = last;
    }
    return result;
  }

  public double Temp(DateTime utc)
  {
    int i = _history.IndexAt(utc);
    double measured = _measured[Math.Clamp(i, 0, _measured.Length - 1)];
    if (utc < OriginUtc || _errorScale == 0)
      return measured;
    if (!_forecast.TryGetValue(utc, out double value))
    {
      double lead = (utc - OriginUtc).TotalHours;
      value = measured + _bias + _errorScale * Gaussian(_random) * (0.5 + 0.04 * lead);
      _forecast[utc] = value;
    }
    return value;
  }

  /// <summary>Mean temperature of the 24 h up to <paramref name="utc"/> — underfloor heating
  /// reacts to the last day's temperature, not the current hour's.</summary>
  public double Temp24(DateTime utc)
  {
    double sum = 0;
    for (int k = 0; k < 24; k++)
      sum += Temp(utc.AddHours(-k));
    return sum / 24;
  }

  static double Gaussian(Random r) =>
    Math.Sqrt(-2 * Math.Log(1 - r.NextDouble())) * Math.Cos(2 * Math.PI * r.NextDouble());
}
