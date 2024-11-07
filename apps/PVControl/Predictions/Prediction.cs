using System.Collections.Generic;
using System.Linq;

namespace NetDeamon.apps.PVControl.Predictions
{
  public class PredictionContainer
  {
    private Dictionary<DateTime, int> _data;
    public PredictionContainer(Dictionary<DateTime, int>? data = null)
    {
      _data = [];
      _data.ClearAndCreateEmptyPredictionData();
      LastUpdated = default;
      if (data != null)
        UpdateData(data);
    }
    public bool DataOK { get; private set; }
    public DateTime LastUpdated { get; private set; }
    //public bool CachePerDay { get; set; }
    public Dictionary<DateTime, int> PredictionData
    {
      get => _data;
      set
      {
        UpdateData(value);
      }
    }
    private void UpdateData(Dictionary<DateTime, int> data)
    {
      if (data is not null && ValidateData(data))
      {
        _data = data;
        LastUpdated = DateTime.Now;
        DataOK = true;
      }
      else
      {
        _data.ClearAndCreateEmptyPredictionData();
        LastUpdated = default;
        DataOK = false;
      }
    }
    private static bool ValidateData(Dictionary<DateTime, int> data)
    {
      int index = 0;
      for (var time = DateTime.Now.Date; time < DateTime.Now.AddDays(2).Date; time = time.AddMinutes(15))
      {
        if (!data.ContainsKey(time))
          return false;
        index++;
      }
      return index == data.Count;
    }
  }
  // all predictions and forecasts must be based on this class
  public abstract class Prediction
  {
    protected abstract Dictionary<DateTime, int> PopulateData();
    public Prediction()
    {
      DataContainer = new PredictionContainer();
      Description = "Base";
    }
    protected void Initialize(string description)
    {
      //_cachedPrediction.CachePerDay = cachePerDay;
      Description = description;
      UpdateData();
    }
    private readonly PredictionContainer DataContainer;
    public Dictionary<DateTime, int> TodayAndTomorrow
    {
      get
      {
        return DataContainer.PredictionData;
      }
    }
    public Dictionary<DateTime, int> Today
    {
      get
      {
        return TodayAndTomorrow.Where(d => d.Key.Date == DateTime.Now.Date).ToDictionary();
      }
    }
    public Dictionary<DateTime, int> Tomorrow
    {
      get
      {
        return TodayAndTomorrow.Where(d => d.Key.Date == DateTime.Now.AddDays(1).Date).ToDictionary();
      }
    }
    public int CurrentValue
    {
      get
      {
        return Today.GetEntryAtTime(DateTime.Now).Value;
      }
    }
    public bool DataOK
    {
      get => DataContainer.DataOK;
    }
    public string Description
    { get; private set; }
    public DateTime LastDataUpdate
    {
      get => DataContainer.LastUpdated;
    }
    public void UpdateData()
    {
      DataContainer.PredictionData = PopulateData();
    }
  }
}
