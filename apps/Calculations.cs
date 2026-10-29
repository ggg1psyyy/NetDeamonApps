using System;
using System.Collections.Generic;

namespace NetDeamon.apps
{
  // The basic calculations used all over the apps — each exists exactly once (CLAUDE.md: same
  // calculation with different inputs = one function).

  /// <summary>Time ranges [start, end) and 15-min slots.</summary>
  public static class TimeRange
  {
    /// <summary>Length of a simulation/price slot in minutes.</summary>
    public const int SlotMinutes = 15;
    /// <summary>Length of a simulation/price slot.</summary>
    public static readonly TimeSpan Slot = TimeSpan.FromMinutes(SlotMinutes);

    /// <summary>Whether a time lies in a range.</summary>
    /// <param name="start">Range start (inclusive).</param>
    /// <param name="end">Range end (exclusive).</param>
    /// <param name="t">Time to test.</param>
    /// <returns>True if <paramref name="start"/> ≤ <paramref name="t"/> &lt; <paramref name="end"/>.</returns>
    public static bool Contains(DateTime start, DateTime end, DateTime t) => start <= t && t < end;

    /// <summary>Overlap of two ranges.</summary>
    /// <param name="aStart">First range start (inclusive).</param>
    /// <param name="aEnd">First range end (exclusive).</param>
    /// <param name="bStart">Second range start (inclusive).</param>
    /// <param name="bEnd">Second range end (exclusive).</param>
    /// <returns>Length of the overlap; zero if the ranges don't overlap.</returns>
    public static TimeSpan Overlap(DateTime aStart, DateTime aEnd, DateTime bStart, DateTime bEnd) =>
      new(Math.Max(0, Math.Min(aEnd.Ticks, bEnd.Ticks) - Math.Max(aStart.Ticks, bStart.Ticks)));

    /// <summary>Share of a slot that lies in a range, e.g. the rest of the running slot (from = now) or its past part (to = now).</summary>
    /// <param name="slotStart">Start of the 15-min slot.</param>
    /// <param name="from">Range start (inclusive).</param>
    /// <param name="to">Range end (exclusive).</param>
    /// <returns>Fraction 0…1 of the slot inside the range.</returns>
    public static double SlotFraction(DateTime slotStart, DateTime from, DateTime to) =>
      Overlap(slotStart, slotStart + Slot, from, to) / Slot;

    /// <summary>Floors a time to a multiple of minutes within its hour.</summary>
    /// <param name="time">Time to floor.</param>
    /// <param name="minutes">Step, a divisor of 60 (15 = quarter-hour, 60 = full hour).</param>
    /// <returns>The floored time; seconds dropped, <see cref="DateTimeKind.Unspecified"/>.</returns>
    public static DateTime Floor(this DateTime time, int minutes) =>
      new DateTime(time.Year, time.Month, time.Day, time.Hour, 0, 0).AddMinutes(time.Minute - time.Minute % minutes);

    /// <summary>The next moment the clock shows a time of day.</summary>
    /// <param name="time">Time of day.</param>
    /// <param name="now">Current time.</param>
    /// <returns>Today at <paramref name="time"/> if still ahead, else tomorrow.</returns>
    public static DateTime NextOccurrence(TimeOnly time, DateTime now)
    {
      var today = now.Date + time.ToTimeSpan();
      return today > now ? today : today.AddDays(1);
    }

    /// <summary>Rounds a time up to a multiple of minutes within its hour.</summary>
    /// <param name="time">Time to round.</param>
    /// <param name="minutes">Step, a divisor of 60.</param>
    /// <returns><paramref name="time"/> if already on a step (no seconds), else the next step.</returns>
    public static DateTime Ceiling(this DateTime time, int minutes)
    {
      var floor = time.Floor(minutes);
      return floor < time ? floor.AddMinutes(minutes) : floor;
    }
  }

