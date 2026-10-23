using System;
using System.Collections.Generic;
using System.Linq;
using NetDeamon.apps.PVControl;

namespace NetDeamon.apps.PVControl.Simulator;

/// <summary>Everything <see cref="EnergySimulator.Simulate"/> needs, free of HA types (filled by HouseEnergy.RunSimulation, or by tests).</summary>
public class SimulationInput
{
  /// <summary>Start of the simulation (now); the first slot is the quarter it lies in, simulated only from here.</summary>
  public required DateTime StartTime { get; init; }

  /// <summary>Battery state of charge at <see cref="StartTime"/>, %.</summary>
  public required int StartSocPercent { get; init; }

  /// <summary>Usable battery capacity, Wh.</summary>
  public required int BatteryCapacityWh { get; init; }

  /// <summary>Hard SoC floor the battery never discharges below, %.</summary>
  public required int AbsoluteMinSocPercent { get; init; }

  /// <summary>Preferred SoC floor, %; a hard floor only with <see cref="EnforcePreferredSoc"/>.</summary>
  public required int PreferredMinSocPercent { get; init; }

  /// <summary>True: <see cref="PreferredMinSocPercent"/> is the floor; false: <see cref="AbsoluteMinSocPercent"/>.</summary>
  public required bool EnforcePreferredSoc { get; init; }

  /// <summary>Maximum battery charge/discharge current, A (× 230 V = <see cref="MaxChargePowerW"/>).</summary>
  public required int MaxChargePowerAmps { get; init; }

  /// <summary>Inverter efficiency one way, 0…1 (round trip = squared).</summary>
  public required float InverterEfficiency { get; init; }

  /// <summary>Gross import prices, €/kWh; unpublished slots up to the end of tomorrow estimated (<see cref="PriceList.WithEstimatedPrices"/>).</summary>
  public required PriceList ImportPrices { get; init; }

  /// <summary>End of the published prices (later ones are estimates); null = all published (tests).</summary>
  public DateTime? PublishedPricesUntil { get; init; }

  /// <summary>How far price-driven plans that commit to a slot (EV sessions) may look.</summary>
  /// <param name="fallback">Used when there are no prices at all.</param>
  /// <returns><see cref="PublishedPricesUntil"/>, else the end of <see cref="ImportPrices"/>, else <paramref name="fallback"/>.</returns>
  public DateTime PriceHorizonEnd(DateTime fallback) =>
    PublishedPricesUntil ?? ImportPrices.End ?? fallback;

  /// <summary>Share of a slot that is still ahead: the running slot only from <see cref="StartTime"/> on.</summary>
  /// <param name="slotStart">Start of the slot.</param>
  /// <returns>0…1; 1 for every later slot.</returns>
  internal double SlotFraction(DateTime slotStart) => TimeRange.SlotFraction(slotStart, StartTime, DateTime.MaxValue);

  /// <summary>Energies of a slot, the running slot only its rest (<see cref="SlotFraction"/>).</summary>
  /// <param name="slotStart">Start of the slot.</param>
  /// <returns>PV, base load and extra loads, Wh.</returns>
  internal (int PvWh, int LoadWh, int ExtraWh) SlotEnergyWh(DateTime slotStart)
  {
    double fraction = SlotFraction(slotStart);
    return ((int)(PVPredictionWh.GetValueOrDefault(slotStart, 0) * fraction),
            (int)(LoadPredictionWh.GetValueOrDefault(slotStart, 0) * fraction),
            (int)(ExtraLoadWh(slotStart) * fraction));
  }

  /// <summary>Simulates with additional loads.</summary>
  /// <param name="loads">Loads added to <see cref="ExtraLoads"/>.</param>
  /// <returns>The simulation result.</returns>
  public SimulationResult SimulateWith(params IEnumerable<ExtraLoad> loads) =>
    EnergySimulator.Simulate(WithExtraLoads([.. ExtraLoads, .. loads]));

  private Dictionary<DateTime, int>? _extraLoadWhBySlot;
  /// <summary>Energy of all <see cref="ExtraLoads"/> in a slot (indexed once per input).</summary>
  /// <param name="slotStart">Start of the slot.</param>
  /// <returns>Wh.</returns>
  internal int ExtraLoadWh(DateTime slotStart) =>
    (_extraLoadWhBySlot ??= BuildExtraLoadWhBySlot()).GetValueOrDefault(slotStart);

  /// <summary>Energy of all <see cref="ExtraLoads"/> by slot start, Wh.</summary>
  private Dictionary<DateTime, int> BuildExtraLoadWhBySlot()
  {
    var bySlot = new Dictionary<DateTime, int>();
    foreach (var e in ExtraLoads)
      for (var t = e.StartTime.RoundToNearestQuarterHour(); t < e.EndTime; t = t.AddMinutes(15))
        bySlot[t] = bySlot.GetValueOrDefault(t) + e.GetWhForSlot(t);
    return bySlot;
  }

