using System;
using System.Collections.Generic;
using System.Linq;
using NetDeamon.apps;
using NetDeamon.apps.PVControl;

namespace NetDeamon.apps.PVControl.Simulator;

/// <summary>Result of one <see cref="EnergySimulator.Simulate"/> run: the slot timeline (also as the list itself) and values derived from it.</summary>
public class SimulationResult : IReadOnlyList<SimulationSlot>
{
  private readonly SimulationInput _input;

  // ── Core timeline ─────────────────────────────────────────────────────────────────────
  /// <summary>One slot per 15 min from the start through the end of tomorrow.</summary>
  public IReadOnlyList<SimulationSlot> Slots { get; }

  // IReadOnlyList<SimulationSlot> delegation — lets callers treat SimulationResult as a slot list
  /// <summary>Number of slots.</summary>
  public int Count => Slots.Count;
  /// <summary>Slot by index.</summary>
  /// <param name="index">0 = the running slot.</param>
  public SimulationSlot this[int index] => Slots[index];
  /// <summary>Enumerates the slots in time order.</summary>
  public IEnumerator<SimulationSlot> GetEnumerator() => Slots.GetEnumerator();
  System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => ((System.Collections.IEnumerable)Slots).GetEnumerator();

  // ── PV window boundaries ──────────────────────────────────────────────────────────────
  /// <summary>First slot today with relevant PV (<see cref="SimulationInput.RelevantPVEdge"/>); null if none.</summary>
  public DateTime? FirstRelevantPVEnergyToday { get; }

  /// <summary>Last slot today with relevant PV; null if none.</summary>
  public DateTime? LastRelevantPVEnergyToday { get; }

  /// <summary>First slot tomorrow with relevant PV; null if none.</summary>
  public DateTime? FirstRelevantPVEnergyTomorrow { get; }

  /// <summary>Last slot tomorrow with relevant PV; null if none.</summary>
  public DateTime? LastRelevantPVEnergyTomorrow { get; }

  /// <summary>PV period at the start (<see cref="SimulationInput.PVPeriodAt"/>).</summary>
  public PVPeriods CurrentPVPeriod { get; }

  // ── SoC peaks (simulation-derived) ────────────────────────────────────────────────────
  /// <summary>True if a slot today reaches SoC ≥ 99 %.</summary>
  public bool WillReachMaxSocToday { get; }

  /// <summary>True if a slot tomorrow reaches SoC ≥ 99 %.</summary>
  public bool WillReachMaxSocTomorrow { get; }

  /// <summary>Total time today at SoC ≥ 99 %.</summary>
  public TimeSpan MaxSocDurationToday { get; }

  // ── Overnight SoC floor ───────────────────────────────────────────────────────────────
  /// <summary>
  /// Lowest SoC (%) of the dark window: before today's PV from the start to today's first PV slot,
  /// otherwise from today's last to tomorrow's first PV slot; 100 if there is none.
  /// </summary>
  public int OvernightMinSocReached { get; }

