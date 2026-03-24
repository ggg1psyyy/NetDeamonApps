using System;
using System.Collections.Generic;
using System.Linq;
using NetDeamon.apps;
using NetDeamon.apps.PVControl;

namespace NetDeamon.apps.PVControl.Simulator;

/// <summary>
/// Complete output of a single <see cref="EnergySimulator.Simulate"/> run.
///
/// Wraps the raw per-slot timeline and pre-computes all derived values that were
/// previously scattered across HouseEnergy (SimWillReachMaxSocToday, SimOvernightMinSocOk,
/// HasNewGrid, IsGridCheap) and PVWindows (FirstRelevantPVEnergyToday/Tomorrow,
/// CurrentPVPeriod) so callers never have to re-derive them from a raw slot list.
///
/// PV window boundaries are computed from the full prediction dictionaries (not only the
/// simulated slots), so they are correct even when the simulation starts mid-day.
/// </summary>
public class SimulationResult
{
  private readonly SimulationInput _input;
  private const int PvNetThresholdWh = 50;

  // ── Core timeline ─────────────────────────────────────────────────────────────────────
  /// <summary>Per-slot simulation timeline from start time through end of tomorrow.</summary>
  public IReadOnlyList<SimulationSlot> Slots { get; }

  // ── PV window boundaries ──────────────────────────────────────────────────────────────
  /// <summary>First slot today where net PV (PV − load) exceeds 50 Wh. Null if no PV today.</summary>
  public DateTime? FirstRelevantPVEnergyToday { get; }

  /// <summary>Last slot today where net PV exceeds 50 Wh. Null if no PV today.</summary>
  public DateTime? LastRelevantPVEnergyToday { get; }

  /// <summary>First slot tomorrow where net PV exceeds 50 Wh. Null if no PV tomorrow.</summary>
  public DateTime? FirstRelevantPVEnergyTomorrow { get; }

  /// <summary>Last slot tomorrow where net PV exceeds 50 Wh. Null if no PV tomorrow.</summary>
  public DateTime? LastRelevantPVEnergyTomorrow { get; }

  /// <summary>Whether simulation start time is before, inside, or after today's solar window.</summary>
  public PVPeriods CurrentPVPeriod { get; }

  // ── SoC peaks (simulation-derived) ────────────────────────────────────────────────────
  /// <summary>True if any simulated slot today shows SoC ≥ 99 %.</summary>
  public bool WillReachMaxSocToday { get; }

  /// <summary>True if any simulated slot tomorrow shows SoC ≥ 99 %.</summary>
  public bool WillReachMaxSocTomorrow { get; }

  /// <summary>Total simulated time today during which SoC is at or above 99 % (15-min steps).</summary>
  public TimeSpan MaxSocDurationToday { get; }

  // ── Overnight SoC floor ───────────────────────────────────────────────────────────────
  /// <summary>
  /// Lowest SoC (%) reached in the overnight window (sunset today → first PV tomorrow).
  /// Returns 100 when no overnight window exists within this simulation.
  /// </summary>
  public int OvernightMinSocReached { get; }

  // ── Inverter mode schedule ─────────────────────────────────────────────────────────────
  /// <summary>Slot start times where the simulator chose force_charge mode.</summary>
  public IReadOnlySet<DateTime> ForceChargeSlots { get; }

