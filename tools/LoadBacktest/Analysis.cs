namespace LoadBacktest;

/// <summary>
/// What drives the night error of M2, and whether it can be compensated:
/// A. which part misses (base load vs heat pump, "oracle" = one part perfect),
/// B. when it misses (heating state and its changes, temperature change, weekday, month),
/// C. which hours of the night,
/// D. correction during the night: at 02:00 (when PVControl sizes the charge) the hours since
///    22:00 are known — how well can 02:00–08:00 be forecast with them?
/// </summary>
public static class Analysis
{
  sealed record Night(DateTime Day, double ABase, double AHp, double FBase, double FHp, double TempNight, double TempBefore,
    double HpBefore, double[] ErrBaseByHour, double[] ErrHpByHour,
    double Rest02Actual, double Rest02From21, double Rest02Reforecast, double Since22Actual, double Since22From21)
  {
    public double Err => FBase + FHp - ABase - AHp;
    public double ErrBase => FBase - ABase;
    public double ErrHp => FHp - AHp;
    public bool HeatingNow => AHp > 1500;
    public bool HeatingBefore => HpBefore > 3000;
  }

  public static void Run(History history, double[] measured, double[] temp24, DateTime from)
  {
    var nights = new List<Night>();
    var model = new TemperatureModel(13, 48, 0.5);
    var model02 = new TemperatureModel(13, 48, 0.5);
    for (var day = from.Date; ; day = day.AddDays(1))
    {
      var originUtc = TimeZoneInfo.ConvertTimeToUtc(day.AddHours(21), History.Vienna);
      if (originUtc.AddHours(12) > history.Hours[^1].Utc) break;
      var hours = Enumerable.Range(0, 11).Select(k => history.At(originUtc.AddHours(k))).ToArray(); // 21:00 … 07:00
      if (hours.Any(h => h is null || double.IsNaN(h.HpHeat))) continue;
      int idx = history.IndexAt(originUtc);
      if (idx < 24 * 60) continue;
      var ctx = new Context(history, originUtc, idx, 11, new Weather(history, measured, originUtc, 0, 1), temp24);
      model.Forecast(ctx);
      double[] eb = new double[10], eh = new double[10];
      double aB = 0, aH = 0, fB = 0, fH = 0;
      for (int k = 1; k <= 10; k++)
      {
        var h = hours[k]!;
        aB += h.Base; aH += h.HpHeat; fB += model.LastBase[k]; fH += model.LastHp[k];
        eb[k - 1] = model.LastBase[k] - h.Base; eh[k - 1] = model.LastHp[k] - h.HpHeat;
      }
      // 02:00 = forecast hours 5..10 (02:00–07:00), known since 22:00 = hours 1..4.
      double since22A = Enumerable.Range(1, 4).Sum(k => hours[k]!.Total);
      double since22F = Enumerable.Range(1, 4).Sum(k => model.LastBase[k] + model.LastHp[k]);
      double restA = Enumerable.Range(5, 6).Sum(k => hours[k]!.Total);
      double restF = Enumerable.Range(5, 6).Sum(k => model.LastBase[k] + model.LastHp[k]);
      var origin02 = originUtc.AddHours(5);
      var re = model02.Forecast(new Context(history, origin02, history.IndexAt(origin02), 6, new Weather(history, measured, origin02, 0, 1), temp24));
      double hpBefore = 0;
      for (int k = 1; k <= 48; k++) if (history.At(originUtc.AddHours(-k)) is { } p && !double.IsNaN(p.HpHeat)) hpBefore += p.HpHeat;
      double tempNight = Enumerable.Range(1, 10).Average(k => measured[history.IndexAt(originUtc.AddHours(k))]);
      double tempBefore = Enumerable.Range(1, 48).Average(k => measured[Math.Max(0, history.IndexAt(originUtc.AddHours(-k)))]);
      nights.Add(new Night(day, aB, aH, fB, fH, tempNight, tempBefore, hpBefore, eb, eh, restA, restF, re.Sum(), since22A, since22F));
    }

    if (Environment.GetEnvironmentVariable("NIGHTS_CSV") is { } csv)
      File.WriteAllLines(csv, nights.Select(x => string.Join(",", x.Day.ToString("yyyy-MM-dd"), x.ABase, x.AHp, x.FBase, x.FHp, x.TempNight, x.TempBefore, x.HpBefore)).Prepend("day,abase,ahp,fbase,fhp,tnight,tbefore,hpbefore"));
    Console.WriteLine($"{nights.Count} nights, M2 (13 °C, level 48 h × 0.5), forecast at 21:00 for 22:00–08:00\n");

    // A. Which part misses
    Console.WriteLine("A. Which part misses (mean absolute night error)");
    foreach (var (name, sel) in Seasons())
    {
      var n = nights.Where(x => sel(x.Day)).ToList();
      if (n.Count == 0) continue;
      double varB = Var(n.Select(x => x.ErrBase)), varH = Var(n.Select(x => x.ErrHp)), varT = Var(n.Select(x => x.Err));
      Console.WriteLine($"  {name,-10} n={n.Count,3}  total {Mae(n, x => x.Err),5:F2}  | heat pump perfect → {Mae(n, x => x.ErrBase),5:F2}" +
                        $"  | base perfect → {Mae(n, x => x.ErrHp),5:F2}  kWh   share of variance: base {100 * varB / varT,3:F0} %, heat pump {100 * varH / varT,3:F0} %" +
                        $"   (night: base {n.Average(x => x.ABase) / 1000:F2}, heat pump {n.Average(x => x.AHp) / 1000:F2} kWh)");
    }

    // B. When it misses
    Console.WriteLine("\nB. When it misses (winter nights, mean absolute error / bias, kWh)");
    var w = nights.Where(x => x.Day.Month is 11 or 12 or 1 or 2 or 3).ToList();
    Group("heating before → tonight", w, x => $"{(x.HeatingBefore ? "on " : "off")} → {(x.HeatingNow ? "on " : "off")}");
    Group("night vs last 48 h temperature", w, x => (x.TempNight - x.TempBefore) switch { < -3 => "colder by >3 K", < -1 => "colder 1–3 K", <= 1 => "±1 K", <= 3 => "warmer 1–3 K", _ => "warmer >3 K" });
    Group("night before", w, x => x.Day.DayOfWeek switch { DayOfWeek.Friday => "Fri→Sat", DayOfWeek.Saturday => "Sat→Sun", DayOfWeek.Sunday => "Sun→Mon", _ => "weekday" });
    Group("month", nights, x => x.Day.ToString("yyyy-MM"));

    // C. Which hours
    Console.WriteLine("\nC. Mean absolute error per hour (winter, Wh): base | heat pump");
    for (int k = 0; k < 10; k++)
      Console.WriteLine($"  {(22 + k) % 24:00}:00  {w.Average(x => Math.Abs(x.ErrBaseByHour[k])),5:F0} | {w.Average(x => Math.Abs(x.ErrHpByHour[k])),5:F0}");

    // D. Correction during the night
    Console.WriteLine("\nD. Rest of the night 02:00–08:00, forecast at 02:00 (mean absolute error, kWh)");
    foreach (var (name, sel) in Seasons())
    {
      var n = nights.Where(x => sel(x.Day)).ToList();
      if (n.Count == 0) continue;
      string Row(double damping) => $"{Mae(n, x => x.Rest02From21 + damping * 6.0 / 4 * (x.Since22Actual - x.Since22From21) - x.Rest02Actual),5:F2}";
      Console.WriteLine($"  {name,-10} actual {n.Average(x => x.Rest02Actual) / 1000,5:F2}  | 21:00 forecast {Row(0)}  | +tonight's deviation ×0.5 {Row(0.5)}  ×1 {Row(1)}" +
                        $"  | new M2 forecast at 02:00 {Mae(n, x => x.Rest02Reforecast - x.Rest02Actual),5:F2}");
    }
  }

  static IEnumerable<(string, Func<DateTime, bool>)> Seasons() =>
  [
    ("all", _ => true),
    ("winter", d => d.Month is 11 or 12 or 1 or 2 or 3),
    ("transition", d => d.Month is 4 or 5 or 9 or 10),
    ("summer", d => d.Month is 6 or 7 or 8),
  ];

  static double Mae(IEnumerable<Night> n, Func<Night, double> err) => n.Average(x => Math.Abs(err(x))) / 1000;
  static double Var(IEnumerable<double> v) { var l = v.ToList(); double m = l.Average(); return l.Average(x => (x - m) * (x - m)); }

  static void Group(string title, List<Night> nights, Func<Night, string> key)
  {
    Console.WriteLine($"  {title}:");
    foreach (var g in nights.GroupBy(key).OrderBy(g => g.Key))
      Console.WriteLine($"    {g.Key,-16} n={g.Count(),3}  error {Mae(g, x => x.Err),5:F2}  bias {g.Average(x => x.Err) / 1000,6:F2}");
  }
}
