using NetDeamon.apps;
using NetDeamon.apps.PVControl.Simulator;
using Xunit;

namespace NetDeamonApps.Tests;

/// <summary>
/// Unit tests for <see cref="LoadSchedulingDecision.Decide"/>.
/// Pure function — no Home Assistant or HA mock required.
///
/// Coverage:
///   - Off / target-reached short-circuits
///   - Emergency ignores PV and battery
///   - Optimal: start requires WillReachMaxSocToday; keep ignores that flag
///   - Priority / PriorityPlus: Schmitt-trigger on SoC and PV
///   - Hysteresis: different start vs keep thresholds
///   - PV level variations (parametrised)
///   - ImportToleranceW boundary
///   - Reason strings for all exit paths
/// </summary>
public class LoadSchedulingDecisionTests
{
  // ── input builder ──────────────────────────────────────────────────────────

  static LoadSchedulingInput Build(
    LoadSchedulingMode mode             = LoadSchedulingMode.Optimal,
    float  targetLevel                  = 80f,
    float  currentLevel                 = 30f,
    int    chargeRateW                  = 3_680,
    int    netPvW                       = 4_000,
    int    batterySoC                   = 90,
    int    preferredMinSoC              = 20,
    bool   willReachMaxSocToday         = true,
    bool   currentlyActive              = false,
    int    hysteresisMarginPct          = 8,
    int    importToleranceW             = 200,
    float  currentImportPriceCt         = 15f,
    float  maxPriceCt                   = 20f) => new()
  {
    Mode                  = mode,
    TargetLevel           = targetLevel,
    CurrentLevel          = currentLevel,
    ChargeRateW           = chargeRateW,
    NetPvW                = netPvW,
    BatterySoC            = batterySoC,
    PreferredMinSoC       = preferredMinSoC,
    WillReachMaxSocToday  = willReachMaxSocToday,
    CurrentlyActive       = currentlyActive,
    HysteresisMarginPct   = hysteresisMarginPct,
    ImportToleranceW      = importToleranceW,
    CurrentImportPriceCt  = currentImportPriceCt,
    MaxPriceCt            = maxPriceCt,
  };

  // ── Off ────────────────────────────────────────────────────────────────────

  [Fact]
  public void Off_AlwaysReturnsFalse()
    => Assert.False(LoadSchedulingDecision.Decide(Build(mode: LoadSchedulingMode.Off)));

  // ── Target reached ─────────────────────────────────────────────────────────

  [Theory]
  [InlineData(LoadSchedulingMode.Optimal)]
  [InlineData(LoadSchedulingMode.Priority)]
  [InlineData(LoadSchedulingMode.PriorityPlus)]
  [InlineData(LoadSchedulingMode.Emergency)]
  public void TargetReached_ReturnsFalse_RegardlessOfMode(LoadSchedulingMode mode)
    => Assert.False(LoadSchedulingDecision.Decide(Build(mode: mode, currentLevel: 80f, targetLevel: 80f)));

  [Fact]
  public void AboveTarget_ReturnsFalse()
    => Assert.False(LoadSchedulingDecision.Decide(Build(currentLevel: 85f, targetLevel: 80f)));

  // ── Emergency ──────────────────────────────────────────────────────────────

  [Fact]
  public void Emergency_BelowTarget_ReturnsTrue()
    => Assert.True(LoadSchedulingDecision.Decide(Build(mode: LoadSchedulingMode.Emergency)));

  [Fact]
  public void Emergency_IgnoresLowBattery()
    => Assert.True(LoadSchedulingDecision.Decide(
        Build(mode: LoadSchedulingMode.Emergency, batterySoC: 5, preferredMinSoC: 30)));

  [Fact]
  public void Emergency_IgnoresNoPV()
    => Assert.True(LoadSchedulingDecision.Decide(
        Build(mode: LoadSchedulingMode.Emergency, netPvW: -5_000)));

  // ── Optimal ────────────────────────────────────────────────────────────────

  [Fact]
  public void Optimal_Start_WillReachMaxSocToday_PvSurplus_ReturnsTrue()
    => Assert.True(LoadSchedulingDecision.Decide(
        Build(mode: LoadSchedulingMode.Optimal,
              willReachMaxSocToday: true, netPvW: 4_000, currentlyActive: false)));

  [Fact]
  public void Optimal_Start_WillNotReachMaxSocToday_ReturnsFalse()
    => Assert.False(LoadSchedulingDecision.Decide(
        Build(mode: LoadSchedulingMode.Optimal,
              willReachMaxSocToday: false, netPvW: 4_000, currentlyActive: false)));

