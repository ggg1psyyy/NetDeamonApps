using Microsoft.ML;
using Microsoft.ML.Data;
using Microsoft.ML.Trainers.FastTree;

namespace LoadBacktest;

/// <summary>Forecasts the total load (Wh) of the <c>horizon</c> hours from <c>originUtc</c> on,
/// using only history before the origin.</summary>
public interface IModel
{
  string Name { get; }
  double[] Forecast(Context c);
}

/// <summary>What a forecast at <see cref="OriginUtc"/> may use: history before it, the weather
/// as forecast then, and per-index measured 24-h mean temperatures for fitting.</summary>
public sealed record Context(History History, DateTime OriginUtc, int OriginIndex, int Horizon, Weather Weather, double[] MeasuredTemp24)
{
  public DateTime OriginLocalDate => TimeZoneInfo.ConvertTimeFromUtc(OriginUtc, History.Vienna).Date;
  public DateTime LocalAt(int k) => TimeZoneInfo.ConvertTimeFromUtc(OriginUtc.AddHours(k), History.Vienna);
}

static class Profile
{
  /// <summary>Weighted mean per local hour, weight exp(−|days before the origin day| / scaling) —
  /// HourlyWeightedAverageLoadPrediction.HourlyWeightedAverages.</summary>
  public static double[] HourOfDay(Context c, Func<Hour, double> value, double scaling)
  {
    var sums = new double[24];
    var weights = new double[24];
    var today = c.OriginLocalDate;
    for (int i = 0; i < c.OriginIndex; i++)
    {
      var h = c.History.Hours[i];
      double v = value(h);
      if (double.IsNaN(v)) continue;
      double w = Math.Exp(-Math.Abs((h.Local - today).Days) / scaling);
      sums[h.Local.Hour] += v * w;
      weights[h.Local.Hour] += w;
    }
    return Enumerable.Range(0, 24).Select(i => weights[i] > 0 ? sums[i] / weights[i] : 0).ToArray();
  }

  /// <summary>Mean deviation of the last 24 h from the profile, limited to ±25 % of the profile's
  /// mean — a level step (e.g. the heat pump's standby +30 W on 2026-10-21) shows up here a day
  /// later instead of over ~10 days.</summary>
  public static double LevelOffset(Context c, double[] profile, Func<Hour, double> value, int hours = 24, double damping = 1)
  {
    double sum = 0;
    int n = 0;
    for (int i = Math.Max(0, c.OriginIndex - hours); i < c.OriginIndex; i++)
    {
      var h = c.History.Hours[i];
      double v = value(h);
      if (double.IsNaN(v)) continue;
      sum += v - profile[h.Local.Hour];
      n++;
    }
    if (n < hours / 2) return 0;
    double limit = 0.25 * profile.Average();
    return damping * Math.Clamp(sum / n, -limit, limit);
  }
}

/// <summary>M0: today's forecast — weighted hour-of-day average of the total load.</summary>
public sealed class CurrentAverage : IModel
{
  public string Name => "M0 average (today)";
  public double[] Forecast(Context c)
  {
    var p = Profile.HourOfDay(c, h => h.Total, 10);
    return Enumerable.Range(0, c.Horizon).Select(k => p[c.LocalAt(k).Hour]).ToArray();
  }
}

/// <summary>M1: M0 plus the level offset of the last 24 h.</summary>
public sealed class AverageWithLevel(int hours = 24, double damping = 1) : IModel
{
  public string Name => $"M1 average + level {hours}h ×{damping}";
  public double[] Forecast(Context c)
  {
    var p = Profile.HourOfDay(c, h => h.Total, 10);
    double offset = Profile.LevelOffset(c, p, h => h.Total, hours, damping);
    return Enumerable.Range(0, c.Horizon).Select(k => Math.Max(0, p[c.LocalAt(k).Hour] + offset)).ToArray();
  }
}