  /// <summary>Power (W), energy (Wh) and time.</summary>
  public static class Energy
  {
    /// <summary>Energy of a constant power over a duration.</summary>
    /// <param name="powerW">Power in W.</param>
    /// <param name="duration">How long the power flows.</param>
    /// <returns>Energy in Wh.</returns>
    public static double Wh(double powerW, TimeSpan duration) => powerW * duration.TotalHours;

    /// <summary>Time a power needs for an energy.</summary>
    /// <param name="wh">Energy in Wh.</param>
    /// <param name="powerW">Power in W.</param>
    /// <returns>Whole minutes (truncated); 0 if <paramref name="powerW"/> is 0.</returns>
    public static int Minutes(double wh, double powerW) => powerW == 0 ? 0 : (int)(wh * 60 / powerW);

    /// <summary>Energy of a 15-min slot at a constant power.</summary>
    /// <param name="powerW">Mean power in W (also: an hour's energy in Wh).</param>
    /// <returns>Energy in Wh, rounded.</returns>
    public static int SlotWh(double powerW) => (int)Math.Round(powerW / 4);

    /// <summary>Mean power of a 15-min slot.</summary>
    /// <param name="wh">Energy of the slot in Wh.</param>
    /// <returns>Mean power in W.</returns>
    public static int SlotW(int wh) => wh * 4;

    /// <summary>Mean power of a 15-min slot (any unit: kWh gives kW).</summary>
    /// <param name="wh">Energy of the slot.</param>
    /// <returns>Mean power.</returns>
    public static double SlotW(double wh) => wh * 60 / TimeRange.SlotMinutes;

    /// <summary>
    /// Energy counted by a meter over its readings: the sum of its increases. A drop below 90 % of the
    /// last counted reading is a reset (counts from 0) unless the meter returns near its level within
    /// <see cref="GlitchLookahead"/> readings (a glitch, skipped); smaller drops are noise.
    /// </summary>
    /// <param name="readings">Meter readings in time order.</param>
    /// <returns>The increase; works for daily-reset and running-total meters.</returns>
    public static float CounterIncrease(IReadOnlyList<float> readings)
    {
      if (readings.Count == 0) return 0;
      float total = 0;
      float reference = readings[0]; // last reading that counted — skipped readings never become the base
      for (int i = 1; i < readings.Count; i++)
      {
        float current = readings[i];
        if (current >= reference) { total += current - reference; reference = current; }
        else if (current < reference * 0.9f && !ReturnsTo(readings, i + 1, reference * 0.9f))
        { total += current; reference = current; }
      }
      return total;
    }

    /// <summary>Readings a meter has to get back to its level in for a drop to be a glitch, not a reset (<see cref="CounterIncrease"/>).</summary>
    public const int GlitchLookahead = 5;

    /// <summary>Whether a meter gets back to a level within <see cref="GlitchLookahead"/> readings.</summary>
    /// <param name="readings">Meter readings.</param>
    /// <param name="from">Index to look from.</param>
    /// <param name="level">Level to reach.</param>
    private static bool ReturnsTo(IReadOnlyList<float> readings, int from, float level)
    {
      for (int i = from; i < Math.Min(readings.Count, from + GlitchLookahead); i++)
        if (readings[i] >= level) return true;
      return false;
    }
  }

  /// <summary>Battery state of charge (%) and energy (Wh).</summary>
  public static class Soc
  {
    /// <summary>Energy of a state of charge.</summary>
    /// <param name="socPercent">State of charge (or a difference of two) in %.</param>
    /// <param name="capacityWh">Battery capacity in Wh.</param>
    /// <returns>Energy in Wh (integer division).</returns>
    public static int ToWh(int socPercent, int capacityWh) => socPercent * capacityWh / 100;

