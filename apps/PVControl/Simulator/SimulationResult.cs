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
/// <summary>
/// Implements <see cref="IReadOnlyList{SimulationSlot}"/> so callers can iterate slots
/// directly (e.g. LINQ, foreach, indexer) without going through the <see cref="Slots"/> property.
/// </summary>
public class SimulationResult : IReadOnlyList<SimulationSlot>
{
  private readonly SimulationInput _input;
  private const int PvNetThresholdWh = 50;

  // ── Core timeline ─────────────────────────────────────────────────────────────────────
  /// <summary>Per-slot simulation timeline from start time through end of tomorrow.</summary>
  public IReadOnlyList<SimulationSlot> Slots { get; }

  // IReadOnlyList<SimulationSlot> delegation — lets callers treat SimulationResult as a slot list
  public int Count => Slots.Count;
  public SimulationSlot this[int index] => Slots[index];
  public IEnumerator<SimulationSlot> GetEnumerator() => Slots.GetEnumerator();
  System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => ((System.Collections.IEnumerable)Slots).GetEnumerator();

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
  /// Lowest SoC (%) reached in the relevant overnight window:
  /// — BeforePV (pre-dawn): StartTime → FirstRelevantPVEnergyToday.
  ///   Captures the current early-morning drain before today's sunrise.
  /// — InPVPeriod / AfterPV: LastRelevantPVEnergyToday → FirstRelevantPVEnergyTomorrow.
  ///   Captures tonight's dark window after today's PV ends.
  /// Returns 100 when no overnight window exists within this simulation.
  /// </summary>
  public int OvernightMinSocReached { get; }

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

    // Overnight floor: the dark window from now to the next PV start.
    //
    // Before today's PV (pre-dawn): now → FirstRelevantPVEnergyToday.
    //   The simulation may look far into tomorrow, but the relevant question is whether the
    //   battery survives until THIS morning's sunrise — not tomorrow night's window (which
    //   will be fine after PV recharges the battery during the day).
    //
    // During or after today's PV: LastRelevantPVEnergyToday → FirstRelevantPVEnergyTomorrow.
    //   Standard tonight window.
    DateTime? overnightStart;
    DateTime? overnightEnd;

    if (CurrentPVPeriod == PVPeriods.BeforePV && FirstRelevantPVEnergyToday.HasValue)
    {
      overnightStart = now;
      overnightEnd   = FirstRelevantPVEnergyToday;
    }
    else
    {
      overnightStart = LastRelevantPVEnergyToday;
      overnightEnd   = FirstRelevantPVEnergyTomorrow;
    }

    if (overnightStart.HasValue && overnightEnd.HasValue)
    {
      var overnight = slots
        .Where(s => s.Time >= overnightStart && s.Time <= overnightEnd)
        .ToList();
      OvernightMinSocReached = overnight.Count > 0 ? overnight.Min(s => s.SoC) : 100;
    }
    else
      OvernightMinSocReached = 100;
  }

  // ── Derived predicates ─────────────────────────────────────────────────────────────────

  /// <summary>
  /// True if the overnight SoC floor stays at or above the required minimum.
  /// Uses <see cref="SimulationInput.PreferredMinSocPercent"/> when
  /// <see cref="SimulationInput.EnforcePreferredSoc"/> is set;
  /// uses <see cref="SimulationInput.AbsoluteMinSocPercent"/> otherwise.
  /// </summary>
  public bool IsOvernightMinSocOk() =>
    OvernightMinSocReached >= _input.GetEffectiveMinSoC();

  /// <summary>
  /// Slots where this simulation imports more grid energy than <paramref name="baseline"/> —
  /// the grid use the extra load causes. Compared by energy, not by inverter mode: the load can
  /// add import to a slot the baseline already supplies from the grid (force_charge, or
  /// grid_only holding the battery at its floor), which a slot-set comparison can't see.
  /// </summary>
  private IEnumerable<DateTime> NewGridSlotsVs(SimulationResult baseline)
  {
    var baseImportWh = baseline.Slots.ToDictionary(s => s.Time, s => s.GridImportWh);
    return Slots.Where(s => s.GridImportWh > baseImportWh.GetValueOrDefault(s.Time, 0)).Select(s => s.Time);
  }

  /// <summary>True if the extra load causes any grid import vs <paramref name="baseline"/>.</summary>
  public bool HasNewGridVs(SimulationResult baseline) => NewGridSlotsVs(baseline).Any();

  /// <summary>
  /// True if every slot with extra grid import (vs <paramref name="baseline"/>) is priced at or
  /// below <see cref="SimulationInput.ForceChargeMaxPrice"/>. True when there is none.
  /// </summary>
  public bool IsGridCheapVs(SimulationResult baseline)
    => NewGridSlotsVs(baseline).All(t => _input.ImportPrices.Any(p =>
        p.StartTime <= t && p.EndTime > t && p.Price <= _input.ForceChargeMaxPrice));

  /// <summary>
  /// Extra grid cost (€) of this simulation vs <paramref name="baseline"/>: import cost minus
  /// export earnings over the whole horizon, plus the battery energy this run leaves missing at
  /// the end valued at the mean import price — it would have to be bought back (otherwise a plan
  /// that just ends with an emptier battery would look cheaper). Lost export counts: battery
  /// energy a load uses tonight is refilled by PV tomorrow that would otherwise be exported.
  /// Slots without a published price use the mean of the published ones — 0 would make import
  /// after the price horizon look free.
  /// </summary>
  public float NetGridCostVs(SimulationResult baseline)
  {
    float missingEndKwh = (baseline.Slots[^1].SoC - Slots[^1].SoC) * _input.BatteryCapacityWh / 100f / 1000f;
    return OwnGridCost - baseline.OwnGridCost + missingEndKwh * MeanPrice(_input.ImportPrices);
  }

  private float? _ownGridCost;
  private float OwnGridCost => _ownGridCost ??= ComputeOwnGridCost();

  private float ComputeOwnGridCost()
  {
    static Dictionary<DateTime, float> ByQuarter(PriceList quarterly)
    {
      var d = new Dictionary<DateTime, float>();
      foreach (var p in quarterly) d.TryAdd(p.StartTime, p.Price);
      return d;
    }
    var import = ByQuarter(_input.QuarterHourlyImportPrices);
    var export = ByQuarter(_input.ExportPrices.NormalizeToQuarterHourly());
    float meanImport = MeanPrice(_input.ImportPrices), meanExport = MeanPrice(_input.ExportPrices);
    return Slots.Sum(s =>
      s.GridImportWh / 1000f * import.GetValueOrDefault(s.Time, meanImport)
      - s.GridExportWh / 1000f * export.GetValueOrDefault(s.Time, meanExport));
  }

  private static float MeanPrice(PriceList prices) => prices.Count > 0 ? prices.Average(p => p.Price) : 0f;

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
