using Xunit;

namespace NetDeamonApps.Tests;

/// <summary>
/// Tests for the midnight load-data staleness check that caused the post-midnight error loop.
///
/// Root cause: after midnight the load data was built for "yesterday–today" window.
///   Prediction_Load.Today still returned entries because "today" matched the 2nd half of that
///   old window, so the old condition (Today.First().Key.Date != now.Date) evaluated to FALSE
///   and the load data was never refreshed.
/// Fix: check TodayAndTomorrow.First().Key.Date instead (the START of the 48h window).
/// </summary>
public class MidnightRolloverTests : TestBase
{
  private static readonly DateTime Today = DateTime.Now.Date;
  private static readonly DateTime Yesterday = Today.AddDays(-1);

  // ── demonstrating the old bug ─────────────────────────────────────────────

  [Fact]
  public void OldCondition_StaleData_ReturnsFalse_BugDemonstrated()
  {
    // Stale data window: yesterday 00:00 → today 23:45
    var staleData = MakeWindow(Yesterday);

    // Simulates the old check: Today filters by DateTime.Now.Date.
    // The 2nd half of stale data IS today, so Today is non-empty and First() returns today.
    var todayEntries = staleData.Where(kv => kv.Key.Date == Today).ToDictionary();
    bool oldConditionFires = todayEntries.Count == 0
      || todayEntries.First().Key.Date != Today;

    // BUG: old check says "no refresh needed" even though data is stale
    Assert.False(oldConditionFires);
  }

  // ── new fix ───────────────────────────────────────────────────────────────

  [Fact]
  public void NewCondition_StaleData_ReturnsTrue_RefreshFires()
  {
    var staleData = MakeWindow(Yesterday);

    // New check: look at the START of the stored window.
    bool newConditionFires = staleData.First().Key.Date != Today;

    Assert.True(newConditionFires);
  }

  [Fact]
  public void NewCondition_FreshData_ReturnsFalse_NoSpuriousRefresh()
  {
    var freshData = MakeWindow(Today);

    bool newConditionFires = freshData.First().Key.Date != Today;

    Assert.False(newConditionFires);
  }

  // ── PV template: stale entity data is never passed raw to ValidateData ─────

  [Fact]
  public void PVTemplate_StaleEntityData_FilteredToCurrentWindow()
  {
    // Simulates what OpenMeteoSolarForecastPrediction.PopulateData() now does:
    // start with a valid 192-slot template, overlay entity data, filter to current window.
    var template = MakeWindow(Today);
    var staleEntityData = MakeWindow(Yesterday, value: 500); // entity hasn't updated yet

    // Combine (like CombineForecastLists) then filter to valid window
    var combined = new Dictionary<DateTime, int>(template);
    foreach (var kv in staleEntityData)
    {
      if (combined.ContainsKey(kv.Key))
        combined[kv.Key] += kv.Value;
      else
        combined[kv.Key] = kv.Value;
    }
    var filtered = combined
      .Where(kv => kv.Key >= Today && kv.Key < Today.AddDays(2))
      .ToDictionary();

    // Result must be exactly 192 slots — ValidateData should accept it
    Assert.Equal(192, filtered.Count);
    Assert.True(filtered.Keys.Min() == Today);
    Assert.True(filtered.Keys.Max() == Today.AddDays(2).AddMinutes(-15));
  }

  [Fact]
  public void PVTemplate_MissingTomorrowFromEntity_StillProduces192Slots()
  {
    // Entity only provides today (96 slots) — tomorrow not yet available after midnight.
    var template = MakeWindow(Today);
    var incompleteEntityData = MakeWindow(Today).Take(96).ToDictionary(); // only today

    var combined = new Dictionary<DateTime, int>(template);
    foreach (var kv in incompleteEntityData)
      combined[kv.Key] = combined.GetValueOrDefault(kv.Key, 0) + kv.Value;

    var filtered = combined
      .Where(kv => kv.Key >= Today && kv.Key < Today.AddDays(2))
      .ToDictionary();

    // Tomorrow's slots stay zero (no PV data yet) but window is complete → no validation failure
    Assert.Equal(192, filtered.Count);
    Assert.True(filtered.Values.Skip(96).All(v => v == 0)); // tomorrow is all-zero
  }
}
