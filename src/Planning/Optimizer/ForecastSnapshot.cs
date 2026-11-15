using NetDeamon.apps.PVControl.Simulator;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;

namespace NetDeamon.apps.PVControl.Optimizer;

/// <summary>A price entry of a snapshot.</summary>
public sealed record PricePoint(DateTime Start, DateTime End, float Price);

/// <summary>An extra load (EV session, warm-water heating) of a snapshot.</summary>
public sealed record LoadPoint(string Name, int Priority, DateTime Start, DateTime End, int PowerW, bool FromGrid);

/// <summary>
/// The import tariffs at a snapshot: each tariff's full price list over the horizon (as if it applied everywhere,
/// estimated like the plan's), so a plugin can price the coming day with another tariff than today's.
/// </summary>
/// <param name="Active">The tariff in force at the snapshot (its name).</param>
/// <param name="NextSwitch">When a tariff chosen now would take effect.</param>
/// <param name="Import">Gross import prices per tariff name; a tariff without its price setting is missing.</param>
public sealed record TariffPrices(string Active, DateTime NextSwitch, Dictionary<string, List<PricePoint>> Import)
{
  /// <summary>The tariffs' lists of a snapshot.</summary>
  /// <param name="active">The tariff in force.</param>
  /// <param name="nextSwitch">The next switch time.</param>
  /// <param name="lists">Each priceable tariff's list.</param>
  public static TariffPrices Of(string active, DateTime nextSwitch, IEnumerable<(string Tariff, PriceList Prices)> lists) =>
    new(active, nextSwitch, lists.ToDictionary(l => l.Tariff, l => l.Prices.Select(p => new PricePoint(p.StartTime, p.EndTime, p.Price)).ToList()));

  /// <summary>Import prices with today's tariff until <see cref="NextSwitch"/> and another one from it.</summary>
  /// <param name="from">The tariff from the switch on.</param>
  /// <returns>The list; null if a tariff has no list.</returns>
  public PriceList? From(string from) =>
    Import.TryGetValue(Active, out var today) && Import.TryGetValue(from, out var next)
      ? new PriceList(today.Where(p => p.Start < NextSwitch).Concat(next.Where(p => p.Start >= NextSwitch))
          .OrderBy(p => p.Start).Select(p => new PriceTableEntry(p.Start, p.End, p.Price)).ToList())
      : null;
}

