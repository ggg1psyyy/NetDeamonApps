using System;
using System.Collections.Generic;
using System.Linq;

namespace NetDeamon.apps.PVControl.LoadModel;

/// <summary>One night (22:00–08:00, named by its start date): both forecasts made at 21:00 and,
/// once DataLogger has the rows, what was really used (Wh).</summary>
public sealed record NightResult(DateTime Night, double Model, double Average, double? Actual);

/// <summary>
/// Shadow-mode comparison of the weather load forecast with the hour-of-day average on the
/// measure that matters — the night sum 22:00–08:00, which sizes the grid charge: at 21:00 both
/// night forecasts are noted, after 08:00 the night is scored against DataLogger's rows.
/// </summary>
public sealed class NightForecastTracker
{
  public const int KeepNights = 30;
  readonly List<NightResult> _nights = [];

  public IReadOnlyList<NightResult> Nights => _nights;

  public void Restore(IEnumerable<NightResult> nights)
  {
    _nights.Clear();
    _nights.AddRange(nights.OrderBy(n => n.Night).TakeLast(KeepNights));
  }

  /// <summary>The night a forecast at <paramref name="now"/> is about: tonight, or the one going on (before 08:00).</summary>
  public static DateTime NightOf(DateTime now) => now.Hour < 8 ? now.Date.AddDays(-1) : now.Date;

  /// <summary>Sum of a 15-min forecast over the night starting at 22:00 on <paramref name="night"/>.</summary>
  public static double NightSum(IReadOnlyDictionary<DateTime, int> slots, DateTime night)
  {
    var from = night.AddHours(22);
    var to = from.AddHours(10);
    return slots.Where(s => s.Key >= from && s.Key < to).Sum(s => s.Value);
  }

  /// <summary>At or after 21:00 (before 22:00): note tonight's forecasts once. True if noted.</summary>
  public bool TakeSnapshot(DateTime now, double model, double average)
  {
    if (now.Hour != 21 || _nights.Any(n => n.Night == now.Date))
      return false;
    _nights.Add(new NightResult(now.Date, model, average, null));
    if (_nights.Count > KeepNights) _nights.RemoveAt(0);
    return true;
  }

  /// <summary>After 08:00: score open nights whose actual sum is available. True if one was scored.</summary>
  public bool Evaluate(DateTime now, Func<DateTime, double?> actualNightSum)
  {
    bool changed = false;
    for (int i = 0; i < _nights.Count; i++)
    {
      var n = _nights[i];
      if (n.Actual is not null || now < n.Night.AddDays(1).AddHours(8)) continue;
      if (actualNightSum(n.Night) is { } actual)
      {
        _nights[i] = n with { Actual = actual };
        changed = true;
      }
    }
    return changed;
  }

  /// <summary>Mean absolute night error (Wh) of both forecasts over the scored nights, and their count.</summary>
  public (double Model, double Average, int Nights) MeanAbsoluteError()
  {
    var scored = _nights.Where(n => n.Actual is not null).ToList();
    return scored.Count == 0
      ? (0, 0, 0)
      : (scored.Average(n => Math.Abs(n.Model - n.Actual!.Value)), scored.Average(n => Math.Abs(n.Average - n.Actual!.Value)), scored.Count);
  }
}
