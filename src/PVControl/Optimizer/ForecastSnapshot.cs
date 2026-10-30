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
  Dictionary<DateTime, int> Pv, Dictionary<DateTime, int> Load, List<LoadPoint> ExtraLoads, int[]? VppW)
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
    i.VppChargePowerW is { } vpp ? Enumerable.Range(0, 101).Select(vpp).ToArray() : null);

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
