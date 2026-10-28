using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using NetDeamon.apps;
using NetDeamon.apps.PVControl.Simulator;
using NetDaemon.HassModel.Entities;
using static NetDeamon.apps.PVControl.PVControlCommon;

namespace NetDeamon.apps.PVControl.Managers
{
  /// <summary>
  /// Snapshot of the live, user-tunable settings that actually feed a scheduling decision,
  /// taken when a session starts. Compared against their CURRENT values each cycle by
  /// FindLoadWindow's minimum on-time latch (value-equality via record struct) so that a manual
  /// change to any of them — not just a mode switch — bypasses the latch and takes effect on
  /// the very next cycle, instead of silently waiting out up to MinWindowMinutes on a decision
  /// made under settings that no longer apply.
  /// </summary>
  public readonly record struct SessionStartSettings(
    int PreferredMinSocPercent, int AbsoluteMinSocPercent, bool EnforcePreferredSoc,
    float ForceChargeMaxPrice, float TargetLevel, TimeOnly? ReadyBy);

  /// <summary>
  /// Runtime state of a schedulable extra load (EV charger, warm-water heat pump, etc.).
  /// Wraps <see cref="SchedulableLoadConfig"/> with live HA entity state, a power
  /// running-average, and the result of the last simulation window search.
  /// </summary>
  public class SchedulableLoadRuntime
  {
    public SchedulableLoadConfig Config { get; }

    // ── HA entity references — set by PVControl during RegisterControlSensors ──────────────
    /// <summary>Mode select entity. Null when Mode is defined statically in YAML config.</summary>
    public Entity? ModeEntity { get; set; }

    /// <summary>Target-level number entity. Null when TargetLevel is defined statically in YAML config.</summary>
    public Entity? TargetLevelEntity { get; set; }

    /// <summary>Charge-now binary sensor — always auto-created in HA.</summary>
    public Entity ChargeNowEntity { get; set; } = null!;

    /// <summary>Accumulating total energy consumed by this load (kWh). Null when ActualPowerEntity is not configured.</summary>
    public Entity? TotalEnergyKwhEntity { get; set; }

    /// <summary>Accumulating total import cost for this load (€ brutto). Null when ActualPowerEntity is not configured.</summary>
    public Entity? TotalCostEurEntity { get; set; }

    /// <summary>Last value of ActualEnergyEntity (kWh), used to compute the delta on each state change.</summary>
    public float LastEnergySum { get; set; }

    /// <summary>Running power average. Set by HouseEnergy when ActualPowerEntity is configured.</summary>
    public RunningIntAverage? PowerAverage { get; internal set; }

    // ── Live derived properties ────────────────────────────────────────────────────────────
    /// <summary>Current scheduling mode: from YAML config or the auto-created HA select entity.</summary>
    public LoadSchedulingMode Mode
    {
      get
      {
        if (Config.Mode.HasValue) return Config.Mode.Value;
        if (ModeEntity is not null
            && ModeEntity.TryGetStateValue(out string s)
            && Enum.TryParse(s, ignoreCase: true, out LoadSchedulingMode m))
          return m;
        return LoadSchedulingMode.Off;
      }
    }

    /// <summary>Target level: from YAML config or the auto-created HA number entity.</summary>
    public float TargetLevel
    {
      get
      {
        if (Config.TargetLevel.HasValue) return Config.TargetLevel.Value;
        if (TargetLevelEntity is not null && TargetLevelEntity.TryGetStateValue(out float v)) return v;
        return Config.TargetLevelDefault;
      }
    }

    /// <summary>"Ready by" time-of-day entity (HA MQTT time). Always auto-created in HA.</summary>
    public Entity? ReadyByEntity { get; set; }

    /// <summary>Switch that turns the ready-by deadline on. Always auto-created in HA.</summary>
    public Entity? EnforceReadyByEntity { get; set; }

    /// <summary>Ready-by time of day, or null when not enforced / not a valid time.</summary>
    public TimeOnly? ReadyByTime =>
      ReadyByEntity is not null && EnforceReadyByEntity is not null
      && EnforceReadyByEntity.TryGetStateValue(out bool enforced) && enforced
      && TryParseReadyBy(ReadyByEntity.State, out var t) ? t : null;

    /// <summary>Parses a ready-by time in HA's formats (H:mm, HH:mm, with or without seconds).</summary>
    /// <param name="text">Time text.</param>
    /// <param name="time">The time; default on failure.</param>
    /// <returns>False if the text is no time.</returns>
    public static bool TryParseReadyBy(string? text, out TimeOnly time) =>
      TimeOnly.TryParseExact(text?.Trim(), ["H:mm", "HH:mm", "H:mm:ss", "HH:mm:ss"], CultureInfo.InvariantCulture, DateTimeStyles.None, out time);


    /// <summary>Current level read from the HA sensor entity (SoC %, temperature °C, …).</summary>
    public float CurrentLevel
    {
      get
      {
        if (!Config.CurrentLevelEntity.TryGetStateValue(out float v))
          PVCC_Logger.LogError("Load {load}: could not get current level from {entity}", Config.Name, Config.CurrentLevelEntity.EntityId);
        return v;
      }
    }