  [Fact]
  public void Optimal_Start_WillReachMax_ButNoPV_ReturnsFalse()
  {
    // PV surplus below charge rate → pvOk=false even if SoC forecast is good
    var input = Build(mode: LoadSchedulingMode.Optimal,
                      willReachMaxSocToday: true, netPvW: 0, chargeRateW: 3_680,
                      currentlyActive: false);
    Assert.False(LoadSchedulingDecision.Decide(input));
  }

  [Fact]
  public void Optimal_Keep_WillNotReachMaxSocToday_ButPvSurplus_ReturnsTrue()
  {
    // Once active, socOk=true regardless of WillReachMaxSocToday
    var input = Build(mode: LoadSchedulingMode.Optimal,
                      willReachMaxSocToday: false, netPvW: 4_000,
                      currentlyActive: true);
    Assert.True(LoadSchedulingDecision.Decide(input));
  }

  [Fact]
  public void Optimal_Keep_PvDropsBeyondImportTolerance_ReturnsFalse()
  {
    const int tol = 200;
    var input = Build(mode: LoadSchedulingMode.Optimal,
                      willReachMaxSocToday: true, netPvW: -(tol + 1),
                      importToleranceW: tol, currentlyActive: true);
    Assert.False(LoadSchedulingDecision.Decide(input));
  }

  [Fact]
  public void Optimal_Keep_PvAtExactlyNegImportTolerance_ReturnsTrue()
  {
    const int tol = 200;
    var input = Build(mode: LoadSchedulingMode.Optimal,
                      willReachMaxSocToday: true, netPvW: -tol,
                      importToleranceW: tol, currentlyActive: true);
    Assert.True(LoadSchedulingDecision.Decide(input));
  }

  // ── Priority ───────────────────────────────────────────────────────────────

  [Fact]
  public void Priority_Start_HighBattery_PvSurplus_ReturnsTrue()
    => Assert.True(LoadSchedulingDecision.Decide(
        Build(mode: LoadSchedulingMode.Priority,
              batterySoC: 80, preferredMinSoC: 20, hysteresisMarginPct: 8,
              netPvW: 4_000, chargeRateW: 3_680, currentlyActive: false)));

  [Fact]
  public void Priority_Start_BatteryJustBelowStartThreshold_ReturnsFalse()
  {
    // Start threshold = preferredMinSoC(20) + hysteresis(8) = 28; bat=27 → false
    var input = Build(mode: LoadSchedulingMode.Priority,
                      batterySoC: 27, preferredMinSoC: 20, hysteresisMarginPct: 8,
                      netPvW: 4_000, currentlyActive: false);
    Assert.False(LoadSchedulingDecision.Decide(input));
  }

  [Fact]
  public void Priority_Start_BatteryAtExactStartThreshold_ReturnsTrue()
  {
    // bat=28 = 20+8 → exactly meets threshold
    var input = Build(mode: LoadSchedulingMode.Priority,
                      batterySoC: 28, preferredMinSoC: 20, hysteresisMarginPct: 8,
                      netPvW: 4_000, currentlyActive: false);
    Assert.True(LoadSchedulingDecision.Decide(input));
  }

  [Fact]
  public void Priority_Start_PvJustBelowChargeRate_ReturnsFalse()
  {
    const int chargeRateW = 3_680;
    var input = Build(mode: LoadSchedulingMode.Priority,
                      batterySoC: 80, netPvW: chargeRateW - 1,
                      chargeRateW: chargeRateW, currentlyActive: false);
    Assert.False(LoadSchedulingDecision.Decide(input));
  }

  [Fact]
  public void Priority_Start_PvExactlyEqualToChargeRate_ReturnsTrue()
  {
    const int chargeRateW = 3_680;
    var input = Build(mode: LoadSchedulingMode.Priority,
                      batterySoC: 80, netPvW: chargeRateW,
                      chargeRateW: chargeRateW, currentlyActive: false);
    Assert.True(LoadSchedulingDecision.Decide(input));
  }

  [Fact]
  public void Priority_Hysteresis_BatteryBetweenKeepAndStart_NotActive_ReturnsFalse()
  {
    // bat=25 is above keep threshold (20) but below start threshold (28) → false when not active
    var input = Build(mode: LoadSchedulingMode.Priority,
                      batterySoC: 25, preferredMinSoC: 20, hysteresisMarginPct: 8,
                      netPvW: 4_000, currentlyActive: false);
    Assert.False(LoadSchedulingDecision.Decide(input));
  }

