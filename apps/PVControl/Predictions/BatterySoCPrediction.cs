using System;
using System.Collections.Generic;

namespace NetDeamon.apps.PVControl.Predictions
{
  /// <summary>Battery SoC per slot, %; set from the simulation every cycle (<see cref="Prediction.UpdateData(Dictionary{DateTime, int}, DateTime?)"/>), never calculated itself.</summary>
  public class BatterySoCPrediction : Prediction
  {
    /// <summary>Creates the prediction with an empty window.</summary>
    public BatterySoCPrediction() => Initialize("Battery SoC Prediction");

    /// <summary>An empty window (zeros).</summary>
    /// <param name="now">Day of the window.</param>
    /// <returns>Zeros by slot start.</returns>
    protected override Dictionary<DateTime, int> PopulateData(DateTime now)
    {
      Dictionary<DateTime, int> result = [];
      result.ClearAndCreateEmptyPredictionData(now);
      return result;
    }
  }
}
