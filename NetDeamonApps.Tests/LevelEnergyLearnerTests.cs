using NetDeamon.apps.PVControl.Managers;
using System;
using System.Collections.Generic;
using System.Linq;
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

  // ── ChargePowerLearner ─────────────────────────────────────────────────────────────────

  static readonly DateTime T0 = new(2026, 10, 18, 18, 0, 0);

  /// <summary>Feeds readings every <paramref name="stepSeconds"/> like the CT clamp and returns the learned value.</summary>
  static double Run(ChargePowerLearner l, double stored, IEnumerable<double> readings, DateTime start, int stepSeconds = 4)
  {
    var t = start;
    foreach (var w in readings)
    {
      if (l.Observe(t, w, 500, stored) is { } v) stored = v;
      t = t.AddSeconds(stepSeconds);
    }
    return stored;
  }

  [Fact]
  public void Power_ConvergesToMeasured_DespiteTinySteps()
  {
    // 1 h of 1845 W readings every 4 s: each step moves < 1 W. The old int-rounded average froze
    // ~75 W short (live: stuck at ~1820 while the CT showed ~1845).
    var learned = Run(new ChargePowerLearner(), 1800, Enumerable.Repeat(1845.0, 900), T0);
    Assert.InRange(learned, 1840, 1845);
  }

  [Fact]
  public void Power_IgnoresWarmUpAndStops()
  {
    var l = new ChargePowerLearner();
    // Ramp-up readings in the first 2 minutes and a stop (0 W) don't count.
    var readings = new[] { 79.0, 1514, 1662, 1720 }
      .Concat(Enumerable.Repeat(1850.0, 10))   // still within warm-up (14 readings · 4 s < 2 min)
      .Concat(new[] { 0.0, 0.0 });              // stop
    Assert.Equal(1800, Run(l, 1800, readings, T0));
  }

  [Fact]
  public void Power_AfterStop_NeedsWarmUpAgain()
  {
    var l = new ChargePowerLearner();
    var learned = Run(l, 1800, Enumerable.Repeat(1850.0, 60), T0);           // 4 min: learns a bit
    Assert.True(learned > 1800);
    var afterStop = Run(l, learned, new[] { 0.0 }.Concat(Enumerable.Repeat(900.0, 20)), T0.AddMinutes(5)); // 80 s after restart
    Assert.Equal(learned, afterStop);
  }
}
