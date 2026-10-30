using NetDeamon.apps.PVControl.Simulator;
using System;
using System.Collections.Generic;
using System.Linq;

namespace NetDeamon.apps.PVControl.Optimizer;

/// <summary>Settings of the DP shadow planner (<see cref="BatteryDpPlanner"/>).</summary>
/// <param name="BandPenaltyPerPctQuarter">Penalty per % below the preferred minimum per quarter, € (without EnforcePreferredSoc;
/// the plan may only trade a dip into the band against a larger saving).</param>
/// <param name="FloorPenaltyPerPctQuarter">Penalty per % below the hard floor per quarter, € — practically forbidden.</param>
/// <param name="StatesPerPercent">SoC grid of the program: states per % (4 = 0.25 % steps).</param>
/// <param name="ChargeStepPct">Charge targets a grid charge can stop at within a quarter, every this many %.</param>
public sealed record DpOptions(
  float BandPenaltyPerPctQuarter = 0.005f,
  float FloorPenaltyPerPctQuarter = 1f,
  int StatesPerPercent = 4,
  int ChargeStepPct = 2);

/// <summary>A quarter of a DP plan.</summary>
/// <param name="Time">Slot start.</param>
/// <param name="SocPct">SoC at the slot start, % (rounded down, like the simulation's).</param>
/// <param name="Mode">Inverter mode.</param>
/// <param name="Bulk">A grid charge's path: bulk (true) or VPP.</param>
/// <param name="TargetSocPct">Target of a grid charge, %; 0 otherwise.</param>
/// <param name="GridImportWh">Grid import, Wh.</param>
/// <param name="GridExportWh">Grid export, Wh.</param>
/// <param name="BatteryChargeWh">Battery charge, Wh.</param>
/// <param name="BatteryDischargeWh">Battery discharge, Wh.</param>
public sealed record DpSlot(DateTime Time, int SocPct, InverterModes Mode, bool Bulk, int TargetSocPct,
  int GridImportWh, int GridExportWh, int BatteryChargeWh, int BatteryDischargeWh);

/// <summary>What a plan costs over the horizon, €: <see cref="Total"/> = grid cost + penalties − the value of the energy left at the end.</summary>
/// <param name="GridCost">Import cost − export earnings.</param>
/// <param name="BandPenalty">Quarters below the preferred minimum.</param>
/// <param name="FloorPenalty">Quarters below the hard floor.</param>
/// <param name="EndValue">Energy above the hard floor at the end, at the mean import price after the round trip.</param>
public sealed record PlanCost(float GridCost, float BandPenalty, float FloorPenalty, float EndValue)
{
  /// <summary>The objective, €.</summary>
  public float Total => GridCost + BandPenalty + FloorPenalty - EndValue;
}

/// <summary>A DP plan: the quarters from now to the simulation's end and its cost.</summary>
/// <param name="Slots">The quarters.</param>
/// <param name="Cost">What it costs (<see cref="PlanEvaluation"/>).</param>
/// <param name="EndSocPct">SoC at the end, %.</param>
public sealed record DpPlan(IReadOnlyList<DpSlot> Slots, PlanCost Cost, int EndSocPct);

/// <summary>
/// The one cost model of a plan — the DP's objective and the yardstick both plans are compared with: grid cost per slot
/// (<see cref="SimulationInput.GridCostOf"/>), a penalty per % and quarter below the preferred minimum (only without
/// EnforcePreferredSoc: there the band is a reserve the plan keeps) and below the hard floor, and the energy left above
/// the hard floor at the end, valued at the mean import price after the round trip (otherwise a plan would empty the
/// battery at the horizon's end for free).
/// </summary>
public static class PlanEvaluation
{
  /// <summary>Penalty of a quarter starting at a SoC, €.</summary>
  /// <param name="input">Simulation input (floors).</param>
  /// <param name="socPct">SoC at the slot start, % (fractional in the DP's grid).</param>
  /// <param name="o">Options.</param>
  public static (float Band, float Floor) Penalty(SimulationInput input, double socPct, DpOptions o)
  {
    int floor = input.GetEffectiveMinSoC();
    float band = !input.EnforcePreferredSoc && socPct < input.GetPreferredMinSoC()
      ? (float)(input.GetPreferredMinSoC() - socPct) * o.BandPenaltyPerPctQuarter : 0f;
    float under = socPct < floor ? (float)(floor - socPct) * o.FloorPenaltyPerPctQuarter : 0f;
    return (band, under);
  }

