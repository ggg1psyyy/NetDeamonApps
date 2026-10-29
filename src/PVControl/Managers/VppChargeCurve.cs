using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace NetDeamon.apps.PVControl.Managers
{
  /// <summary>
  /// The battery charge power a remote-controlled (VPP) grid charge reaches, by SoC. The inverter
  /// limits VPP charging below its hardware maximum, falling with the SoC (SolaX G4, measured
  /// 2026-10-26/27: 5.07 kW at 21 %, 3.33 kW at 63 %; Manual Force Charge ~7.45 kW up to 89 %, BMS
  /// limit 35 A). Learned from VPP charges that run below the command: per <see cref="BucketPct"/>-%
  /// SoC bucket a moving average of the measured power, a line fitted through the buckets once they
  /// span <see cref="MinSpanPct"/>; until then the configured default line.
  /// </summary>
  public sealed class VppChargeCurve(double defaultAt0W, double defaultSlopeWPerPct)
  {
    /// <summary>Width of a SoC bucket, %.</summary>
    public const int BucketPct = 5;
    /// <summary>Weight of a new reading in its bucket's moving average.</summary>
    public const double Alpha = 0.05;
    /// <summary>Readings a bucket needs before it counts for the fit.</summary>
    public const int MinReadings = 8;
    /// <summary>SoC span the counted buckets need for a fit, %.</summary>
    public const int MinSpanPct = 15;
    /// <summary>Lowest power the curve returns, W (it never plans VPP charging as impossible).</summary>
    public const int MinW = 500;

    private readonly Dictionary<int, (double W, int N)> _buckets = new();

    /// <summary>Adds a reading of a VPP charge that runs below its command (the inverter's limit).</summary>
    /// <param name="socPercent">SoC, %.</param>
    /// <param name="batteryW">Measured battery charge power, W.</param>
    public void Observe(int socPercent, double batteryW)
    {
      if (socPercent is < 0 or > 100 || batteryW <= 0) return;
      int b = Bucket(socPercent);
      _buckets[b] = _buckets.TryGetValue(b, out var v) ? (v.W + Alpha * (batteryW - v.W), v.N + 1) : (batteryW, 1);
    }

    /// <summary>The curve in use: fitted when the buckets allow it, else the default.</summary>
    public (double At0W, double SlopeWPerPct, bool Learned) Line
    {
      get
      {
        var used = _buckets.Where(b => b.Value.N >= MinReadings).OrderBy(b => b.Key).ToList();
        if (used.Count >= 2 && used[^1].Key - used[0].Key >= MinSpanPct
            && Regression.LinearFit(used.Select(b => b.Key + BucketPct / 2.0).ToList(), used.Select(b => b.Value.W).ToList()) is { } fit)
          return (fit.Intercept, fit.Slope, true);
        return (defaultAt0W, defaultSlopeWPerPct, false);
      }
    }

    /// <summary>VPP charge power at a SoC.</summary>
    /// <param name="socPercent">SoC, %.</param>
    /// <returns>W, at least <see cref="MinW"/>.</returns>
    public int MaxW(int socPercent)
    {
      var (at0, slope, _) = Line;
      return Math.Max(MinW, (int)Math.Round(at0 + slope * Math.Clamp(socPercent, 0, 100)));
    }

    /// <summary>The buckets for persisting ("bucket:W:N" joined by ';').</summary>
    public string Serialize() => string.Join(';', _buckets.OrderBy(b => b.Key)
      .Select(b => string.Create(CultureInfo.InvariantCulture, $"{b.Key}:{b.Value.W:F0}:{b.Value.N}")));

    /// <summary>Restores buckets written by <see cref="Serialize"/>; unreadable entries are skipped.</summary>
    /// <param name="text">Serialized buckets.</param>
    public void Restore(string? text)
    {
      foreach (var part in (text ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
      {
        var f = part.Split(':');
        if (f.Length == 3 && int.TryParse(f[0], CultureInfo.InvariantCulture, out int b)
            && double.TryParse(f[1], CultureInfo.InvariantCulture, out double w) && int.TryParse(f[2], CultureInfo.InvariantCulture, out int n))
          _buckets[b] = (w, n);
      }
    }

    private static int Bucket(int socPercent) => Math.Min(socPercent, 99) / BucketPct * BucketPct;
  }
}
