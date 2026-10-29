using NetDeamon.apps;
using NetDeamon.apps.PVControl;
using Xunit;

namespace NetDeamonApps.Tests;

/// <summary>
/// The load prediction's day rollover after midnight (HouseEnergy.NeedsDayRollover — the one place it is
/// decided) and the PV forecast's window filter (Extensions.CombineForecastLists + the window cut).
/// Root cause of the old error loop: after midnight the load window was still "yesterday–today"; a check
/// on Today.First() saw today's half and never refreshed. The START of the 48-h window decides.
/// </summary>
public class MidnightRolloverTests : TestBase
{
  static readonly DateTime Today = new(2026, 10, 20);
  static readonly DateTime Yesterday = Today.AddDays(-1);

  [Fact]
  public void NeedsDayRollover_YesterdaysWindowAfterMidnight_True() =>
    Assert.True(HouseEnergy.NeedsDayRollover(MakeWindow(Yesterday), Today.AddSeconds(1)));

  [Fact]
  public void NeedsDayRollover_TodaysWindow_False() =>
    Assert.False(HouseEnergy.NeedsDayRollover(MakeWindow(Today), Today.AddHours(13)));

  [Fact]
  public void NeedsDayRollover_EmptyWindow_TrueInsteadOfThrowing() =>
    Assert.True(HouseEnergy.NeedsDayRollover(new Dictionary<DateTime, int>(), Today));

  // ── PV template: stale entity data is never passed raw to ValidateData (OpenMeteoSolarForecastPrediction.PopulateData) ──

  static Dictionary<DateTime, int> Window(Dictionary<DateTime, int> combined) =>
    combined.Where(kv => kv.Key >= Today && kv.Key < Today.AddDays(2)).ToDictionary();

  [Fact]
  public void PVTemplate_StaleEntityData_FilteredToCurrentWindow()
  {
    // A valid 192-slot template plus the entity's data that hasn't rolled over yet, cut to the window.
    var combined = MakeWindow(Today).CombineForecastLists(MakeWindow(Yesterday, value: 500));
    var filtered = Window(combined);
    Assert.Equal(192, filtered.Count);
    Assert.Equal(Today, filtered.Keys.Min());
    Assert.Equal(Today.AddDays(2).AddMinutes(-15), filtered.Keys.Max());
    Assert.Equal(500, filtered[Today]);                       // today's half of the stale data is kept
    Assert.Equal(0, filtered[Today.AddDays(1)]);              // tomorrow isn't in the entity yet
  }

  [Fact]
  public void PVTemplate_MissingTomorrowFromEntity_StillProduces192Slots()
  {
    var combined = MakeWindow(Today).CombineForecastLists(MakeWindow(Today, value: 300).Take(96).ToDictionary());
    var filtered = Window(combined);
    Assert.Equal(192, filtered.Count);
    Assert.True(filtered.Values.Skip(96).All(v => v == 0)); // tomorrow stays zero — no validation failure
  }
}
