using System;
using System.Collections.Generic;
using NetDeamon.apps;
using NetDeamon.apps.PVControl.Predictions;
using Xunit;

namespace NetDeamonApps.Tests;

/// <summary>
/// Regression tests for NetEnergyPrediction.WithRunningAvgCorrection's near-term ramp, which
/// blends the next few forecast slots toward the live PV/load running average. Pure
/// computation over plain dictionaries — no HA dependencies.
/// </summary>
public class NetEnergyPredictionTests
{
  [Fact]
  public void PVCorrection_NeverFabricatesPVInSlotsForecastAsZero()
  {
    // Reported live bug: at 22:08 on an October evening, the forecast correctly shows 0 PV for
    // the rest of the day, but the live 5-min PV running-average sensor hadn't fully decayed to
    // zero after sunset (lingering self-consumption/noise reading). The ramp blended the next 4
    // slots toward that nonzero average regardless of the forecast saying it should be dark,
    // injecting a false "net PV" slot that corrupted FirstRelevantPVEnergyToday/Tomorrow and
    // made the EV charger schedule a session for 22:35 tonight instead of ~7am the next morning.
    var now = new DateTime(2026, 10, 17, 22, 8, 0);
    var today = now.Date;

    var raw = new Dictionary<DateTime, int>();
    for (int i = 0; i < 40; i++) raw[today.AddMinutes(i * 15)] = 0;    // 00:00–09:45, pre-dawn
    for (int i = 40; i < 72; i++) raw[today.AddMinutes(i * 15)] = 500; // 10:00–17:45, daylight
    for (int i = 72; i < 96; i++) raw[today.AddMinutes(i * 15)] = 0;   // 18:00–23:45, correctly dark

    const int lingeringRunningAvgW = 400; // nonzero even though it's well after sunset

    var corrected = NetEnergyPrediction.WithRunningAvgCorrection(raw, lingeringRunningAvgW, actualPVEnergyWh: 4000, now);

    // The next 4 slots after `now` (22:15, 22:30, 22:45, 23:00) must stay at 0 — the forecast
    // says there's no PV potential at all, so the live average (sensor noise, not real PV)
    // must never be blended in here.
    foreach (var minutesAfter in new[] { 7, 22, 37, 52 }) // 22:08+7=22:15, +22=22:30, …
    {
      var slot = now.AddMinutes(minutesAfter).RoundToNearestQuarterHour();
      Assert.Equal(0, corrected[slot]);
    }
  }

  [Fact]
  public void PVCorrection_StillRampsNearTermSlotsThatForecastRealPV()
  {
    // The ramp must still do its job during the day: a slot the forecast says has real PV
    // potential should blend toward the live average, same as before this fix.
    var now = new DateTime(2026, 7, 10, 10, 8, 0);
    var today = now.Date;

    var raw = new Dictionary<DateTime, int>();
    for (int i = 0; i < 96; i++) raw[today.AddMinutes(i * 15)] = 500; // forecast PV all day

    const int liveRunningAvgW = 2_000; // well above the flat 500 Wh/slot forecast

    var corrected = NetEnergyPrediction.WithRunningAvgCorrection(raw, liveRunningAvgW, actualPVEnergyWh: 2000, now);

    var firstSlot = now.AddMinutes(7).RoundToNearestQuarterHour(); // 10:15 — full ramp weight
    Assert.Equal(liveRunningAvgW / 4, corrected[firstSlot]);
  }
}
