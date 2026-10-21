using LinqToDB;
using PVControl;
using System.Collections.Generic;
using System.Linq;

namespace NetDeamon.apps.PVControl.Predictions
{
  public class HourlyWeightedAverageLoadPrediction : Prediction
  {
    private readonly string _dbLocation;
    private readonly float _weightScaling;
    private readonly bool _baseLoadOnly;
    private readonly IReadOnlyList<string> _excludeColumns;

    /// <param name="excludeColumns">
    /// Column names from the hourly DB table to subtract from the house load, so that
    /// each scheduled load's past energy is not double-counted when it is added back as
    /// an ExtraLoad in the simulation. Known names: "carcharge", "warmwaterenergy",
    /// "heatpumpenergy". Has no effect when <paramref name="baseLoadOnly"/> is true
    /// (which already subtracts carcharge and warmwaterenergy).
    /// </param>
    public HourlyWeightedAverageLoadPrediction(string dbLocation, float weightScaling = 20f, bool baseLoadOnly = false, IReadOnlyList<string>? excludeColumns = null)
    {
      _dbLocation = dbLocation;
      if (string.IsNullOrEmpty(_dbLocation) || !System.IO.File.Exists(_dbLocation))
        throw new ArgumentException("DBLocation missing or file not found");

      _weightScaling = weightScaling;
      _baseLoadOnly = baseLoadOnly;
      _excludeColumns = excludeColumns ?? [];
      Initialize("Load Prediction");
    }

    protected override Dictionary<DateTime, int> PopulateData(DateTime now)
    {
      DateTime today = now.Date;
      var hourly = HourlyWeightedAverages(LoadHistory(), today, _weightScaling);

      Dictionary<DateTime, int> data = [];
      for (var time = today; time < today.AddDays(2); time = time.AddMinutes(15))
        data.Add(time, hourly[time.Hour] / 4);
      return data;
    }

    /// <summary>All logged hours with house energy, as (timestamp, value after exclusions).</summary>
    private List<(DateTime Timestamp, int Value)> LoadHistory()
    {
      using var db = new EnergyHistoryDb(new DataOptions().UseSQLite(string.Format("Data Source={0}", _dbLocation)));
      // Subtraction is done in memory so the exclusion list can be dynamic. The hour is NOT
      // filtered in SQL: LinqToDB turns Timestamp.Hour into strftime('%H', …), which SQLite
      // evaluates in UTC — that shifted every forecast hour by the UTC offset (1–2 h).
      return db.Hourlies
        .Where(h => h.Houseenergy != null)
        .Select(h => new { h.Timestamp, h.Houseenergy, h.Carcharge, h.Warmwaterenergy, h.Heatpumpenergy })
        .AsEnumerable()
        .Select(r => (r.Timestamp, ComputeValue(r.Houseenergy, r.Carcharge, r.Warmwaterenergy, r.Heatpumpenergy)))
        .ToList();
    }

    /// <summary>
    /// Weighted average energy per local hour of day (index 0–23, Wh). A row stamped hh:59:59
    /// covers hour hh. Older days weigh less: exp(−age in days / weightScaling). Hours without
    /// history get 0.
    /// </summary>
    public static int[] HourlyWeightedAverages(IEnumerable<(DateTime Timestamp, int Value)> rows, DateTime today, float weightScaling)
    {
      var sums = new double[24];
      var weights = new double[24];
      foreach (var (timestamp, value) in rows)
      {
        var local = timestamp.Kind == DateTimeKind.Utc ? timestamp.ToLocalTime() : timestamp;
        double w = Math.Exp(-Math.Abs((local - today).Days) / weightScaling);
        sums[local.Hour] += value * w;
        weights[local.Hour] += w;
      }
      return Enumerable.Range(0, 24)
        .Select(h => weights[h] > 0 ? (int)Math.Round(sums[h] / weights[h]) : 0)
        .ToArray();
    }

    private int ComputeValue(int? houseenergy, int? carcharge, int? warmwaterenergy, int? heatpumpenergy)
    {
      int value = houseenergy ?? 0;
      if (_baseLoadOnly)
        return value - (carcharge ?? 0) - (warmwaterenergy ?? 0);

      foreach (var col in _excludeColumns)
        value -= col.ToLowerInvariant() switch
        {
          "carcharge"       => carcharge ?? 0,
          "warmwaterenergy" => warmwaterenergy ?? 0,
          "heatpumpenergy"  => heatpumpenergy ?? 0,
          _                 => 0,
        };
      return value;
    }
  }
}
