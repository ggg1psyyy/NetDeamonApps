using NetDeamon.apps.PVControl.Simulator;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace NetDeamon.apps.PVControl.Managers
{
  /// <summary>
  /// State a restart would lose, published as a JSON attribute (<c>restore_state</c>) and read back at startup: the
  /// decisions after a start continue from it instead of from nothing (2026-11-09 audit). One JSON format for all.
  /// </summary>
  public static class RestoreState
  {
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

    /// <summary>A state as the attribute's text.</summary>
    /// <typeparam name="T">The state's type.</typeparam>
    /// <param name="state">The state.</param>
    public static string ToJson<T>(T state) => JsonSerializer.Serialize(state, Options);

    /// <summary>A state from the attribute's text.</summary>
    /// <typeparam name="T">The state's type.</typeparam>
    /// <param name="json">The text; null or unreadable = none.</param>
    /// <returns>The state; null if there is none or it can't be read (an older format: start as before).</returns>
    public static T? FromJson<T>(string? json) where T : class
    {
      if (string.IsNullOrWhiteSpace(json)) return null;
      try { return JsonSerializer.Deserialize<T>(json, Options); }
      catch (JsonException) { return null; }
    }
  }

  /// <summary>A planned or running session of a load (<see cref="ExtraLoad"/> without its name and priority).</summary>
  /// <param name="Start">Start.</param>
  /// <param name="End">End.</param>
  /// <param name="PowerW">Power, W.</param>
  /// <param name="FromGrid">Directly from the grid (the battery held).</param>
  public sealed record SessionWindow(DateTime Start, DateTime End, int PowerW, bool FromGrid);

  /// <summary>
  /// A schedulable load's session state across a restart: whether it runs (the plan's decision, not the import cap's
  /// pause), when and in which mode its session started or stopped (the min-on and min-off latches), and its planned
  /// sessions (a planned later start, a split's blocks). Lost, a running EV counted as stopped: the "never stop a
  /// running session for a cheaper start" rule, its supply and both latches were gone.
  /// </summary>
  /// <param name="ChargeNow">The plan runs the load.</param>
  /// <param name="StartTime">Session start; null when not running.</param>
  /// <param name="StartMode">Mode at the start.</param>
  /// <param name="StopTime">Last stop; null when running or cleared.</param>
  /// <param name="StopMode">Mode at the stop.</param>
  /// <param name="Sessions">The planned sessions.</param>
  public sealed record LoadSessionState(bool ChargeNow, DateTime? StartTime, LoadSchedulingMode? StartMode,
    DateTime? StopTime, LoadSchedulingMode? StopMode, List<SessionWindow> Sessions)
  {
    /// <summary>A load's state now.</summary>
    /// <param name="load">The load.</param>
    public static LoadSessionState Of(SchedulableLoadRuntime load) => new(load.ChargeNow, load.SessionStartTime, load.SessionStartMode,
      load.SessionStopTime, load.SessionStopMode, load.ExtraLoads.Select(e => new SessionWindow(e.StartTime, e.EndTime, e.PowerW, e.FromGrid)).ToList());

    /// <summary>
    /// Restores the state into a load after a restart: sessions that ended meanwhile are dropped (and with them a run
    /// that has nothing left), the settings the latches compare with are today's (taken at the first plan,
    /// <see cref="SchedulableLoadRuntime.TakeSettingsRestore"/>) — unchanged over a restart.
    /// </summary>
    /// <param name="load">The load (just created).</param>
    /// <param name="now">Current time.</param>
    public void ApplyTo(SchedulableLoadRuntime load, DateTime now)
    {
      var sessions = Sessions.Where(s => s.End > now)
        .Select(s => new ExtraLoad { Name = load.Config.Name, Priority = load.Config.Priority, StartTime = s.Start, EndTime = s.End, PowerW = s.PowerW, FromGrid = s.FromGrid })
        .ToList();
      bool running = ChargeNow && sessions.Any(s => s.StartTime <= now);
      load.ChargeNow = running;
      load.ExtraLoads = sessions;
      load.PredictedEnd = sessions.Count > 0 ? sessions.Max(s => s.EndTime) : null;
      load.SessionStartTime = running ? StartTime : null;
      load.SessionStartMode = running ? StartMode : null;
      // A run that ended while PVControl was down stopped at its last session's end at the latest.
      load.SessionStopTime = running ? null : StopTime ?? (ChargeNow ? Sessions.Where(s => s.End <= now).Select(s => (DateTime?)s.End).Max() : null);
      load.SessionStopMode = running ? null : StopMode ?? (ChargeNow ? StartMode : null);
      load.MarkSettingsRestore();
    }
  }

  /// <summary>
  /// The warm-water control's timers across a restart: the last command and start (the start-acknowledge wait and the
  /// command cooldown — without them a start a few minutes before the restart read as "not taken" and was stopped),
  /// the last stop and whether a heating ran (the minimum off time), and the boost (pressed, and whether it ran).
  /// </summary>
  /// <param name="LastCommand">Last switch command.</param>
  /// <param name="LastStart">Last start command.</param>
  /// <param name="LastStop">Last end of a one-time heating.</param>
  /// <param name="WasActive">A one-time heating ran in the last cycle.</param>
  /// <param name="BoostSince">Boost pressed; null = none.</param>
  /// <param name="BoostSawRunning">The boost's heating has run.</param>
  public sealed record WarmWaterControlState(DateTime? LastCommand, DateTime? LastStart, DateTime? LastStop, bool WasActive,
    DateTime? BoostSince, bool BoostSawRunning);
}
