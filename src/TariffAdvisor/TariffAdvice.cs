using System;
using System.Collections.Generic;
using System.Linq;
using NetDeamon.apps.PVControl.Optimizer;
using NetDeamon.apps.PVControl.Simulator;

namespace NetDeamon.apps.TariffAdvisor;

/// <summary>The advisor's settings (from <see cref="TariffAdvisorConfig"/>).</summary>
/// <param name="MinSavingEur">The other tariff is the result only if it saves at least this much over the horizon, €.</param>
/// <param name="MaxSnapshotAgeMinutes">An older snapshot is not judged (PVControl not running).</param>
public sealed record AdviceSettings(float MinSavingEur, int MaxSnapshotAgeMinutes);

/// <summary>What the advisor found.</summary>
/// <param name="State">The tariff for the coming day (<c>Market</c>, <c>Fixed</c>: the select's names), or
/// <see cref="TariffAdvice.Unavailable"/> / <see cref="TariffAdvice.Error"/>.</param>
/// <param name="Status"><c>decided</c> (judged on the day's published prices), <c>pending</c> (not published yet: today's
/// tariff), <c>unavailable</c>, <c>error</c>.</param>
/// <param name="Reason">Why, in words.</param>
/// <param name="ForDay">The day the result is for (from the next switch time on).</param>
/// <param name="CurrentTariff">Today's tariff: the result is relative to it (one change a day — a choice already made
/// for tomorrow doesn't count).</param>
/// <param name="CostEur">The horizon's cost per tariff for the day, €.</param>
/// <param name="SavingEur">What the best other tariff saves against today's, € (negative: it costs more).</param>
/// <param name="SnapshotAt">The quarter of the snapshot judged.</param>
public sealed record Advice(string State, string Status, string Reason, DateOnly? ForDay = null, string? CurrentTariff = null,
  IReadOnlyDictionary<string, float>? CostEur = null, float? SavingEur = null, DateTime? SnapshotAt = null);

/// <summary>
/// Which import tariff is cheaper for the coming day: the DP planner plans PVControl's current situation (its latest
/// <see cref="ForecastSnapshot"/>) once per tariff — today's quarters with today's tariff, the day from the next switch
/// with the candidate — and the plans' costs are compared. Pure; the HA side is <see cref="TariffAdvisor"/>.
/// </summary>
public static class TariffAdvice
{
  /// <summary>The state when there is nothing to judge (no, a stale or an incomplete snapshot).</summary>
  public const string Unavailable = "Unavailable";
  /// <summary>The state when judging failed.</summary>
  public const string Error = "Error";
  /// <summary>Judged every quarter from here (tomorrow's day-ahead prices are published ~13:00) …</summary>
  public static readonly TimeSpan WindowStart = TimeSpan.FromHours(13);
  /// <summary>… until here; afterwards (and before the start) the last result stays — the decision for tomorrow.</summary>
  public static readonly TimeSpan WindowEnd = TimeSpan.FromHours(20);

  /// <summary>The advice when judging failed (the DB unreadable, the planner threw).</summary>
  /// <param name="error">The error.</param>
  public static Advice Failed(Exception error) => new(Error, "error", $"judging failed: {error.Message}");

  /// <summary>What to show after a judgement: a decision for a coming day stays once made — a later failure or a
  /// stale snapshot in the window (e.g. PVControl restarting at 19:45) doesn't replace it, so the evening keeps the
  /// decided tariff; a new decision does.</summary>
  /// <param name="shown">What is shown now; null after a start.</param>
  /// <param name="next">The new judgement.</param>
  /// <param name="now">Current time.</param>
  /// <returns>The advice to show.</returns>
  public static Advice Shown(Advice? shown, Advice next, DateTime now) =>
    shown is { Status: "decided", ForDay: { } day } && day > DateOnly.FromDateTime(now) && next.Status != "decided" ? shown : next;

  /// <summary>Whether the coming day is judged now: from <see cref="WindowStart"/> until <see cref="WindowEnd"/>.</summary>
  /// <param name="now">Current time.</param>
  public static bool Due(DateTime now) => now.TimeOfDay >= WindowStart && now.TimeOfDay < WindowEnd;

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
    if (latest is not { } l) return new(Unavailable, "unavailable", "no snapshot from PVControl yet (its dp_shadow.db)");
    var (quarter, snap) = l;
    if (now - quarter > TimeSpan.FromMinutes(s.MaxSnapshotAgeMinutes))
      return new(Unavailable, "unavailable", $"PVControl's latest snapshot is from {quarter:dd.MM. HH:mm}", SnapshotAt: quarter);
    if (snap.ImportTariffs is not { } t)
      return new(Unavailable, "unavailable", "the snapshot has no tariff prices (PVControl's version is older)", SnapshotAt: quarter);
    var forDay = DateOnly.FromDateTime(t.NextSwitch);
    var others = t.Import.Keys.Where(k => k != t.Active).ToList();
    if (!t.Import.ContainsKey(t.Active) || others.Count == 0)
      return new(Unavailable, "unavailable", $"only {string.Join(", ", t.Import.Keys)} can be priced (PVControl.yaml: the other tariff's price)",
        forDay, t.Active, SnapshotAt: quarter);
    var dayEnd = t.NextSwitch.AddDays(1);
    if (snap.PublishedPricesUntil is { } until && until < dayEnd)
      return new(t.Active, "pending", $"the prices for {forDay:dd.MM.} aren't published yet (until {until:dd.MM. HH:mm}): today's tariff",
        forDay, t.Active, SnapshotAt: quarter);

    plan ??= (input, options) => BatteryDpPlanner.Plan(input, options);
    var input = snap.ToInput();
    SimulationInput With(string tariff) => input.WithPrices(t.From(tariff)!, input.ExportPrices, input.PublishedPricesUntil);
    var today = With(t.Active);
    float refill = PlanEvaluation.RefillPrice(today);
    var cost = t.Import.Keys.ToDictionary(k => k, k => CostOf(plan(k == t.Active ? today : With(k), snap.Options), today, refill));
    var best = others.MinBy(k => cost[k])!;
    float saving = cost[t.Active] - cost[best];
    return saving >= s.MinSavingEur
      ? new(best, "decided", $"{best} saves {saving:F2} € on {forDay:dd.MM.} (at least {s.MinSavingEur:F2} €)", forDay, t.Active, cost, saving, quarter)
      : new(t.Active, "decided", saving > 0
          ? $"{best} would save only {saving:F2} € on {forDay:dd.MM.} (under {s.MinSavingEur:F2} €)"
          : $"{t.Active} is cheaper on {forDay:dd.MM.} by {-saving:F2} €", forDay, t.Active, cost, saving, quarter);
  }
}
