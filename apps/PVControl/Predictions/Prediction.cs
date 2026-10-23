using System;
using System.Collections.Generic;
using System.Linq;
using static NetDeamon.apps.PVControl.PVControlCommon;

namespace NetDeamon.apps.PVControl.Predictions
{
  /// <summary>Holds a prediction's 48-h window (192 quarter-hour slots, today 00:00 → tomorrow 23:45) and whether it is valid.</summary>
  public class PredictionContainer
  {
    private Dictionary<DateTime, int> _data;
    /// <summary>Creates an empty window for today, then stores the data if given.</summary>
    /// <param name="data">Initial data; null = empty.</param>
    public PredictionContainer(Dictionary<DateTime, int>? data = null)
    {
      _data = [];
      _data.ClearAndCreateEmptyPredictionData(DateTime.Now);
      LastUpdated = default;
      if (data != null)
        UpdateData(data, DateTime.Now);
    }
    /// <summary>The last stored data covered the window exactly.</summary>
    public bool DataOK { get; private set; }
    /// <summary>Time of the last valid update; default if none.</summary>
    public DateTime LastUpdated { get; private set; }
    //public bool CachePerDay { get; set; }
    /// <summary>The window; setting it validates against today (<see cref="UpdateData"/>).</summary>
    public Dictionary<DateTime, int> PredictionData
    {
      get => _data;
      set
      {
        UpdateData(value, DateTime.Now);
      }
    }
    /// <summary>Stores data if it covers exactly the window of a day, else an empty window of that day.</summary>
    /// <param name="data">Values by slot start.</param>
    /// <param name="now">Day of the window; the same time the data was built with (a second clock read can be the next day at midnight).</param>
    /// <returns>True if the data was valid.</returns>
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
    /// <summary>Whether data has exactly the 192 slots of a day's window.</summary>
    /// <param name="data">Values by slot start.</param>
    /// <param name="now">Day of the window.</param>
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
  /// <summary>Base of all predictions: a 48-h window of values per quarter-hour (Wh or %), recalculated by <see cref="PopulateData"/>.</summary>
  public abstract class Prediction
  {
    /// <summary>Calculates the window (today 00:00 → tomorrow 23:45) for the day of a time.</summary>
    /// <param name="now">Time of this cycle; never read the clock again.</param>
    /// <returns>Values by slot start.</returns>
    protected abstract Dictionary<DateTime, int> PopulateData(DateTime now);
    /// <summary>Creates the prediction with an empty window.</summary>
    public Prediction()
    {
      DataContainer = new PredictionContainer();
      Description = "Base";
    }
    /// <summary>Sets the name and calculates the first window.</summary>
    /// <param name="description">Name for logs.</param>
    protected void Initialize(string description)
    {
      //_cachedPrediction.CachePerDay = cachePerDay;
      Description = description;
      UpdateData();
    }
    private readonly PredictionContainer DataContainer;
    /// <summary>The whole window.</summary>
    public Dictionary<DateTime, int> TodayAndTomorrow
    {
      get
      {
        return DataContainer.PredictionData;
      }
    }
    /// <summary>Today's slots.</summary>
    public Dictionary<DateTime, int> Today
    {
      get
      {
        return TodayAndTomorrow.Where(d => d.Key.Date == DateTime.Now.Date).ToDictionary();
      }
    }
    /// <summary>Tomorrow's slots.</summary>
    public Dictionary<DateTime, int> Tomorrow
    {
      get
      {
        return TodayAndTomorrow.Where(d => d.Key.Date == DateTime.Now.AddDays(1).Date).ToDictionary();
      }
    }
    /// <summary>Value of the running slot.</summary>
    public int CurrentValue
    {
      get
      {
        return Today.GetEntryAtTime(DateTime.Now).Value;
      }
    }
    /// <summary>The window is valid.</summary>
    public bool DataOK
    {
      get => DataContainer.DataOK;
    }
    /// <summary>Name for logs.</summary>
    public string Description
    { get; private set; }
    /// <summary>Time of the last valid update.</summary>
    public DateTime LastDataUpdate
    {
      get => DataContainer.LastUpdated;
    }
    /// <summary>Recalculates the window for now.</summary>
    public void UpdateData() => UpdateData(DateTime.Now);

    /// <summary>Recalculates the window for the day of a time.</summary>
    /// <param name="now">Time of this cycle.</param>
    public void UpdateData(DateTime now) => Store(PopulateData(now), now);

    /// <summary>Sets the window from outside (e.g. the simulation), without <see cref="PopulateData"/>.</summary>
    /// <param name="data">Values by slot start.</param>
    /// <param name="now">Day of the window; null = now.</param>
    public void UpdateData(Dictionary<DateTime, int> data, DateTime? now = null) => Store(data, now ?? DateTime.Now);

    /// <summary>Stores the window; logs an error if it is invalid.</summary>
    /// <param name="data">Values by slot start.</param>
    /// <param name="now">Day of the window.</param>
    private void Store(Dictionary<DateTime, int> data, DateTime now)
    {
      if (!DataContainer.UpdateData(data, now))
        PVCC_Logger.LogError("{Prediction} could not be validated: {Count} slots {First:dd.MM. HH:mm} → {Last:dd.MM. HH:mm}, expected 192 from {Expected:dd.MM.}",
          Description, data?.Count ?? 0, data?.Keys.DefaultIfEmpty().Min(), data?.Keys.DefaultIfEmpty().Max(), now.Date);
    }
  }
}