  /// <summary>Value of the energy in the battery at the end, €.</summary>
  /// <param name="input">Simulation input.</param>
  /// <param name="endEnergyWh">Energy at the end, Wh.</param>
  public static float EndValue(SimulationInput input, int endEnergyWh) =>
    Math.Max(0, endEnergyWh - input.SocToWh(input.GetEffectiveMinSoC())) / 1000f * input.ImportPrices.Mean * input.RoundTripEfficiency;

  /// <summary>Cost of a planned run (the greedy simulation or a DP plan) under the one model.</summary>
  /// <param name="input">Simulation input.</param>
  /// <param name="slots">(slot start, SoC % at the start, import Wh, export Wh) of each quarter.</param>
  /// <param name="endEnergyWh">Energy at the end, Wh.</param>
  /// <param name="o">Options.</param>
  public static PlanCost Evaluate(SimulationInput input, IEnumerable<(DateTime Time, double SocPct, int ImportWh, int ExportWh)> slots,
    int endEnergyWh, DpOptions o)
  {
    float grid = 0, band = 0, floor = 0;
    foreach (var s in slots)
    {
      grid += input.GridCostOf(s.Time, s.ImportWh, s.ExportWh);
      var (b, f) = Penalty(input, s.SocPct, o);
      band += b;
      floor += f;
    }
    return new PlanCost(grid, band, floor, EndValue(input, endEnergyWh));
  }

  /// <summary>Cost of the greedy simulation's plan under the one model.</summary>
  /// <param name="result">The simulation.</param>
  /// <param name="o">Options.</param>
  public static PlanCost Evaluate(SimulationResult result, DpOptions o)
  {
    var input = result.Input;
    var last = result.Slots[^1];
    int endWh = Math.Clamp(input.SocToWh(last.SoC) + last.BatteryChargeWh - last.BatteryDischargeWh, 0, input.BatteryCapacityWh);
    return Evaluate(input, result.Slots.Select(s => (s.Time, (double)s.SoC, s.GridImportWh, s.GridExportWh)), endWh, o);
  }
}

/// <summary>
/// Global battery plan by dynamic programming (shadow: computed and published next to the greedy simulation, never
/// steering). States: the battery's energy on a grid of <see cref="DpOptions.StatesPerPercent"/> per %; per quarter
/// the actions normal, grid_only and grid charges to targets every <see cref="DpOptions.ChargeStepPct"/> % on the VPP and
/// the bulk path. Each transition's flows come from the simulation's own <see cref="EnergySimulator.ComputeEnergyFlows"/>
/// (same limits, efficiency, import cap); a quarter with a load that never draws the battery holds it as the simulation
/// does. Backwards from the end (<see cref="PlanEvaluation.EndValue"/>) the cheapest rest is computed for every state,
/// then the plan is followed forwards from the real SoC with exact energies.
/// </summary>
public static class BatteryDpPlanner
{
  private const int SlotMinutes = TimeRange.SlotMinutes;

