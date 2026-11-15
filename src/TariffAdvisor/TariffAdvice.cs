using System;
using System.Collections.Generic;
using System.Linq;
using NetDeamon.apps.PVControl.Optimizer;
using NetDeamon.apps.PVControl.Simulator;

namespace NetDeamon.apps.TariffAdvisor;

/// <summary>The advisor's settings (from <see cref="TariffAdvisorConfig"/>).</summary>
/// <param name="MinSavingEur">A switch is advised only if it saves at least this much over the horizon, €.</param>
/// <param name="StartTime">Time of day from which the coming day is judged (tomorrow's prices are published ~13:00).</param>
/// <param name="MaxSnapshotAgeMinutes">An older snapshot is not judged (PVControl not running).</param>
public sealed record AdviceSettings(float MinSavingEur, TimeSpan StartTime, int MaxSnapshotAgeMinutes);

/// <summary>What the advisor found.</summary>
/// <param name="State"><c>stay</c>, <c>switch_to_&lt;tariff&gt;</c>, <c>pending</c> (the day's prices aren't published yet) or
/// <c>unavailable</c>.</param>
/// <param name="Reason">Why, in words.</param>
/// <param name="ForDay">The day the advice is for (from the next switch time on).</param>
/// <param name="CurrentTariff">Today's tariff: the advice is relative to it (one change a day — a choice already made
/// for tomorrow doesn't count).</param>
/// <param name="CostEur">The horizon's cost per tariff for the day, €.</param>
/// <param name="SavingEur">What the best other tariff saves against today's, € (negative: it costs more).</param>
/// <param name="SnapshotAt">The quarter of the snapshot judged.</param>
public sealed record Advice(string State, string Reason, DateOnly? ForDay = null, string? CurrentTariff = null,
  IReadOnlyDictionary<string, float>? CostEur = null, float? SavingEur = null, DateTime? SnapshotAt = null);

/// <summary>
/// Which import tariff is cheaper for the coming day: the DP planner plans PVControl's current situation (its latest
/// <see cref="ForecastSnapshot"/>) once per tariff — today's quarters with today's tariff, the day from the next switch
/// with the candidate — and the plans' costs are compared. Pure; the HA side is <see cref="TariffAdvisor"/>.
/// </summary>
public static class TariffAdvice
{
  /// <summary>The state that advises a tariff.</summary>
  /// <param name="tariff">The tariff's name (as PVControl's select).</param>
  public static string SwitchTo(string tariff) => "switch_to_" + tariff.ToLowerInvariant();

  /// <summary>Whether the coming day is judged now (from <see cref="AdviceSettings.StartTime"/> until midnight; before
  /// that the last advice — for today — stays).</summary>
  /// <param name="now">Current time.</param>
  /// <param name="s">Settings.</param>
  public static bool Due(DateTime now, AdviceSettings s) => now.TimeOfDay >= s.StartTime;

  /// <summary>
  /// What a plan costs for the comparison, €: its grid cost over the whole horizon (tonight's charging depends on
  /// tomorrow's tariff too) less the energy left at the end, valued at one refill price for every plan — the planner's
  /// own end value uses each list's refill price, so the same energy would be worth more under the dearer tariff.
  /// Penalties (band, floor) aren't money and are left out.
  /// </summary>
  /// <param name="plan">The plan.</param>
  /// <param name="valuedWith">The input whose refill price values the end (today's tariff's).</param>
  /// <param name="refillPrice"><see cref="PlanEvaluation.RefillPrice"/> of <paramref name="valuedWith"/>.</param>
  public static float CostOf(DpPlan plan, SimulationInput valuedWith, float refillPrice) =>
    plan.Cost.GridCost - PlanEvaluation.EndValue(valuedWith, plan.EndEnergyWh, refillPrice);

  /// <summary>The advice for the coming day.</summary>
  /// <param name="latest">PVControl's latest snapshot (<see cref="ForecastSnapshotStore.ReadLatestAsync"/>); null if none.</param>
  /// <param name="now">Current time.</param>
  /// <param name="s">Settings.</param>
  /// <param name="plan">The planner (tests); default the DP with the snapshot's options.</param>
  public static Advice Decide((DateTime Quarter, ForecastSnapshot Snapshot)? latest, DateTime now, AdviceSettings s,
    Func<SimulationInput, DpOptions?, DpPlan>? plan = null)
  {
    if (latest is not { } l) return new("unavailable", "no snapshot from PVControl yet (its dp_shadow.db)");
    var (quarter, snap) = l;
    if (now - quarter > TimeSpan.FromMinutes(s.MaxSnapshotAgeMinutes))
      return new("unavailable", $"PVControl's latest snapshot is from {quarter:dd.MM. HH:mm}", SnapshotAt: quarter);
    if (snap.ImportTariffs is not { } t)
      return new("unavailable", "the snapshot has no tariff prices (PVControl's version is older)", SnapshotAt: quarter);
    var forDay = DateOnly.FromDateTime(t.NextSwitch);
    var others = t.Import.Keys.Where(k => k != t.Active).ToList();
    if (!t.Import.ContainsKey(t.Active) || others.Count == 0)
      return new("unavailable", $"only {string.Join(", ", t.Import.Keys)} can be priced (PVControl.yaml: the other tariff's price)",
        forDay, t.Active, SnapshotAt: quarter);
    var dayEnd = t.NextSwitch.AddDays(1);
    if (snap.PublishedPricesUntil is { } until && until < dayEnd)
      return new("pending", $"the prices for {forDay:dd.MM.} aren't published yet (until {until:dd.MM. HH:mm})", forDay, t.Active, SnapshotAt: quarter);

    plan ??= (input, options) => BatteryDpPlanner.Plan(input, options);
    var input = snap.ToInput();
    SimulationInput With(string tariff) => input.WithPrices(t.From(tariff)!, input.ExportPrices, input.PublishedPricesUntil);
    var today = With(t.Active);
    float refill = PlanEvaluation.RefillPrice(today);
    var cost = t.Import.Keys.ToDictionary(k => k, k => CostOf(plan(k == t.Active ? today : With(k), snap.Options), today, refill));
    var best = others.MinBy(k => cost[k])!;
    float saving = cost[t.Active] - cost[best];
    return saving >= s.MinSavingEur
      ? new(SwitchTo(best), $"{best} saves {saving:F2} € on {forDay:dd.MM.} (at least {s.MinSavingEur:F2} €)", forDay, t.Active, cost, saving, quarter)
      : new("stay", saving > 0
          ? $"{best} would save only {saving:F2} € on {forDay:dd.MM.} (under {s.MinSavingEur:F2} €)"
          : $"{t.Active} is cheaper on {forDay:dd.MM.} by {-saving:F2} €", forDay, t.Active, cost, saving, quarter);
  }
}
