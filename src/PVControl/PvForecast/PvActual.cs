using System;
using System.Collections.Generic;
using System.Linq;
using LinqToDB;
using PVControl;

namespace NetDeamon.apps.PVControl.PvForecast
{
  /// <summary>One logged hour: its PV energy and outside temperature.</summary>
  /// <param name="Hour">Local start of the hour.</param>
  /// <param name="PvWh">PV energy, Wh.</param>
  /// <param name="Temp">Outside temperature, °C.</param>
  public readonly record struct PvHour(DateTime Hour, int? PvWh, double? Temp);

  /// <summary>What a day delivered, from DataLogger's hourly rows.</summary>
  public static class PvActual
  {
    /// <summary>
    /// A day's PV and its mean temperature 09–14 h from its logged hours: only when every hour of the day is there
    /// (DST: 23 or 25). A negative hour counts as 0 — older rows held the inverter counter's midnight reset in the last
    /// hour of the day, when there is no PV.
    /// </summary>
    /// <param name="hours">Logged hours (any range).</param>
    /// <param name="day">The day.</param>
    /// <param name="zone">Time zone (DST).</param>
    /// <returns>PV, Wh, and the temperature; null unless the day is complete.</returns>
    public static (double PvWh, double? TempDay)? OfDay(IEnumerable<PvHour> hours, DateTime day, TimeZoneInfo zone)
    {
      var of = hours.Where(h => h.Hour.Date == day.Date).GroupBy(h => h.Hour).Select(g => g.First()).ToList();
      var expected = Enumerable.Range(0, 24).Select(k => day.Date.AddHours(k)).Where(h => !zone.IsInvalidTime(h)).ToList();
      if (!expected.All(h => of.Any(x => x.Hour == h)) || of.Any(h => h.PvWh is null)) return null;
      var midday = of.Where(h => h.Hour.Hour is >= 9 and <= 14 && h.Temp is not null).Select(h => h.Temp!.Value).ToList();
      return (of.Sum(h => Math.Max(0, h.PvWh!.Value)), midday.Count == 0 ? null : Math.Round(midday.Average(), 1));
    }

    /// <summary>A day from DataLogger's DB.</summary>
    /// <param name="dbPath">DataLogger's DB.</param>
    /// <param name="day">The day.</param>
    /// <returns>See <see cref="OfDay"/>.</returns>
    public static (double PvWh, double? TempDay)? FromDataLogger(string dbPath, DateTime day)
    {
      using var db = new EnergyHistoryDb(SqliteOptions.ForFile(dbPath));
      // Rows are stamped at the hour's end (hh:59:59): a day's hours lie between its start and the next day's first hour.
      var from = day.Date.AddHours(-1);
      var to = day.Date.AddDays(1).AddHours(1);
      var rows = db.Hourlies.Select(h => new { h.Timestamp, h.Pvyield, h.Temperatureoutside }).AsEnumerable()
        .Select(r => new PvHour(r.Timestamp.AsLocal().Floor(60), r.Pvyield, r.Temperatureoutside))
        .Where(h => h.Hour >= from && h.Hour < to);
      return OfDay(rows, day, TimeZoneInfo.Local);
    }
  }
}
