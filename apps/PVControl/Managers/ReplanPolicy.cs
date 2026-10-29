using System;

namespace NetDeamon.apps.PVControl.Managers
{
  /// <summary>The inputs a load's plan was made with, compared each cycle to decide whether to replan.</summary>
  /// <param name="Time">Time of the plan.</param>
  /// <param name="HouseSoc">House battery SoC, %.</param>
  /// <param name="Level">Load level (EV SoC, …).</param>
  /// <param name="Drawing">The load drew power.</param>
  /// <param name="Latched">The min-on or min-off latch held — its end replans right away.</param>
  public sealed record ReplanSnapshot(DateTime Time, int HouseSoc, float Level, bool Drawing, bool Latched = false);

  /// <summary>When a load's window search (FindLoadWindow) runs again; between searches it keeps its last decision.</summary>
  public static class ReplanPolicy
  {
    /// <summary>Why the load must replan now.</summary>
    /// <param name="last">Snapshot of the last plan; null = none yet.</param>
    /// <param name="now">Snapshot now.</param>
    /// <param name="intervalMinutes">Replan at least this often.</param>
    /// <param name="requested">Pending request (<see cref="SchedulableLoadRuntime.TakeReplanRequest"/>).</param>
    /// <returns>The reason; null to keep the last decision.</returns>
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

  /// <summary>Fires when the house load stays off its forecast by more than <see cref="MinDeviationW"/> and <see cref="MinDeviationFraction"/> for <see cref="Duration"/>; then starts over.</summary>
  public sealed class LoadDeviationTracker
  {
    /// <summary>Minimum deviation, W.</summary>
    public const int MinDeviationW = 500;
    /// <summary>Minimum deviation as a fraction of the forecast.</summary>
    public const double MinDeviationFraction = 0.3;
    /// <summary>How long the deviation must last.</summary>
    public static readonly TimeSpan Duration = TimeSpan.FromMinutes(5);

    private DateTime? _since;

    /// <summary>Adds a reading.</summary>
    /// <param name="now">Current time.</param>
    /// <param name="actualW">Measured house load, W.</param>
    /// <param name="predictedW">Forecast house load, W.</param>
    /// <returns>True when the deviation has lasted <see cref="Duration"/>.</returns>
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
