using NetDeamon.apps;
using Xunit;

namespace NetDeamonApps.Tests;
/// <summary>PVControl's own helpers (prediction windows, quarter hours, daily running sums, RunningIntAverage)
/// and TryGetStateValue with its enum.</summary>
public class PVControlExtensionsTests
{
  private static readonly DateTime Day1 = new(2025, 6, 15);
  private static readonly DateTime Day2 = new(2025, 6, 16);

  // ── CombineForecastLists ─────────────────────────────────────────────────

  [Fact]
  public void CombineForecastLists_SumsOverlappingKeysAndAddsNewOnes()
  {
    var list1 = new Dictionary<DateTime, int> { [Day1] = 100, [Day1.AddMinutes(15)] = 200 };
    var list2 = new Dictionary<DateTime, int> { [Day1] = 50, [Day1.AddMinutes(30)] = 300 };

    var result = list1.CombineForecastLists(list2);

    Assert.Equal(150, result[Day1]);                    // overlapping key: summed
    Assert.Equal(200, result[Day1.AddMinutes(15)]);      // only in list1: unchanged
    Assert.Equal(300, result[Day1.AddMinutes(30)]);      // only in list2: added
    Assert.Equal(3, result.Count);
  }

  [Fact]
  public void CombineForecastLists_ResultIsOrderedByKey()
  {
    var list1 = new Dictionary<DateTime, int> { [Day1.AddMinutes(30)] = 3, [Day1] = 1 };
    var list2 = new Dictionary<DateTime, int> { [Day1.AddMinutes(15)] = 2 };

    var result = list1.CombineForecastLists(list2).ToList();

    Assert.Equal([Day1, Day1.AddMinutes(15), Day1.AddMinutes(30)], result.Select(r => r.Key));
  }

  // ── FirstMinOrDefault / FirstMaxOrDefault ────────────────────────────────

  [Fact]
  public void FirstMinOrDefault_ReturnsFirstOccurrenceOfMinimumValue()
  {
    var list = new Dictionary<DateTime, int>
    {
      [Day1] = 10,
      [Day1.AddMinutes(15)] = 5,
      [Day1.AddMinutes(30)] = 5, // tie with the 15-min slot — first occurrence must win
      [Day1.AddMinutes(45)] = 20,
    };

    var result = list.FirstMinOrDefault();

    Assert.Equal(Day1.AddMinutes(15), result.Key);
    Assert.Equal(5, result.Value);
  }

  [Fact]
  public void FirstMaxOrDefault_ReturnsFirstOccurrenceOfMaximumValue()
  {
    var list = new Dictionary<DateTime, int>
    {
      [Day1] = 10,
      [Day1.AddMinutes(15)] = 20,
      [Day1.AddMinutes(30)] = 20, // tie — first occurrence must win
      [Day1.AddMinutes(45)] = 5,
    };

    var result = list.FirstMaxOrDefault();

    Assert.Equal(Day1.AddMinutes(15), result.Key);
    Assert.Equal(20, result.Value);
  }

  [Fact]
  public void FirstMinOrDefault_RespectsStartAndEndRange()
  {
    var list = new Dictionary<DateTime, int>
    {
      [Day1] = 1,                  // global min, but before the range
      [Day1.AddMinutes(15)] = 8,
      [Day1.AddMinutes(30)] = 3,   // min within the range
      [Day1.AddMinutes(45)] = 9,
    };

    var result = list.FirstMinOrDefault(start: Day1.AddMinutes(15), end: Day1.AddMinutes(45));

    Assert.Equal(Day1.AddMinutes(30), result.Key);
    Assert.Equal(3, result.Value);
  }

  // ── FirstUnderOrDefault ───────────────────────────────────────────────────

  [Fact]
  public void FirstUnderOrDefault_ReturnsFirstEntryAtOrBelowThreshold()
  {
    var list = new Dictionary<DateTime, int>
    {
      [Day1] = 90,
      [Day1.AddMinutes(15)] = 80,
      [Day1.AddMinutes(30)] = 40, // first at/below 50
      [Day1.AddMinutes(45)] = 10,
    };

    var result = list.FirstUnderOrDefault(50);

    Assert.Equal(Day1.AddMinutes(30), result.Key);
    Assert.Equal(40, result.Value);
  }

  [Fact]
  public void FirstUnderOrDefault_RespectsStartRange()
  {
    var list = new Dictionary<DateTime, int>
    {
      [Day1] = 10,                // below threshold, but before the range
      [Day1.AddMinutes(15)] = 90,
      [Day1.AddMinutes(30)] = 20, // first at/below 50 within the range
    };

    var result = list.FirstUnderOrDefault(50, start: Day1.AddMinutes(15));

    Assert.Equal(Day1.AddMinutes(30), result.Key);
  }