  /// <summary>Plans the battery over the simulation's horizon (the running quarter through the end of tomorrow).</summary>
  /// <param name="input">The simulation input (the greedy run's: same forecasts, prices, extra loads).</param>
  /// <param name="options">Settings; defaults if null.</param>
  /// <returns>The plan.</returns>
  public static DpPlan Plan(SimulationInput input, DpOptions? options = null)
  {
    var o = options ?? new DpOptions();
    var start = input.StartTime.RoundToNearestQuarterHour();
    var end = start.Date.AddDays(2);
    var times = new List<DateTime>();
    for (var t = start; t < end; t = t.AddMinutes(SlotMinutes)) times.Add(t);
    int n = times.Count;
    int states = 100 * o.StatesPerPercent + 1;
    double whPerState = input.BatteryCapacityWh / (double)(states - 1);
    int StateOf(int wh) => Math.Clamp((int)Math.Round(wh / whPerState), 0, states - 1);
    int WhOf(int state) => (int)Math.Round(state * whPerState);
    double PctOf(int state) => state / (double)o.StatesPerPercent;

    var energies = times.Select(t => input.SlotEnergyWh(t)).ToArray();
    var fractions = times.Select(t => input.SlotFraction(t)).ToArray();

    // value[i][s]: cheapest cost from slot i in state s to the end; policy[i][s]: the action taken
    var value = new float[n + 1][];
    var policy = new Action[n][];
    value[n] = new float[states];
    for (int s = 0; s < states; s++) value[n][s] = -PlanEvaluation.EndValue(input, WhOf(s));

    for (int i = n - 1; i >= 0; i--)
    {
      value[i] = new float[states];
      policy[i] = new Action[states];
      var (pv, load, extra) = energies[i];
      int totalLoad = load + extra;
      bool held = input.BatteryHeldForLoad(times[i], pv, totalLoad);
      var actions = Actions(input, o, held);
      for (int s = 0; s < states; s++)
      {
        int wh = WhOf(s);
        var (band, floor) = PlanEvaluation.Penalty(input, PctOf(s), o);
        float best = float.MaxValue;
        Action bestAction = actions[0];
        foreach (var a in actions)
        {
          if (a.Mode == InverterModes.force_charge && a.TargetSocPct <= (int)PctOf(s)) continue;   // nothing to charge
          var (ch, dis, imp, exp) = Flows(input, a, pv, totalLoad, wh, fractions[i]);
          int next = StateOf(Math.Clamp(wh + ch - dis, 0, input.BatteryCapacityWh));
          float rest = input.GridCostOf(times[i], imp, exp) + band + floor + value[i + 1][next];
          if (rest < best - 1e-6f) { best = rest; bestAction = a; }
        }
        value[i][s] = best;
        policy[i][s] = bestAction;
      }
    }

    // Forwards from the real SoC with exact energies (the policy by the nearest state)
    var slots = new List<DpSlot>(n);
    int energy = input.SocToWh(input.StartSocPercent);
    for (int i = 0; i < n; i++)
    {
      var (pv, load, extra) = energies[i];
      var a = policy[i][StateOf(energy)];
      var (ch, dis, imp, exp) = Flows(input, a, pv, load + extra, energy, fractions[i]);
      slots.Add(new DpSlot(times[i], input.WhToSoc(energy), a.Mode, a.Bulk, a.Mode == InverterModes.force_charge ? a.TargetSocPct : 0, imp, exp, ch, dis));
      energy = Math.Clamp(energy + ch - dis, 0, input.BatteryCapacityWh);
    }
    var cost = PlanEvaluation.Evaluate(input, slots.Select(s => (s.Time, (double)s.SocPct, s.GridImportWh, s.GridExportWh)), energy, o);
    return new DpPlan(slots, cost, input.WhToSoc(energy));
  }

  /// <summary>An action of a quarter: a mode, and for a grid charge its path and target.</summary>
  private readonly record struct Action(InverterModes Mode, bool Bulk, int TargetSocPct);

  /// <summary>The actions of a quarter: normal (grid_only where a load never draws the battery), grid_only, and grid
  /// charges to every <see cref="DpOptions.ChargeStepPct"/> % target on both paths.</summary>
  private static List<Action> Actions(SimulationInput input, DpOptions o, bool held)
  {
    var list = new List<Action> { new(held ? InverterModes.grid_only : InverterModes.normal, false, 0) };
    if (!held) list.Add(new(InverterModes.grid_only, false, 0));
    for (int target = o.ChargeStepPct; target <= 100; target += o.ChargeStepPct)
    {
      list.Add(new(InverterModes.force_charge, false, target));
      list.Add(new(InverterModes.force_charge, true, target));
    }
    return list;
  }

  /// <summary>The flows of an action, by the simulation's own function.</summary>
  private static (int ch, int dis, int imp, int exp) Flows(SimulationInput input, Action a, int pv, int totalLoad, int wh, double fraction)
  {
    var state = new InverterState(a.Mode) { Bulk = a.Bulk };
    return EnergySimulator.ComputeEnergyFlows(state, pv, totalLoad, wh, input, a.TargetSocPct, fraction);
  }
}
