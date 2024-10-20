using System.Collections.Generic;
using System.Linq;

namespace PVControl
{
  public class BatterySoCPrediction : Prediction
  {
    private readonly Prediction _netEnergyPrediction;
    private readonly int _currentSoc;
    private readonly int _battCapacity;

    public BatterySoCPrediction(Prediction netEnergyPrediction, int currentSoc, int battCapacity)
    {
      _netEnergyPrediction = netEnergyPrediction;
      _currentSoc = currentSoc;
      _battCapacity = battCapacity;
      base.Initialize("Battery SoC Prediction");
    }

    protected override Dictionary<DateTime, int> PopulateData()
    {
      Dictionary<DateTime, int> result = [];
      result.ClearAndCreateEmptyPredictionData();

      int curEnergy = CalculateBatteryEnergyAtSoC(_currentSoc);
      int curIndex = _netEnergyPrediction.TodayAndTomorrow.Keys.ToList().IndexOf(_netEnergyPrediction.TodayAndTomorrow.Keys.FirstOrDefault(k => k >= DateTime.Now));

      for (int i = curIndex; i < result.Count; i++)
      {
        curEnergy = Math.Min(curEnergy + _netEnergyPrediction.TodayAndTomorrow[result.ElementAt(i).Key], _battCapacity);
        result[result.ElementAt(i).Key] = CalculateBatterySoCAtEnergy(curEnergy);
      }
      curEnergy = CalculateBatteryEnergyAtSoC(_currentSoc);
      for (int i = curIndex - 1; i >= 0; i--)
      {
        curEnergy = Math.Min(curEnergy - _netEnergyPrediction.TodayAndTomorrow[result.ElementAt(i).Key], _battCapacity);
        result[result.ElementAt(i).Key] = CalculateBatterySoCAtEnergy(curEnergy);
      }
      return result;
    }
    private int CalculateBatteryEnergyAtSoC(int soc)
    {
      float e = ((float)_battCapacity * (float)soc / 100);
      return (int) Math.Round(e, 0);
    }
    public int CalculateBatterySoCAtEnergy(int energy)
    {
      return (int) Math.Round((float)energy * 100 / (float)_battCapacity, 0);
    }
  }
}