  [Fact]
  public void Priority_Hysteresis_BatteryBetweenKeepAndStart_AlreadyActive_ReturnsTrue()
  {
    // Same bat=25 but active → relaxed keep threshold (20) applies → true
    var input = Build(mode: LoadSchedulingMode.Priority,
                      batterySoC: 25, preferredMinSoC: 20, hysteresisMarginPct: 8,
                      netPvW: 4_000, currentlyActive: true);
    Assert.True(LoadSchedulingDecision.Decide(input));
  }

  [Fact]
  public void Priority_Keep_BatteryAtPreferredMin_ReturnsTrue()
  {
    // bat exactly at keep threshold (preferredMinSoC) → still ok
    var input = Build(mode: LoadSchedulingMode.Priority,
                      batterySoC: 20, preferredMinSoC: 20, hysteresisMarginPct: 8,
                      netPvW: 4_000, currentlyActive: true);
    Assert.True(LoadSchedulingDecision.Decide(input));
  }

  [Fact]
  public void Priority_Keep_BatteryDropsBelowPreferredMin_ReturnsFalse()
  {
    var input = Build(mode: LoadSchedulingMode.Priority,
                      batterySoC: 19, preferredMinSoC: 20, hysteresisMarginPct: 8,
                      netPvW: 4_000, currentlyActive: true);
    Assert.False(LoadSchedulingDecision.Decide(input));
  }

  [Fact]
  public void Priority_Keep_ImportWithinTolerance_ReturnsTrue()
  {
    const int tol = 300;
    var input = Build(mode: LoadSchedulingMode.Priority,
                      batterySoC: 50, netPvW: -tol,
                      importToleranceW: tol, currentlyActive: true);
    Assert.True(LoadSchedulingDecision.Decide(input));
  }

  [Fact]
  public void Priority_Keep_ImportExceedsTolerance_ReturnsFalse()
  {
    const int tol = 300;
    var input = Build(mode: LoadSchedulingMode.Priority,
                      batterySoC: 50, netPvW: -(tol + 1),
                      importToleranceW: tol, currentlyActive: true);
    Assert.False(LoadSchedulingDecision.Decide(input));
  }

  // ── PriorityPlus ───────────────────────────────────────────────────────────

  [Fact]
  public void PriorityPlus_CheapPrice_NoPV_HighBattery_ReturnsTrue()
  {
    // Cheap price bypasses PV requirement; battery SoC condition still applies
    var input = Build(mode: LoadSchedulingMode.PriorityPlus,
                      netPvW: 0, batterySoC: 80, preferredMinSoC: 20, hysteresisMarginPct: 8,
                      currentImportPriceCt: 10f, maxPriceCt: 20f, currentlyActive: false);
    Assert.True(LoadSchedulingDecision.Decide(input));
  }

  [Fact]
  public void PriorityPlus_CheapPrice_BatteryTooLow_ReturnsFalse()
  {
    // Cheap price does NOT bypass battery SoC start threshold
    var input = Build(mode: LoadSchedulingMode.PriorityPlus,
                      netPvW: 0, batterySoC: 20, preferredMinSoC: 20, hysteresisMarginPct: 8,
                      currentImportPriceCt: 10f, maxPriceCt: 20f, currentlyActive: false);
    Assert.False(LoadSchedulingDecision.Decide(input));
  }

  [Fact]
  public void PriorityPlus_PriceAtExactThreshold_TreatedAsCheap()
  {
    // CurrentImportPriceCt == MaxPriceCt uses <=, so this is priceGood
    var input = Build(mode: LoadSchedulingMode.PriorityPlus,
                      netPvW: 0, batterySoC: 80,
                      currentImportPriceCt: 20f, maxPriceCt: 20f, currentlyActive: false);
    Assert.True(LoadSchedulingDecision.Decide(input));
  }

  [Fact]
  public void PriorityPlus_PriceAboveThreshold_NoPV_ReturnsFalse()
  {
    // Expensive price → price bypass inactive; no PV → pvOk=false
    var input = Build(mode: LoadSchedulingMode.PriorityPlus,
                      netPvW: 0, batterySoC: 80,
                      currentImportPriceCt: 25f, maxPriceCt: 20f, currentlyActive: false);
    Assert.False(LoadSchedulingDecision.Decide(input));
  }

