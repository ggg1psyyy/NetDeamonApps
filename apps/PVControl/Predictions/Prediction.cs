using System;
using System.Collections.Generic;
using System.Linq;
using static NetDeamon.apps.PVControl.PVControlCommon;

namespace NetDeamon.apps.PVControl.Predictions
{
  public class PredictionContainer
  {
    private Dictionary<DateTime, int> _data;
    public PredictionContainer(Dictionary<DateTime, int>? data = null)
    {
      _data = [];
      _data.ClearAndCreateEmptyPredictionData(DateTime.Now);
      LastUpdated = default;
      if (data != null)
        UpdateData(data, DateTime.Now);
    }
    public bool DataOK { get; private set; }
    public DateTime LastUpdated { get; private set; }
    //public bool CachePerDay { get; set; }
    public Dictionary<DateTime, int> PredictionData
    {
      get => _data;
      set
      {
        UpdateData(value, DateTime.Now);
      }
    }
    /// <summary>Stores <paramref name="data"/> if it covers exactly the window of <paramref name="now"/>
    /// (today 00:00 → tomorrow 23:45); otherwise an empty window of that day. The caller passes the
    /// same <paramref name="now"/> it built the data with — around midnight a second clock read can
    /// already be the next day (live 2026-10-20 00:00:00).</summary>
    public bool UpdateData(Dictionary<DateTime, int> data, DateTime now)
    {
      if (data is not null && ValidateData(data, now))
      {
        _data = data;
        LastUpdated = DateTime.Now;
        DataOK = true;
      }
      else
      {
        _data.ClearAndCreateEmptyPredictionData(now);
        LastUpdated = default;
        DataOK = false;
      }
      return DataOK;
    }
    private static bool ValidateData(Dictionary<DateTime, int> data, DateTime now)
    {
      int index = 0;
      for (var time = now.Date; time < now.Date.AddDays(2); time = time.AddMinutes(15))
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
    /// <summary>The 48-h window (today 00:00 → tomorrow 23:45) for the day of <paramref name="now"/>.
    /// Use <paramref name="now"/>, never a new clock read (see <see cref="PredictionContainer.UpdateData"/>).</summary>
    protected abstract Dictionary<DateTime, int> PopulateData(DateTime now);
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
    public void UpdateData() => UpdateData(DateTime.Now);

    /// <summary>Recalculates the prediction for the day of <paramref name="now"/> (one clock read per cycle).</summary>
    public void UpdateData(DateTime now) => Store(PopulateData(now), now);

    /// <summary>Directly sets prediction data from an external source (e.g. simulation output), bypassing PopulateData().</summary>
    public void UpdateData(Dictionary<DateTime, int> data, DateTime? now = null) => Store(data, now ?? DateTime.Now);

    private void Store(Dictionary<DateTime, int> data, DateTime now)
    {
      if (!DataContainer.UpdateData(data, now))
        PVCC_Logger.LogError("{Prediction} could not be validated: {Count} slots {First:dd.MM. HH:mm} → {Last:dd.MM. HH:mm}, expected 192 from {Expected:dd.MM.}",
          Description, data?.Count ?? 0, data?.Keys.DefaultIfEmpty().Min(), data?.Keys.DefaultIfEmpty().Max(), now.Date);
    }
  }
}
