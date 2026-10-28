using NetDeamon.apps;
using NetDeamon.apps.PVControl;
using NetDeamon.apps.PVControl.Managers;
using NetDeamon.apps.PVControl.Simulator;
using Xunit;

namespace NetDeamonApps.Tests;

/// <summary>
/// Live 2026-10-28 21:28, PriorityPlus EV 16 → 20 % (90 min), ready by 06:30, every night price over the
/// 0.35 limit: the scan started the EV "now" at 0.455 €/kWh although 23:00–03:00 cost 0.376–0.384.
/// The running slot 21:15 was 13 min in the past; the simulation counts only its rest, so "start 21:15,
/// end 22:45" charged ~77 min, 0.4 kWh less than every later candidate, and came out cheapest.
/// A session in the running slot now lasts its duration from now (<see cref="SessionPlanner.SessionEnd"/>).
/// </summary>
public class RunningSlotSessionTests
{
  static readonly DateTime D = new(2026, 10, 28);
  static readonly DateTime Now = D.AddHours(21).AddMinutes(28);
  // Gross €/kWh per hour, 28.10 (evening live) and 29.10 (night and morning live).
  static readonly float[] P28 = [0.40f, 0.40f, 0.40f, 0.40f, 0.40f, 0.40f, 0.45f, 0.47f, 0.45f, 0.43f, 0.40f, 0.39f, 0.38f, 0.38f, 0.40f, 0.43f, 0.46f, 0.49f, 0.50f, 0.49f, 0.4862f, 0.4546f, 0.4159f, 0.3804f];
  static readonly float[] P29 = [0.3839f, 0.3811f, 0.3756f, 0.3790f, 0.3943f, 0.4057f, 0.4775f, 0.5206f, 0.5452f, 0.50f, 0.45f, 0.42f, 0.40f, 0.40f, 0.42f, 0.45f, 0.48f, 0.50f, 0.50f, 0.49f, 0.47f, 0.45f, 0.42f, 0.40f];

  static SimulationInput Input()
  {
    var prices = new List<PriceTableEntry>();
    for (var t = D; t < D.AddDays(2); t = t.AddHours(1))
      prices.Add(new PriceTableEntry(t, t.AddHours(1), t.Date == D ? P28[t.Hour] : P29[t.Hour]));
    var load = new Dictionary<DateTime, int>();
    var pv = new Dictionary<DateTime, int>();
    for (var t = D.AddDays(-1); t < D.AddDays(3); t = t.AddMinutes(15))
    {
      load[t] = t.Hour is >= 17 and < 23 ? 450 : t.Hour is >= 23 || t.Hour < 6 ? 220 : 300;
      pv[t] = t.Date > D && t.Hour is >= 9 and < 15 ? 900 : 0;
    }
    return new SimulationInput
    {
      StartTime = Now, StartSocPercent = 12, BatteryCapacityWh = 11_520,
      AbsoluteMinSocPercent = 12, PreferredMinSocPercent = 25, EnforcePreferredSoc = false,
      MaxChargePowerAmps = 30, InverterEfficiency = 0.95f, ChargeVoltageV = 240,
      ImportPrices = new PriceList(prices), ExportPrices = new PriceList(prices.Select(p => new PriceTableEntry(p.StartTime, p.EndTime, 0.17f))),
      PublishedPricesUntil = D.AddDays(2), LoadPredictionWh = load, PVPredictionWh = pv,
      EnableCheapForceCharge = false, OpportunisticDischarge = false, ForceChargeMaxPrice = 0.35f, ForceChargeTargetSocPercent = 95,
      CurrentMode = new InverterState(InverterModes.grid_only, ForceChargeReasons.GoingUnderAbsoluteMinima),
      MaxGridImportW = 9800, VppChargePowerW = new VppChargeCurve(5950, -41).MaxW,
    };
  }

  [Fact]
  public void SessionInTheRunningSlot_LastsFromNow_SoTheCheapNightWins()
  {
    var input = Input();
    var baseline = EnergySimulator.Simulate(input);
    var ev = new ExtraLoad { Name = "EV", Priority = 10, PowerW = 1938 };
    SimulationResult Run(DateTime s, DateTime e) => SessionPlanner.ChooseSupply(l => input.SimulateWith(l), ev.Between(s, e), baseline).Sim;
    var slot = Now.RoundToNearestQuarterHour();

    var p = SessionPlanner.FindBestPriceAwareStart(slot, D.AddDays(2), 90, Run, baseline, D.AddDays(1).AddHours(6).AddMinutes(30),
      preferredStart: D.AddHours(23), now: Now);

    Assert.NotNull(p);
    Assert.NotEqual(slot, p.Value.Start);
    Assert.InRange(p.Value.Start, D.AddHours(23), D.AddDays(1).AddHours(3).AddMinutes(30));
  }

  [Fact]
  public void SessionEnd_RunningSlotFromNow_LaterStartsFromTheirStart()
  {
    var slot = Now.RoundToNearestQuarterHour();
    Assert.Equal(Now.AddMinutes(90), SessionPlanner.SessionEnd(slot, 90, Now));
    Assert.Equal(D.AddHours(23).AddMinutes(90), SessionPlanner.SessionEnd(D.AddHours(23), 90, Now));
    Assert.Equal(slot.AddMinutes(90), SessionPlanner.SessionEnd(slot, 90, null));
  }
}