    /// <summary>State of charge of an energy.</summary>
    /// <param name="wh">Energy in the battery in Wh.</param>
    /// <param name="capacityWh">Battery capacity in Wh.</param>
    /// <returns>State of charge in % (integer division).</returns>
    public static int FromWh(int wh, int capacityWh) => wh * 100 / capacityWh;

    /// <summary>Time to charge (or discharge) between two states of charge: (end − start) × capacity × efficiency / power.</summary>
    /// <param name="startSoc">Start state of charge in %.</param>
    /// <param name="endSoc">End state of charge in %; below <paramref name="startSoc"/> for discharging.</param>
    /// <param name="capacityWh">Battery capacity in Wh.</param>
    /// <param name="efficiency">Inverter efficiency 0…1.</param>
    /// <param name="powerW">Charge power in W; negative for discharging.</param>
    /// <returns>Whole minutes; 0 without power.</returns>
    public static int ChargeMinutes(int startSoc, int endSoc, int capacityWh, double efficiency, double powerW) =>
      Energy.Minutes((endSoc - startSoc) / 100.0 * capacityWh * efficiency, powerW);
  }

  /// <summary>Means.</summary>
  public static class Mean
  {
    /// <summary>Weighted mean of two values.</summary>
    /// <param name="a">First value.</param>
    /// <param name="weightA">Weight of <paramref name="a"/>.</param>
    /// <param name="b">Second value.</param>
    /// <param name="weightB">Weight of <paramref name="b"/>.</param>
    /// <returns>(a·weightA + b·weightB) / (weightA + weightB).</returns>
    public static double Weighted(double a, double weightA, double b, double weightB) =>
      (a * weightA + b * weightB) / (weightA + weightB);

    /// <summary>Weight of a day by its age: exp(−|age in days| / scaleDays).</summary>
    /// <param name="day">Day of the value (time ignored).</param>
    /// <param name="today">Reference day (time ignored).</param>
    /// <param name="scaleDays">Age at which the weight has fallen to 1/e.</param>
    /// <returns>Weight 0…1.</returns>
    public static double AgeWeight(DateTime day, DateTime today, double scaleDays) =>
      Math.Exp(-Math.Abs((day.Date - today.Date).Days) / scaleDays);

    /// <summary>Age-weighted mean per hour of day (<see cref="AgeWeight"/>).</summary>
    /// <param name="values">(local time, value); NaN values are skipped.</param>
    /// <param name="today">Reference day for the age.</param>
    /// <param name="scaleDays">Age weighting, see <see cref="AgeWeight"/>.</param>
    /// <returns>Mean per hour 0–23; 0 for hours without values.</returns>
    public static double[] HourOfDayProfile(IEnumerable<(DateTime Local, double Value)> values, DateTime today, double scaleDays)
    {
      var sums = new double[24];
      var weights = new double[24];
      foreach (var (local, value) in values)
      {
        if (double.IsNaN(value)) continue;
        double w = AgeWeight(local, today, scaleDays);
        sums[local.Hour] += value * w;
        weights[local.Hour] += w;
      }
      for (int h = 0; h < 24; h++)
        sums[h] = weights[h] > 0 ? sums[h] / weights[h] : 0;
      return sums;
    }
  }

  /// <summary>Linear regression.</summary>
  public static class Regression
  {
    /// <summary>(Weighted) least squares y = slope·x + intercept.</summary>
    /// <param name="xs">x values.</param>
    /// <param name="ys">y values.</param>
    /// <param name="weights">Weight per point; null = all 1.</param>
    /// <returns>Slope and intercept; null without points or without spread in x.</returns>
    public static (double Slope, double Intercept)? LinearFit(IReadOnlyList<double> xs, IReadOnlyList<double> ys, IReadOnlyList<double>? weights = null)
    {
      if (Means(xs, ys, weights) is not { } m) return null;
      double sxx = 0, sxy = 0;
      for (int i = 0; i < xs.Count; i++)
      {
        double w = weights?[i] ?? 1;
        sxx += w * (xs[i] - m.X) * (xs[i] - m.X);
        sxy += w * (xs[i] - m.X) * (ys[i] - m.Y);
      }
      if (sxx < 1e-9) return null;
      double slope = sxy / sxx;
      return (slope, m.Y - slope * m.X);
    }

