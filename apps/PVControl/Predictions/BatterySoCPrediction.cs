using NetDaemon.HassModel.Entities;
using System.Collections.Generic;
using System.Linq;
using static NetDeamon.apps.PVControl.PVControlCommon;

namespace NetDeamon.apps.PVControl.Predictions
{
  public class BatterySoCPrediction : Prediction
  {
    private readonly Prediction _netEnergyPrediction;
    private readonly Entity _currentSocEntity;
    private readonly int _battCapacity;
    private readonly int _curSocStatic;

    public BatterySoCPrediction(Prediction netEnergyPrediction, Entity currentSocEntity, int battCapacity, int curSocStatic = -99)
    {
      _netEnergyPrediction = netEnergyPrediction;
      _currentSocEntity = currentSocEntity;
      _battCapacity = battCapacity;
      _curSocStatic = curSocStatic;
      Initialize("Battery SoC Prediction");
    }

    protected override Dictionary<DateTime, int> PopulateData()
    {
      Dictionary<DateTime, int> result = [];
      int curSoc = _curSocStatic;
      result.ClearAndCreateEmptyPredictionData();
      if (_curSocStatic < 0)
      {
        if (!_currentSocEntity.TryGetStateValue(out curSoc))
        {
          PVCC_Logger.LogError("Could not get current SoC");
          return [];
        }
      }
      int curEnergy = CalculateBatteryEnergyAtSoC(curSoc);
      int curIndex = _netEnergyPrediction.TodayAndTomorrow.Keys.ToList().IndexOf(_netEnergyPrediction.TodayAndTomorrow.Keys.FirstOrDefault(k => k >= DateTime.Now));

      for (int i = curIndex; i < result.Count; i++)
      {
        curEnergy = Math.Max(Math.Min(curEnergy + _netEnergyPrediction.TodayAndTomorrow[result.ElementAt(i).Key], _battCapacity), 0);
        result[result.ElementAt(i).Key] = CalculateBatterySoCAtEnergy(curEnergy);
      }
      curEnergy = CalculateBatteryEnergyAtSoC(curSoc);
      for (int i = curIndex - 1; i >= 0; i--)
      {
        curEnergy = Math.Max(Math.Min(curEnergy - _netEnergyPrediction.TodayAndTomorrow[result.ElementAt(i).Key], _battCapacity), 0);
        result[result.ElementAt(i).Key] = CalculateBatterySoCAtEnergy(curEnergy);
      }
      return result;
    }
    private int CalculateBatteryEnergyAtSoC(int soc)
    {
      float e = _battCapacity * (float)soc / 100;
      return (int)Math.Round(e, 0);
    }
    public int CalculateBatterySoCAtEnergy(int energy)
    {
      return (int)Math.Round((float)energy * 100 / _battCapacity, 0);
    }
  }
}
