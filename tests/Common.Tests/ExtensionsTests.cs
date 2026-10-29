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

/// <summary>Parsing helpers of Extensions.cs: number lists ("3", "5-8") and enums by name.</summary>
public class ParseExtensionsTests
{
  public enum Mode { automatic, force_charge, grid_only }

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

  // TryGetStateValue's enum case: any enum by name (live: PVControl's InverterModes, LoadSchedulingMode).
  [Theory]
  [InlineData("force_charge", true, Mode.force_charge)]
  [InlineData("grid_only", true, Mode.grid_only)]
  [InlineData("Force_Charge", false, Mode.automatic)]   // case-sensitive, as before
  [InlineData("7", false, Mode.automatic)]              // a number isn't a mode name
  [InlineData("reset", false, Mode.automatic)]          // removed mode
  public void TryParseEnum_ByName(string state, bool ok, Mode expected)
  {
    Assert.Equal(ok, Extensions.TryParseEnum(state, out Mode mode));
    Assert.Equal(expected, mode);
  }
}
