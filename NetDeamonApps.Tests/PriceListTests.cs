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
}
