using System;
using System.Collections.Generic;
using System.Linq;

namespace NetDeamon.apps.PVControl.LoadModel;

/// <summary>
/// One logged hour (local start) as the load models see it: Total = the load PVControl forecasts
/// (house energy without the schedulable loads' columns, e.g. EV and warm water), HpHeat = heat
/// pump energy without warm water (space heating + standby; NaN if unknown), OutdoorTemp measured.
/// </summary>
public readonly record struct LoadHour(DateTime Local, double Total, double HpHeat, double? OutdoorTemp)
{
  public double Base => double.IsNaN(HpHeat) ? double.NaN : Total - HpHeat;

  /// <summary>
  /// A DataLogger row as a load hour, or null when it's structurally impossible (negative energies,
  /// warm water above the heat pump, heat pump or excluded loads above the house — ~45 hours since
  /// 2024 such as warm water −19 kWh in one hour, each able to wreck a night's sum).
  /// </summary>
  public static LoadHour? FromRow(DateTime local, double? house, double excluded, double? heatPump, double? warmWater, double? outdoor)
  {
    if (house is not { } h || h < 0 || excluded < 0) return null;
    double ww = warmWater ?? 0;
    if (ww < 0 || excluded > h + 50) return null;
    double hpHeat = double.NaN;
    if (heatPump is { } hp)
    {
      if (hp < 0 || ww > hp + 50 || hp > h + 50) return null;
      hpHeat = Math.Max(0, hp - ww);
    }
    double total = Math.Max(0, h - excluded);
    if (!double.IsNaN(hpHeat)) hpHeat = Math.Min(hpHeat, total);
    return new LoadHour(local, total, hpHeat, outdoor);
  }
}

/// <summary>
/// The house's heat demand — electrical heat pump energy per local hour for space heating plus
/// standby: a_h + s·b_h·HDH, HDH = max(0, 13 °C − 24-h mean outside temperature) (underfloor
/// heating follows the last day's temperature, not the current hour's). a_h, b_h by weighted least
/// squares over the history (half-life ~3 months — the heating curve may change); s scales the
/// heating part to what the heat pump did in the last 48 h (heating curve changes). Reusable: the
/// load forecast uses it, a heating planner can use the same energy signature.
/// Backtest (tools/LoadBacktest, 2025-09 → 2026-10): with the base load model below, −20 % night
/// error in winter against the hour-of-day average.
/// </summary>
public sealed class HeatDemandModel
{
  public const double BaseTemp = 13;
  const double HalfLifeDays = 130;

  readonly double[] _a = new double[24], _b = new double[24];
  public double Scale { get; private set; } = 1;
  public double StandbyOffset { get; private set; }
  /// <summary>Mean heat pump energy (Wh/h) of the last 24 h — the forecast while heating is off.</summary>
  public double RecentLevel { get; private set; }

  public static double Hdh(double temp24) => Math.Max(0, BaseTemp - temp24);

  /// <summary>Wh per heating degree-hour of a local hour (the energy signature's slope).</summary>
  public double Slope(int localHour) => _b[localHour];
  public double Intercept(int localHour) => _a[localHour];

  /// <param name="history">Hours before <paramref name="now"/>, in time order.</param>
  /// <param name="temp24">24-h mean outside temperature for each history hour.</param>
  /// <param name="heatingOnSince">When heating was switched on (heat pump mode), if known. Within
  /// 48 h of that the last 48 h don't show the heating yet, so the signature is used unscaled.</param>
  public static HeatDemandModel Fit(IReadOnlyList<LoadHour> history, IReadOnlyList<double> temp24, DateTime now, DateTime? heatingOnSince = null)
  {
    var m = new HeatDemandModel();
    var s0 = new double[24]; var sx = new double[24]; var sy = new double[24]; var sxx = new double[24]; var sxy = new double[24];
    for (int i = 0; i < history.Count; i++)
    {
      var h = history[i];
      if (double.IsNaN(h.HpHeat)) continue;
      double w = Math.Exp(-Math.Abs((h.Local.Date - now.Date).Days) / HalfLifeDays);
      double x = Hdh(temp24[i]);
      int hr = h.Local.Hour;
      s0[hr] += w; sx[hr] += w * x; sy[hr] += w * h.HpHeat; sxx[hr] += w * x * x; sxy[hr] += w * x * h.HpHeat;
    }
    for (int hr = 0; hr < 24; hr++)
    {
      double det = s0[hr] * sxx[hr] - sx[hr] * sx[hr];
      m._b[hr] = det > 1e-9 ? Math.Max(0, (s0[hr] * sxy[hr] - sx[hr] * sy[hr]) / det) : 0;
      m._a[hr] = s0[hr] > 0 ? (sy[hr] - m._b[hr] * sx[hr]) / s0[hr] : 0;
    }

    // Last 48 h: scale of the heating part; last 24 h: standby level and the overall level.
    double modelHeat = 0, actualHeat = 0, standbyDiff = 0, recent = 0;
    int standbyN = 0, recentN = 0;
    for (int i = 0; i < history.Count; i++)
    {
      var h = history[i];
      double ago = (now - h.Local).TotalHours;
      if (ago > 48 || ago < 0 || double.IsNaN(h.HpHeat)) continue;
      int hr = h.Local.Hour;
      double heat = m._b[hr] * Hdh(temp24[i]);
      modelHeat += heat;
      actualHeat += h.HpHeat - m._a[hr];
      if (ago <= 24)
      {
        recent += h.HpHeat; recentN++;
        if (heat < 1) { standbyDiff += h.HpHeat - m._a[hr]; standbyN++; }
      }
    }
    // Heating switched on less than 48 h ago: the last 48 h don't show it yet — unscaled.
    // Otherwise unbounded below (backtest: heating winding down is predicted best that way).
    bool justSwitchedOn = heatingOnSince is { } on && now - on < TimeSpan.FromHours(48);
    m.Scale = !justSwitchedOn && modelHeat > 1000 ? Math.Clamp(actualHeat / modelHeat, 0, 1.5) : 1;
    m.StandbyOffset = standbyN >= 6 ? standbyDiff / standbyN : 0;
    m.RecentLevel = recentN > 0 ? recent / recentN : 0;
    return m;
  }

