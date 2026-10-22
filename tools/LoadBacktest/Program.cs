using System.Globalization;
using LoadBacktest;

// Rolling-origin backtest: every day at 21:00 local each model forecasts the next 25 hours from
// the history before it; scored against what really happened. The night sum (22:00–08:00) is what
// sizes PVControl's grid charge, so it's the main measure.

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
bool analyze = args.Contains("--analyze");
args = args.Where(a => a != "--analyze").ToArray();
string db = args.Length > 0 ? args[0] : "energy_history.db";
var from = args.Length > 1 ? DateTime.Parse(args[1], CultureInfo.InvariantCulture) : new DateTime(2025, 9, 15);
var history = History.Load(db);
Console.WriteLine($"{history.Hours.Count} hours {history.Hours[0].Local:yyyy-MM-dd} → {history.Hours[^1].Local:yyyy-MM-dd HH:mm}, evaluating from {from:yyyy-MM-dd}");

var measured = Weather.FillMeasured(history);
var temp24 = new double[measured.Length];
for (int i = 0; i < measured.Length; i++)
{
  int lo = Math.Max(0, i - 23);
  double s = 0;
  for (int j = lo; j <= i; j++) s += measured[j];
  temp24[i] = s / (i - lo + 1);
}

if (analyze)
{
  Analysis.Run(history, measured, temp24, from);
  return;
}

const int Horizon = 25;
var variants = new List<(string Name, IModel Model, double ErrorScale)>
{
  ("M0 average (today)", new CurrentAverage(), 0),
  ("M1 level 24h ×1", new AverageWithLevel(24, 1), 0),
  ("M1 level 48h ×0.5", new AverageWithLevel(48, 0.5), 0),
  ("M1 level 72h ×0.5", new AverageWithLevel(72, 0.5), 0),
  ("M2 15° base 24h ×1", new TemperatureModel(15, 24, 1), 0),
  ("M2 15° base 48h ×0.5", new TemperatureModel(15, 48, 0.5), 0),
  ("M2 15° base no level", new TemperatureModel(15, 24, 0), 0),
  ("M2 13° base 48h ×0.5", new TemperatureModel(13, 48, 0.5), 0),
  ("M2 17° base 48h ×0.5", new TemperatureModel(17, 48, 0.5), 0),
  ("M2 15° 48h ×0.5, forecast err", new TemperatureModel(15, 48, 0.5), 1),
  ("M3 FastTree", new BoostedTrees(), 0),
};
var scores = variants.ToDictionary(v => v.Name, _ => new Dictionary<string, Score>());

static string Season(DateTime d) => d.Month switch
{
  11 or 12 or 1 or 2 or 3 => "winter",
  6 or 7 or 8 => "summer",
  _ => "transition",
};

int origins = 0;
var nightErrors = new List<(string Model, DateTime Day, double Actual, double Forecast, double Temp)>();
var last = history.Hours[^1].Utc;
for (var day = from.Date; ; day = day.AddDays(1))
{
  var originLocal = day.AddHours(21);
  var originUtc = TimeZoneInfo.ConvertTimeToUtc(originLocal, History.Vienna);
  if (originUtc.AddHours(Horizon) > last) break;
  int originIndex = history.IndexAt(originUtc);
  // Complete data only: the 24 h before and the 25 h after the origin.
  var actual = Enumerable.Range(0, Horizon).Select(k => history.At(originUtc.AddHours(k))).ToArray();
  if (actual.Any(a => a is null || double.IsNaN(a.Total)) || originIndex < 24 * 60
      || Enumerable.Range(1, 24).Any(k => history.At(originUtc.AddHours(-k)) is null))
    continue;
  origins++;
  foreach (var (name, model, errorScale) in variants)
  {
    var weather = new Weather(history, measured, originUtc, errorScale, seed: originIndex);
    var forecast = model.Forecast(new Context(history, originUtc, originIndex, Horizon, weather, temp24));
    double nightA = Enumerable.Range(1, 10).Sum(k => actual[k]!.Total), nightF = Enumerable.Range(1, 10).Sum(k => forecast[k]);
    nightErrors.Add((name, day, nightA, nightF, Enumerable.Range(1, 10).Average(k => measured[history.IndexAt(originUtc.AddHours(k))])));
    foreach (var key in new[] { "all", Season(originLocal) })
    {
      if (!scores[name].TryGetValue(key, out var s)) scores[name][key] = s = new Score();
      s.Add(actual.Select(a => a!.Total).ToArray(), forecast);
    }
  }
}

Console.WriteLine($"{origins} forecasts (21:00 each day)\n");
foreach (var key in new[] { "all", "winter", "transition", "summer" })
{
  Console.WriteLine($"── {key} ─────────────────────────────────────────────────────────────────────────────");
  Console.WriteLine($"{"model",-34} {"n",4} {"night kWh",9} {"night MAE",10} {"night bias",11} {"worst night",12} {"day MAE",9} {"hour MAE",9}");
  foreach (var (name, _, _) in variants)
  {
    if (!scores[name].TryGetValue(key, out var s)) continue;
    Console.WriteLine($"{name,-34} {s.N,4} {s.NightActualMean / 1000,9:F2} {s.NightMae / 1000,6:F2} {100 * s.NightMae / s.NightActualMean,3:F0}% " +
                      $"{s.NightBias / 1000,7:F2} kWh {s.NightWorst / 1000,8:F2} kWh {s.DayMae / 1000,6:F2} kWh {s.HourMae,6:F0} Wh");
  }
  Console.WriteLine();
}

foreach (var model in new[] { "M0 average (today)", "M2 13° base 48h ×0.5" })
{
  Console.WriteLine($"Worst nights {model}:");
  foreach (var e in nightErrors.Where(e => e.Model == model).OrderByDescending(e => Math.Abs(e.Forecast - e.Actual)).Take(8))
    Console.WriteLine($"  {e.Day:yyyy-MM-dd} actual {e.Actual / 1000,5:F2} forecast {e.Forecast / 1000,5:F2} kWh  ({e.Temp,5:F1} °C)");
}

/// <summary>Errors of one model: the night (22:00–08:00, forecast hours 1–10) and day
/// (08:00–22:00, hours 11–24) sums, and every hour.</summary>
sealed class Score
{
  public int N;
  double _nightAbs, _nightSigned, _nightActual, _dayAbs, _hourAbs;
  int _hours;
  public double NightWorst;

  public void Add(double[] actual, double[] forecast)
  {
    N++;
    double nightA = 0, nightF = 0, dayA = 0, dayF = 0;
    for (int k = 1; k <= 10; k++) { nightA += actual[k]; nightF += forecast[k]; }
    for (int k = 11; k <= 24; k++) { dayA += actual[k]; dayF += forecast[k]; }
    for (int k = 0; k < actual.Length; k++) { _hourAbs += Math.Abs(actual[k] - forecast[k]); _hours++; }
    _nightAbs += Math.Abs(nightF - nightA);
    _nightSigned += nightF - nightA;
    _nightActual += nightA;
    _dayAbs += Math.Abs(dayF - dayA);
    NightWorst = Math.Max(NightWorst, Math.Abs(nightF - nightA));
  }

  public double NightMae => _nightAbs / N;
  public double NightBias => _nightSigned / N;
  public double NightActualMean => _nightActual / N;
  public double DayMae => _dayAbs / N;
  public double HourMae => _hourAbs / _hours;
}