  // ── Construction ──────────────────────────────────────────────────────────────────────
  internal SimulationResult(List<SimulationSlot> slots, SimulationInput input)
  {
    _input = input;
    Slots = slots.AsReadOnly();

    var today    = input.StartTime.Date;
    var tomorrow = today.AddDays(1);
    var pv       = input.PVPredictionWh;
    var load     = input.LoadPredictionWh;

    // PV windows from the full prediction dictionaries so boundaries cover the whole day
    // even when the simulation starts mid-day (e.g. sunrise at 07:00 when started at 14:00).
    FirstRelevantPVEnergyToday    = PvEdge(pv, load, today,    first: true);
    LastRelevantPVEnergyToday     = PvEdge(pv, load, today,    first: false);
    FirstRelevantPVEnergyTomorrow = PvEdge(pv, load, tomorrow, first: true);
    LastRelevantPVEnergyTomorrow  = PvEdge(pv, load, tomorrow, first: false);

    var now = input.StartTime;
    CurrentPVPeriod =
      (FirstRelevantPVEnergyToday is null || now < FirstRelevantPVEnergyToday) ? PVPeriods.BeforePV :
      (LastRelevantPVEnergyToday  is null || now > LastRelevantPVEnergyToday)  ? PVPeriods.AfterPV  :
      PVPeriods.InPVPeriod;

    // SoC peaks from simulation
    WillReachMaxSocToday    = slots.Any(s => s.Time.Date == today    && s.SoC >= 99);
    WillReachMaxSocTomorrow = slots.Any(s => s.Time.Date == tomorrow && s.SoC >= 99);
    MaxSocDurationToday     = TimeSpan.FromMinutes(15 * slots.Count(s => s.Time.Date == today && s.SoC >= 99));

    // Overnight floor: window from last PV today to first PV tomorrow
    if (LastRelevantPVEnergyToday.HasValue && FirstRelevantPVEnergyTomorrow.HasValue)
    {
      var overnight = slots
        .Where(s => s.Time >= LastRelevantPVEnergyToday && s.Time <= FirstRelevantPVEnergyTomorrow)
        .ToList();
      OvernightMinSocReached = overnight.Count > 0 ? overnight.Min(s => s.SoC) : 100;
    }
    else
      OvernightMinSocReached = 100;

    // Force-charge slot index
    ForceChargeSlots = slots
      .Where(s => s.State.Mode == InverterModes.force_charge)
      .Select(s => s.Time)
      .ToHashSet();
  }

  // ── Derived predicates ─────────────────────────────────────────────────────────────────

  /// <summary>
  /// True if the overnight SoC floor stays at or above the required minimum.
  /// Uses <see cref="SimulationInput.PreferredMinSocPercent"/> when
  /// <paramref name="alwaysEnforcePreferred"/> is true or
  /// <see cref="SimulationInput.EnforcePreferredSoc"/> is set;
  /// uses <see cref="SimulationInput.AbsoluteMinSocPercent"/> otherwise.
  /// </summary>
  public bool IsOvernightMinSocOk(bool alwaysEnforcePreferred = false)
  {
    var threshold = (alwaysEnforcePreferred || _input.EnforcePreferredSoc)
      ? _input.PreferredMinSocPercent
      : _input.AbsoluteMinSocPercent;
    return OvernightMinSocReached >= threshold;
  }

  /// <summary>
  /// True if this simulation introduced any force_charge slots not present in
  /// <paramref name="baseline"/> — i.e. the extra load causes new grid charging.
  /// </summary>
  public bool HasNewGridVs(SimulationResult baseline)
    => ForceChargeSlots.Any(t => !baseline.ForceChargeSlots.Contains(t));

  /// <summary>
  /// True if every new force_charge slot (vs <paramref name="baseline"/>) falls within
  /// a price window at or below <see cref="SimulationInput.ForceChargeMaxPrice"/>.
  /// Returns true when no new grid slots were introduced.
  /// </summary>
  public bool IsGridCheapVs(SimulationResult baseline)
    => !ForceChargeSlots
      .Where(t => !baseline.ForceChargeSlots.Contains(t))
      .Any(t => !_input.ImportPrices.Any(p =>
        p.StartTime <= t && p.EndTime > t && p.Price <= _input.ForceChargeMaxPrice));

  // ── Helpers ────────────────────────────────────────────────────────────────────────────
  private static DateTime? PvEdge(
    Dictionary<DateTime, int> pv,
    Dictionary<DateTime, int> load,
    DateTime date,
    bool first)
  {
    var candidates = pv
      .Where(k => k.Key.Date == date && k.Value - load.GetValueOrDefault(k.Key, 0) > PvNetThresholdWh)
      .Select(k => (DateTime?)k.Key);
    return first ? candidates.FirstOrDefault() : candidates.LastOrDefault();
  }
}
