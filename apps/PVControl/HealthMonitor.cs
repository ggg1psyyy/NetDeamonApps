using System;
using System.Collections.Generic;
using System.Linq;

namespace NetDeamon.apps.PVControl;

public enum HealthLevel { ok, degraded, error }

/// <summary>
/// A problem a check found in this cycle. <paramref name="Key"/> identifies it across cycles;
/// with <paramref name="EscalateAfter"/> a degraded issue becomes an error once it has lasted that long.
/// </summary>
public sealed record HealthIssue(string Key, HealthLevel Level, string Message, TimeSpan? EscalateAfter = null);

/// <summary>An issue that counts: present for at least the monitor's raise delay.</summary>
public sealed record ActiveIssue(string Key, HealthLevel Level, string Message, DateTime Since);

public sealed record HealthStatus(HealthLevel Level, IReadOnlyList<ActiveIssue> Issues);

/// <summary>
/// Overall health of PVControl from the issues its checks report every cycle
/// (sensor.pv_control_health). An issue only counts after <c>raiseAfter</c> (a 15-s sensor blip
/// like 2026-10-21 13:28 doesn't notify anyone) and keeps counting until it has been gone for
/// <c>clearAfter</c> (no ok/degraded flapping). The level is the worst counting issue.
/// </summary>
public sealed class HealthMonitor(TimeSpan raiseAfter, TimeSpan clearAfter)
{
  sealed class Tracked(HealthIssue issue, DateTime firstSeen)
  {
    public HealthIssue Issue = issue;
    public DateTime FirstSeen = firstSeen, LastSeen = firstSeen;
    public bool Active;
  }

  readonly Dictionary<string, Tracked> _tracked = [];

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
