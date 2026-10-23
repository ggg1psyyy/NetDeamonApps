using NetDeamon.apps;
using NetDeamon.apps.PVControl;
using NetDeamon.apps.PVControl.Simulator;
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace NetDeamonApps.Tests;

/// <summary>
/// SessionPlanner.PlanFollowUpSessions: the PriorityPlus preview of sessions after the current
/// one (e.g. a PV session ending at 18:15, then a cheap window after midnight).
/// </summary>
public class SessionPlannerTests : TestBase
{
  const int BatCap  = 11_520;
  const int EvW     = 1_856;
  const float MaxPrice = 0.34f;
  static readonly DateTime Start      = new(2026, 10, 18, 16, 0, 0);
  static readonly DateTime FirstEnd   = new(2026, 10, 18, 18, 15, 0);
  static readonly DateTime CheapFrom  = new(2026, 10, 19, 0, 0, 0);
  static readonly DateTime CheapTo    = new(2026, 10, 19, 4, 0, 0);

  static SimulationInput Input(int socPercent, Func<DateTime, float> price, float maxPrice = MaxPrice)
  {
    var date   = Start.Date;
    var prices = new List<PriceTableEntry>();
    for (var t = date; t < date.AddDays(2); t = t.AddMinutes(15))
      prices.Add(new PriceTableEntry(t, t.AddMinutes(15), price(t)));
    var load = new Dictionary<DateTime, int>();
    var pv   = new Dictionary<DateTime, int>();
    for (var t = date; t < date.AddDays(3); t = t.AddMinutes(15))
    {
      load[t] = 112;
      pv[t]   = t.Date > date && t.Hour >= 6 && t.Hour < 18 ? 600 : 0;
    }
    return new SimulationInput
    {
      StartTime                   = Start,
      StartSocPercent             = socPercent,
      BatteryCapacityWh           = BatCap,
      AbsoluteMinSocPercent       = 12,
      PreferredMinSocPercent      = 15,
      EnforcePreferredSoc         = false,
      MaxChargePowerAmps          = 30,
      InverterEfficiency          = 0.9f,
      ImportPrices                = prices,
      ExportPrices                = prices,
      LoadPredictionWh            = load,
      PVPredictionWh              = pv,
      ExtraLoads                  = [],
      EnableCheapForceCharge      = false,
      OpportunisticDischarge      = false,
      ForceChargeMaxPrice         = maxPrice,
      ForceChargeTargetSocPercent = 95,
      CurrentMode                 = new InverterState(InverterModes.normal),
    };
  }

  static float CheapAfterMidnight(DateTime t) => t >= CheapFrom && t < CheapTo ? 0.20f : 0.40f;

  static ExtraLoad Ev(DateTime start, DateTime end) =>
    new() { Name = "EV", Priority = 10, StartTime = start, EndTime = end, PowerW = EvW };

  static int Wh(IEnumerable<ExtraLoad> sessions) =>
    (int)sessions.Sum(e => (e.EndTime - e.StartTime).TotalHours * e.PowerW);

  [Fact]
  public void CurrentSessionCoversTarget_NoFollowUp()
  {
    var input = Input(50, CheapAfterMidnight);
    var first = Ev(Start, FirstEnd);

    var planned = SessionPlanner.PlanFollowUpSessions(input, [first], Wh([first]), CheapTo);

    Assert.Empty(planned);
  }

  [Fact]
  public void CurrentSessionEndsShort_FollowUpCoversRemainingEnergyAndPassesPriceCheck()
  {
    var input = Input(50, CheapAfterMidnight);
    var first = Ev(Start, FirstEnd);
    const int needWh = 18_000;

    var planned = SessionPlanner.PlanFollowUpSessions(input, [first], needWh, input.ImportPrices.Max(p => p.EndTime));

    var next = Assert.Single(planned);
    Assert.True(next.StartTime >= FirstEnd, $"Follow-up starts {next.StartTime:HH:mm}, before the current session ends.");
    Assert.InRange(Wh([first, next]), needWh - EvW / 4, needWh);

    // Same acceptance rule the scan uses, against a baseline that already has the first session.
    var baseline = EnergySimulator.Simulate(input.WithExtraLoads([first]));
    var withNext = EnergySimulator.Simulate(input.WithExtraLoads([first, next]));
    Assert.True(!withNext.HasNewGridVs(baseline) || withNext.IsGridCheapVs(baseline));

    // The follow-up must not buy grid at over-threshold prices — including while the battery is
    // held at its floor (grid_only) waiting for the cheap window. Continuing straight at 18:15
    // did exactly that: ~10.7 kWh at 40 ct from 19:00 to midnight.
    var baseBySlot = baseline.Slots.ToDictionary(s => s.Time);
    int expensiveExtraImportWh = withNext.Slots
      .Where(s => input.ImportPrices.GetPrice(s.Time) > MaxPrice)
      .Sum(s => s.GridImportWh - baseBySlot[s.Time].GridImportWh);
    Assert.True(expensiveExtraImportWh <= 0,
      $"Follow-up {next.StartTime:HH:mm}–{next.EndTime:dd. HH:mm} adds {expensiveExtraImportWh} Wh of grid import over threshold.");
  }

