using NetDaemon.HassModel.Entities;
using static NetDeamon.apps.PVControl.PVControlCommon;

namespace NetDeamon.apps.PVControl
{
  public partial class PVConfig
  {
    public Entity WarmwaterTemperatureEntity { get; set; } = null!;
    public Entity WarmwaterSetPointNormalEntity { get; set; } = null!;
    public Entity WarmwaterSetPointOnceEntity { get; set; } = null!;
    public Entity WarmwaterEnergyUsageEntity { get; set; } = null!;
    public Entity WarmwaterStartOnceEntity { get; set; } = null!;
    public int WarmWaterEnergyNeededPerDegree { get; set; } = 100;
    public int MinutesPerDegree { get; set; } = 2;
  }
}

namespace NetDeamon.apps.PVControl.Managers
{

  internal class HeatpumpManager : ILoadManager
  {
    private PowerRequest _PowerRequest;
    public PowerRequest Initialize()
    {
      _PowerRequest = new PowerRequest
      {
        LoadManager = this,
        CanBeInterrupted = true,
        RequestStatus = PowerReqestStatus.Idle,
        RequestedUpdateRate = 15,
        LastUpdate = DateTime.Now,
        RequestDescription = "Heatpump - Warmwater",
      };
      Update();
      return _PowerRequest;
    }

    public void Pause()
    {
      if (PVCC_Config.WarmwaterStartOnceEntity.TurnOff())
        _PowerRequest.RequestStatus = PowerReqestStatus.Paused;
      else
        _PowerRequest.RequestStatus = PowerReqestStatus.Error;
    }
    int _startEnergyUsage = 0;
    float _startWWTemp = 0;
    public void Start()
    {
      Update();
      if (_PowerRequest.RequestStatus == PowerReqestStatus.Running)
      {

      }
      else if (PVCC_Config.WarmwaterTemperatureEntity.TryGetStateValue(out _startWWTemp) && PVCC_Config.WarmwaterSetPointOnceEntity.TryGetStateValue(out float setValue))
      {
        _startEnergyUsage = 0;
        if (PVCC_Config.WarmwaterEnergyUsageEntity.TryGetStateValue(out int val))
          _startEnergyUsage = val;
        if (_startWWTemp < (setValue - 8))
        {
          if (PVCC_Config.WarmwaterStartOnceEntity.TurnOn())
          {
            _PowerRequest.RequestStatus = PowerReqestStatus.Running;
            PVCC_Logger.LogInformation("Started WarmWater");
          }
          else
          {
            _PowerRequest.RequestStatus = PowerReqestStatus.Error;
            PVCC_Logger.LogInformation("Error starting WarmWater");
          }
          _PowerRequest.EnergyUsed = _startEnergyUsage;
        }
        else
          _PowerRequest.RequestStatus = PowerReqestStatus.Idle;
      }
      else
        _PowerRequest.RequestStatus = PowerReqestStatus.Error;

      _PowerRequest.LastUpdate = DateTime.Now;
    }

    public void Stop()
    {
      if (PVCC_Config.WarmwaterStartOnceEntity.TurnOff())
      {
        _PowerRequest.RequestStatus = PowerReqestStatus.Idle;
        PVCC_Logger.LogInformation("Stoped WarmWater");
      }
      else
        _PowerRequest.RequestStatus = PowerReqestStatus.Error;

      if (PVCC_Config.WarmwaterEnergyUsageEntity.TryGetStateValue(out int val))
        _PowerRequest.EnergyUsed = val - _startEnergyUsage;
    }

    public void Update()
    {
      if (PVCC_Config.WarmwaterTemperatureEntity.TryGetStateValue(out float curTemp) 
        && PVCC_Config.WarmwaterSetPointOnceEntity.TryGetStateValue(out float targetTemp)
        && PVCC_Config.WarmwaterSetPointNormalEntity.TryGetStateValue(out float minTemp)
        && PVCC_Config.WarmwaterStartOnceEntity.TryGetStateValue(out bool isActive))
      {
        float diffToMin = curTemp - minTemp;
        float diffToTarget = targetTemp - curTemp;
        _PowerRequest.EstimatedEnergyNeeded = (int)(diffToTarget * PVCC_Config.WarmWaterEnergyNeededPerDegree);
        _PowerRequest.EstimatedTimeNeeded = (int)(diffToTarget * PVCC_Config.MinutesPerDegree);
        if (diffToMin - minTemp < 5)
        {
        }
        //if (PVCC_Config.WarmwaterEnergyUsageEntity.TryGetStateValue(out int val))
        //  _PowerRequest.EnergyUsed = val - _startEnergyUsage;
        if (isActive)
        {
          if (curTemp >= targetTemp)
          {
            Stop();
          }
          else
            _PowerRequest.RequestStatus = PowerReqestStatus.Running;
        }
        else
          _PowerRequest.RequestStatus = PowerReqestStatus.Idle;
      }
      else
        _PowerRequest.RequestStatus = PowerReqestStatus.Error;

      _PowerRequest.LastUpdate = DateTime.Now;
    }
  }
}
