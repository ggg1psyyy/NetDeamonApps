using System.Collections.Generic;

namespace NetDeamon.apps.PVControl.Predictions
{
  /// <summary>
  /// Placeholder battery SoC prediction, empty until the energy simulator produces its first
  /// real result. HouseEnergy.RunSimulation() overwrites the data every cycle via the
  /// <see cref="Prediction.UpdateData(Dictionary{DateTime, int})"/> bypass overload — this
  /// class never computes a forecast itself.
  /// </summary>
  public class BatterySoCPrediction : Prediction
  {
    public BatterySoCPrediction() => Initialize("Battery SoC Prediction");

    protected override Dictionary<DateTime, int> PopulateData()
    {
      Dictionary<DateTime, int> result = [];
      result.ClearAndCreateEmptyPredictionData();
      return result;
    }
  }
}