  /// <summary>
  /// grid_only (battery held at its floor, grid covers the load) is grid supply just like
  /// force_charge: an EV that drains the battery to its floor during expensive hours causes new
  /// grid use at those prices, so it must fail the price check.
  /// </summary>
  [Fact]
  public void EvDrainingBatteryToFloorBeforeCheapWindow_FailsPriceCheck()
  {
    var input    = Input(50, CheapAfterMidnight);
    var first    = Ev(Start, FirstEnd);
    var baseline = EnergySimulator.Simulate(input.WithExtraLoads([first]));
    var evening  = EnergySimulator.Simulate(input.WithExtraLoads([first, Ev(FirstEnd, new DateTime(2026, 10, 19, 1, 45, 0))]));

    Assert.Contains(evening.Slots, s => s.State.Mode == InverterModes.grid_only && s.Time < CheapFrom);
    Assert.True(evening.HasNewGridVs(baseline));
    Assert.False(evening.IsGridCheapVs(baseline));
  }

  [Fact]
  public void NoAcceptableStartBeforePriceHorizon_NoFollowUp()
  {
    // Flat expensive prices, low battery: an evening EV start needs new grid over threshold,
    // and the horizon ends right after the current session, so nothing can be planned.
    var input = Input(30, _ => 0.40f);
    var first = Ev(Start, FirstEnd);

    var planned = SessionPlanner.PlanFollowUpSessions(input, [first], 18_000, FirstEnd.AddHours(1));

    Assert.Empty(planned);
  }

  // ── FindBestPriceAwareStart: cheapest instead of earliest start, optional deadline ─────────

  // Battery already at its floor, so every EV Wh comes straight from the grid at that slot's
  // price: the cost difference between starts is just the price difference.
  static readonly DateTime EveningFrom = new(2026, 10, 18, 18, 0, 0);
  const int TwoHours = 120;

  static PlannedStart? Best(Func<DateTime, float> price, DateTime? deadline = null, float maxPrice = MaxPrice, DateTime? preferred = null)
  {
    var input = Input(12, price, maxPrice);
    var baseline = EnergySimulator.Simulate(input);
    return SessionPlanner.FindBestPriceAwareStart(EveningFrom, input.ImportPrices.Max(p => p.EndTime), TwoHours,
      (s, e) => EnergySimulator.Simulate(input.WithExtraLoads([Ev(s, e)])), baseline, deadline, preferredStart: preferred);
  }

  // Sticky plan: live inputs move every cycle, so a planned start's saving hovers around the
  // threshold. On 2026-10-18 a 23:15 plan (~2 % cheaper) flipped to "start now" for one cycle,
  // which committed the session.

  [Fact]
  public void PreferredStart_SlightlyCheaperThanEarliest_IsKept()
  {
    // 0.5 % cheaper: on its own not worth waiting for, but it is last cycle's plan.
    var p = Best(t => t >= CheapFrom && t < CheapTo ? 0.2985f : 0.30f, preferred: CheapFrom);

    Assert.NotNull(p);
    Assert.Equal(CheapFrom, p.Value.Start);
    Assert.Equal(StartChoice.Cheaper, p.Value.Choice);
  }

  [Fact]
  public void PreferredStart_NoLongerCheaperThanEarliest_StartsEarliest()
  {
    var p = Best(t => t >= CheapFrom && t < CheapTo ? 0.31f : 0.30f, preferred: CheapFrom);

    Assert.NotNull(p);
    Assert.Equal(EveningFrom, p.Value.Start);
    Assert.Equal(StartChoice.Earliest, p.Value.Choice);
  }

  [Fact]
  public void PreferredStart_ClearlyBeatenByAnotherStart_Switches()
  {
    var laterBand = CheapFrom.AddHours(2);
    var p = Best(t => t >= laterBand && t < CheapTo ? 0.20f : t >= CheapFrom && t < laterBand ? 0.29f : 0.30f, preferred: CheapFrom);

    Assert.NotNull(p);
    // Somewhere in the cheaper band (the house battery, at its floor, charges in the same band
    // with its forecast margin since 2026-10-22, so the EV may start a little later than 02:00).
    Assert.InRange(p.Value.Start, laterBand, CheapTo.AddMinutes(-60));
  }

  [Fact]
  public void LaterStartClearlyCheaper_WaitsForIt()
  {
    var p = Best(t => t >= CheapFrom && t < CheapTo ? 0.20f : 0.30f);

    Assert.NotNull(p);
    Assert.Equal(StartChoice.Cheaper, p.Value.Choice);
    Assert.InRange(p.Value.Start, CheapFrom, CheapTo.AddMinutes(-TwoHours));
    Assert.True(p.Value.SavingPercent >= SessionPlanner.DefaultMinSavingFraction * 100);
  }

