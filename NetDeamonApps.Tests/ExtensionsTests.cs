using NetDeamon.apps;
using Xunit;

namespace NetDeamonApps.Tests;

/// <summary>
/// Tests for GetUnitMultiplicator's string-parsing overload — the SI-prefix lookup that
/// converts a unit_of_measurement string (kWh, MWh, ct, etc.) to a base-unit multiplier.
/// The (Entity) overload isn't tested here since Entity requires a live IHaContext.
/// </summary>
public class ExtensionsTests
{
  [Theory]
  [InlineData("kWh", 1000f)]
  [InlineData("kW", 1000f)]
  [InlineData("MWh", 1000000f)]
  [InlineData("MW", 1000000f)]
  [InlineData("mWh", 0.001f)]
  [InlineData("mW", 0.001f)]
  [InlineData("ct", 0.01f)]
  [InlineData("ct/kWh", 0.01f)]
  [InlineData("€", 1f)]
  [InlineData("EUR", 1f)]
  [InlineData("W", 1f)]
  [InlineData("Wh", 1f)]
  [InlineData("%", 1f)]
  [InlineData(null, 1f)]
  // Real HA unit strings that merely start with k/M/m without being SI-prefixed W/Wh —
  // must fall through unscaled (1f), not be treated as kilo/mega/milli.
  [InlineData("min", 1f)]
  [InlineData("mbar", 1f)]
  [InlineData("mA", 1f)]
  [InlineData("Mbit/s", 1f)]
  [InlineData("kB/s", 1f)]
  public void GetUnitMultiplicator_ReturnsExpectedMultiplier(string? unit, float expected)
  {
    Assert.Equal(expected, Extensions.GetUnitMultiplicator(unit));
  }

  [Fact]
  public void GetUnitMultiplicator_DistinguishesKiloMegaMilliByCase()
  {
    // Case sensitivity is the whole point of the fix: 'k' (kilo), 'M' (mega) and 'm' (milli)
    // must not collapse into the same bucket just because a naive check lowercases first.
    Assert.Equal(1000f, Extensions.GetUnitMultiplicator("kWh"));
    Assert.Equal(1000000f, Extensions.GetUnitMultiplicator("MWh"));
    Assert.Equal(0.001f, Extensions.GetUnitMultiplicator("mWh"));
  }
}

/// <summary>Tests for Extensions.cs's Dictionary&lt;DateTime, int&gt; time-series helpers — pure
/// data transformations with no HA dependency, used throughout the prediction/simulation pipeline.</summary>
public class DictionaryTimeSeriesExtensionsTests
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

  // ── TryParseToIntList ─────────────────────────────────────────────────────

  [Fact]
  public void TryParseToIntList_ParsesSingleValuesAndRanges()
  {
    bool ok = new List<string> { "3", "5-8", "1" }.TryParseToIntList(out var numbers);

    Assert.True(ok);
    Assert.Equal([3, 5, 6, 7, 8, 1], numbers);
  }

  [Fact]
  public void TryParseToIntList_ReturnsFalseForEmptyList()
  {
    Assert.False(((List<string>)[]).TryParseToIntList(out var numbers));
    Assert.Empty(numbers);
  }

  [Fact]
  public void TryParseToIntList_ReturnsFalseForNullList()
  {
    List<string>? list = null;
    Assert.False(list!.TryParseToIntList(out var numbers));
    Assert.Empty(numbers);
  }

  [Fact]
  public void TryParseToIntList_SkipsMalformedEntriesWithoutFailing()
  {
    bool ok = new List<string> { "3", "not-a-number", "5" }.TryParseToIntList(out var numbers);

    Assert.True(ok);
    Assert.Equal([3, 5], numbers);
  }
}
