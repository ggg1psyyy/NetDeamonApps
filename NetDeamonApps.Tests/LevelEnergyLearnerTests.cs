using NetDeamon.apps.PVControl.Managers;
using Xunit;

namespace NetDeamonApps.Tests;

/// <summary>
/// Learning a load's energy per level unit (grid-side kWh per % EV SoC) and its typical power.
/// The HA wiring (sensor subscriptions, persisting to the number entities) needs live HA; the
/// learning itself is pure and tested here.
/// </summary>
public class LevelEnergyLearnerTests
{
  [Fact]
  public void Measurement_StartsAtFirstLevelStep_AndNeedsThreeLevels()
  {
    var l = new LevelEnergyLearner();
    Assert.Null(l.Observe(18, 10.0f, 0.6f)); // mid-step: don't start here
    Assert.Null(l.Observe(18, 10.3f, 0.6f));
    Assert.Null(l.Observe(19, 10.5f, 0.6f)); // first step: measurement starts (19 %, 10.5 kWh)
    Assert.Null(l.Observe(21, 11.9f, 0.6f)); // only 2 levels
    var est = l.Observe(22, 12.6f, 0.6f);    // 3 levels, 2.1 kWh → 0.7 kWh/%

    Assert.NotNull(est);
    Assert.Equal(LevelEnergyLearner.Blend(0.6f, 2.1f, 3), est!.Value, 4);
    Assert.InRange(est.Value, 0.6f, 0.7f);
  }

  [Fact]
  public void LongSession_MovesMostOfTheWayToMeasuredValue()
  {
    // 40 levels at 0.7 kWh/% with a stored 0.6: (0.6·20 + 28) / 60 ≈ 0.667
    Assert.Equal(0.6667f, LevelEnergyLearner.Blend(0.6f, 28f, 40), 3);
  }

  [Fact]
  public void Estimate_BlendsTheValueStoredAtMeasurementStart_NotTheLatest()
  {
    var l = new LevelEnergyLearner();
    l.Observe(18, 0f, 0.6f);
    l.Observe(19, 0f, 0.6f);
    var first = l.Observe(22, 2.1f, 0.6f);
    // The caller persists each estimate; feeding it back as "stored" must not compound.
    var again = l.Observe(22, 2.1f, first!.Value);

    Assert.Equal(first.Value, again!.Value, 5);
  }

  [Fact]
  public void LevelDrop_RestartsMeasurement()
  {
    var l = new LevelEnergyLearner();
    l.Observe(18, 0f, 0.6f);
    l.Observe(19, 0f, 0.6f);
    Assert.Null(l.Observe(17, 1f, 0.6f)); // e.g. driven since — start over
    Assert.Null(l.Observe(21, 3f, 0.6f)); // first step after restart
    Assert.Null(l.Observe(23, 4.4f, 0.6f));
    Assert.NotNull(l.Observe(24, 5.1f, 0.6f));
  }

  [Fact]
  public void EnergyCounterDrop_RestartsMeasurement()
  {
    var l = new LevelEnergyLearner();
    l.Observe(18, 5f, 0.6f);
    l.Observe(19, 5f, 0.6f);
    Assert.Null(l.Observe(22, 1f, 0.6f));
  }

  [Fact]
  public void Reset_EndsMeasurement()
  {
    var l = new LevelEnergyLearner();
    l.Observe(18, 0f, 0.6f);
    l.Observe(19, 0f, 0.6f);
    l.Reset();
    Assert.Null(l.Observe(23, 3f, 0.6f)); // new run: this is only its first observation
  }

  [Theory]
  [InlineData(1800, 2100, 0, 1800)]     // no time passed → unchanged
  [InlineData(1800, 2100, 600, 1990)]   // one time constant → ~63 % of the way
  [InlineData(1800, 2100, 6000, 2100)]  // long gap → measured value
  public void LearnPower_TimeBasedAverage(int stored, int measured, double seconds, int expected)
  {
    Assert.InRange(SchedulableLoadRuntime.LearnPower(stored, measured, seconds), expected - 1, expected + 1);
  }
}
