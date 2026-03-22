using System;
using System.Linq;
using NetDeamon.apps;
using NetDeamon.apps.PVControl.Predictions;

namespace NetDeamon.apps.PVControl;

/// <summary>
/// Derives PV sunrise/sunset window boundaries and max-SoC duration queries
/// from the net-energy and battery-SoC predictions.
/// </summary>
public class PVWindows
{
  private readonly Prediction _netEnergy;
  private readonly Prediction _batterySoC;

  public PVWindows(Prediction netEnergy, Prediction batterySoC)
  {
    _netEnergy = netEnergy;
    _batterySoC = batterySoC;
  }

  public DateTime FirstRelevantPVEnergyToday
  {
    get
    {
      var result = _netEnergy.Today.Where(f => f.Value > 50).Select(f => f.Key).FirstOrDefault();
      return result != default ? result : DateTime.Now.Date.AddDays(2).AddMinutes(-1);
    }
  }

  public DateTime FirstRelevantPVEnergyTomorrow
  {
    get
    {
      var result = _netEnergy.Tomorrow.Where(f => f.Value > 50).Select(f => f.Key).FirstOrDefault();
      return result != default ? result : DateTime.Now.Date.AddDays(2).AddMinutes(-1);
    }
  }

  public DateTime LastRelevantPVEnergyToday
  {
    get
    {
      var result = _netEnergy.Today.Where(f => f.Value > 50).Select(f => f.Key).LastOrDefault();
      return result != default ? result : DateTime.Now.Date.AddDays(2).AddMinutes(-1);
    }
  }

  public DateTime LastRelevantPVEnergyTomorrow
  {
    get
    {
      var result = _netEnergy.Tomorrow.Where(f => f.Value > 50).Select(f => f.Key).LastOrDefault();
      return result != default ? result : DateTime.Now.Date.AddDays(2).AddMinutes(-1);
    }
  }

  public PVPeriods CurrentPVPeriod
  {
    get
    {
      var now = DateTime.Now;
      if (now < FirstRelevantPVEnergyToday)
        return PVPeriods.BeforePV;
      else if (now > LastRelevantPVEnergyToday)
        return PVPeriods.AfterPV;
      else
        return PVPeriods.InPVPeriod;
    }
  }

  public double MaxSocDurationToday
  {
    get
    {
      var maxSocRestOfToday = _batterySoC.Today.FirstMaxOrDefault(start: DateTime.Now);
      if (maxSocRestOfToday.Value < 99)
        return 0;
      var firstUnderMax = _batterySoC.Today.FirstUnderOrDefault(99, maxSocRestOfToday.Key);
      var span = firstUnderMax.Key - maxSocRestOfToday.Key;
      return span.TotalHours;
    }
  }

  public bool WillReachMaxSocToday
  {
    get
    {
      var maxSocRestOfToday = _batterySoC.Today.FirstMaxOrDefault(start: DateTime.Now);
      return maxSocRestOfToday.Value >= 99;
    }
  }

  public bool WillReachmaxSocTomorrow
  {
    get
    {
      var maxSocTomorrow = _batterySoC.Tomorrow.FirstMaxOrDefault(start: DateTime.Now);
      return maxSocTomorrow.Value >= 99;
    }
  }
}
