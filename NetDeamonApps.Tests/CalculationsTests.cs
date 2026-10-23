using System;
using NetDeamon.apps;
using NetDeamon.apps.PVControl.Managers;
using Xunit;

namespace NetDeamonApps.Tests;

/// <summary>
/// The basic calculations every app shares (apps/Calculations.cs) — each exists once, the callers
/// differ only in their parameters. The cases pin the results of the copies they replaced.
/// </summary>
public class CalculationsTests
{
  static readonly DateTime T = new(2026, 10, 23, 14, 0, 0);

  // ── TimeRange ────────────────────────────────────────────────────────────────────────

  [Fact]
  public void Overlap_PartialNoneAndContained()
  {
    Assert.Equal(TimeSpan.FromMinutes(5), TimeRange.Overlap(T, T.AddMinutes(15), T.AddMinutes(10), T.AddHours(1)));
    Assert.Equal(TimeSpan.Zero, TimeRange.Overlap(T, T.AddMinutes(15), T.AddMinutes(15), T.AddHours(1)));
    Assert.Equal(TimeSpan.FromMinutes(15), TimeRange.Overlap(T, T.AddMinutes(15), T.AddHours(-1), T.AddHours(1)));
  }

  [Fact]
  public void SlotFraction_RestOfTheRunningSlot_AndThePartAlreadyOver()
  {
    var now = T.AddMinutes(12);
    Assert.Equal(0.2, TimeRange.SlotFraction(T, now, DateTime.MaxValue), 6); // simulation: the rest
    Assert.Equal(0.8, TimeRange.SlotFraction(T, T.Date, now), 6);            // PV day ratio: the part over
    Assert.Equal(1.0, TimeRange.SlotFraction(T.AddMinutes(15), now, DateTime.MaxValue), 6);
  }

  [Theory]
  [InlineData(14, 0, 0, 15, 14, 0)]
  [InlineData(14, 29, 59, 15, 14, 15)]
  [InlineData(14, 59, 30, 60, 14, 0)]
  public void Floor_QuarterAndHour(int h, int m, int s, int minutes, int eh, int em) =>
    Assert.Equal(new DateTime(2026, 10, 23, eh, em, 0), new DateTime(2026, 10, 23, h, m, s).Floor(minutes));

  [Theory]
  [InlineData(0, 0, 15)]   // on a quarter → the next one
  [InlineData(15, 30, 30)] // seconds into a quarter → the next one
  [InlineData(29, 59, 30)]
  public void NextQuarterHour_StrictlyAfter(int m, int s, int expectedMinute) =>
    Assert.Equal(T.AddMinutes(expectedMinute), T.AddMinutes(m).AddSeconds(s).GetNextQuarterHour());

  // ── Energy, SoC ──────────────────────────────────────────────────────────────────────

  [Fact]
  public void Energy_PowerTimeAndSlots()
  {
    Assert.Equal(925, Energy.Wh(1850, TimeSpan.FromMinutes(30)), 6);
    Assert.Equal(97, Energy.Minutes(3000, 1850));       // was EnergyNeededWh * 60 / power
    Assert.Equal(0, Energy.Minutes(3000, 0));
    Assert.Equal(462, Energy.SlotWh(1850));
    Assert.Equal(1848, Energy.SlotW(462));
  }

  [Fact]
  public void Soc_WhAndChargeMinutes()
  {
    Assert.Equal(2560, Soc.ToWh(25, 10240));
    Assert.Equal(25, Soc.FromWh(2600, 10240));
    // 20 → 80 % of 10.24 kWh × 0.95 at 3680 W (16 A × 230 V): 6144 × 0.95 / 3680 h = 95 min
    Assert.Equal(95, Soc.ChargeMinutes(20, 80, 10240, 0.95, 3680));
    // Discharging: falling SoC at negative power gives the time until the lower SoC.
    Assert.Equal(95, Soc.ChargeMinutes(80, 20, 10240, 0.95, -3680));
  }

  // ── Means ────────────────────────────────────────────────────────────────────────────

  [Fact]
  public void WeightedMean_SameAsTheBlendsItReplaced()
  {
    // Battery average cost: 5 kWh at 0.10 + 1 kWh at 0.40
    Assert.Equal(0.15, Mean.Weighted(0.10, 5, 0.40, 1), 6);
    // Energy per level: (stored × prior + measured) / (prior + levels)
    float stored = 0.62f, measured = 2.1f, levels = 3f;
    Assert.Equal((stored * LevelEnergyLearner.PriorWeightLevels + measured) / (LevelEnergyLearner.PriorWeightLevels + levels),
      LevelEnergyLearner.Blend(stored, measured, levels), 5);
  }

  [Fact]
  public void StepFunction_PiecesClippedToThePeriod()
  {
    (DateTime, string)[] changes = [(T.AddMinutes(-10), "a"), (T.AddMinutes(20), "b"), (T.AddMinutes(90), "c")];
    var pieces = StepFunction.Pieces(changes, T, T.AddHours(1));
    Assert.Equal([("a", TimeSpan.FromMinutes(20)), ("b", TimeSpan.FromMinutes(40))], pieces);
  }
}