/// <summary>
/// M2 (transparent): base load = hour-of-day average + level (as M1); heat pump = per local hour
/// c + s·b·HDH, HDH = max(0, 15 °C − 24-h mean temperature), fitted by weighted least squares
/// (recent days weigh more, half-life ~3 months — the heating curve may change). s scales the
/// heating part to what the heat pump did in the last 48 h (heating switched off → ~0, curve
/// lowered → &lt; 1), limited to 0…1.5.
/// </summary>
public sealed class TemperatureModel(double baseTemp = 15, int levelHours = 24, double levelDamping = 1) : IModel
{
  public string Name => $"M2 base {levelHours}h ×{levelDamping}";

  double Hdh(double temp24) => Math.Max(0, baseTemp - temp24);

  /// <summary>The two parts of the last forecast (for the error analysis).</summary>
  public double[] LastBase { get; private set; } = [];
  public double[] LastHp { get; private set; } = [];

  public double[] Forecast(Context c)
  {
    var baseProfile = Profile.HourOfDay(c, h => h.Base, 10);
    double baseOffset = Profile.LevelOffset(c, baseProfile, h => h.Base, levelHours, levelDamping);

    // Weighted least squares hp = a + b·hdh per local hour.
    var s0 = new double[24]; var sx = new double[24]; var sy = new double[24]; var sxx = new double[24]; var sxy = new double[24];
    var today = c.OriginLocalDate;
    for (int i = 0; i < c.OriginIndex; i++)
    {
      var h = c.History.Hours[i];
      if (double.IsNaN(h.HpHeat)) continue;
      double w = Math.Exp(-Math.Abs((h.Local - today).Days) / 130.0);
      double x = Hdh(c.MeasuredTemp24[i]);
      int hr = h.Local.Hour;
      s0[hr] += w; sx[hr] += w * x; sy[hr] += w * h.HpHeat; sxx[hr] += w * x * x; sxy[hr] += w * x * h.HpHeat;
    }
    var a = new double[24]; var b = new double[24];
    for (int hr = 0; hr < 24; hr++)
    {
      double det = s0[hr] * sxx[hr] - sx[hr] * sx[hr];
      b[hr] = det > 1e-9 ? Math.Max(0, (s0[hr] * sxy[hr] - sx[hr] * sy[hr]) / det) : 0;
      a[hr] = s0[hr] > 0 ? (sy[hr] - b[hr] * sx[hr]) / s0[hr] : 0;
    }

    // Scale of the heating part from the last 48 h; level of the standby part from the last 24 h.
    double modelHeat = 0, actualHeat = 0, standbyDiff = 0;
    int standbyN = 0;
    for (int i = Math.Max(0, c.OriginIndex - 48); i < c.OriginIndex; i++)
    {
      var h = c.History.Hours[i];
      if (double.IsNaN(h.HpHeat)) continue;
      int hr = h.Local.Hour;
      double heat = b[hr] * Hdh(c.MeasuredTemp24[i]);
      modelHeat += heat;
      actualHeat += h.HpHeat - a[hr];
      if (i >= c.OriginIndex - 24 && heat < 1) { standbyDiff += h.HpHeat - a[hr]; standbyN++; }
    }
    double scale = modelHeat > 1000 ? Math.Clamp(actualHeat / modelHeat, 0, 1.5) : 1;
    double standby = standbyN >= 6 ? standbyDiff / standbyN : 0;

    var result = new double[c.Horizon];
    LastBase = new double[c.Horizon];
    LastHp = new double[c.Horizon];
    for (int k = 0; k < c.Horizon; k++)
    {
      var utc = c.OriginUtc.AddHours(k);
      int hr = c.LocalAt(k).Hour;
      LastHp[k] = Math.Max(0, a[hr] + standby + scale * b[hr] * Hdh(c.Weather.Temp24(utc)));
      LastBase[k] = Math.Max(0, baseProfile[hr] + baseOffset);
      result[k] = LastBase[k] + LastHp[k];
    }
    return result;
  }
}

/// <summary>M3: ML.NET FastTree (gradient-boosted trees) on the total load, retrained weekly.
/// Features: local hour, weekday, weekend, season, temperature and its 24-h mean, clouds, and
/// the load 48 h earlier plus the mean of the day before that (both known for a 36-h horizon).</summary>
public sealed class BoostedTrees : IModel
{
  sealed class Row
  {
    public float Label { get; set; }
    [VectorType(FeatureCount)] public float[] Features { get; set; } = [];
  }
  sealed class Output { public float Score { get; set; } }
  const int FeatureCount = 10;

