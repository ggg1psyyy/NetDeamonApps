using System;
using System.Collections.Generic;
using System.Linq;

namespace NetDeamon.apps.PVControl.LoadModel;

/// <summary>One night (22:00–08:00): both forecasts made at 21:00 and what was really used.</summary>
/// <param name="Night">Start date of the night.</param>
/// <param name="Model">Weather model's night sum, Wh.</param>
/// <param name="Average">Hour-of-day average's night sum, Wh.</param>
/// <param name="Actual">Measured night sum, Wh; null until scored.</param>
public sealed record NightResult(DateTime Night, double Model, double Average, double? Actual);

/// <summary>Shadow-mode score of the weather load forecast vs the hour-of-day average on the night sum 22:00–08:00 (noted at 21:00, scored after 08:00).</summary>
public sealed class NightForecastTracker
{
  /// <summary>Nights kept.</summary>
  public const int KeepNights = 30;
  readonly DayLog<NightResult> _nights = new(KeepNights, n => n.Night);

  /// <summary>Recorded nights, oldest first.</summary>
  public IReadOnlyList<NightResult> Nights => _nights.Items;

  /// <summary>Restores recorded nights after a restart (the last <see cref="KeepNights"/>).</summary>
  /// <param name="nights">Nights from the sensor's attribute.</param>
  public void Restore(IEnumerable<NightResult> nights) => _nights.Restore(nights);

  /// <summary>The night a forecast is about: tonight, or the one going on (before 08:00).</summary>
  /// <param name="now">Time of the forecast.</param>
  /// <returns>Start date of the night.</returns>
  public static DateTime NightOf(DateTime now) => now.Hour < 8 ? now.Date.AddDays(-1) : now.Date;

  /// <summary>Sum of a 15-min forecast over a night (22:00 → 08:00).</summary>
  /// <param name="slots">Forecast by slot start, Wh.</param>
  /// <param name="night">Start date of the night.</param>
  /// <returns>Wh.</returns>
  public static double NightSum(IReadOnlyDictionary<DateTime, int> slots, DateTime night)
  {
    var (from, to) = NightWindow(night);
    return slots.Where(s => TimeRange.Contains(from, to, s.Key)).Sum(s => s.Value);
  }

  /// <summary>The night window 22:00 → 08:00.</summary>
  /// <param name="night">Start date of the night.</param>
  /// <returns>Start (inclusive) and end (exclusive).</returns>
  public static (DateTime From, DateTime To) NightWindow(DateTime night) => (night.AddHours(22), night.AddHours(32));

  /// <summary>Notes tonight's forecasts once, between 21:00 and 22:00.</summary>
  /// <param name="now">Current time.</param>
  /// <param name="model">Weather model's night sum, Wh.</param>
  /// <param name="average">Hour-of-day average's night sum, Wh.</param>
  /// <returns>True if noted.</returns>
  public bool TakeSnapshot(DateTime now, double model, double average)
  {
    if (now.Hour != 21 || _nights.Has(now.Date))
      return false;
    _nights.Add(new NightResult(now.Date, model, average, null));
    return true;
  }

  /// <summary>Scores ended nights (after 08:00) whose measured sum is available.</summary>
  /// <param name="now">Current time.</param>
  /// <param name="actualNightSum">Measured sum of a night, Wh; null while DataLogger lacks rows.</param>
  /// <returns>True if a night was scored.</returns>
  public bool Evaluate(DateTime now, Func<DateTime, double?> actualNightSum)
  {
    bool changed = false;
    for (int i = 0; i < _nights.Items.Count; i++)
    {
      var n = _nights.Items[i];
      if (n.Actual is not null || now < n.Night.AddDays(1).AddHours(8)) continue;
      if (actualNightSum(n.Night) is { } actual)
      {
        _nights.Set(i, n with { Actual = actual });
        changed = true;
      }
    }
    return changed;
  }

  /// <summary>Mean absolute night error of both forecasts over the scored nights.</summary>
  /// <returns>Errors of the model and the average, Wh, and the number of scored nights.</returns>
  public (double Model, double Average, int Nights) MeanAbsoluteError()
  {
    var scored = _nights.Items.Where(n => n.Actual is not null).ToList();
    return scored.Count == 0
      ? (0, 0, 0)
      : (scored.Average(n => Math.Abs(n.Model - n.Actual!.Value)), scored.Average(n => Math.Abs(n.Average - n.Actual!.Value)), scored.Count);
  }
}
