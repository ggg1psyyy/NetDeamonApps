using System;

namespace NetDeamon.apps.PVControl.Managers
{
  /// <summary>The inputs a load's plan was made with, compared each cycle to decide whether to replan.</summary>
  /// <param name="Latched">Whether the min-on or min-off latch held — its end must replan right away.</param>
  public sealed record ReplanSnapshot(DateTime Time, int HouseSoc, float Level, bool Drawing, bool Latched = false);

  /// <summary>
  /// Decides when a schedulable load's window search (FindLoadWindow — the expensive part of a
  /// cycle) must run again. Between replans the load keeps its last decision while the house
  /// simulation and inverter command still update every cycle. The load's own live safety
  /// checks (target reached, house battery floor, availability) live inside the window search,
  /// so every input they depend on is a trigger here.
  /// </summary>
  public static class ReplanPolicy
  {
    /// <summary>Returns why the load must replan now, or null to keep the last decision.</summary>
    public static string? Reason(ReplanSnapshot? last, ReplanSnapshot now, int intervalMinutes, string? requested)
    {
      if (last is null) return "first plan";
      if (requested is not null) return requested;
      if (now.Time.RoundToNearestQuarterHour() != last.Time.RoundToNearestQuarterHour()) return "new quarter-hour";
      if (now.HouseSoc != last.HouseSoc) return "house battery SoC changed";
      if (now.Level != last.Level) return "level changed";
      if (now.Drawing != last.Drawing) return now.Drawing ? "started drawing power" : "stopped drawing power";
      if (last.Latched && !now.Latched) return "latch ended";
      if ((now.Time - last.Time).TotalMinutes >= intervalMinutes) return "interval";
      return null;
    }
  }

  /// <summary>
  /// Fires when the measured house load stays noticeably off its forecast — more than
  /// <see cref="MinDeviationW"/> and <see cref="MinDeviationFraction"/> of the forecast — for
  /// <see cref="Duration"/>. Then the plans were made for a load that isn't happening. After
  /// firing, the next window starts over, so a lasting deviation replans once per Duration.
  /// </summary>
  public sealed class LoadDeviationTracker
  {
    public const int MinDeviationW = 500;
    public const double MinDeviationFraction = 0.3;
    public static readonly TimeSpan Duration = TimeSpan.FromMinutes(5);

    private DateTime? _since;

    public bool Observe(DateTime now, int actualW, int predictedW)
    {
      int deviation = Math.Abs(actualW - predictedW);
      if (deviation <= MinDeviationW || deviation <= MinDeviationFraction * Math.Abs(predictedW))
      {
        _since = null;
        return false;
      }
      _since ??= now;
      if (now - _since.Value < Duration) return false;
      _since = now;
      return true;
    }
  }
}
