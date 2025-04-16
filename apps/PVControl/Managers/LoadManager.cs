using NetDaemon.Extensions.Scheduler;
using NetDaemon.HassModel.Entities;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using static NetDeamon.apps.PVControl.PVControlCommon;

namespace NetDeamon.apps.PVControl.Managers
{
  public enum PowerReqestStatus
  {
    Idle = 0,
    Requested = 1,
    Running = 2,
    Paused = 4,
    Stopped = 8,
    Error = 16,
  }
  public struct PowerRequest
  {
    public ILoadManager LoadManager;
    public PowerReqestStatus RequestStatus;
    public int EstimatedEnergyNeeded; // in Wh
    public int EstimatedTimeNeeded; // in minutes
    public DateTime PreferedStartTime;
    public DateTime LatestStartTime;
    public int EnergyUsed;
    public int RunTime;
    public bool CanBeInterrupted;
    public int RequestedUpdateRate; // in minutes
    public DateTime LastUpdate;
    public string RequestDescription;
  }
  public interface ILoadManager
  {
    public PowerRequest Initialize();
    public void Start();
    public void Stop();
    public void Pause();
    public void Update();
  }
  public class LoadManager
  {
    private readonly HouseEnergy _house;
    private readonly List<PowerRequest> _powerRequests;
    private Entity _ManagerEntity = null!;

    public LoadManager(HouseEnergy house)
    {
      _house = house;
#if !DEBUG
      PVCC_Scheduler.ScheduleCron("*/30 * * * * *", async () => await ScheduledOperations(), true);
#endif
      _powerRequests = [];
      _powerRequests.Add(new HeatpumpManager().Initialize());

      _ = ScheduledOperations();
    }

    private async Task ScheduledOperations()
    {
      //if (_ManagerEntity is null)
      //{
      //  _ManagerEntity = await RegisterSensor("sensor.pv_control_managed_loads", "Managed individual loads", "None", "mdi:pac-man",
      //    //addConfig: new
      //    //{
      //    //  unit_of_measurement = "€/kWh",
      //    //},
      //    defaultValue: _powerRequests.Count.ToString(),
      //    reRegister: true
      //  );
      //}
      //await PVCC_EntityManager.SetStateAsync(_ManagerEntity.EntityId, _powerRequests.Count.ToString());
      DateTime now = DateTime.Now;
      foreach (var powerRequest in _powerRequests)
      {
        if (now.AddMinutes(-powerRequest.RequestedUpdateRate) > powerRequest.LastUpdate)
          powerRequest.LoadManager.Update();
        if (_house.IsNowCheapestImportWindowToday && powerRequest.RequestStatus != PowerReqestStatus.Running)
        {
          powerRequest.LoadManager.Start();
        }
      }
      //var attr_Manager = new
      //{
      //  loads = _powerRequests,
      //};
      //await PVCC_EntityManager.SetAttributesAsync(_ManagerEntity.EntityId, attr_Manager);
    }
  }
}
