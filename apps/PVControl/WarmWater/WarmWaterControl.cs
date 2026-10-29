using System;
using System.Collections.Generic;
using System.Linq;

namespace NetDeamon.apps.PVControl.WarmWater;

/// <summary>How far PVControl controls the warm water.</summary>
public enum WarmWaterMode
{
  /// <summary>Hands off — the heat pump's automatic runs the tank; PVControl only forecasts it.</summary>
  Off,
  /// <summary>Plans and publishes, but doesn't touch the heat pump — to compare plan and automatic first.</summary>
  PlanOnly,
  /// <summary>Starts the planned one-time heatings.</summary>
  Auto,
}

/// <summary>Live inputs of one control decision.</summary>
/// <param name="Now">Current time.</param>
/// <param name="Temp">Tank temperature, °C.</param>
/// <param name="SwitchOn">One-time heating switch (the command); stays on after the setpoint is reached.</param>
/// <param name="OneTimeActive">One-time heating actually runs (the feedback).</param>
/// <param name="Setpoint">One-time setpoint, °C; null if unknown.</param>
/// <param name="LastCommand">Time of the last command.</param>
/// <param name="LastStart">When PVControl last switched the one-time heating on.</param>
/// <param name="Mode">Control mode.</param>
/// <param name="Runs">Planned runs.</param>
/// <param name="MaxTemp">Highest allowed setpoint, °C.</param>
/// <param name="BoostOn">Boost switch.</param>
/// <param name="BoostTemp">Boost target, °C.</param>
/// <param name="BoostSince">When the boost was switched on.</param>
/// <param name="BoostSawRunning">The heating was seen running since the boost started.</param>
/// <param name="BoostTimeoutMinutes">A boost ends after this long.</param>
/// <param name="LastStop">When a one-time heating last stopped (minimum off-time).</param>
/// <param name="ImportHeadroomW">Grid import left under the import cap, W; null = no cap.</param>
/// <param name="StartPowerW">Heat pump power of a heating, W (checked against the headroom).</param>
public readonly record struct ControlInput(
  DateTime Now, double Temp, bool SwitchOn, bool OneTimeActive, double? Setpoint, DateTime? LastCommand, DateTime? LastStart,
  WarmWaterMode Mode, IReadOnlyList<PlannedRun> Runs, double MaxTemp,
  bool BoostOn, double BoostTemp, DateTime? BoostSince, bool BoostSawRunning, int BoostTimeoutMinutes,
  DateTime? LastStop = null, int? ImportHeadroomW = null, int StartPowerW = 0);

/// <summary>What to do with the heat pump.</summary>
public enum ControlCommand
{
  None,
  /// <summary>Set the one-time setpoint to the target, then switch the one-time heating on.</summary>
  Start,
  /// <summary>Switch the one-time heating off — only once the heat pump has stopped (or a start wasn't taken).</summary>
  Stop,
  /// <summary>Raise the setpoint of the running one-time heating.</summary>
  SetSetpoint,
}

/// <summary>Result of <see cref="WarmWaterControl.Decide"/>.</summary>
/// <param name="Command">What to do.</param>
/// <param name="Target">Setpoint for Start / SetSetpoint, °C.</param>
/// <param name="EndBoost">Why the boost is over (turn the boost switch off); null = not over.</param>
/// <param name="Reason">Explanation for the sensor.</param>
public readonly record struct ControlDecision(ControlCommand Command, double? Target, string? EndBoost, string Reason)
{
  /// <summary>A decision to do nothing.</summary>
  /// <param name="reason">Explanation for the sensor.</param>
  public static ControlDecision Wait(string reason) => new(ControlCommand.None, null, null, reason);
}

/// <summary>
/// What to do with the heat pump this cycle. A running heating is never stopped (compressor starts
/// wear it): it continues to its setpoint, which may only be raised. The switch goes off only after
/// the heat pump stopped by itself or a start wasn't taken. New planned runs need ≥ <see cref="MinRunK"/>
/// rise and <see cref="MinOffTime"/> since the last; planned runs only in Auto, a boost in any mode.
/// </summary>
public static class WarmWaterControl
{
  /// <summary>A planned run is only started when it raises the tank at least this much.</summary>
  public const double MinRunK = 3.0;
  /// <summary>No planned start within this time after the last heating stopped.</summary>
  public static readonly TimeSpan MinOffTime = TimeSpan.FromMinutes(60);
  /// <summary>A boost is done this close to its temperature.</summary>
  public const double BoostReachedK = 0.3;
  /// <summary>No new command within this time after the last one — the heat pump and its sensors need time to react.</summary>
  public static readonly TimeSpan CommandCooldown = TimeSpan.FromMinutes(3);
  /// <summary>How long a start may go unacknowledged (switch on, "active" off) before it counts as not taken (measured: active within 5–7 s, compressor ~2.5 min).</summary>
  public static readonly TimeSpan StartAckTimeout = TimeSpan.FromMinutes(10);

