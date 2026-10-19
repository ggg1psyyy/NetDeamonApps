using System;
using System.Collections.Generic;
using System.Linq;

namespace NetDeamon.apps.PVControl.WarmWater;

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
/// <param name="SwitchOn">State of the one-time heating switch — the command. It stays on after the
/// heat pump reached the setpoint, so it has to be switched off again.</param>
/// <param name="OneTimeActive">Whether the one-time heating actually runs — the feedback.</param>
/// <param name="Setpoint">Current one-time setpoint, null when unknown.</param>
/// <param name="LastStart">When PVControl last switched the one-time heating on.</param>
/// <param name="BoostSawRunning">The one-time heating was seen running since the boost started.</param>
public readonly record struct ControlInput(
  DateTime Now, double Temp, bool SwitchOn, bool OneTimeActive, double? Setpoint, DateTime? LastCommand, DateTime? LastStart,
  WarmWaterMode Mode, IReadOnlyList<PlannedRun> Runs, double MaxTemp,
  bool BoostOn, double BoostTemp, DateTime? BoostSince, bool BoostSawRunning, int BoostTimeoutMinutes);

public enum ControlCommand
{
  None,
  /// <summary>Set the one-time setpoint to the target, then switch the one-time heating on.</summary>
  Start,
  /// <summary>Switch the one-time heating off (run finished, or the plan doesn't want it now).</summary>
  Stop,
  /// <summary>Change the setpoint of the running one-time heating.</summary>
  SetSetpoint,
}

/// <param name="Target">Setpoint for Start / SetSetpoint.</param>
/// <param name="EndBoost">Why the boost is over (turn the boost switch off), or null.</param>
public readonly record struct ControlDecision(ControlCommand Command, double? Target, string? EndBoost, string Reason)
{
  public static ControlDecision Wait(string reason) => new(ControlCommand.None, null, null, reason);
}

/// <summary>
/// What to do with the heat pump this cycle. The one-time switch is the command, the "active"
/// sensor the feedback: a run is started by setpoint + switch on, finished or cancelled by switch
/// off (the switch stays on after the heat pump stopped at the setpoint), and its setpoint follows
/// the plan's target. Only in Auto, and for a boost in any mode. Pure, so it's testable without
/// Home Assistant.
/// </summary>
public static class WarmWaterControl
{
  /// <summary>A planned run isn't started when the tank is already this close to its target.</summary>
  public const double StartDeadbandK = 1.0;
  /// <summary>A boost is done this close to its temperature.</summary>
  public const double BoostReachedK = 0.3;
  /// <summary>No new command within this time after the last one — the heat pump and its sensors need time to react.</summary>
  public static readonly TimeSpan CommandCooldown = TimeSpan.FromMinutes(3);
  /// <summary>
  /// How long a start may go unacknowledged (switch on, "active" still off) before it counts as
  /// not taken and the switch is reset. Measured: "active" follows the switch within 5–7 s, the
  /// compressor ~2.5 min later (2026-10-15: 144 s) — waiting longer covers a slower start.
  /// </summary>
  public static readonly TimeSpan StartAckTimeout = TimeSpan.FromMinutes(10);

  public static ControlDecision Decide(ControlInput c)
  {
    bool cooledDown = c.LastCommand is not { } last || c.Now - last >= CommandCooldown;

    if (c.BoostOn)
    {
      string? end = c.Temp >= c.BoostTemp - BoostReachedK ? $"reached {c.Temp:F1} °C"
        : c.BoostSawRunning && !c.OneTimeActive ? "heat pump stopped"
        : c.BoostSince is { } since && c.Now - since >= TimeSpan.FromMinutes(c.BoostTimeoutMinutes) ? "timeout"
        : null;
      if (end is not null)
        // The plan takes over from here (in Auto it may keep heating) — it starts again if needed.
        return new(c.SwitchOn ? ControlCommand.Stop : ControlCommand.None, null, end, $"Boost over ({end})");
      return Drive(c, SetpointFor(c.BoostTemp, c.MaxTemp), cooledDown, "Boost");
    }

    if (c.Mode != WarmWaterMode.Auto)
      return ControlDecision.Wait(c.Mode == WarmWaterMode.Off ? "Off (heat pump automatic)" : "Plan only (not controlling)");

    var run = c.Runs.FirstOrDefault(r => r.Start <= c.Now && r.End > c.Now);
    if (run == default)
    {
      if (c.SwitchOn)
        return cooledDown
          ? new(ControlCommand.Stop, null, null, c.OneTimeActive ? "Plan says stop" : "Run finished — switch off")
          : ControlDecision.Wait("Waiting for the heat pump");
      var next = c.Runs.FirstOrDefault(r => r.Start > c.Now);
      return ControlDecision.Wait(next == default ? "No heating planned" : $"Next heating {next.Start:HH:mm} → {next.TargetTemp:F1} °C");
    }
    if (!c.SwitchOn && c.Temp >= run.TargetTemp - StartDeadbandK)
      return ControlDecision.Wait($"Planned run not needed ({c.Temp:F1} °C ≈ {run.TargetTemp:F1} °C)");
    return Drive(c, SetpointFor(run.TargetTemp, c.MaxTemp), cooledDown, "Plan");
  }

  /// <summary>Gets a one-time heating to <paramref name="target"/> running, or keeps it there.</summary>
  static ControlDecision Drive(ControlInput c, double target, bool cooledDown, string who)
  {
    if (!cooledDown)
      return ControlDecision.Wait($"{who}: waiting for the heat pump");
    if (!c.SwitchOn)
      return new(ControlCommand.Start, target, null, $"{who}: start heating to {target:F1} °C");
    if (!c.OneTimeActive)
    {
      // Switch on but nothing runs: left on after an earlier run, or the heat pump didn't take
      // the start. Off now, the next cycle starts again cleanly — but give our own start time.
      if (c.LastStart is { } started && c.Now - started < StartAckTimeout)
        return ControlDecision.Wait($"{who}: waiting for the heat pump to start ({(c.Now - started).TotalMinutes:F0} min)");
      return new(ControlCommand.Stop, null, null, $"{who}: switch on without heating — reset");
    }
    if (c.Setpoint is not { } sp || Math.Abs(sp - target) >= 0.25)
      return new(ControlCommand.SetSetpoint, target, null, $"{who}: setpoint {c.Setpoint:F1} → {target:F1} °C");
    return ControlDecision.Wait($"{who}: heating to {target:F1} °C");
  }

  /// <summary>
  /// The comfort windows from now to <paramref name="horizonEnd"/>: every period daily at its time
  /// for its duration (a window that started before now and is still open counts too).
  /// </summary>
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

  /// <summary>The heat pump's setpoint has 0.5 K steps — round up, so it heats at least as far as planned.</summary>
  public static double SetpointFor(double target, double maxTemp) => Math.Min(maxTemp, Math.Ceiling(target * 2) / 2);
}
