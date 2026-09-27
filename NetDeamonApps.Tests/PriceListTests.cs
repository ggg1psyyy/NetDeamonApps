using NetDeamon.apps;
using NetDeamon.apps.PVControl;
using Xunit;

namespace NetDeamonApps.Tests;

/// <summary>
/// Tests for PriceList.NormalizeToQuarterHourly / WithResolution — the logic that lets the price
/// pipeline accept both hourly-native and quarter-hourly-native EPEX sensors while always
/// exposing quarter-hourly PriceTableEntry data to consumers, optionally averaged per hour to
/// match hourly-billing providers.
/// </summary>
public class PriceListTests
{
  // ── NormalizeToQuarterHourly ─────────────────────────────────────────────

  [Fact]
  public void NormalizeToQuarterHourly_SplitsHourlyEntryIntoFourIdenticalPriceSlots()
  {
    var start = new DateTime(2026, 8, 10, 12, 0, 0);
    var source = new PriceList([new PriceTableEntry(start, start.AddHours(1), 10f)]);

    var result = source.NormalizeToQuarterHourly().ToList();

    Assert.Equal(4, result.Count);
    for (int i = 0; i < 4; i++)
    {
      Assert.Equal(start.AddMinutes(i * 15), result[i].StartTime);
      Assert.Equal(start.AddMinutes((i + 1) * 15), result[i].EndTime);
      Assert.Equal(10f, result[i].Price);
    }
  }

  [Fact]
  public void NormalizeToQuarterHourly_LeavesQuarterHourEntriesUnchanged()
  {
    var start = new DateTime(2026, 8, 10, 12, 0, 0);
    var source = new PriceList([
      new PriceTableEntry(start, start.AddMinutes(15), 10f),
      new PriceTableEntry(start.AddMinutes(15), start.AddMinutes(30), 20f),
      new PriceTableEntry(start.AddMinutes(30), start.AddMinutes(45), 30f),
      new PriceTableEntry(start.AddMinutes(45), start.AddMinutes(60), 40f),
    ]);

    var result = source.NormalizeToQuarterHourly().ToList();

    Assert.Equal(4, result.Count);
    Assert.Equal([10f, 20f, 30f, 40f], result.Select(r => r.Price));
  }

  // ── WithResolution ───────────────────────────────────────────────────────

  [Fact]
  public void WithResolution_Hourly_AveragesFourQuarterSlots()
  {
    var start = new DateTime(2026, 8, 10, 12, 0, 0);
    var source = new PriceList([
      new PriceTableEntry(start, start.AddMinutes(15), 10f),
      new PriceTableEntry(start.AddMinutes(15), start.AddMinutes(30), 20f),
      new PriceTableEntry(start.AddMinutes(30), start.AddMinutes(45), 30f),
      new PriceTableEntry(start.AddMinutes(45), start.AddMinutes(60), 40f),
    ]);

    var result = source.WithResolution(PriceResolution.Hourly).ToList();

    Assert.Equal(4, result.Count);
    Assert.All(result, r => Assert.Equal(25f, r.Price));
    Assert.Equal([start, start.AddMinutes(15), start.AddMinutes(30), start.AddMinutes(45)], result.Select(r => r.StartTime));
  }

  [Fact]
  public void WithResolution_QuarterHourly_LeavesEntriesUnchanged()
  {
    var start = new DateTime(2026, 8, 10, 12, 0, 0);
    var source = new PriceList([
      new PriceTableEntry(start, start.AddMinutes(15), 10f),
      new PriceTableEntry(start.AddMinutes(15), start.AddMinutes(30), 20f),
    ]);

    var result = source.WithResolution(PriceResolution.QuarterHourly).ToList();

    Assert.Equal([10f, 20f], result.Select(r => r.Price));
  }

  [Fact]
  public void WithResolution_Hourly_DoesNotConflateSameHourAcrossDays()
  {
    var day1 = new DateTime(2026, 8, 10, 12, 0, 0);
    var day2 = new DateTime(2026, 8, 11, 12, 0, 0);
    var source = new PriceList([
      new PriceTableEntry(day1, day1.AddMinutes(15), 10f),
      new PriceTableEntry(day1.AddMinutes(15), day1.AddMinutes(30), 20f),
      new PriceTableEntry(day2, day2.AddMinutes(15), 100f),
      new PriceTableEntry(day2.AddMinutes(15), day2.AddMinutes(30), 200f),
    ]);

    var result = source.WithResolution(PriceResolution.Hourly).ToList();

    Assert.Equal(15f, result.Single(r => r.StartTime == day1).Price);
    Assert.Equal(150f, result.Single(r => r.StartTime == day2).Price);
  }

  [Fact]
  public void WithResolution_Hourly_OnNormalizedHourlySource_IsANoOp()
  {
    var start = new DateTime(2026, 8, 10, 12, 0, 0);
    var source = new PriceList([new PriceTableEntry(start, start.AddHours(1), 42f)]);

    var result = source.NormalizeToQuarterHourly().WithResolution(PriceResolution.Hourly).ToList();

    Assert.Equal(4, result.Count);
    Assert.All(result, r => Assert.Equal(42f, r.Price));
  }

  // ── GetPriceRank / GetPricePercentage regression (hour-bucket bug) ──────

