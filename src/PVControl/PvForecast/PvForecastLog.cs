using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace NetDeamon.apps.PVControl.PvForecast
{
  /// <summary>A source's forecast for a day as noted the evening before.</summary>
  /// <param name="Day">The day forecast.</param>
  /// <param name="Source">The source.</param>
  /// <param name="Issued">When it was noted.</param>
  /// <param name="Run">The source's run, if named.</param>
  /// <param name="TotalWh">Expected PV, Wh.</param>
  /// <param name="LowWh">Low estimate, Wh.</param>
  /// <param name="HighWh">High estimate, Wh.</param>
  /// <param name="TempForecast">Forecast temperature 09–14 h, °C.</param>
  /// <param name="Hourly">Expected PV by hour of day, Wh.</param>
  public sealed record DayAhead(DateTime Day, string Source, DateTime Issued, string? Run, double TotalWh, double? LowWh, double? HighWh,
    double? TempForecast, IReadOnlyDictionary<int, double> Hourly);

  /// <summary>
  /// The PV forecast log (SQLite, <see cref="PvForecastConfig.DBLocation"/>): every source's day-ahead forecast, the day's
  /// measured PV and temperature, and measured values of weather services — the history the correction learns from
  /// and the sources are compared on. Local dates (<c>yyyy-MM-dd</c>) throughout.
  /// </summary>
  /// <param name="path">The DB file.</param>
  public sealed class PvForecastLog(string path)
  {
    private string Cs => $"Data Source={path};Pooling=False";
    private bool _created;

    private static string Day(DateTime d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    private static DateTime ParseDay(string s) => DateTime.ParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture);

    private async Task<SqliteConnection> OpenAsync()
    {
      var con = new SqliteConnection(Cs);
      await con.OpenAsync();
      if (!_created)
      {
        await SqliteSchema.ExecuteAsync(con, """
          CREATE TABLE IF NOT EXISTS dayahead (day TEXT NOT NULL, source TEXT NOT NULL, issued TEXT NOT NULL, run TEXT,
            total_wh REAL NOT NULL, low_wh REAL, high_wh REAL, temp_fc REAL, hourly TEXT, PRIMARY KEY (day, source));
          CREATE TABLE IF NOT EXISTS actual (day TEXT PRIMARY KEY, pv_wh REAL NOT NULL, temp_day REAL);
          CREATE TABLE IF NOT EXISTS observation (day TEXT NOT NULL, key TEXT NOT NULL, value REAL NOT NULL, PRIMARY KEY (day, key));
          """);
        _created = true;
      }
      return con;
    }

    /// <summary>Notes a day-ahead forecast (a second one for the same day and source replaces it).</summary>
    /// <param name="f">The forecast.</param>
    public async Task NoteAsync(DayAhead f)
    {
      await using var con = await OpenAsync();
      await using var cmd = con.CreateCommand();
      cmd.CommandText = "INSERT OR REPLACE INTO dayahead VALUES ($d, $s, $i, $r, $t, $lo, $hi, $tf, $h)";
      cmd.Parameters.AddWithValue("$d", Day(f.Day));
      cmd.Parameters.AddWithValue("$s", f.Source);
      cmd.Parameters.AddWithValue("$i", f.Issued.ToISO8601());
      cmd.Parameters.AddWithValue("$r", (object?)f.Run ?? DBNull.Value);
      cmd.Parameters.AddWithValue("$t", f.TotalWh);
      cmd.Parameters.AddWithValue("$lo", (object?)f.LowWh ?? DBNull.Value);
      cmd.Parameters.AddWithValue("$hi", (object?)f.HighWh ?? DBNull.Value);
      cmd.Parameters.AddWithValue("$tf", (object?)f.TempForecast ?? DBNull.Value);
      cmd.Parameters.AddWithValue("$h", JsonSerializer.Serialize(f.Hourly.ToDictionary(h => h.Key.ToString(CultureInfo.InvariantCulture), h => Math.Round(h.Value))));
      await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>Notes what a day delivered.</summary>
    /// <param name="day">The day.</param>
    /// <param name="pvWh">Measured PV, Wh.</param>
    /// <param name="tempDay">Measured temperature 09–14 h, °C.</param>
    public async Task SetActualAsync(DateTime day, double pvWh, double? tempDay)
    {
      await using var con = await OpenAsync();
      await using var cmd = con.CreateCommand();
      cmd.CommandText = "INSERT OR REPLACE INTO actual VALUES ($d, $pv, $t)";
      cmd.Parameters.AddWithValue("$d", Day(day));
      cmd.Parameters.AddWithValue("$pv", pvWh);
      cmd.Parameters.AddWithValue("$t", (object?)tempDay ?? DBNull.Value);
      await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>Notes a day's measured values (a weather service's).</summary>
    /// <param name="day">The day.</param>
    /// <param name="values">Values by key.</param>
    public async Task SetObservationsAsync(DateTime day, IReadOnlyDictionary<string, double> values)
    {
      if (values.Count == 0) return;
      await using var con = await OpenAsync();
      foreach (var (key, value) in values)
      {
        await using var cmd = con.CreateCommand();
        cmd.CommandText = "INSERT OR REPLACE INTO observation VALUES ($d, $k, $v)";
        cmd.Parameters.AddWithValue("$d", Day(day));
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", value);
        await cmd.ExecuteNonQueryAsync();
      }
    }

    /// <summary>Whether a source's forecast for a day is noted.</summary>
    /// <param name="day">The day.</param>
    /// <param name="source">The source.</param>
    public async Task<bool> HasDayAheadAsync(DateTime day, string source)
    {
      await using var con = await OpenAsync();
      await using var cmd = con.CreateCommand();
      cmd.CommandText = "SELECT 1 FROM dayahead WHERE day = $d AND source = $s";
      cmd.Parameters.AddWithValue("$d", Day(day));
      cmd.Parameters.AddWithValue("$s", source);
      return await cmd.ExecuteScalarAsync() is not null;
    }

    /// <summary>Days since <paramref name="from"/> with a noted forecast of any source but no measured PV yet.</summary>
    /// <param name="from">First day.</param>
    /// <param name="before">Days before this only (ended days).</param>
    public async Task<List<DateTime>> UnscoredDaysAsync(DateTime from, DateTime before)
    {
      await using var con = await OpenAsync();
      await using var cmd = con.CreateCommand();
      cmd.CommandText = "SELECT DISTINCT day FROM dayahead WHERE day >= $f AND day < $b AND day NOT IN (SELECT day FROM actual) ORDER BY day";
      cmd.Parameters.AddWithValue("$f", Day(from));
      cmd.Parameters.AddWithValue("$b", Day(before));
      var days = new List<DateTime>();
      await using var rd = await cmd.ExecuteReaderAsync();
      while (await rd.ReadAsync()) days.Add(ParseDay(rd.GetString(0)));
      return days;
    }

    /// <summary>Days since <paramref name="from"/> with measured PV but none of the given observation key.</summary>
    /// <param name="from">First day.</param>
    /// <param name="key">An observation key every observed day has.</param>
    public async Task<List<DateTime>> UnobservedDaysAsync(DateTime from, string key)
    {
      await using var con = await OpenAsync();
      await using var cmd = con.CreateCommand();
      cmd.CommandText = "SELECT day FROM actual WHERE day >= $f AND day NOT IN (SELECT day FROM observation WHERE key = $k) ORDER BY day";
      cmd.Parameters.AddWithValue("$f", Day(from));
      cmd.Parameters.AddWithValue("$k", key);
      var days = new List<DateTime>();
      await using var rd = await cmd.ExecuteReaderAsync();
      while (await rd.ReadAsync()) days.Add(ParseDay(rd.GetString(0)));
      return days;
    }

    /// <summary>A source's scored days since <paramref name="from"/>: its day-ahead forecast next to the measured day.</summary>
    /// <param name="source">The source.</param>
    /// <param name="from">First day.</param>
    public async Task<List<ScoredDay>> ScoredAsync(string source, DateTime from)
    {
      await using var con = await OpenAsync();
      await using var cmd = con.CreateCommand();
      cmd.CommandText = """
        SELECT f.day, f.total_wh, a.pv_wh, a.temp_day FROM dayahead f JOIN actual a ON a.day = f.day
        WHERE f.source = $s AND f.day >= $f ORDER BY f.day
        """;
      cmd.Parameters.AddWithValue("$s", source);
      cmd.Parameters.AddWithValue("$f", Day(from));
      var days = new List<ScoredDay>();
      await using var rd = await cmd.ExecuteReaderAsync();
      while (await rd.ReadAsync())
        days.Add(new ScoredDay(ParseDay(rd.GetString(0)), rd.GetDouble(1), rd.GetDouble(2), rd.IsDBNull(3) ? null : rd.GetDouble(3)));
      return days;
    }
  }
}