  private PriceList? _quarterHourlyImportPrices;
  /// <summary><see cref="ImportPrices"/> as 15-min entries (built once per input).</summary>
  internal PriceList QuarterHourlyImportPrices => _quarterHourlyImportPrices ??= ImportPrices.NormalizeToQuarterHourly();

  /// <summary>Gross export prices, €/kWh (fixed tariff, scaled import prices or their own series); estimated like <see cref="ImportPrices"/>.</summary>
  public required PriceList ExportPrices { get; init; }

  /// <summary>Base house load forecast per 15-min slot start, Wh.</summary>
  public required Dictionary<DateTime, int> LoadPredictionWh { get; init; }

  /// <summary>PV forecast per 15-min slot start, Wh.</summary>
  public required Dictionary<DateTime, int> PVPredictionWh { get; init; }

  /// <summary>Loads on top of <see cref="LoadPredictionWh"/> (EV sessions, warm-water heatings).</summary>
  public List<ExtraLoad> ExtraLoads { get; init; } = [];

  /// <summary>User switch: charge to <see cref="ForceChargeTargetSocPercent"/> in the cheapest window.</summary>
  public required bool EnableCheapForceCharge { get; init; }

  /// <summary>Allows discharging to the grid at the day's price peaks when the SoC forecast allows it.</summary>
  public required bool OpportunisticDischarge { get; init; }

  /// <summary>Import price limit, €/kWh: grid charging only at or below it; opportunistic discharge at or above it.</summary>
  public required float ForceChargeMaxPrice { get; init; }

  /// <summary>Target SoC of the user force charge, %.</summary>
  public required int ForceChargeTargetSocPercent { get; init; }

  /// <summary>Inverter mode at the start (continues hysteresis, e.g. an ongoing force_charge).</summary>
  public required InverterState CurrentMode { get; init; }

  // ── Derived helpers ─────────────────────────────────────────────────────────────────────

  private const int ChargeVoltage = 230; // V — assumed fixed for charge power calculations

  /// <summary>Maximum battery charge/discharge power, W.</summary>
  public int MaxChargePowerW => MaxChargePowerAmps * ChargeVoltage;

  /// <summary>Maximum battery charge/discharge energy in a slot.</summary>
  /// <param name="fraction">Simulated share of the slot (<see cref="SlotFraction"/>).</param>
  /// <returns>Wh.</returns>
  public int MaxChargeWhPerSlot(double fraction = 1.0) => (int)(Energy.SlotWh(MaxChargePowerW) * fraction);

  /// <summary>Time to charge at the maximum power (<see cref="Soc.ChargeMinutes"/>).</summary>
  /// <param name="startSoC">Start SoC, %.</param>
  /// <param name="endSoC">End SoC, %.</param>
  /// <returns>Minutes.</returns>
  public int CalculateChargingDuration(int startSoC, int endSoC) =>
    Soc.ChargeMinutes(startSoC, endSoC, BatteryCapacityWh, InverterEfficiency, MaxChargePowerW);

  /// <summary>Energy of a SoC with this battery (<see cref="Soc.ToWh"/>).</summary>
  /// <param name="socPercent">SoC or SoC difference, %.</param>
  /// <returns>Wh.</returns>
  public int SocToWh(int socPercent) => Soc.ToWh(socPercent, BatteryCapacityWh);
  /// <summary>SoC of an energy with this battery (<see cref="Soc.FromWh"/>).</summary>
  /// <param name="wh">Energy, Wh.</param>
  /// <returns>%.</returns>
  public int WhToSoc(int wh) => Soc.FromWh(wh, BatteryCapacityWh);

  /// <summary>Preferred floor, at least the absolute one, %.</summary>
  public int GetPreferredMinSoC() =>
    Math.Max(PreferredMinSocPercent, AbsoluteMinSocPercent);

  /// <summary>The floor in force (<see cref="EnforcePreferredSoc"/>), %.</summary>
  public int GetEffectiveMinSoC() =>
    EnforcePreferredSoc ? PreferredMinSocPercent : AbsoluteMinSocPercent;

  /// <summary>Whether a time lies in the day's PV window (<see cref="PVPeriodAt"/>).</summary>
  /// <param name="now">Time to check.</param>
  /// <returns>True between the day's first and last relevant PV slot.</returns>
  public bool IsInPVPeriod(DateTime now) => PVPeriodAt(now) == PVPeriods.InPVPeriod;