  // ── Construction ──────────────────────────────────────────────────────────────────────
  /// <summary>Wraps a simulated timeline and derives its values.</summary>
  /// <param name="slots">The simulated slots.</param>
  /// <param name="input">The input they were simulated from.</param>
  internal SimulationResult(List<SimulationSlot> slots, SimulationInput input)
  {
    _input = input;
    Slots = slots.AsReadOnly();

    var today    = input.StartTime.Date;
    var tomorrow = today.AddDays(1);

    // PV windows from the full prediction dictionaries so boundaries cover the whole day
    // even when the simulation starts mid-day (e.g. sunrise at 07:00 when started at 14:00).
    FirstRelevantPVEnergyToday    = input.RelevantPVEdge(today,    first: true);
    LastRelevantPVEnergyToday     = input.RelevantPVEdge(today,    first: false);
    FirstRelevantPVEnergyTomorrow = input.RelevantPVEdge(tomorrow, first: true);
    LastRelevantPVEnergyTomorrow  = input.RelevantPVEdge(tomorrow, first: false);

    var now = input.StartTime;
    CurrentPVPeriod = input.PVPeriodAt(now);

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

  /// <summary>Whether the dark window stays at or above the floor in force (<see cref="SimulationInput.GetEffectiveMinSoC"/>).</summary>
  public bool IsOvernightMinSocOk() =>
    OvernightMinSocReached >= _input.GetEffectiveMinSoC();

  /// <summary>Slots importing more energy than the baseline (by energy, not by mode).</summary>
  /// <param name="baseline">Same input without the load in question.</param>
  /// <returns>Slot starts.</returns>
  private IEnumerable<DateTime> NewGridSlotsVs(SimulationResult baseline)
  {
    var baseImportWh = baseline.Slots.ToDictionary(s => s.Time, s => s.GridImportWh);
    return Slots.Where(s => s.GridImportWh > baseImportWh.GetValueOrDefault(s.Time, 0)).Select(s => s.Time);
  }

  /// <summary>Whether the load raises a slot's import above <see cref="SimulationInput.MaxGridImportW"/> (a slot already over it without the load counts only if the load adds to it).</summary>
  /// <param name="baseline">Same input without the load in question.</param>
  /// <returns>False without a limit.</returns>
  public bool ExceedsImportCapVs(SimulationResult baseline)
  {
    if (_input.MaxGridImportW <= 0) return false;
    var baseImportWh = baseline.Slots.ToDictionary(s => s.Time, s => s.GridImportWh);
    return Slots.Any(s => s.GridImportWh > baseImportWh.GetValueOrDefault(s.Time, 0)
                          && Energy.SlotW(s.GridImportWh) > _input.MaxGridImportW * _input.SlotFraction(s.Time) + 1);
  }

  /// <summary>Whether the load causes extra grid import.</summary>
  /// <param name="baseline">Same input without the load in question.</param>
  /// <returns>True if any slot imports more than in the baseline.</returns>
  public bool HasNewGridVs(SimulationResult baseline) => NewGridSlotsVs(baseline).Any();

  /// <summary>Whether all extra grid import is at or below <see cref="SimulationInput.ForceChargeMaxPrice"/>.</summary>
  /// <param name="baseline">Same input without the load in question.</param>
  /// <returns>True also if there is no extra import; false for a slot without price.</returns>
  public bool IsGridCheapVs(SimulationResult baseline)
    => NewGridSlotsVs(baseline).All(t => _input.ImportPrices.TryGetPrice(t, out float p) && p <= _input.ForceChargeMaxPrice);

  /// <summary>
  /// Extra grid cost vs the baseline: import cost − export earnings over the horizon (lost export
  /// counts), plus the battery energy missing at the end at the mean import price, plus the round-trip
  /// loss of the extra energy the load takes out of the battery (it has to be put back in).
  /// </summary>
  /// <param name="baseline">Same input without the load in question.</param>
  /// <returns>€; prices via <see cref="PriceList.GetPriceOrMean"/>, PV valued at the export price (<see cref="SourceShares.Cost"/>).</returns>
  public float NetGridCostVs(SimulationResult baseline)
  {
    float missingEndKwh = _input.SocToWh(baseline.Slots[^1].SoC - Slots[^1].SoC) / 1000f;
    float extraDischargeKwh = Math.Max(0, Slots.Sum(s => s.BatteryDischargeWh) - baseline.Slots.Sum(s => s.BatteryDischargeWh)) / 1000f;
    float roundTripLossKwh = extraDischargeKwh * (1 - _input.RoundTripEfficiency);
    return OwnGridCost - baseline.OwnGridCost + (missingEndKwh + roundTripLossKwh) * _input.ImportPrices.Mean;
  }

  private float? _ownGridCost;
  /// <summary>Import cost − export earnings of this run, €.</summary>
  private float OwnGridCost => _ownGridCost ??= Slots.Sum(s =>
    new SourceShares(Pv: -s.GridExportWh / 1000f, Grid: s.GridImportWh / 1000f, Battery: 0)
      .Cost(_input.ImportPrices.GetPriceOrMean(s.Time), batteryPrice: 0, pvPrice: _input.ExportPrices.GetPriceOrMean(s.Time)));
}