    // ── Learned characteristics (start from config, persisted in HA, editable there) ────────
    /// <summary>Number entity holding <see cref="EnergyPerLevelKwh"/>. Always auto-created in HA.</summary>
    public Entity? EnergyPerLevelEntity { get; set; }

    /// <summary>Number entity holding <see cref="LearnedPowerW"/>. Always auto-created in HA.</summary>
    public Entity? ChargePowerEntity { get; set; }

    /// <summary>
    /// Measured energy per level unit (kWh, e.g. per % SoC), on the meter's side — charger
    /// losses included. Starts at Config.EnergyPerLevelUnitKwh; learned while running.
    /// </summary>
    public float EnergyPerLevelKwh { get; set; }

    /// <summary>Typical measured power while running (W). Starts at Config.AvgPowerW; learned while running.</summary>
    public double LearnedPower { get; set; }
    public int LearnedPowerW => (int)Math.Round(LearnedPower);

    public LevelEnergyLearner EnergyLearner { get; } = new();
    public ChargePowerLearner PowerLearner { get; } = new();

    /// <summary>
    /// Effective power draw in W: running average of actual power if above MinActivePowerW,
    /// otherwise the learned typical power.
    /// </summary>
    public int EffectivePowerW
    {
      get
      {
        if (PowerAverage is not null)
        {
          int avg = PowerAverage.GetAverage();
          if (avg > Config.MinActivePowerW) return avg;
        }
        return LearnedPowerW;
      }
    }

    /// <summary>True while the load measurably draws power.</summary>
    public bool IsDrawingPower => PowerAverage is not null && PowerAverage.GetAverage() > Config.MinActivePowerW;

    /// <summary>Energy still needed to reach TargetLevel, in Wh.</summary>
    public int EnergyNeededWh =>
      (int)Math.Max(0, (TargetLevel - CurrentLevel) * EnergyPerLevelKwh * 1000);

    /// <summary>Estimated duration to reach TargetLevel at EffectivePowerW, in minutes.</summary>
    public int DurationMinutes
    {
      get
      {
        int pw = EffectivePowerW;
        return pw > 0 ? Energy.Minutes(EnergyNeededWh, pw) : 0;
      }
    }

    // ── Energy/cost running totals (maintained in memory; persisted to HA on each update) ───
    /// <summary>Accumulated kWh delivered to this load since the app was last reset. Written to HA each update.</summary>
    public float TotalEnergyKwh { get; set; }
    /// <summary>Accumulated cost (€) for energy delivered to this load. Written to HA each update.</summary>
    public float TotalCostEur { get; set; }

    // ── Simulation output — written by HouseEnergy.FindLoadWindow ──────────────────────────
    /// <summary>True when this load should be running now according to the simulation oracle.</summary>
    public bool ChargeNow { get; internal set; }

    /// <summary>Human-readable explanation of the current ChargeNow decision.</summary>
    public string ChargeReason { get; internal set; } = "Not initialized";

    /// <summary><see cref="ChargeReason"/> for display, with the latch minutes counted live.</summary>
    /// <param name="now">Current time.</param>
    public string ChargeReasonAt(DateTime now) => WithLiveLatchMinutes(ChargeReason, now, SessionStartTime, SessionStopTime);

