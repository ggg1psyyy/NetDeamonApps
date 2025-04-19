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
    public HourlyWeightedAverageLoadPrediction(string dbLocation, float weightScaling = 20f, bool baseLoadOnly = false)
    {
      _dbLocation = dbLocation;
      if (string.IsNullOrEmpty(_dbLocation) || !System.IO.File.Exists(_dbLocation))
        throw new ArgumentException("DBLocation missing or file not found");

      _weightScaling = weightScaling;
      _baseLoadOnly = baseLoadOnly;
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
      var weights = db.Hourlies.Where(h => h.Timestamp.Hour == hour && h.Houseenergy != null).Select(h => new 
      { 
        Date = h.Timestamp, Value = _baseLoadOnly ? h.Houseenergy - h.Carcharge - h.Warmwaterenergy : h.Houseenergy, Weight = (float)Math.Exp(-Math.Abs((h.Timestamp - now).Days) / _weightScaling) 
      }
      ).ToList();
      float weightedSum = weights.Sum(w => w.Value is null ? 0 : (float)w.Value * w.Weight);
      float sumOfWeights = weights.Sum(w => w.Weight);
      float weightedAverage = weightedSum / sumOfWeights;
      return (int)Math.Round(weightedAverage, 0);
    }
  }
}
