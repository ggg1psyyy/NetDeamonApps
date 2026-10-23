using System;

namespace NetDeamon.apps.PVControl.Managers
{
  /// <summary>
  /// Learns a load's energy per level unit (e.g. grid-side kWh per % EV SoC, charger losses
  /// included) from measured energy and level while it runs. The level sensor only reports
  /// whole steps, so a measurement runs from one observed level step to a later one —
  /// starting mid-step would skew short measurements badly — and is used once it spans at least
  /// <see cref="MinLevelDelta"/>. The estimate blends the value stored when the measurement
  /// began, weighted as if it had been measured over <see cref="PriorWeightLevels"/>, with the
  /// measurement itself, so it updates live while running and one longer session moves it most
  /// of the way to a new hardware's value.
  /// </summary>
  public sealed class LevelEnergyLearner
  {
    public const float MinLevelDelta = 3f;
    public const float PriorWeightLevels = 20f;

    private float? _firstLevel;
    private (float Level, float EnergyKwh, float Stored)? _start;

    /// <summary>
    /// Feed one observation while the load is actively running. <paramref name="cumulativeEnergyKwh"/>
    /// must be a monotonic counter. Returns a new estimate once the measurement spans
    /// <see cref="MinLevelDelta"/>, otherwise null.
    /// </summary>
    public float? Observe(float level, float cumulativeEnergyKwh, float stored)
    {
      if (_firstLevel is null || level < _firstLevel || (_start is { } s0 && (level < s0.Level || cumulativeEnergyKwh < s0.EnergyKwh)))
      {
        Reset();
        _firstLevel = level;
        return null;
      }

      if (_start is null)
      {
        if (level > _firstLevel) _start = (level, cumulativeEnergyKwh, stored);
        return null;
      }

      var s = _start.Value;
      float delta = level - s.Level;
      return delta >= MinLevelDelta ? Blend(s.Stored, cumulativeEnergyKwh - s.EnergyKwh, delta) : null;
    }

    /// <summary>Ends the current measurement (load stopped). The next run starts a new one.</summary>
    public void Reset()
    {
      _firstLevel = null;
      _start = null;
    }

    /// <summary>Weighted mean of the stored value (as if over PriorWeightLevels) and the measurement (per level, over levelDelta).</summary>
    public static float Blend(float stored, float measuredKwh, float levelDelta) =>
      (float)Mean.Weighted(stored, PriorWeightLevels, measuredKwh / levelDelta, levelDelta);
  }

  /// <summary>
  /// Learns a load's typical power while it actually runs: only instantaneous readings above the
  /// active threshold count, and only after it has drawn continuously for <see cref="WarmUp"/>
  /// (skips ramp-up and short stops). Exponential moving average with time constant
  /// <see cref="TauSeconds"/>, so it doesn't depend on how often the sensor reports. Kept as a
  /// double: rounding each 4-second step to whole watts would freeze it ~75 W short.
  /// </summary>
  public sealed class ChargePowerLearner
  {
    public static readonly TimeSpan WarmUp = TimeSpan.FromMinutes(2);
    public const double TauSeconds = 600;

    private DateTime? _drawingSince;
    private DateTime? _last;

    /// <summary>Returns the updated typical power, or null when this reading doesn't count.</summary>
    public double? Observe(DateTime now, double measuredW, int minActiveW, double stored)
    {
      if (measuredW <= minActiveW)
      {
        _drawingSince = null;
        _last = null;
        return null;
      }
      _drawingSince ??= now;
      if (now - _drawingSince.Value < WarmUp) return null;

      var last = _last;
      _last = now;
      if (last is null) return null;
      return stored + (measuredW - stored) * (1 - Math.Exp(-(now - last.Value).TotalSeconds / TauSeconds));
    }
  }
}
