using System;
using System.Collections.Generic;
using System.Linq;

namespace NetDeamon.apps.PVControl.Simulator;

/// <summary>
/// What loads added to the house cost, € — the one question behind every EV and warm-water decision (a start, the supply
/// battery vs grid, a split, the heating price per slot). Two answers: <see cref="GreedyLoadPricer"/> (the greedy simulation
/// with the loads against the one without) while the greedy plan steers, the DP's (<c>Optimizer.DpLoadPricer</c>) while
/// the DP does. The interface takes the loads, not a simulation: the DP's answer needs none, so the greedy simulation can
/// go one day without touching the pricing.
/// </summary>
public interface ILoadPricer
{
  /// <summary>Which pricer ("greedy", "dp") — diagnostics.</summary>
  string Name { get; }

  /// <summary>The marginal cost of loads on top of the base, €.</summary>
  /// <param name="added">The loads as supplied (<see cref="ExtraLoad.FromGrid"/> set).</param>
  /// <param name="simulated">The greedy simulation with them — run only by the pricer that needs it.</param>
  float Cost(IReadOnlyList<ExtraLoad> added, Func<SimulationResult> simulated);
}

/// <summary>The greedy simulation's answer: with the loads against without (<see cref="SimulationResult.NetGridCostVs"/>).</summary>
/// <param name="baseline">The simulation without the loads.</param>
public sealed class GreedyLoadPricer(SimulationResult baseline) : ILoadPricer
{
  /// <inheritdoc/>
  public string Name => "greedy";

  /// <inheritdoc/>
  public float Cost(IReadOnlyList<ExtraLoad> added, Func<SimulationResult> simulated) => simulated().NetGridCostVs(baseline);
}

/// <summary>Helpers of the load pricing.</summary>
public static class LoadPricing
{
  /// <summary>The loads a simulation has on top of its baseline: <see cref="SimulationInput.SimulateWith"/> appends them to
  /// the base's (the same objects), so they are the ones the base doesn't have.</summary>
  /// <param name="sim">The simulation with the loads.</param>
  /// <param name="baseline">The simulation without them.</param>
  public static IReadOnlyList<ExtraLoad> AddedLoads(SimulationResult sim, SimulationResult baseline)
  {
    var inBase = new HashSet<ExtraLoad>(baseline.Input.ExtraLoads, ReferenceEqualityComparer.Instance);
    return sim.Input.ExtraLoads.Where(l => !inBase.Contains(l)).ToList();
  }

  /// <summary>A simulation's cost with a pricer (greedy if none): the loads it adds to the baseline.</summary>
  /// <param name="pricer">The pricer; null: <see cref="GreedyLoadPricer"/>.</param>
  /// <param name="sim">The simulation with the loads.</param>
  /// <param name="baseline">The simulation without them.</param>
  public static float CostOf(ILoadPricer? pricer, SimulationResult sim, SimulationResult baseline) =>
    pricer is null ? sim.NetGridCostVs(baseline) : pricer.Cost(AddedLoads(sim, baseline), () => sim);
}
