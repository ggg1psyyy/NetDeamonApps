using System;
using System.Collections.Generic;
using System.Linq;

namespace NetDeamon.apps.PVControl;

/// <summary>Health level of sensor.pv_control_health.</summary>
public enum HealthLevel { ok, degraded, error }

/// <summary>A problem a check found in this cycle.</summary>
/// <param name="Key">Identifies the issue across cycles.</param>
/// <param name="Level">Its level.</param>
/// <param name="Message">Text for the sensor.</param>
/// <param name="EscalateAfter">A degraded issue becomes an error after lasting this long; null = never.</param>
public sealed record HealthIssue(string Key, HealthLevel Level, string Message, TimeSpan? EscalateAfter = null);

/// <summary>An issue that counts (present at least the raise delay).</summary>
/// <param name="Key">Identifies the issue.</param>
/// <param name="Level">Its level (escalated if due).</param>
/// <param name="Message">Text for the sensor.</param>
/// <param name="Since">First seen.</param>
public sealed record ActiveIssue(string Key, HealthLevel Level, string Message, DateTime Since);

/// <summary>Overall health.</summary>
/// <param name="Level">Worst level of the active issues; ok without any.</param>
/// <param name="Issues">The active issues.</param>
public sealed record HealthStatus(HealthLevel Level, IReadOnlyList<ActiveIssue> Issues);

/// <summary>Debounces the checks' issues into the overall health: an issue counts after raiseAfter and until gone for clearAfter.</summary>
/// <param name="raiseAfter">How long an issue must be present to count.</param>
/// <param name="clearAfter">How long a counting issue must be gone to clear.</param>
public sealed class HealthMonitor(TimeSpan raiseAfter, TimeSpan clearAfter)
{
  sealed class Tracked(HealthIssue issue, DateTime firstSeen)
  {
    public HealthIssue Issue = issue;
    public DateTime FirstSeen = firstSeen, LastSeen = firstSeen;
    public bool Active;
  }

  readonly Dictionary<string, Tracked> _tracked = [];

  /// <summary>Adds this cycle's issues and returns the overall health.</summary>
  /// <param name="now">Current time.</param>
  /// <param name="current">Issues found this cycle.</param>
  /// <returns>Level and active issues.</returns>
  public HealthStatus Evaluate(DateTime now, IEnumerable<HealthIssue> current)
  {
    foreach (var issue in current)
    {
      if (_tracked.TryGetValue(issue.Key, out var t))
      {
        t.Issue = issue;
        t.LastSeen = now;
      }
      else
        _tracked[issue.Key] = new Tracked(issue, now);
    }

    var active = new List<ActiveIssue>();
    foreach (var (key, t) in _tracked.ToList())
    {
      bool present = t.LastSeen == now;
      // Not counting yet: must be present continuously. Counting: cleared once gone for clearAfter.
      if (!present && (!t.Active || now - t.LastSeen >= clearAfter))
      {
        _tracked.Remove(key);
        continue;
      }
      if (present && now - t.FirstSeen >= raiseAfter)
        t.Active = true;
      if (!t.Active)
        continue;
      var level = t.Issue.EscalateAfter is { } after && now - t.FirstSeen >= after ? HealthLevel.error : t.Issue.Level;
      active.Add(new ActiveIssue(key, level, t.Issue.Message, t.FirstSeen));
    }
    return new HealthStatus(active.Count == 0 ? HealthLevel.ok : active.Max(i => i.Level),
      active.OrderByDescending(i => i.Level).ThenBy(i => i.Since).ToList());
  }
}
