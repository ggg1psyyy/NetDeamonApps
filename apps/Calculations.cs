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
    /// <returns>Energy in Wh (integer division).</returns>
    public static int SlotWh(int powerW) => powerW / 4;

    /// <summary>Mean power of a 15-min slot.</summary>
    /// <param name="wh">Energy of the slot in Wh.</param>
    /// <returns>Mean power in W.</returns>
    public static int SlotW(int wh) => wh * 4;
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