  [Fact]
  public void GetPriceRank_WithQuarterHourEntries_PicksMatchingSlot()
  {
    var start = new DateTime(2026, 8, 10, 12, 0, 0);
    var list = new PriceList([
      new PriceTableEntry(start, start.AddMinutes(15), 40f),           // rank 4
      new PriceTableEntry(start.AddMinutes(15), start.AddMinutes(30), 10f), // rank 1
      new PriceTableEntry(start.AddMinutes(30), start.AddMinutes(45), 30f), // rank 3
      new PriceTableEntry(start.AddMinutes(45), start.AddMinutes(60), 20f), // rank 2
    ]);

    Assert.Equal(4, list.GetPriceRank(start));
    Assert.Equal(1, list.GetPriceRank(start.AddMinutes(15)));
    Assert.Equal(3, list.GetPriceRank(start.AddMinutes(30)));
    Assert.Equal(2, list.GetPriceRank(start.AddMinutes(45)));
  }

  [Fact]
  public void GetPricePercentage_WithQuarterHourEntries_PicksMatchingSlot()
  {
    var start = new DateTime(2026, 8, 10, 12, 0, 0);
    var list = new PriceList([
      new PriceTableEntry(start, start.AddMinutes(15), 0f),
      new PriceTableEntry(start.AddMinutes(15), start.AddMinutes(30), 50f),
      new PriceTableEntry(start.AddMinutes(30), start.AddMinutes(45), 100f),
    ]);

    Assert.Equal(0, list.GetPricePercentage(start));
    Assert.Equal(50, list.GetPricePercentage(start.AddMinutes(15)));
    Assert.Equal(100, list.GetPricePercentage(start.AddMinutes(30)));
  }

  // ── GetBestChargeWindow ──────────────────────────────────────────────────

  /// <summary>
  /// Regression test for a stale-tie-breaking bug: when several consecutive quarter-hour price
  /// entries tie at the same (cheapest) price — the normal case for an hourly-native source
  /// normalized to quarter-hourly, e.g. 13:00-13:45 all at the same price before a genuinely
  /// different price at 14:00 — `OrderBy(Price)` is a stable sort, so ties always resolve to the
  /// EARLIEST entry. `GetBestChargeWindow` used to filter candidates by
  /// `StartTime >= now.Date.AddHours(now.Hour)` (floored to the calendar hour), so once "now"
  /// passed the earliest tied entry's own end time (here, 13:15) but stayed within the same
  /// hour, that already-elapsed 13:00-13:15 entry remained in the candidate set and kept winning
  /// the tie — so the returned window no longer contained "now" at all, and stayed that way for
  /// the rest of the tied price bracket (13:15-13:45), since nothing beats a tied cheapest price
  /// until a genuinely different (pricier) bracket arrives. The caller's `inWindow` check
  /// (`now >= StartTime && now < EndTime`) then fails for the whole rest of that bracket, so the
  /// charge doesn't start until the NEXT, more expensive price bracket, even though several
  /// equally-cheap quarter-hours were still available right now.
  /// </summary>
  [Theory]
  [InlineData(0)]  // now = 13:00 (the earliest tied entry itself) — always worked
  [InlineData(15)] // now = 13:15 — first minute the bug could show
  [InlineData(30)] // now = 13:30
  [InlineData(45)] // now = 13:45 — last minute still in the tied-cheapest bracket
  public void GetBestChargeWindow_TiedCheapestBracket_ReturnsWindowContainingNow(int minutesPastStart)
  {
    var start = new DateTime(2026, 9, 27, 13, 0, 0);
    var list = new PriceList([
      new PriceTableEntry(start,                     start.AddMinutes(15), 0.09396f),
      new PriceTableEntry(start.AddMinutes(15),       start.AddMinutes(30), 0.09396f),
      new PriceTableEntry(start.AddMinutes(30),       start.AddMinutes(45), 0.09396f),
      new PriceTableEntry(start.AddMinutes(45),       start.AddMinutes(60), 0.09396f),
      new PriceTableEntry(start.AddMinutes(60),       start.AddMinutes(75), 0.09612f), // 14:00, genuinely pricier
    ]);
    var now = start.AddMinutes(minutesPastStart);
    var need = new NeedToChargeResult(estimatedSoc: 20, latestChargeTime: start.AddHours(8), needToCharge: true);

    var result = list.GetBestChargeWindow(need, now);

    Assert.True(result.StartTime <= now && result.EndTime > now,
      $"now={now:HH:mm} should fall within the returned window [{result.StartTime:HH:mm}, {result.EndTime:HH:mm}) " +
      $"— a tied-cheapest price bracket must not go stale before it actually ends.");
  }

  /// <summary>
  /// Once "now" reaches a genuinely different (pricier) bracket, GetBestChargeWindow must not
  /// keep reporting an earlier, now-impossible-to-use window either — same property, later slot.
  /// </summary>
  [Fact]
  public void GetBestChargeWindow_PricierBracket_ReturnsWindowContainingNow()
  {
    var start = new DateTime(2026, 9, 27, 13, 0, 0);
    var list = new PriceList([
      new PriceTableEntry(start,               start.AddMinutes(15), 0.09396f),
      new PriceTableEntry(start.AddMinutes(15), start.AddMinutes(30), 0.09396f),
      new PriceTableEntry(start.AddMinutes(30), start.AddMinutes(45), 0.09396f),
      new PriceTableEntry(start.AddMinutes(45), start.AddMinutes(60), 0.09396f),
      new PriceTableEntry(start.AddMinutes(60), start.AddMinutes(75), 0.09612f), // 14:00
    ]);
    var now = start.AddMinutes(60); // 14:00 — first minute of the pricier bracket
    var need = new NeedToChargeResult(estimatedSoc: 20, latestChargeTime: start.AddHours(8), needToCharge: true);

    var result = list.GetBestChargeWindow(need, now);

    Assert.True(result.StartTime <= now && result.EndTime > now,
      $"now={now:HH:mm} should fall within the returned window [{result.StartTime:HH:mm}, {result.EndTime:HH:mm})");
  }
}
