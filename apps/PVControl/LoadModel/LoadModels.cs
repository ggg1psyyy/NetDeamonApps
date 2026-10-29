using System;
using System.Collections.Generic;
using System.Linq;

namespace NetDeamon.apps.PVControl.LoadModel;

/// <summary>One logged hour as the load models see it.</summary>
/// <param name="Local">Local start of the hour.</param>
/// <param name="Total">Load PVControl forecasts: house energy without the excluded loads, Wh.</param>
/// <param name="HpHeat">Heat pump energy without warm water (heating + standby), Wh; NaN if unknown.</param>
/// <param name="OutdoorTemp">Measured outside temperature, °C.</param>
public readonly record struct LoadHour(DateTime Local, double Total, double HpHeat, double? OutdoorTemp)
{
  /// <summary>Load without the heat pump, Wh; NaN if the heat pump is unknown.</summary>
  public double Base => double.IsNaN(HpHeat) ? double.NaN : Total - HpHeat;

  /// <summary>A DataLogger row as a load hour; null if impossible (negative energies, parts above their total).</summary>
  /// <param name="local">Local start of the hour.</param>
  /// <param name="house">House energy, Wh.</param>
  /// <param name="excluded">Energy of the excluded loads, Wh.</param>
  /// <param name="heatPump">Heat pump energy, Wh.</param>
  /// <param name="warmWater">Warm-water energy, Wh.</param>
  /// <param name="outdoor">Outside temperature, °C.</param>
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
/// Heat pump energy per local hour for heating + standby: a_h + s·b_h·HDH, HDH = max(0, 13 °C − 24-h
/// mean outside temperature). a, b by weighted least squares over the history; s scales the heating
/// part to the last 48 h (heating curve changes).
/// </summary>
public sealed class HeatDemandModel
{
  /// <summary>Heating limit of the degree-hours, °C.</summary>
  public const double BaseTemp = 13;
  const double HalfLifeDays = 130;

  readonly double[] _a = new double[24], _b = new double[24];
  /// <summary>Factor on the heating part from the last 48 h (0…1.5; 1 if unknown).</summary>
  public double Scale { get; private set; } = 1;
  /// <summary>Recent standby level vs the fitted intercepts, Wh/h.</summary>
  public double StandbyOffset { get; private set; }
  /// <summary>Mean heat pump energy (Wh/h) of the last 24 h — the forecast while heating is off.</summary>
  public double RecentLevel { get; private set; }

  /// <summary>Heating degree-hours of an hour.</summary>
  /// <param name="temp24">24-h mean outside temperature, °C.</param>
  /// <returns>max(0, <see cref="BaseTemp"/> − temp24).</returns>
  public static double Hdh(double temp24) => Math.Max(0, BaseTemp - temp24);

  /// <summary>Wh per heating degree-hour of a local hour (the signature's slope).</summary>
  /// <param name="localHour">Hour of day 0–23.</param>
  public double Slope(int localHour) => _b[localHour];
  /// <summary>Heat pump Wh of a local hour without heating demand (the signature's intercept).</summary>
  /// <param name="localHour">Hour of day 0–23.</param>
  public double Intercept(int localHour) => _a[localHour];

  /// <summary>Fits the model.</summary>
  /// <param name="history">Hours before <paramref name="now"/>, in time order.</param>
  /// <param name="temp24">24-h mean outside temperature per history hour.</param>
  /// <param name="now">Current time (age weighting, last 48 h).</param>
  /// <param name="heatingOnSince">When heating was switched on; within 48 h the signature is used unscaled.</param>
  /// <returns>The model.</returns>
  public static HeatDemandModel Fit(IReadOnlyList<LoadHour> history, IReadOnlyList<double> temp24, DateTime now, DateTime? heatingOnSince = null)
  {
    var m = new HeatDemandModel();
    var xs = new List<double>[24]; var ys = new List<double>[24]; var ws = new List<double>[24];
    for (int hr = 0; hr < 24; hr++) { xs[hr] = []; ys[hr] = []; ws[hr] = []; }
    for (int i = 0; i < history.Count; i++)
    {
      var h = history[i];
      if (double.IsNaN(h.HpHeat)) continue;
      int hr = h.Local.Hour;
      xs[hr].Add(Hdh(temp24[i]));
      ys[hr].Add(h.HpHeat);
      ws[hr].Add(Mean.AgeWeight(h.Local, now, HalfLifeDays));
    }
    // Per hour: weighted least squares, the slope not below 0 (the intercept then through the means).
    for (int hr = 0; hr < 24; hr++)
    {
      m._b[hr] = Math.Max(0, Regression.LinearFit(xs[hr], ys[hr], ws[hr])?.Slope ?? 0);
      m._a[hr] = Regression.InterceptFor(xs[hr], ys[hr], ws[hr], m._b[hr]);
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

  /// <summary>Expected heat pump energy of a local hour.</summary>
  /// <param name="localHour">Hour of day 0–23.</param>
  /// <param name="temp24">24-h mean outside temperature, °C.</param>
  /// <param name="heatingOn">Heating mode on; off = the recent level only.</param>
  /// <returns>Wh.</returns>
  public double Expected(int localHour, double temp24, bool heatingOn) =>
    heatingOn
      ? Math.Max(0, _a[localHour] + StandbyOffset + Scale * _b[localHour] * Hdh(temp24))
      : RecentLevel;
}

/// <summary>
/// Weather-aware load forecast (backtest model M2): age-weighted hour-of-day base load (scale 10
/// days) plus half its deviation over the last 48 h (±25 %), plus the heat pump from
/// <see cref="HeatDemandModel"/>.
/// </summary>
public sealed class WeatherLoadModel
{
  /// <summary>Base load per local hour, Wh.</summary>
  public double[] BaseProfile { get; private init; } = new double[24];
  /// <summary>Shift of the base load from the last 48 h, Wh.</summary>
  public double BaseLevel { get; private set; }
  /// <summary>The heat pump part.</summary>
  public HeatDemandModel HeatDemand { get; private set; } = null!;

  /// <summary>Fits the model.</summary>
  /// <param name="history">Hours before <paramref name="now"/>, in time order.</param>
  /// <param name="temp24">24-h mean outside temperature per history hour.</param>
  /// <param name="now">Current time.</param>
  /// <param name="heatingOnSince">When heating was switched on (see <see cref="HeatDemandModel.Fit"/>).</param>
  /// <returns>The model.</returns>
  public static WeatherLoadModel Fit(IReadOnlyList<LoadHour> history, IReadOnlyList<double> temp24, DateTime now, DateTime? heatingOnSince = null)
  {
    var m = new WeatherLoadModel { BaseProfile = Mean.HourOfDayProfile(history.Select(h => (h.Local, h.Base)), now, 10.0) };

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

  /// <summary>Base load of a local hour (profile + level, not below 0).</summary>
  /// <param name="localHour">Hour of day 0–23.</param>
  /// <returns>Wh.</returns>
  public double BaseAt(int localHour) => Math.Max(0, BaseProfile[localHour] + BaseLevel);

  /// <summary>Forecast load of a local hour: base + heat pump.</summary>
  /// <param name="localHour">Hour of day 0–23.</param>
  /// <param name="temp24">24-h mean outside temperature, °C.</param>
  /// <param name="heatingOn">Heating mode on.</param>
  /// <returns>Wh.</returns>
  public double Forecast(int localHour, double temp24, bool heatingOn) =>
    BaseAt(localHour) + HeatDemand.Expected(localHour, temp24, heatingOn);

  /// <summary>24-h means of an hourly temperature series (gaps filled with the previous value; the first hours average what's there).</summary>
  /// <param name="temps">Hourly temperatures, °C; null = missing.</param>
  /// <returns>24-h mean per hour, °C.</returns>
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
