using Microsoft.Data.Sqlite;
using NetDeamon.apps.PVControl.Simulator;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NetDeamon.apps.PVControl.Optimizer
{
  /// <summary>YAML (<c>DpShadow:</c>): the DP shadow planner next to the greedy simulation — computed and published, never steering.</summary>
  public sealed class DpShadowConfig
  {
    /// <summary>Off: nothing is computed.</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>Minutes between two DP runs (the greedy simulation runs every 15 s).</summary>
    public int IntervalMinutes { get; set; } = 5;
    /// <summary>Penalty per % below the preferred minimum per quarter, € (<see cref="DpOptions.BandPenaltyPerPctQuarter"/>).</summary>
    public float BandPenaltyPerPctQuarter { get; set; } = 0.005f;
    /// <summary>The shadow log (one row per quarter); relative = in the plugin's folder.</summary>
    public string DBLocation { get; set; } = "dp_shadow.db";

    /// <summary>The planner's options from this config.</summary>
    public DpOptions Options => new(BandPenaltyPerPctQuarter: BandPenaltyPerPctQuarter);
  }

  /// <summary>An entry of the DP's predicted SoC (the format of sensor.pv_control_info_predicted_soc's <c>data_actual</c>).</summary>
  /// <param name="Time">Slot start.</param>
  /// <param name="Soc">SoC at the slot start, %.</param>
  /// <param name="Mode">The DP's mode; "past" before now.</param>
  /// <param name="ExtraLoad">A planned extra load runs (the same loads as the greedy plan).</param>
  public sealed record PredictedSocEntry(DateTime Time, int Soc, string Mode, bool ExtraLoad);

  /// <summary>A grid charge of a plan: a block of consecutive force_charge quarters.</summary>
  /// <param name="Start">First quarter.</param>
  /// <param name="End">End of the last quarter.</param>
  /// <param name="Kwh">Grid energy into the battery and the house, kWh.</param>
  /// <param name="AvgPrice">Mean import price of its quarters, €/kWh.</param>
  public sealed record ChargeBlock(DateTime Start, DateTime End, float Kwh, float AvgPrice);

  /// <summary>One comparison of the two plans at a point in time (published as attributes, logged per quarter).</summary>
  public sealed record DpShadowReport(
    DateTime ComputedAt, int RuntimeMs, InverterModes GreedyModeNow, InverterModes DpModeNow,
    PlanCost Greedy, PlanCost Dp, IReadOnlyList<ChargeBlock> GreedyCharges, IReadOnlyList<ChargeBlock> DpCharges,
    int GreedyMinSoc, DateTime GreedyMinAt, int DpMinSoc, DateTime DpMinAt, int SocNow)
  {
    /// <summary>What the DP plan saves against the greedy one under the one model, € (negative: the DP plan is dearer).</summary>
    public float Saving => Greedy.Total - Dp.Total;
    /// <summary>Both plans do the same now.</summary>
    public bool AgreeNow => GreedyModeNow == DpModeNow;
  }

  /// <summary>
  /// The DP shadow: every <see cref="DpShadowConfig.IntervalMinutes"/> the DP plans on the greedy run's input in the
  /// background, both plans are judged by the one model (<see cref="PlanEvaluation"/>), and the comparison is published
  /// and logged once per quarter. Nothing of it reaches the inverter: the greedy simulation keeps deciding.
  /// </summary>
  public sealed class DpShadow(DpShadowConfig config, string dbPath)
  {
    private readonly string _connectionString = $"Data Source={dbPath};Pooling=False";
    private DateTime _lastRun = DateTime.MinValue;
    private DateTime _lastLoggedQuarter = DateTime.MinValue;
    private int _running;

    /// <summary>The last comparison; null before the first run.</summary>
    public DpShadowReport? Last { get; private set; }

    /// <summary>The last DP plan; null before the first run.</summary>
    public DpPlan? LastPlan { get; private set; }

    /// <summary>Whether a run is due: the interval passed and none is running.</summary>
    /// <param name="now">Current time.</param>
    public bool Due(DateTime now) => config.Enabled && Volatile.Read(ref _running) == 0 && now - _lastRun >= TimeSpan.FromMinutes(config.IntervalMinutes);

    /// <summary>Runs the DP in the background on the greedy run's input and compares; logs the quarter's row.</summary>
    /// <param name="greedy">The greedy simulation of this cycle.</param>
    /// <param name="now">Current time.</param>
    /// <returns>The comparison; null if a run was already going.</returns>
    public async Task<DpShadowReport?> RunAsync(SimulationResult greedy, DateTime now)
    {
      if (Interlocked.Exchange(ref _running, 1) == 1) return null;
      try
      {
        _lastRun = now;
        var sw = Stopwatch.StartNew();
        var dp = await Task.Run(() => BatteryDpPlanner.Plan(greedy.Input, config.Options));
        LastPlan = dp;
        Last = Report(greedy, dp, now, (int)sw.ElapsedMilliseconds, config.Options);
        var quarter = TimeRange.Floor(now, TimeRange.SlotMinutes);
        if (quarter > _lastLoggedQuarter)
        {
          await LogAsync(quarter, Last);
          _lastLoggedQuarter = quarter;
        }
        return Last;
      }
      finally { Volatile.Write(ref _running, 0); }
    }

    /// <summary>The comparison of the two plans (pure).</summary>
    /// <param name="greedy">The greedy simulation.</param>
    /// <param name="dp">The DP plan on its input.</param>
    /// <param name="now">Current time.</param>
    /// <param name="runtimeMs">The DP's run time.</param>
    /// <param name="o">The cost model's options.</param>
    public static DpShadowReport Report(SimulationResult greedy, DpPlan dp, DateTime now, int runtimeMs, DpOptions o)
    {
      var input = greedy.Input;
      var gMin = greedy.Slots.MinBy(s => s.SoC)!;
      var dMin = dp.Slots.MinBy(s => s.SocPct)!;
      return new DpShadowReport(now, runtimeMs, greedy.Slots[0].State.Mode, dp.Slots[0].Mode,
        PlanEvaluation.Evaluate(greedy, o), dp.Cost,
        Charges(input, greedy.Slots.Select(s => (s.Time, s.State.Mode == InverterModes.force_charge, s.GridImportWh))),
        Charges(input, dp.Slots.Select(s => (s.Time, s.Mode == InverterModes.force_charge, s.GridImportWh))),
        gMin.SoC, gMin.Time, dMin.SocPct, dMin.Time, greedy.Slots[0].SoC);
    }

    /// <summary>The grid charges of a plan: blocks of consecutive charging quarters with their energy and mean price.</summary>
    /// <param name="input">Simulation input (prices).</param>
    /// <param name="slots">(slot start, charging, grid import Wh) per quarter.</param>
    public static List<ChargeBlock> Charges(SimulationInput input, IEnumerable<(DateTime Time, bool Charging, int ImportWh)> slots)
    {
      var blocks = new List<ChargeBlock>();
      DateTime? start = null; DateTime last = default; int wh = 0; float priceSum = 0; int n = 0;
      void Close()
      {
        if (start is { } s) blocks.Add(new ChargeBlock(s, last.AddMinutes(TimeRange.SlotMinutes), wh / 1000f, n > 0 ? priceSum / n : 0));
        start = null; wh = 0; priceSum = 0; n = 0;
      }
      foreach (var (time, charging, importWh) in slots)
      {
        if (!charging) { Close(); continue; }
        start ??= time;
        last = time;
        wh += importWh;
        priceSum += input.ImportPrices.GetPriceOrMean(time);
        n++;
      }
      Close();
      return blocks;
    }

    /// <summary>
    /// The DP's predicted SoC over today and tomorrow, as sensor.pv_control_info_predicted_soc has it (so a second graph
    /// only swaps the entity): the plan's quarters with its SoC and mode, the past quarters with the same actual SoC as
    /// the greedy sensor (mode "past") — both graphs start alike and part from now.
    /// </summary>
    /// <param name="dp">The DP plan.</param>
    /// <param name="todayAndTomorrow">The greedy prediction's SoC over today and tomorrow (its past quarters: the actual SoC).</param>
    /// <param name="greedy">The greedy simulation (the extra loads of a quarter).</param>
    public static List<PredictedSocEntry> PredictedSoc(DpPlan dp, IReadOnlyDictionary<DateTime, int> todayAndTomorrow, SimulationResult greedy)
    {
      var planned = dp.Slots.ToDictionary(s => s.Time);
      var loads = greedy.Slots.ToDictionary(s => s.Time, s => s.ExtraLoadWh > 0);
      return todayAndTomorrow.OrderBy(x => x.Key).Select(x => planned.TryGetValue(x.Key, out var slot)
          ? new PredictedSocEntry(x.Key, slot.SocPct, slot.Mode.ToString(), loads.GetValueOrDefault(x.Key))
          : new PredictedSocEntry(x.Key, x.Value, "past", false))
        .ToList();
    }

    /// <summary>The attributes of sensor.pv_control_dp_shadow_predicted_soc (sensor.pv_control_info_predicted_soc's format).</summary>
    /// <param name="entries">The predicted SoC (<see cref="PredictedSoc"/>).</param>
    /// <param name="now">Current time.</param>
    /// <param name="computedAt">When the plan was computed.</param>
    public static object PredictedSocAttributes(IReadOnlyList<PredictedSocEntry> entries, DateTime now, DateTime computedAt) => new
    {
      current_entry_time = entries.LastOrDefault(e => e.Time <= now)?.Time.ToISO8601(),
      computed_at = computedAt.ToISO8601(),
      data_actual = entries.Select(e => new
      {
        datetime = e.Time.ToISO8601(), soc = e.Soc, mode = e.Mode, extra_load = e.ExtraLoad, export_allowed = true,
      }),
    };

    /// <summary>The attributes of sensor.pv_control_dp_shadow.</summary>
    /// <param name="r">The comparison.</param>
    public static object Attributes(DpShadowReport r) => new
    {
      computed_at = r.ComputedAt.ToISO8601(),
      runtime_ms = r.RuntimeMs,
      greedy_mode_now = r.GreedyModeNow.ToString(),
      agree_now = r.AgreeNow,
      saving_eur = Math.Round(r.Saving, 3),
      greedy_total_eur = Math.Round(r.Greedy.Total, 3),
      dp_total_eur = Math.Round(r.Dp.Total, 3),
      greedy_grid_eur = Math.Round(r.Greedy.GridCost, 3),
      dp_grid_eur = Math.Round(r.Dp.GridCost, 3),
      greedy_band_penalty_eur = Math.Round(r.Greedy.BandPenalty, 3),
      dp_band_penalty_eur = Math.Round(r.Dp.BandPenalty, 3),
      greedy_min_soc = r.GreedyMinSoc,
      greedy_min_at = r.GreedyMinAt.ToISO8601(),
      dp_min_soc = r.DpMinSoc,
      dp_min_at = r.DpMinAt.ToISO8601(),
      greedy_charges = r.GreedyCharges.Select(Block),
      dp_charges = r.DpCharges.Select(Block),
    };

    private static object Block(ChargeBlock b) => new
    {
      start = b.Start.ToISO8601(), end = b.End.ToISO8601(), kwh = Math.Round(b.Kwh, 2), avg_price = Math.Round(b.AvgPrice, 4),
    };

    /// <summary>One row per quarter: what both plans would do now and what they cost.</summary>
    private async Task LogAsync(DateTime quarter, DpShadowReport r)
    {
      await using var con = new SqliteConnection(_connectionString);
      await con.OpenAsync();
      await NetDeamon.apps.SqliteSchema.ExecuteAsync(con, """
        CREATE TABLE IF NOT EXISTS shadow (
          quarter TEXT PRIMARY KEY, soc INTEGER, greedy_mode TEXT, dp_mode TEXT,
          greedy_total REAL, dp_total REAL, greedy_grid REAL, dp_grid REAL,
          greedy_min_soc INTEGER, dp_min_soc INTEGER, greedy_next_charge TEXT, dp_next_charge TEXT, runtime_ms INTEGER)
        """);
      await using var cmd = con.CreateCommand();
      cmd.CommandText = """
        INSERT OR REPLACE INTO shadow VALUES ($q, $soc, $gm, $dm, $gt, $dt, $gg, $dg, $gmin, $dmin, $gnc, $dnc, $ms)
        """;
      cmd.Parameters.AddWithValue("$q", quarter.ToISO8601());
      cmd.Parameters.AddWithValue("$soc", r.SocNow);
      cmd.Parameters.AddWithValue("$gm", r.GreedyModeNow.ToString());
      cmd.Parameters.AddWithValue("$dm", r.DpModeNow.ToString());
      cmd.Parameters.AddWithValue("$gt", r.Greedy.Total);
      cmd.Parameters.AddWithValue("$dt", r.Dp.Total);
      cmd.Parameters.AddWithValue("$gg", r.Greedy.GridCost);
      cmd.Parameters.AddWithValue("$dg", r.Dp.GridCost);
      cmd.Parameters.AddWithValue("$gmin", r.GreedyMinSoc);
      cmd.Parameters.AddWithValue("$dmin", r.DpMinSoc);
      cmd.Parameters.AddWithValue("$gnc", (object?)r.GreedyCharges.FirstOrDefault()?.Start.ToISO8601() ?? DBNull.Value);
      cmd.Parameters.AddWithValue("$dnc", (object?)r.DpCharges.FirstOrDefault()?.Start.ToISO8601() ?? DBNull.Value);
      cmd.Parameters.AddWithValue("$ms", r.RuntimeMs);
      await cmd.ExecuteNonQueryAsync();
    }
  }
}

namespace NetDeamon.apps.PVControl
{
  public partial class PVConfig
  {
    /// <summary>The DP shadow planner (<see cref="Optimizer.DpShadow"/>); null = off.</summary>
    public Optimizer.DpShadowConfig? DpShadow { get; set; }
  }
}