    /// <summary>Intercept of the line with a given slope through the (weighted) means — e.g. after limiting the fitted slope.</summary>
    /// <param name="xs">x values.</param>
    /// <param name="ys">y values.</param>
    /// <param name="weights">Weight per point; null = all 1.</param>
    /// <param name="slope">The slope.</param>
    /// <returns>mean(y) − slope·mean(x); 0 without points.</returns>
    public static double InterceptFor(IReadOnlyList<double> xs, IReadOnlyList<double> ys, IReadOnlyList<double>? weights, double slope) =>
      Means(xs, ys, weights) is { } m ? m.Y - slope * m.X : 0;

    /// <summary>(Weighted) means of x and y; null without points or weight.</summary>
    private static (double X, double Y)? Means(IReadOnlyList<double> xs, IReadOnlyList<double> ys, IReadOnlyList<double>? weights)
    {
      double sw = 0, sx = 0, sy = 0;
      for (int i = 0; i < xs.Count; i++)
      {
        double w = weights?[i] ?? 1;
        sw += w; sx += w * xs[i]; sy += w * ys[i];
      }
      return sw > 0 ? (sx / sw, sy / sw) : null;
    }
  }

  /// <summary>Values that hold from each change until the next one (the HA history of a sensor).</summary>
  public static class StepFunction
  {
    /// <summary>Each value with how long it held within a period.</summary>
    /// <typeparam name="T">Type of the value (state string, number, …).</typeparam>
    /// <param name="changes">(time, value) in time order; the first one may lie before <paramref name="from"/>.</param>
    /// <param name="from">Period start (inclusive).</param>
    /// <param name="to">Period end (exclusive); the last value holds until here.</param>
    /// <returns>(value, held) per change, clipped to the period; changes without time in it are skipped.</returns>
    public static IEnumerable<(T Value, TimeSpan Held)> Pieces<T>(IReadOnlyList<(DateTime Time, T Value)> changes, DateTime from, DateTime to)
    {
      for (int i = 0; i < changes.Count; i++)
      {
        var start = changes[i].Time < from ? from : changes[i].Time;
        var end = i + 1 < changes.Count && changes[i + 1].Time < to ? changes[i + 1].Time : to;
        if (end > start) yield return (changes[i].Value, end - start);
      }
    }

    /// <summary>The value held at a time.</summary>
    /// <param name="changes">(time, value) in time order.</param>
    /// <param name="time">Time to look up.</param>
    /// <returns>The last value at or before <paramref name="time"/>; the first one if none is before; NaN without values.</returns>
    public static double ValueAt(IReadOnlyList<(DateTime Time, double Value)> changes, DateTime time)
    {
      double v = changes.Count > 0 ? changes[0].Value : double.NaN;
      foreach (var (t, x) in changes)
      {
        if (t > time) break;
        v = x;
      }
      return v;
    }

    /// <summary>Time-weighted mean of a value over a period.</summary>
    /// <param name="changes">(time, value) in time order; null = unknown, not counted.</param>
    /// <param name="from">Period start (inclusive).</param>
    /// <param name="to">Period end (exclusive).</param>
    /// <returns>Mean over the part of the period with a value; null if there is none.</returns>
    public static double? TimeWeightedAverage(IReadOnlyList<(DateTime Time, double? Value)> changes, DateTime from, DateTime to)
    {
      double sum = 0, weight = 0;
      foreach (var (value, held) in Pieces(changes, from, to))
        if (value is { } v)
        {
          sum += v * held.TotalMinutes;
          weight += held.TotalMinutes;
        }
      return weight > 0 ? sum / weight : null;
    }
  }
}