  /// <summary>Expected heat pump energy (Wh) of a local hour; with heating off only the recent
  /// standby level.</summary>
  public double Expected(int localHour, double temp24, bool heatingOn) =>
    heatingOn
      ? Math.Max(0, _a[localHour] + StandbyOffset + Scale * _b[localHour] * Hdh(temp24))
      : RecentLevel;
}

/// <summary>
/// Weather-aware load forecast (backtest model M2): base load = weighted hour-of-day average of the
/// load without the heat pump (exp(−days/10), as the current forecast) plus half of its mean
/// deviation over the last 48 h (limited to ±25 %); plus the heat pump from
/// <see cref="HeatDemandModel"/> with the 24-h mean of measured and forecast temperatures.
/// </summary>
public sealed class WeatherLoadModel
{
  public double[] BaseProfile { get; } = new double[24];
  public double BaseLevel { get; private set; }
  public HeatDemandModel HeatDemand { get; private set; } = null!;

  public static WeatherLoadModel Fit(IReadOnlyList<LoadHour> history, IReadOnlyList<double> temp24, DateTime now, DateTime? heatingOnSince = null)
  {
    var m = new WeatherLoadModel();
    var weights = new double[24];
    foreach (var h in history)
    {
      if (double.IsNaN(h.Base)) continue;
      double w = Math.Exp(-Math.Abs((h.Local.Date - now.Date).Days) / 10.0);
      m.BaseProfile[h.Local.Hour] += h.Base * w;
      weights[h.Local.Hour] += w;
    }
    for (int hr = 0; hr < 24; hr++)
      m.BaseProfile[hr] = weights[hr] > 0 ? m.BaseProfile[hr] / weights[hr] : 0;

    double sum = 0; int n = 0;
    foreach (var h in history)
    {
      double ago = (now - h.Local).TotalHours;
      if (ago > 48 || ago < 0 || double.IsNaN(h.Base)) continue;
      sum += h.Base - m.BaseProfile[h.Local.Hour];
      n++;
    }
    double limit = 0.25 * m.BaseProfile.Average();
    m.BaseLevel = n >= 24 ? 0.5 * Math.Clamp(sum / n, -limit, limit) : 0;
    m.HeatDemand = HeatDemandModel.Fit(history, temp24, now, heatingOnSince);
    return m;
  }

  public double BaseAt(int localHour) => Math.Max(0, BaseProfile[localHour] + BaseLevel);

  /// <summary>Forecast load (Wh) of a local hour.</summary>
  public double Forecast(int localHour, double temp24, bool heatingOn) =>
    BaseAt(localHour) + HeatDemand.Expected(localHour, temp24, heatingOn);

  /// <summary>24-h mean temperatures for a series of hourly temperatures (gaps filled with the
  /// previous value; the first hours average what's there).</summary>
  public static double[] Rolling24(IReadOnlyList<double?> temps)
  {
    var filled = new double[temps.Count];
    double last = temps.FirstOrDefault(t => t is not null) ?? 10;
    for (int i = 0; i < temps.Count; i++)
    {
      if (temps[i] is { } t) last = t;
      filled[i] = last;
    }
    var result = new double[temps.Count];
    double window = 0;
    for (int i = 0; i < filled.Length; i++)
    {
      window += filled[i];
      if (i >= 24) window -= filled[i - 24];
      result[i] = window / Math.Min(i + 1, 24);
    }
    return result;
  }
}