  readonly MLContext _ml = new(seed: 1);
  PredictionEngine<Row, Output>? _engine;
  DateTime _trainedFor = DateTime.MinValue;

  public string Name => "M3 ML.NET FastTree";

  static float[] Feat(DateTime local, double temp, double temp24, double cloud, double lag48, double mean48) =>
  [
    local.Hour, (float)local.DayOfWeek, local.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday ? 1 : 0,
    (float)Math.Sin(2 * Math.PI * local.DayOfYear / 365.25), (float)Math.Cos(2 * Math.PI * local.DayOfYear / 365.25),
    (float)temp, (float)temp24, (float)cloud, (float)lag48, (float)mean48,
  ];

  static (double lag48, double mean48)? Lags(History h, DateTime utc)
  {
    var lag = h.At(utc.AddHours(-48));
    if (lag is null || double.IsNaN(lag.Total)) return null;
    double sum = 0; int n = 0;
    for (int k = 48; k < 72; k++)
      if (h.At(utc.AddHours(-k)) is { } x && !double.IsNaN(x.Total)) { sum += x.Total; n++; }
    return n < 20 ? null : (lag.Total, sum / n);
  }

  public double[] Forecast(Context c)
  {
    if (_engine is null || c.OriginUtc - _trainedFor >= TimeSpan.FromDays(7))
      Train(c);
    var result = new double[c.Horizon];
    for (int k = 0; k < c.Horizon; k++)
    {
      var utc = c.OriginUtc.AddHours(k);
      var lags = Lags(c.History, utc);
      double cloud = c.History.At(utc)?.Cloud ?? 50;
      var row = new Row { Features = Feat(c.LocalAt(k), c.Weather.Temp(utc), c.Weather.Temp24(utc), cloud,
        lags?.lag48 ?? 0, lags?.mean48 ?? 0) };
      result[k] = Math.Max(0, _engine!.Predict(row).Score);
    }
    return result;
  }

  void Train(Context c)
  {
    var rows = new List<Row>();
    var measured = Weather.FillMeasured(c.History);
    for (int i = 72; i < c.OriginIndex; i++)
    {
      var h = c.History.Hours[i];
      if (double.IsNaN(h.Total) || Lags(c.History, h.Utc) is not { } lags) continue;
      rows.Add(new Row { Label = (float)h.Total, Features = Feat(h.Local, measured[i], c.MeasuredTemp24[i], h.Cloud ?? 50, lags.lag48, lags.mean48) });
    }
    var data = _ml.Data.LoadFromEnumerable(rows);
    var trainer = _ml.Regression.Trainers.FastTree(new FastTreeRegressionTrainer.Options
    {
      NumberOfTrees = 200, NumberOfLeaves = 31, MinimumExampleCountPerLeaf = 20, LearningRate = 0.1,
    });
    var model = trainer.Fit(data);
    _engine = _ml.Model.CreatePredictionEngine<Row, Output>(model);
    _trainedFor = c.OriginUtc;
  }
}

/// <summary>The production model (apps/PVControl/LoadModel/LoadModels.cs), heating assumed on
/// (the live model reads the heat pump mode; history has no mode before DataLogger v6).</summary>
public sealed class AppModel : IModel
{
  public string Name => "App WeatherLoadModel";

  public double[] Forecast(Context c)
  {
    var hours = new List<NetDeamon.apps.PVControl.LoadModel.LoadHour>();
    var temps = new List<double>();
    for (int i = 0; i < c.OriginIndex; i++)
    {
      var h = c.History.Hours[i];
      if (double.IsNaN(h.Total)) continue;
      hours.Add(new(h.Local, h.Total, h.HpHeat, h.Temp));
      temps.Add(c.MeasuredTemp24[i]);
    }
    var model = NetDeamon.apps.PVControl.LoadModel.WeatherLoadModel.Fit(hours, temps, c.LocalAt(0));
    return Enumerable.Range(0, c.Horizon)
      .Select(k => model.Forecast(c.LocalAt(k).Hour, c.Weather.Temp24(c.OriginUtc.AddHours(k)), heatingOn: true)).ToArray();
  }
}
