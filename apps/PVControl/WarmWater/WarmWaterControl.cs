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
/// <param name="LastStop">When a one-time heating last stopped (active → off), for the minimum off-time.</param>
/// <param name="BoostSawRunning">The one-time heating was seen running since the boost started.</param>
public readonly record struct ControlInput(
  DateTime Now, double Temp, bool SwitchOn, bool OneTimeActive, double? Setpoint, DateTime? LastCommand, DateTime? LastStart,
  WarmWaterMode Mode, IReadOnlyList<PlannedRun> Runs, double MaxTemp,
  bool BoostOn, double BoostTemp, DateTime? BoostSince, bool BoostSawRunning, int BoostTimeoutMinutes,
  DateTime? LastStop = null);

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

/// <param name="Target">Setpoint for Start / SetSetpoint.</param>
/// <param name="EndBoost">Why the boost is over (turn the boost switch off), or null.</param>
public readonly record struct ControlDecision(ControlCommand Command, double? Target, string? EndBoost, string Reason)
{
  public static ControlDecision Wait(string reason) => new(ControlCommand.None, null, null, reason);
}

/// <summary>
/// What to do with the heat pump this cycle. The one-time switch is the command, the "active"
/// sensor the feedback. Every compressor start wears the heat pump, so a heating that runs is
/// never stopped: it continues to its setpoint, which may only be raised (a later plan or a boost
/// wanting more). The switch is switched off only after the heat pump stopped by itself (it stays
/// on otherwise), or when a start wasn't taken. New planned runs need ≥ 3 K of rise and 60 min since
/// the last run ended. Planned runs only in Auto; a boost in any mode. Pure, so it's testable
/// without Home Assistant.
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
  /// <summary>
  /// How long a start may go unacknowledged (switch on, "active" still off) before it counts as
  /// not taken and the switch is reset. Measured: "active" follows the switch within 5–7 s, the
  /// compressor ~2.5 min later (2026-10-15: 144 s) — waiting longer covers a slower start.
  /// </summary>
  public static readonly TimeSpan StartAckTimeout = TimeSpan.FromMinutes(10);

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

    // Switch off: start if needed.
    if (boosting)
      return cooledDown
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
    return With(cooledDown
      ? new(ControlCommand.Start, target, null, $"Plan: start heating to {target:F1} °C")
      : ControlDecision.Wait("Plan: waiting for the heat pump"));
  }

  static string HandsOff(WarmWaterMode mode) => mode == WarmWaterMode.Off ? "Off (heat pump automatic)" : "Plan only (not controlling)";

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