    static readonly System.Text.RegularExpressions.Regex LatchMinutes =
      new(@"(\d+)/(\d+) min min-(on|off)", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>Replaces "x/y min min-on|off" in a reason with the live minutes (the reason is built only when the search runs).</summary>
    /// <param name="reason">Reason text.</param>
    /// <param name="now">Current time.</param>
    /// <param name="sessionStart">Session start (min-on counts from here).</param>
    /// <param name="sessionStop">Session stop (min-off counts from here).</param>
    /// <returns>The reason, x at most y; unchanged without a latch or the time it counts from.</returns>
    public static string WithLiveLatchMinutes(string reason, DateTime now, DateTime? sessionStart, DateTime? sessionStop) =>
      LatchMinutes.Replace(reason, m =>
      {
        var since = m.Groups[3].Value == "on" ? sessionStart : sessionStop;
        if (since is not { } t) return m.Value;
        int length = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        double elapsed = Math.Clamp((now - t).TotalMinutes, 0, length);
        return $"{elapsed.ToString("F0", CultureInfo.InvariantCulture)}/{length} min min-{m.Groups[3].Value}";
      });

    /// <summary>Predicted end of the current active session. Null when not running.</summary>
    public DateTime? PredictedEnd { get; internal set; }
    /// <summary>The PriorityPlus scan's candidates of the last search (<see cref="Simulator.PlannedStart.Scan"/>); "n/a" when it didn't run.</summary>
    public string PriceScan { get; internal set; } = "n/a";

    /// <summary>
    /// Wall-clock time when the current session started (ChargeNow transitioned false→true).
    /// Used by FindLoadWindow to enforce the minimum on-time latch (MinWindowMinutes).
    /// Null when not active.
    /// </summary>
    public DateTime? SessionStartTime { get; internal set; }

    /// <summary>
    /// The scheduling mode that was active when the current session started. Used by
    /// FindLoadWindow to bypass the minimum on-time latch when the user changes Mode mid-session
    /// — otherwise a manual mode switch would silently keep echoing the PREVIOUS mode's schedule
    /// for up to MinWindowMinutes, since the latch itself only checks whether the load was
    /// already active, not which mode produced that session. Null when not active.
    /// </summary>
    public LoadSchedulingMode? SessionStartMode { get; internal set; }

    /// <summary>
    /// Snapshot of the live settings that fed the decision when the current session started
    /// (see <see cref="SessionStartSettings"/>). Used by FindLoadWindow to ALSO bypass the
    /// minimum on-time latch when the user changes one of them mid-session — a manual setting
    /// change should take effect on the next cycle, not silently wait out up to
    /// MinWindowMinutes of a decision made under settings that no longer apply, same principle
    /// as the mode-switch bypass above. Null when not active.
    /// </summary>
    public SessionStartSettings? SessionStartSettings { get; internal set; }

    /// <summary>When the last session stopped (for the min-off latch). Null when running or cleared.</summary>
    public DateTime? SessionStopTime { get; internal set; }
    public LoadSchedulingMode? SessionStopMode { get; internal set; }
    public SessionStartSettings? SessionStopSettings { get; internal set; }

    /// <summary>Clears the min-off latch (e.g. the load became available again).</summary>
    public void ClearOffLatch() => SessionStopTime = null;

    /// <summary>End of the min-off latch.</summary>
    /// <param name="stopTime">When the load stopped; null = no recorded stop.</param>
    /// <param name="minOffMinutes">Minimum off time; 0 = disabled.</param>
    /// <param name="now">Current time.</param>
    /// <param name="sameModeAndSettings">False: mode or settings changed since the stop, the latch is bypassed.</param>
    /// <returns>The end; null when the latch doesn't hold.</returns>
    public static DateTime? OffLatchUntil(DateTime? stopTime, int minOffMinutes, DateTime now, bool sameModeAndSettings)
    {
      if (stopTime is null || minOffMinutes <= 0 || !sameModeAndSettings) return null;
      var until = stopTime.Value.AddMinutes(minOffMinutes);
      return now < until ? until : null;
    }

    /// <summary>Moves sessions so none starts before a time; each still delivers its energy (<see cref="SessionPlanner.EndForEnergy"/>:
    /// one that began in the running slot has its end counted from now, not from its start) and keeps its supply (<see cref="ExtraLoad.FromGrid"/>).</summary>
    /// <param name="sessions">Sessions.</param>
    /// <param name="notBefore">Earliest start.</param>
    /// <param name="now">Current time.</param>
    /// <returns>The sessions; unchanged ones are the same objects.</returns>
    public static List<ExtraLoad> DelayUntil(IEnumerable<ExtraLoad> sessions, DateTime notBefore, DateTime now) =>
      sessions.Select(e => e.StartTime >= notBefore ? e
        : e.Between(notBefore, SessionPlanner.EndForEnergy(notBefore, (int)Math.Round(e.EnergyFromWh(now)), e.PowerW, now))).ToList();

    // ── Replanning (see ReplanPolicy) ──────────────────────────────────────────────────────
    /// <summary>The inputs of the last FindLoadWindow run. Null until the first plan.</summary>
    public ReplanSnapshot? LastPlan { get; set; }

    /// <summary>Why the last FindLoadWindow run happened (published as an attribute).</summary>
    public string LastPlanReason { get; set; } = "";

    private string? _replanRequest;

    /// <summary>Forces a replan on the next cycle; the first reason wins.</summary>
    /// <param name="reason">Why (shown as the plan reason).</param>
    public void RequestReplan(string reason) => _replanRequest ??= reason;

    /// <summary>Returns and clears a pending replan request.</summary>
    /// <returns>The reason; null if none.</returns>
    public string? TakeReplanRequest()
    {
      var r = _replanRequest;
      _replanRequest = null;
      return r;
    }

    /// <summary>ExtraLoad windows found by the last FindLoadWindow call.</summary>
    public List<ExtraLoad> ExtraLoads { get; internal set; } = [];

    /// <summary>
    /// Follow-up sessions planned after <see cref="ExtraLoads"/> until the target level is
    /// reached (PriorityPlus only). Preview for HA attributes — not part of the control
    /// simulation; each cycle re-decides ChargeNow on its own.
    /// </summary>
    public List<ExtraLoad> PlannedSessions { get; internal set; } = [];

    /// <summary>Live state of a load, starting from its configuration.</summary>
    /// <param name="config">The load's YAML configuration.</param>
    public SchedulableLoadRuntime(SchedulableLoadConfig config)
    {
      Config = config;
      EnergyPerLevelKwh = config.EnergyPerLevelUnitKwh;
      LearnedPower = config.AvgPowerW;
    }

    /// <summary>HA entity-ID slug: lowercase Name with spaces replaced by underscores.</summary>
    public string Slug => Config.Name.ToSlug();
  }
}