  [Fact]
  public void FirstUnderOrDefault_ReturnsDefaultWhenNothingMatches()
  {
    var list = new Dictionary<DateTime, int> { [Day1] = 90, [Day1.AddMinutes(15)] = 80 };

    var result = list.FirstUnderOrDefault(50);

    Assert.Equal(default, result.Key);
  }

  // ── GetRunningSumsDaily ───────────────────────────────────────────────────

  [Fact]
  public void GetRunningSumsDaily_AccumulatesWithinADayAndResetsAtDayBoundary()
  {
    var list = new Dictionary<DateTime, int>
    {
      [Day1] = 10,
      [Day1.AddHours(1)] = 20,
      [Day1.AddHours(2)] = 5,
      [Day2] = 7, // new day — must restart, not continue accumulating from Day1's total
      [Day2.AddHours(1)] = 3,
    };

    var result = list.GetRunningSumsDaily();

    Assert.Equal(10, result[Day1]);
    Assert.Equal(30, result[Day1.AddHours(1)]);
    Assert.Equal(35, result[Day1.AddHours(2)]);
    Assert.Equal(7, result[Day2]);
    Assert.Equal(10, result[Day2.AddHours(1)]);
  }

  // ── GetNextQuarterHour ────────────────────────────────────────────────────

  static readonly DateTime Quarter = new(2026, 10, 23, 14, 0, 0);

  [Theory]
  [InlineData(0, 0, 15)]   // on a quarter → the next one
  [InlineData(15, 30, 30)] // seconds into a quarter → the next one
  [InlineData(29, 59, 30)]
  public void NextQuarterHour_StrictlyAfter(int m, int s, int expectedMinute) =>
    Assert.Equal(Quarter.AddMinutes(expectedMinute), Quarter.AddMinutes(m).AddSeconds(s).GetNextQuarterHour());


  [Theory]
  [InlineData(2025, 6, 15, 10, 0, 0, 2025, 6, 15, 10, 15)]   // exactly on a boundary -> advances to the next one
  [InlineData(2025, 6, 15, 10, 0, 30, 2025, 6, 15, 10, 15)]  // seconds within a boundary minute -> still advances
  [InlineData(2025, 6, 15, 10, 1, 0, 2025, 6, 15, 10, 15)]   // just past a boundary
  [InlineData(2025, 6, 15, 10, 14, 0, 2025, 6, 15, 10, 15)]  // just before the next boundary
  [InlineData(2025, 6, 15, 10, 59, 0, 2025, 6, 15, 11, 0)]   // hour rollover
  [InlineData(2025, 6, 15, 23, 59, 0, 2025, 6, 16, 0, 0)]    // day rollover
  public void GetNextQuarterHour_RoundsUpToNextBoundary(
    int y, int mo, int d, int h, int mi, int s,
    int ey, int emo, int ed, int eh, int emi)
  {
    var time = new DateTime(y, mo, d, h, mi, s);
    var expected = new DateTime(ey, emo, ed, eh, emi, 0);

    Assert.Equal(expected, time.GetNextQuarterHour());
  }

  [Fact]
  public void RunningIntAverage_Live20261022_SilentSensorDoesNotKeepOldValues()
  {
    // The carport CT (reports on change only) went 1950 → 170 → 0 W at 14:13:46 and then stayed
    // silent. The EV's 2-min average must become 0 after the window, not stay at ~1.7 kW (which
    // was subtracted from the house load until the next restart).
    var t = new DateTime(2026, 10, 22, 12, 13, 0, DateTimeKind.Utc);
    var avg = new RunningIntAverage(TimeSpan.FromMinutes(2), () => t);
    foreach (var w in new[] { 1950, 1980, 1990, 1760, 170, 132 }) { avg.AddValue(w); t = t.AddSeconds(4); }
    avg.AddValue(0);
    Assert.True(avg.GetAverage() > 1000);

    t = t.AddMinutes(3); // nothing new from the sensor
    Assert.Equal(0, avg.GetAverage()); // only the latest value (the current state) is left
  }

  [Fact]
  public void RunningIntAverage_LatestValueStaysWhenAllExpired()
  {
    var t = new DateTime(2026, 10, 22, 12, 0, 0, DateTimeKind.Utc);
    var avg = new RunningIntAverage(TimeSpan.FromMinutes(1), () => t);
    avg.AddValue(500);
    t = t.AddHours(1);
    Assert.Equal(500, avg.GetAverage());
    Assert.Equal(1, avg.Count);
  }

  [Fact]
  public void TryParseEnum_LoadSchedulingMode() =>
    Assert.True(Extensions.TryParseEnum("PriorityPlus", out LoadSchedulingMode m) && m == LoadSchedulingMode.PriorityPlus);
}
