using NetDeamon.apps.PVControl.Simulator;
using Xunit;

namespace NetDeamonApps.Tests;

/// <summary>
/// Unit tests for <see cref="LoadSchedulingDecision.PrioritySoCGateOk"/> — HouseEnergy.
/// FindLoadWindow's real-time SoC safety overlay on the Priority/PriorityPlus restart hysteresis.
/// Pure function — no Home Assistant or HA mock required.
/// </summary>
public class LoadSchedulingDecisionTests
{
  // ── PrioritySoCGateOk (HouseEnergy.FindLoadWindow's real-time SoC gate) ────────────────────
  // hysteresisMarginPct=5, pvBypassMinSocBufferPct=3 throughout — matches HouseEnergy.cs's
  // PrioritySoCHysteresisPct / PvBypassMinSocBufferPct constants.

  [Fact]
  public void PrioritySoCGateOk_Stop_BelowFloor_ReturnsFalse()
    => Assert.False(LoadSchedulingDecision.PrioritySoCGateOk(
        currentlyActive: true, currentSoC: 19, socFloor: 20, netPvW: 4_000, chargeRateW: 1_800,
        hysteresisMarginPct: 5, pvBypassMinSocBufferPct: 3));

  [Fact]
  public void PrioritySoCGateOk_Stop_AtFloor_ReturnsTrue()
    => Assert.True(LoadSchedulingDecision.PrioritySoCGateOk(
        currentlyActive: true, currentSoC: 20, socFloor: 20, netPvW: -4_000, chargeRateW: 1_800,
        hysteresisMarginPct: 5, pvBypassMinSocBufferPct: 3));

  [Fact]
  public void PrioritySoCGateOk_Restart_JustOverFloor_NoPV_StaysBlocked()
  {
    // This is the reported bug: without hysteresis, currentSoC=21 > floor=20 would restart
    // immediately after stopping — then drain straight back under the floor.
    Assert.False(LoadSchedulingDecision.PrioritySoCGateOk(
        currentlyActive: false, currentSoC: 21, socFloor: 20, netPvW: 0, chargeRateW: 1_800,
        hysteresisMarginPct: 5, pvBypassMinSocBufferPct: 3));
  }

  [Fact]
  public void PrioritySoCGateOk_Restart_AtHysteresisThreshold_ReturnsTrue()
    => Assert.True(LoadSchedulingDecision.PrioritySoCGateOk(
        currentlyActive: false, currentSoC: 25, socFloor: 20, netPvW: 0, chargeRateW: 1_800,
        hysteresisMarginPct: 5, pvBypassMinSocBufferPct: 3));

  [Fact]
  public void PrioritySoCGateOk_Restart_JustBelowHysteresisThreshold_ReturnsFalse()
    => Assert.False(LoadSchedulingDecision.PrioritySoCGateOk(
        currentlyActive: false, currentSoC: 24, socFloor: 20, netPvW: 0, chargeRateW: 1_800,
        hysteresisMarginPct: 5, pvBypassMinSocBufferPct: 3));

  [Fact]
  public void PrioritySoCGateOk_Restart_JustOverFloor_PvCoversChargeRate_StillBlocked()
  {
    // Regression test for the live oscillation bug: SoC hovering right at the hard floor
    // (21, only 1% above floor=20) with PV comfortably covering the charge rate used to
    // bypass the hysteresis wait immediately — since a live PV reading is very often already
    // above a single load's charge rate, this let the load restart the instant SoC ticked up
    // to the floor, then drain straight back under it within minutes. The SoC buffer
    // (pvBypassMinSocBufferPct) must block the bypass here even though PV is ample.
    Assert.False(LoadSchedulingDecision.PrioritySoCGateOk(
        currentlyActive: false, currentSoC: 21, socFloor: 20, netPvW: 4_000, chargeRateW: 1_800,
        hysteresisMarginPct: 5, pvBypassMinSocBufferPct: 3));
  }

  [Fact]
  public void PrioritySoCGateOk_Restart_PvCoversChargeRate_BypassesHysteresisOnceBufferMet()
  {
    // Once SoC has climbed the full pvBypassMinSocBufferPct above the floor (20+3=23) — not
    // merely reached it — and PV alone can sustain the charge rate, restarting won't draw the
    // battery down further, so the rest of the hysteresis wait is skipped.
    Assert.True(LoadSchedulingDecision.PrioritySoCGateOk(
        currentlyActive: false, currentSoC: 23, socFloor: 20, netPvW: 1_800, chargeRateW: 1_800,
        hysteresisMarginPct: 5, pvBypassMinSocBufferPct: 3));
  }

  [Fact]
  public void PrioritySoCGateOk_Restart_JustBelowBypassBuffer_PvSufficient_StaysBlocked()
    => Assert.False(LoadSchedulingDecision.PrioritySoCGateOk(
        currentlyActive: false, currentSoC: 22, socFloor: 20, netPvW: 4_000, chargeRateW: 1_800,
        hysteresisMarginPct: 5, pvBypassMinSocBufferPct: 3));

  [Fact]
  public void PrioritySoCGateOk_Restart_BufferMet_PvBelowChargeRate_StaysBlocked()
    => Assert.False(LoadSchedulingDecision.PrioritySoCGateOk(
        currentlyActive: false, currentSoC: 23, socFloor: 20, netPvW: 1_799, chargeRateW: 1_800,
        hysteresisMarginPct: 5, pvBypassMinSocBufferPct: 3));

  [Fact]
  public void PrioritySoCGateOk_Restart_BelowFloor_StaysBlockedEvenWithPV()
    => Assert.False(LoadSchedulingDecision.PrioritySoCGateOk(
        currentlyActive: false, currentSoC: 19, socFloor: 20, netPvW: 10_000, chargeRateW: 1_800,
        hysteresisMarginPct: 5, pvBypassMinSocBufferPct: 3));
}