  [Fact]
  public void PriorityPlus_PriceAboveThreshold_PvSurplus_ReturnsTrue()
  {
    // Expensive price but PV covers charge rate → falls through to PV path
    const int chargeRateW = 3_680;
    var input = Build(mode: LoadSchedulingMode.PriorityPlus,
                      netPvW: chargeRateW, chargeRateW: chargeRateW, batterySoC: 80,
                      currentImportPriceCt: 25f, maxPriceCt: 20f, currentlyActive: false);
    Assert.True(LoadSchedulingDecision.Decide(input));
  }

  // ── PV level parametrised (Priority start threshold) ──────────────────────

  [Theory]
  [InlineData(0,     false)]  // no PV at all
  [InlineData(1_000, false)]  // partial PV, well below charge rate
  [InlineData(3_679, false)]  // 1 W below charge rate
  [InlineData(3_680, true)]   // exactly equal to charge rate
  [InlineData(5_000, true)]   // comfortably above charge rate
  public void Priority_Start_VaryNetPvW(int netPvW, bool expected)
  {
    const int chargeRateW = 3_680;
    var input = Build(mode: LoadSchedulingMode.Priority,
                      batterySoC: 80, netPvW: netPvW,
                      chargeRateW: chargeRateW, currentlyActive: false);
    Assert.Equal(expected, LoadSchedulingDecision.Decide(input));
  }

  // ── Import tolerance parametrised (Priority keep threshold) ───────────────

  [Theory]
  [InlineData(500,   true)]   // healthy surplus
  [InlineData(0,     true)]   // balanced (pvThreshold = -200, so 0 >= -200)
  [InlineData(-200,  true)]   // exactly at tolerance boundary
  [InlineData(-201,  false)]  // 1 W beyond tolerance
  [InlineData(-1_000, false)] // deep import
  public void Priority_Keep_VaryNetPvW(int netPvW, bool expected)
  {
    var input = Build(mode: LoadSchedulingMode.Priority,
                      batterySoC: 50, netPvW: netPvW,
                      importToleranceW: 200, currentlyActive: true);
    Assert.Equal(expected, LoadSchedulingDecision.Decide(input));
  }

  // ── Reason strings ─────────────────────────────────────────────────────────

  [Fact]
  public void Reason_Off_ContainsOff()
  {
    LoadSchedulingDecision.Decide(Build(mode: LoadSchedulingMode.Off), out string reason);
    Assert.Contains("Off", reason);
  }

  [Fact]
  public void Reason_TargetReached_ContainsTargetReached()
  {
    LoadSchedulingDecision.Decide(Build(currentLevel: 80f, targetLevel: 80f), out string reason);
    Assert.Contains("Target reached", reason);
  }

  [Fact]
  public void Reason_Optimal_WontReach100_ContainsWontReach()
  {
    var input = Build(mode: LoadSchedulingMode.Optimal,
                      willReachMaxSocToday: false, currentlyActive: false);
    LoadSchedulingDecision.Decide(input, out string reason);
    Assert.Contains("won't reach 100%", reason);
  }

  [Fact]
  public void Reason_Priority_PvInsufficient_ContainsPvInsufficient()
  {
    var input = Build(mode: LoadSchedulingMode.Priority,
                      netPvW: 0, chargeRateW: 3_680, batterySoC: 80, currentlyActive: false);
    LoadSchedulingDecision.Decide(input, out string reason);
    Assert.Contains("PV insufficient", reason);
  }

  [Fact]
  public void Reason_Priority_BatteryTooLow_ContainsBatterySoCTooLow()
  {
    var input = Build(mode: LoadSchedulingMode.Priority,
                      batterySoC: 10, preferredMinSoC: 20, hysteresisMarginPct: 8,
                      netPvW: 4_000, currentlyActive: false);
    LoadSchedulingDecision.Decide(input, out string reason);
    Assert.Contains("Battery SoC too low", reason);
  }

  [Fact]
  public void Reason_ActivePriority_ContainsModeAndNetPv()
  {
    var input = Build(mode: LoadSchedulingMode.Priority,
                      netPvW: 4_000, batterySoC: 80, currentlyActive: false);
    LoadSchedulingDecision.Decide(input, out string reason);
    Assert.Contains("Priority", reason);
    Assert.Contains("net_pv=", reason);
  }

  [Fact]
  public void Reason_PriorityPlus_CheapPrice_ContainsPriceInfo()
  {
    var input = Build(mode: LoadSchedulingMode.PriorityPlus,
                      netPvW: 0, batterySoC: 80,
                      currentImportPriceCt: 10f, maxPriceCt: 20f, currentlyActive: false);
    LoadSchedulingDecision.Decide(input, out string reason);
    Assert.Contains("cheap price", reason);
  }
}
