using System;
using System.Collections.Generic;
using System.Linq;
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

    // The running slot (10:00) is the simulation's first slot — it gets the live average in full,
    // the ramp then goes down to the day-scaled forecast over the next three.
    Assert.Equal(liveRunningAvgW / 4, corrected[now.RoundToNearestQuarterHour()]);
  }

  /// <summary>A day the forecast is far too low (synthetic: ≈ 300 Wh per quarter in the
  /// afternoon, actual PV 1.83× of it, live average ≈ 2.5 kW) — where the boundary jump is largest.</summary>
  static (Dictionary<DateTime, int> Raw, Func<DateTime, int> Actual) Live20261022()
  {
    var today = new DateTime(2026, 10, 22);
    var raw = new Dictionary<DateTime, int>();
    for (int i = 0; i < 96; i++)
    {
      var t = today.AddMinutes(15 * i);
      raw[t] = t.Hour is >= 8 and < 17 ? 300 : 0;
    }
    // Actual energy grows continuously: 1.83 × the forecast up to that moment.
    int Actual(DateTime now) => (int)(1.83 * raw.Sum(kv =>
      kv.Value * Math.Clamp((now - kv.Key).TotalMinutes / 15.0, 0.0, 1.0)));
    return (raw, Actual);
  }

  static int SumFrom(Dictionary<DateTime, int> pv, DateTime from) =>
    pv.Where(kv => kv.Key >= from && kv.Key < from.Date.AddDays(1)).Sum(kv => kv.Value);

  [Fact]
  public void PVCorrection_Live20261022_NoJumpAtQuarterBoundary()
  {
    // Before, the 14:15 slot counted fully as "until now" at 14:15:15 (actual ≈ 0 for it) and as
    // past (raw, no correction): here ≈ −600 Wh of PV in the simulation. Live 2026-10-22 (forecast
    // about right, ratio 0.90 → 0.85) it was −163 Wh — part of the jump that restarted a grid
    // charge at 14:15:15; the larger part was the running slot (RunningSlotTests).
    var (raw, actual) = Live20261022();
    var before = new DateTime(2026, 10, 22, 14, 12, 15);
    var after = new DateTime(2026, 10, 22, 14, 15, 15);
    var from = new DateTime(2026, 10, 22, 14, 15, 0); // PV still ahead at both times

    int pvBefore = SumFrom(NetEnergyPrediction.WithRunningAvgCorrection(raw, 2500, actual(before), before), from);
    int pvAfter = SumFrom(NetEnergyPrediction.WithRunningAvgCorrection(raw, 2500, actual(after), after), from);

    // What's left is the 4-slot live-average ramp moving on by one slot (≈ 75 Wh here, < 1 % SoC).
    Assert.InRange(pvAfter - pvBefore, -100, 100);
  }

  /// <summary>An October evening: forecast falling from 600 Wh per quarter at 16:00 to 0 at 18:00
  /// (steep), the PV doing exactly what the forecast says.</summary>
  static Dictionary<DateTime, int> SteepEvening()
  {
    var today = new DateTime(2026, 10, 20);
    var raw = new Dictionary<DateTime, int>();
    for (int i = 0; i < 96; i++)
    {
      var t = today.AddMinutes(15 * i);
      raw[t] = t.Hour >= 10 && t.Hour < 16 ? 600 : 0;
    }
    for (var t = today.AddHours(16); t < today.AddHours(18); t = t.AddMinutes(15))
      raw[t] = (int)(600 * (1 - (t - today.AddHours(16)).TotalHours / 2)); // 16:00 600 → 17:45 75
    return raw;
  }

  [Fact]
  public void PVCorrection_SteepEvening_KeepsTheForecastSlope()
  {
    // 17:00, the PV exactly on forecast: the last 15 min averaged 1.5 kW (forecast 375 Wh).
    // The absolute ramp would carry those 375 Wh per quarter into 17:00–17:45, where the forecast
    // falls from 300 to 75 Wh — too much PV every evening (10 days: evening error 154 → 77 Wh).
    var raw = SteepEvening();
    var now = new DateTime(2026, 10, 20, 17, 0, 0);
    int actualToday = raw.Where(kv => kv.Key < now).Sum(kv => kv.Value);
    int lastQuarterWh = raw[now.AddMinutes(-15)];
    var corrected = NetEnergyPrediction.WithRunningAvgCorrection(raw, lastQuarterWh * 4, actualToday, now);
    foreach (var minutes in new[] { 0, 15, 30, 45 })
    {
      var slot = now.AddMinutes(minutes);
      Assert.InRange(corrected[slot], raw[slot] - 2, raw[slot] + 2); // on forecast → stays on forecast
    }
  }

  [Fact]
  public void PVCorrection_Dawn_TooLittleForecastForARatio_UsesTheLiveAverage()
  {
    // 08:10, the forecast in the last 15 min under NearTermRatioMinWh: the live average is used
    // absolutely (the ratio of two tiny numbers is noise).
    var today = new DateTime(2026, 10, 20);
    var raw = new Dictionary<DateTime, int>();
    for (int i = 0; i < 96; i++) raw[today.AddMinutes(15 * i)] = i >= 32 && i < 72 ? 40 * (i - 31) : 0; // 08:00 40 Wh, rising
    var now = today.AddHours(8).AddMinutes(10);
    var corrected = NetEnergyPrediction.WithRunningAvgCorrection(raw, 400, 30, now);
    Assert.Equal(400 / 4, corrected[today.AddHours(8)]);
  }

  [Fact]
  public void PVCorrection_SlotTheSimulationStartsWith_IsCorrected()
  {
    // 15 s into the 14:15 slot the simulation still starts with it — it must get the day scale
    // and the live average like any slot ahead, not the raw forecast.
    var (raw, actual) = Live20261022();
    var now = new DateTime(2026, 10, 22, 14, 15, 15);
    var corrected = NetEnergyPrediction.WithRunningAvgCorrection(raw, 2500, actual(now), now);
    Assert.Equal(2500 / 4, corrected[now.RoundToNearestQuarterHour()]);
    Assert.InRange(corrected[new DateTime(2026, 10, 22, 16, 0, 0)], 548, 550); // 300 × 1.83
  }
}
