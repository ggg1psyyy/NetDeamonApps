using LinqToDB;
using System.Collections.Generic;
using System.Linq;

namespace PVControl
{
  public class HourlyWeightedAverageLoadPrediction : Prediction
  {
    private readonly string _dbLocation;
    private readonly float _weightScaling;
    public HourlyWeightedAverageLoadPrediction(string dbLocation, float weightScaling=20f) 
    {
      _dbLocation = dbLocation;
      if (String.IsNullOrEmpty(_dbLocation) || !System.IO.File.Exists(_dbLocation))
        throw new ArgumentException("DBLocation missing or file not found");

      _weightScaling = weightScaling;
      base.Initialize("Load Prediction");
    }
    protected override Dictionary<DateTime, int> PopulateData()
    {
      Dictionary<DateTime, int> data = [];
      for (var time = DateTime.Now.Date; time < DateTime.Now.AddDays(2).Date; time = time.AddMinutes(15))
      {
        data.Add(time, GetHourlyHouseEnergyUsageHistory(time.Hour)/4);
      }
      return data;
    }
    private int GetHourlyHouseEnergyUsageHistory(int hour)
    {
      using var db = new EnergyHistoryDb(new DataOptions().UseSQLite(String.Format("Data Source={0}",_dbLocation)));
      DateTime now = DateTime.Now;
      var weights = db.Hourlies.Where(h => h.Timestamp.Hour == hour && h.Houseenergy != null).Select(h => new { Date = h.Timestamp, Value = h.Houseenergy, Weight = (float)Math.Exp(-Math.Abs((h.Timestamp - now).Days) / _weightScaling) }).ToList();
      float weightedSum = weights.Sum(w => w.Value is null ? 0 : (float)w.Value * w.Weight);
      float sumOfWeights = weights.Sum(w => w.Weight);
      float weightedAverage = weightedSum / sumOfWeights;
      return (int)Math.Round(weightedAverage, 0);
    }
  }
}
