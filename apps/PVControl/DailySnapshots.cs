using System;
using System.Collections.Generic;
using System.Linq;
using NetDeamon.apps;
using NetDeamon.apps.PVControl.Predictions;
using NetDeamon.apps.PVControl.Simulator;

namespace NetDeamon.apps.PVControl;

/// <summary>
/// Holds the once-per-day snapshot of SoC, charge, discharge, and inverter-mode predictions
/// used for comparing "what we predicted at midnight" against "what actually happened".
/// Snapshot is lazily refreshed at 00:01 or if older than 24 h.
/// </summary>
public class DailySnapshots
{
  private readonly Prediction _pvPrediction;
  private readonly Prediction _loadPrediction;
  private readonly Prediction _batterySoCPrediction;
  private readonly Func<SimulationResult> _getSimulationResult;

  private Dictionary<DateTime, int> _dailySoCPrediction = [];
  private Dictionary<DateTime, string> _dailyModePrediction = [];
  private Dictionary<DateTime, int> _dailyChargePrediction = [];
  private Dictionary<DateTime, int> _dailyDischargePrediction = [];

  public DateTime LastSnapshotUpdate { get; private set; } = default;

  public DailySnapshots(
    Prediction pvPrediction,
    Prediction loadPrediction,
    Prediction batterySoCPrediction,
    Func<SimulationResult> getSimulationResult)
  {
    _pvPrediction = pvPrediction;
    _loadPrediction = loadPrediction;
    _batterySoCPrediction = batterySoCPrediction;
    _getSimulationResult = getSimulationResult;
  }

  private void UpdateSnapshots()
  {
    DateTime now = DateTime.Now;
    if (_dailySoCPrediction.Count == 0 || _dailyChargePrediction.Count == 0 || _dailyDischargePrediction.Count == 0
        || now is { Hour: 0, Minute: 1 }
        || (now - LastSnapshotUpdate).TotalMinutes > 24 * 60)
    {
      _dailyChargePrediction = _pvPrediction.TodayAndTomorrow.GetRunningSumsDaily();
      _dailyDischargePrediction = _loadPrediction.TodayAndTomorrow.GetRunningSumsDaily();
      // Snapshot the current simulation result so we can later compare prediction vs. reality.
      // RunSimulation() is always called before snapshot access in the 15-min cycle,
      // so _batterySoCPrediction and the simulation result already reflect the latest simulation.
      _dailySoCPrediction = new Dictionary<DateTime, int>(_batterySoCPrediction.TodayAndTomorrow);
      // Also snapshot the inverter mode per slot so the snapshot data set is self-contained.
      _dailyModePrediction = _getSimulationResult().Slots.ToDictionary(s => s.Time, s => s.State.Mode.ToString());
      LastSnapshotUpdate = now;
    }
  }

  public Dictionary<DateTime, int> DailyBatterySoCPredictionTodayAndTomorrow
  {
    get { UpdateSnapshots(); return _dailySoCPrediction; }
  }

  public Dictionary<DateTime, string> DailyModePredictionTodayAndTomorrow
  {
    get { UpdateSnapshots(); return _dailyModePrediction; }
  }

  public Dictionary<DateTime, int> DailyChargePredictionTodayAndTomorrow
  {
    get { UpdateSnapshots(); return _dailyChargePrediction; }
  }

  public Dictionary<DateTime, int> DailyDischargePredictionTodayAndTomorrow
  {
    get { UpdateSnapshots(); return _dailyDischargePrediction; }
  }
}