/// <summary>
/// Everything a simulation input holds at a moment — the forecasts (PV, load), the prices, the planned extra loads, the
/// settings, the current mode and the learned VPP curve (sampled per %) — so a replay can rebuild the planning situation
/// exactly (<see cref="ToInput"/>): the DP shadow stores one per quarter (dp_shadow.db, table forecast) to replay the DP
/// on its own path against what really happened. The shadow alone only compares both plans from the greedy run's state.
/// </summary>
public sealed record ForecastSnapshot(
  DateTime StartTime, int StartSocPercent, int BatteryCapacityWh, int AbsoluteMinSocPercent, int PreferredMinSocPercent,
  bool EnforcePreferredSoc, int MaxChargePowerAmps, float InverterEfficiency, int ChargeVoltageV, int MaxGridImportW,
  float ForceChargeMaxPrice, int ForceChargeTargetSocPercent, bool EnableCheapForceCharge, bool OpportunisticDischarge,
  DateTime? PublishedPricesUntil, InverterModes CurrentMode, ForceChargeReasons CurrentReason, bool CurrentBulk,
  bool CurrentChargeEnable, bool CurrentExportAllowed, List<PricePoint> ImportPrices, List<PricePoint> ExportPrices,
  Dictionary<DateTime, int> Pv, Dictionary<DateTime, int> Load, List<LoadPoint> ExtraLoads, int[]? VppW,
  DateTime? TopUpArmedAt = null, TimeOnly? TopUpReadyBy = null, TariffPrices? ImportTariffs = null, DpOptions? Options = null)
{
  /// <summary>The snapshot of an input.</summary>
  /// <param name="i">The simulation input.</param>
  public static ForecastSnapshot From(SimulationInput i) => new(
    i.StartTime, i.StartSocPercent, i.BatteryCapacityWh, i.AbsoluteMinSocPercent, i.PreferredMinSocPercent,
    i.EnforcePreferredSoc, i.MaxChargePowerAmps, i.InverterEfficiency, i.ChargeVoltageV, i.MaxGridImportW,
    i.ForceChargeMaxPrice, i.ForceChargeTargetSocPercent, i.EnableCheapForceCharge, i.OpportunisticDischarge,
    i.PublishedPricesUntil, i.CurrentMode.Mode, i.CurrentMode.ModeReason, i.CurrentMode.Bulk,
    i.CurrentMode.BatteryChargeEnable, i.CurrentMode.ExportAllowed,
    i.ImportPrices.Select(p => new PricePoint(p.StartTime, p.EndTime, p.Price)).ToList(),
    i.ExportPrices.Select(p => new PricePoint(p.StartTime, p.EndTime, p.Price)).ToList(),
    new Dictionary<DateTime, int>(i.PVPredictionWh), new Dictionary<DateTime, int>(i.LoadPredictionWh),
    i.ExtraLoads.Select(e => new LoadPoint(e.Name, e.Priority, e.StartTime, e.EndTime, e.PowerW, e.FromGrid)).ToList(),
    i.VppChargePowerW is { } vpp ? Enumerable.Range(0, 101).Select(vpp).ToArray() : null, i.TopUpArmedAt, i.TopUpReadyBy);

  /// <summary>The simulation input again.</summary>
  public SimulationInput ToInput()
  {
    var vpp = VppW;
    return new SimulationInput
    {
      StartTime = StartTime, StartSocPercent = StartSocPercent, BatteryCapacityWh = BatteryCapacityWh,
      AbsoluteMinSocPercent = AbsoluteMinSocPercent, PreferredMinSocPercent = PreferredMinSocPercent,
      EnforcePreferredSoc = EnforcePreferredSoc, MaxChargePowerAmps = MaxChargePowerAmps, InverterEfficiency = InverterEfficiency,
      ChargeVoltageV = ChargeVoltageV, MaxGridImportW = MaxGridImportW, ForceChargeMaxPrice = ForceChargeMaxPrice,
      ForceChargeTargetSocPercent = ForceChargeTargetSocPercent, EnableCheapForceCharge = EnableCheapForceCharge,
      TopUpArmedAt = TopUpArmedAt, TopUpReadyBy = TopUpReadyBy,
      OpportunisticDischarge = OpportunisticDischarge, PublishedPricesUntil = PublishedPricesUntil,
      CurrentMode = new InverterState(CurrentMode, CurrentReason, CurrentChargeEnable, CurrentExportAllowed) { Bulk = CurrentBulk },
      ImportPrices = ImportPrices.Select(p => new PriceTableEntry(p.Start, p.End, p.Price)).ToList(),
      ExportPrices = ExportPrices.Select(p => new PriceTableEntry(p.Start, p.End, p.Price)).ToList(),
      PVPredictionWh = new Dictionary<DateTime, int>(Pv), LoadPredictionWh = new Dictionary<DateTime, int>(Load),
      ExtraLoads = ExtraLoads.Select(e => new ExtraLoad
      {
        Name = e.Name, Priority = e.Priority, StartTime = e.Start, EndTime = e.End, PowerW = e.PowerW, FromGrid = e.FromGrid,
      }).ToList(),
      VppChargePowerW = vpp is null ? null : soc => vpp[Math.Clamp(soc, 0, vpp.Length - 1)],
    };
  }

  /// <summary>Gzip-compressed JSON (a row of the shadow's forecast table, ~2 kB).</summary>
  public byte[] ToBytes()
  {
    using var ms = new MemoryStream();
    using (var gz = new GZipStream(ms, CompressionLevel.SmallestSize))
      JsonSerializer.Serialize(gz, this);
    return ms.ToArray();
  }

  /// <summary>A snapshot from <see cref="ToBytes"/>.</summary>
  /// <param name="bytes">The stored row.</param>
  public static ForecastSnapshot FromBytes(byte[] bytes)
  {
    using var gz = new GZipStream(new MemoryStream(bytes), CompressionMode.Decompress);
    return JsonSerializer.Deserialize<ForecastSnapshot>(gz)!;
  }
}