  /// <summary>The control decision of this cycle.</summary>
  /// <param name="c">Live inputs.</param>
  /// <returns>Command, target and reason.</returns>
  public static ControlDecision Decide(ControlInput c)
  {
    bool cooledDown = c.LastCommand is not { } last || c.Now - last >= CommandCooldown;
    bool controlling = c.Mode == WarmWaterMode.Auto || c.BoostOn;

    string? endBoost = null;
    if (c.BoostOn)
      endBoost = c.Temp >= c.BoostTemp - BoostReachedK ? $"reached {c.Temp:F1} °C"
        : c.BoostSawRunning && !c.OneTimeActive ? "heat pump stopped"
        : c.BoostSince is { } since && c.Now - since >= TimeSpan.FromMinutes(c.BoostTimeoutMinutes) ? "timeout"
        : null;
    bool boosting = c.BoostOn && endBoost is null;

    // What the heating should reach now: the boost, or the planned run (Auto).
    var run = c.Mode == WarmWaterMode.Auto ? c.Runs.FirstOrDefault(r => r.Start <= c.Now && r.End > c.Now) : default;
    double? target = boosting ? SetpointFor(c.BoostTemp, c.MaxTemp)
      : run != default ? SetpointFor(run.TargetTemp, c.MaxTemp)
      : null;
    string who = boosting ? "Boost" : "Plan";

    ControlDecision With(ControlDecision d) => d with { EndBoost = endBoost, Reason = endBoost is null ? d.Reason : $"Boost over ({endBoost}); {d.Reason}" };

    // Heating runs: never stopped, the setpoint may only go up.
    if (c.SwitchOn && c.OneTimeActive)
    {
      if (controlling && target is { } up && (c.Setpoint is not { } sp || up >= sp + 0.25) && cooledDown)
        return With(new(ControlCommand.SetSetpoint, up, null, $"{who}: setpoint {c.Setpoint:F1} → {up:F1} °C"));
      return With(ControlDecision.Wait($"Heating to {c.Setpoint:F1} °C"));
    }

    // Switch on, nothing heats: the heating finished (the switch stays on), or our start isn't
    // taken yet — give it time, then switch off so the next start is clean.
    if (c.SwitchOn)
    {
      if (!controlling && c.LastStart is null)
        return ControlDecision.Wait(HandsOff(c.Mode));
      if (c.LastStart is { } started && c.Now - started < StartAckTimeout)
        return With(ControlDecision.Wait($"Waiting for the heat pump to start ({(c.Now - started).TotalMinutes:F0} min)"));
      return With(cooledDown
        ? new(ControlCommand.Stop, null, null, "Heating finished — switch off")
        : ControlDecision.Wait("Waiting for the heat pump"));
    }

    // Switch off: start if needed — not while the import cap has no room for the heat pump.
    string? capFull = c.ImportHeadroomW is { } room && room < c.StartPowerW
      ? $"Import cap: {room} W left, heating needs {c.StartPowerW} W — waiting" : null;
    if (boosting)
      return capFull is not null ? ControlDecision.Wait($"Boost: {capFull}") : cooledDown
        ? new(ControlCommand.Start, target, null, $"Boost: start heating to {target:F1} °C")
        : ControlDecision.Wait("Boost: waiting for the heat pump");
    if (c.Mode != WarmWaterMode.Auto)
      return With(ControlDecision.Wait(HandsOff(c.Mode)));
    if (run == default)
    {
      var next = c.Runs.FirstOrDefault(r => r.Start > c.Now);
      return With(ControlDecision.Wait(next == default ? "No heating planned" : $"Next heating {next.Start:HH:mm} → {next.TargetTemp:F1} °C"));
    }
    if (c.Temp > run.TargetTemp - MinRunK)
      return With(ControlDecision.Wait($"Planned run too small ({c.Temp:F1} → {run.TargetTemp:F1} °C, < {MinRunK:F0} K)"));
    if (c.LastStop is { } stopped && c.Now - stopped < MinOffTime)
      return With(ControlDecision.Wait($"Off-time: no start before {stopped + MinOffTime:HH:mm}"));
    if (capFull is not null)
      return With(ControlDecision.Wait($"Plan: {capFull}"));
    return With(cooledDown
      ? new(ControlCommand.Start, target, null, $"Plan: start heating to {target:F1} °C")
      : ControlDecision.Wait("Plan: waiting for the heat pump"));
  }

  /// <summary>Reason text for the modes that don't control.</summary>
  /// <param name="mode">Off or PlanOnly.</param>
  static string HandsOff(WarmWaterMode mode) => mode == WarmWaterMode.Off ? "Off (heat pump automatic)" : "Plan only (not controlling)";

  /// <summary>Comfort windows from now to the horizon: every period daily at its time (a window still open counts).</summary>
  /// <param name="periods">(start time, duration in minutes) per period.</param>
  /// <param name="now">Current time.</param>
  /// <param name="horizonEnd">End of the plan.</param>
  /// <param name="minTemp">Minimum temperature of the windows, °C.</param>
  /// <returns>Windows in time order.</returns>
  public static List<ComfortWindow> ComfortWindows(IEnumerable<(TimeOnly Time, int DurationMinutes)> periods, DateTime now, DateTime horizonEnd, double minTemp)
  {
    var windows = new List<ComfortWindow>();
    foreach (var (time, duration) in periods)
      for (var day = now.Date.AddDays(-1); day < horizonEnd; day = day.AddDays(1))
      {
        var start = day + time.ToTimeSpan();
        var end = start.AddMinutes(duration);
        if (end >= now && start < horizonEnd)
          windows.Add(new ComfortWindow(start, end, minTemp));
      }
    return windows.OrderBy(w => w.Start).ToList();
  }

  /// <summary>Heat pump setpoint for a target: rounded up to its 0.5 K steps, at most the maximum.</summary>
  /// <param name="target">Planned temperature, °C.</param>
  /// <param name="maxTemp">Highest setpoint, °C.</param>
  /// <returns>°C.</returns>
  public static double SetpointFor(double target, double maxTemp) => Math.Min(maxTemp, Math.Ceiling(target * 2) / 2);
}