  /// <summary>Before, inside or after the day's PV window: between the first and last <see cref="RelevantPVEdge"/> (cloudy slots in between are inside).</summary>
  /// <param name="time">Time to check.</param>
  /// <returns>BeforePV also if the day has no relevant PV.</returns>
  public PVPeriods PVPeriodAt(DateTime time)
  {
    var first = RelevantPVEdge(time.Date, first: true);
    var last = RelevantPVEdge(time.Date, first: false);
    return first is null || time < first ? PVPeriods.BeforePV
      : last is null || time > last ? PVPeriods.AfterPV
      : PVPeriods.InPVPeriod;
  }

  /// <summary>Net PV (PV − load) per slot above which PV is relevant, Wh.</summary>
  public const int RelevantPVNetWh = 50;

  /// <summary>First or last slot of a day with net PV above <see cref="RelevantPVNetWh"/> (whole-day forecasts).</summary>
  /// <param name="date">Day.</param>
  /// <param name="first">True = first slot, false = last.</param>
  /// <param name="from">Only slots from here on; default = the whole day.</param>
  /// <returns>Slot start; null if none.</returns>
  public DateTime? RelevantPVEdge(DateTime date, bool first, DateTime from = default)
  {
    var candidates = PVPredictionWh
      .Where(k => k.Key.Date == date && k.Key >= from
                  && k.Value - LoadPredictionWh.GetValueOrDefault(k.Key, 0) > RelevantPVNetWh)
      .Select(k => (DateTime?)k.Key);
    return first ? candidates.FirstOrDefault() : candidates.LastOrDefault();
  }

  /// <summary>Next start of relevant PV (<see cref="RelevantPVEdge"/>, first).</summary>
  /// <param name="date">Day.</param>
  /// <param name="from">Only slots from here on.</param>
  /// <returns>Slot start; far future (date + 2 days − 1 min) if none.</returns>
  public DateTime GetFirstRelevantPVTime(DateTime date, DateTime from) =>
    RelevantPVEdge(date, first: true, from) ?? date.AddDays(2).AddMinutes(-1);

  /// <summary>End of the day's relevant PV (<see cref="RelevantPVEdge"/>, last).</summary>
  /// <param name="date">Day.</param>
  /// <returns>Slot start; far future (date + 2 days − 1 min) if none.</returns>
  public DateTime GetLastRelevantPVTime(DateTime date) =>
    RelevantPVEdge(date, first: false) ?? date.AddDays(2).AddMinutes(-1);

  /// <summary>Copy with other extra loads.</summary>
  /// <param name="loads">Replaces <see cref="ExtraLoads"/>.</param>
  /// <returns>The copy.</returns>
  public SimulationInput WithExtraLoads(List<ExtraLoad> loads) => Copy(loads, ImportPrices, ExportPrices, PublishedPricesUntil);

  /// <summary>Copy with other prices.</summary>
  /// <param name="import">Replaces <see cref="ImportPrices"/>.</param>
  /// <param name="export">Replaces <see cref="ExportPrices"/>.</param>
  /// <param name="publishedUntil">Replaces <see cref="PublishedPricesUntil"/>.</param>
  /// <returns>The copy.</returns>
  public SimulationInput WithPrices(PriceList import, PriceList export, DateTime? publishedUntil = null) =>
    Copy(ExtraLoads, import, export, publishedUntil);

  /// <summary>Copy with the given loads and prices, everything else the same.</summary>
  /// <param name="loads">Extra loads.</param>
  /// <param name="import">Import prices.</param>
  /// <param name="export">Export prices.</param>
  /// <param name="publishedUntil">End of the published prices.</param>
  /// <returns>The copy.</returns>
  private SimulationInput Copy(List<ExtraLoad> loads, PriceList import, PriceList export, DateTime? publishedUntil) => new()
  {
    StartTime = StartTime,
    StartSocPercent = StartSocPercent,
    BatteryCapacityWh = BatteryCapacityWh,
    AbsoluteMinSocPercent = AbsoluteMinSocPercent,
    PreferredMinSocPercent = PreferredMinSocPercent,
    EnforcePreferredSoc = EnforcePreferredSoc,
    MaxChargePowerAmps = MaxChargePowerAmps,
    InverterEfficiency = InverterEfficiency,
    ImportPrices = import,
    ExportPrices = export,
    PublishedPricesUntil = publishedUntil,
    LoadPredictionWh = LoadPredictionWh,
    PVPredictionWh = PVPredictionWh,
    ExtraLoads = loads,
    EnableCheapForceCharge = EnableCheapForceCharge,
    OpportunisticDischarge = OpportunisticDischarge,
    ForceChargeMaxPrice = ForceChargeMaxPrice,
    ForceChargeTargetSocPercent = ForceChargeTargetSocPercent,
    CurrentMode = CurrentMode,
  };
}