  [Fact]
  public void LaterStartLessThanMinSavingCheaper_StartsEarliest()
  {
    // 0.3 % cheaper — under the 1 % minimum saving.
    var p = Best(t => t >= CheapFrom && t < CheapTo ? 0.299f : 0.30f);

    Assert.NotNull(p);
    Assert.Equal(StartChoice.Earliest, p.Value.Choice);
    Assert.Equal(EveningFrom, p.Value.Start);
  }

  [Fact]
  public void Deadline_ExcludesCheaperStartThatWouldFinishTooLate()
  {
    var deadline = new DateTime(2026, 10, 19, 1, 0, 0);
    var p = Best(t => t >= CheapFrom && t < CheapTo ? 0.20f : 0.30f, deadline);

    Assert.NotNull(p);
    Assert.True(p.Value.Start.AddMinutes(TwoHours) <= deadline, $"Start {p.Value.Start:HH:mm} finishes after the deadline.");
  }

  [Fact]
  public void Deadline_NoStartUnderThreshold_TakesCheapestThatFinishesInTime()
  {
    var deadline = new DateTime(2026, 10, 19, 6, 0, 0);
    var p = Best(t => t >= CheapFrom && t < CheapTo ? 0.20f : 0.30f, deadline, maxPrice: 0.10f);

    Assert.NotNull(p);
    Assert.Equal(StartChoice.DeadlineCheapest, p.Value.Choice);
    Assert.InRange(p.Value.Start, CheapFrom, CheapTo.AddMinutes(-TwoHours));
  }

  [Fact]
  public void NoDeadline_NoGridStartUnderThreshold_WaitsForPvNextMorning()
  {
    // Every night start needs grid over 0.10; at 06:00 PV (600 Wh/slot) covers house + EV.
    var p = Best(t => t >= CheapFrom && t < CheapTo ? 0.20f : 0.30f, maxPrice: 0.10f);

    Assert.NotNull(p);
    Assert.Equal(new DateTime(2026, 10, 19, 6, 0, 0), p.Value.Start);
  }

  [Fact]
  public void NoDeadline_NoStartPassesBeforeHorizon_NoStart()
  {
    var input    = Input(12, _ => 0.30f, maxPrice: 0.10f);
    var baseline = EnergySimulator.Simulate(input);

    Assert.Null(SessionPlanner.FindBestPriceAwareStart(EveningFrom, EveningFrom.AddHours(3), TwoHours,
      (s, e) => EnergySimulator.Simulate(input.WithExtraLoads([Ev(s, e)])), baseline));
  }

  [Fact]
  public void Deadline_TooCloseToFinish_StartsNow()
  {
    var p = Best(_ => 0.30f, deadline: EveningFrom.AddHours(1));

    Assert.NotNull(p);
    Assert.Equal(StartChoice.DeadlineNow, p.Value.Choice);
    Assert.Equal(EveningFrom, p.Value.Start);
  }

  [Fact]
  public void NetGridCost_EvAtFloor_IsItsGridEnergyTimesPrice()
  {
    var input    = Input(12, _ => 0.30f);
    var baseline = EnergySimulator.Simulate(input);
    var withEv   = EnergySimulator.Simulate(input.WithExtraLoads([Ev(EveningFrom, EveningFrom.AddMinutes(TwoHours))]));

    float evKwh = EvW * TwoHours / 60f / 1000f;
    Assert.InRange(withEv.NetGridCostVs(baseline), evKwh * 0.30f * 0.95f, evKwh * 0.30f * 1.05f);
  }

  [Theory]
  [InlineData("06:30", true, 6, 30)]
  [InlineData("6:05", true, 6, 5)]
  [InlineData(" 23:59 ", true, 23, 59)]
  [InlineData("07:00:00", true, 7, 0)]
  [InlineData("6:45:00", true, 6, 45)]
  [InlineData("off", false, 0, 0)]
  [InlineData("24:00", false, 0, 0)]
  [InlineData("", false, 0, 0)]
  [InlineData(null, false, 0, 0)]
  public void ReadyBy_Parse(string? text, bool ok, int h, int m)
  {
    Assert.Equal(ok, NetDeamon.apps.PVControl.Managers.SchedulableLoadRuntime.TryParseReadyBy(text, out var t));
    if (ok) Assert.Equal(new TimeOnly(h, m), t);
  }

  [Fact]
  public void ReadyBy_NextOccurrence_TodayIfStillAhead_ElseTomorrow()
  {
    var now = new DateTime(2026, 10, 18, 17, 0, 0);
    Assert.Equal(new DateTime(2026, 10, 18, 22, 0, 0), NetDeamon.apps.TimeRange.NextOccurrence(new TimeOnly(22, 0), now));
    Assert.Equal(new DateTime(2026, 10, 19, 6, 30, 0), NetDeamon.apps.TimeRange.NextOccurrence(new TimeOnly(6, 30), now));
    Assert.Equal(new DateTime(2026, 10, 19, 17, 0, 0), NetDeamon.apps.TimeRange.NextOccurrence(new TimeOnly(17, 0), now));
  }
}
