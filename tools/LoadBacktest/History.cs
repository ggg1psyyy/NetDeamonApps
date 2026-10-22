using System.Globalization;
using Microsoft.Data.Sqlite;

namespace LoadBacktest;

/// <summary>
/// One logged hour of DataLogger's hourly table (row stamped hh:59:59 covers hour hh).
/// Total = the load PVControl forecasts today: house energy without EV charging and warm water.
/// HpHeat = heat pump energy without warm water (space heating + standby), Base = Total − HpHeat.
/// </summary>
public sealed record Hour(DateTime Utc, DateTime Local, double Total, double Base, double HpHeat, double? Temp, double? Cloud);

public sealed class History
{
  public static readonly TimeZoneInfo Vienna = TimeZoneInfo.FindSystemTimeZoneById("Europe/Vienna");

  public IReadOnlyList<Hour> Hours { get; }
  readonly Dictionary<DateTime, Hour> _byUtc;

  History(List<Hour> hours)
  {
    Hours = hours;
    _byUtc = hours.ToDictionary(h => h.Utc);
  }

  public Hour? At(DateTime utc) => _byUtc.GetValueOrDefault(utc);

  /// <summary>Index of the first hour at or after <paramref name="utc"/> (hours are sorted).</summary>
  public int IndexAt(DateTime utc)
  {
    int lo = 0, hi = Hours.Count;
    while (lo < hi)
    {
      int mid = (lo + hi) / 2;
      if (Hours[mid].Utc < utc) lo = mid + 1; else hi = mid;
    }
    return lo;
  }

  public static History Load(string dbPath)
  {
    using var connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly;Pooling=False");
    connection.Open();
    using var command = connection.CreateCommand();
    command.CommandText = "SELECT timestamp, houseenergy, carcharge, warmwaterenergy, heatpumpenergy, temperatureoutside, cloudcover " +
                          "FROM hourly WHERE houseenergy IS NOT NULL ORDER BY timestamp";
    using var reader = command.ExecuteReader();
    var byUtc = new Dictionary<DateTime, Hour>();
    while (reader.Read())
    {
      var end = DateTimeOffset.Parse(reader.GetString(0), CultureInfo.InvariantCulture);
      var startUtc = new DateTime(end.UtcDateTime.Year, end.UtcDateTime.Month, end.UtcDateTime.Day, end.UtcDateTime.Hour, 0, 0, DateTimeKind.Utc);
      double house = reader.GetDouble(1);
      double car = reader.IsDBNull(2) ? 0 : reader.GetDouble(2);
      double ww = reader.IsDBNull(3) ? 0 : reader.GetDouble(3);
      double hp = reader.IsDBNull(4) ? double.NaN : reader.GetDouble(4);
      // Structurally impossible hours (counter glitches, e.g. warm water −19 kWh in one hour) are
      // missing, not data: ~45 hours since 2024, each one able to wreck a whole night's sum.
      bool corrupt = house < 0 || car < 0 || ww < 0 || hp < 0
                     || (!double.IsNaN(hp) && (ww > hp + 50 || hp > house + 50)) || car > house + 50;
      double total = corrupt ? double.NaN : Math.Max(0, house - car - ww);
      double hpHeat = corrupt || double.IsNaN(hp) ? double.NaN : Math.Clamp(hp - ww, 0, total);
      byUtc[startUtc] = new Hour(startUtc, TimeZoneInfo.ConvertTimeFromUtc(startUtc, Vienna), total, double.IsNaN(hpHeat) ? double.NaN : total - hpHeat, hpHeat,
        reader.IsDBNull(5) ? null : reader.GetDouble(5), reader.IsDBNull(6) ? null : reader.GetDouble(6));
    }
    return new History(byUtc.Values.OrderBy(h => h.Utc).ToList());
  }
}
