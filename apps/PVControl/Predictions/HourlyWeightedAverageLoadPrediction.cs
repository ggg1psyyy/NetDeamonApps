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

    protected override Dictionary<DateTime, int> PopulateData()
    {
      Dictionary<DateTime, int> data = [];
      DateTime now = DateTime.Now.Date;
      for (var time = now; time < now.AddDays(2).Date; time = time.AddMinutes(15))
      {
        data.Add(time, GetHourlyHouseEnergyUsageHistory(time.Hour, now) / 4);
      }
      return data;
    }

    private int GetHourlyHouseEnergyUsageHistory(int hour, DateTime now)
    {
      using var db = new EnergyHistoryDb(new DataOptions().UseSQLite(string.Format("Data Source={0}", _dbLocation)));

      // Fetch the columns we need; subtraction is done in memory so that the exclusion list
      // can be dynamic without complex SQL expression trees.
      var rows = db.Hourlies
        .Where(h => h.Timestamp.Hour == hour && h.Houseenergy != null)
        .Select(h => new { h.Timestamp, h.Houseenergy, h.Carcharge, h.Warmwaterenergy, h.Heatpumpenergy })
        .ToList();

      var weights = rows.Select(r => new
      {
        Value = ComputeValue(r.Houseenergy, r.Carcharge, r.Warmwaterenergy, r.Heatpumpenergy),
        Weight = (float)Math.Exp(-Math.Abs((r.Timestamp - now).Days) / _weightScaling),
      }).ToList();

      float weightedSum = weights.Sum(w => w.Value * w.Weight);
      float sumOfWeights = weights.Sum(w => w.Weight);
      float weightedAverage = weightedSum / sumOfWeights;
      return (int)Math.Round(weightedAverage, 0);
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
