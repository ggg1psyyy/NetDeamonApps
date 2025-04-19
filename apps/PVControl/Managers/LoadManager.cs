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
    public int CurrentManagedLoad; // current load managed by this integration in W
    public int EstimatedEnergyNeeded; // in Wh
    public int EstimatedTimeNeeded; // in minutes
    public DateTime PreferedStartTime;
    public DateTime LatestStartTime;
    public int EnergyUsed;
    public int RunTime;
    public bool CanBeInterrupted;
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
      _powerRequests = [];
      _powerRequests.Add(new HeatpumpManager().Initialize());

    }
    public void Update()
    {
      Parallel.ForEach(_powerRequests, (powerRequest) => {powerRequest.LoadManager.Update();});
    }
  }
}
