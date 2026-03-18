using NetDeamon.apps.PVControl.Predictions;
using Xunit;

namespace NetDeamonApps.Tests;

/// <summary>
/// Tests for PredictionContainer.ValidateData – the 192-slot window check that caused the
/// midnight "Prediction could not be validated" error loop.
/// </summary>
public class PredictionContainerTests : TestBase
{
  // ── helpers ──────────────────────────────────────────────────────────────

  // Set data on a fresh container and return DataOK.
  static bool DataOK(Dictionary<DateTime, int> data)
  {
    var c = new PredictionContainer();
    c.PredictionData = data;
    return c.DataOK;
  }

  // ── valid cases ───────────────────────────────────────────────────────────

  [Fact]
  public void CurrentWindow_192Slots_IsAccepted()
  {
    var data = MakeWindow(DateTime.Now.Date);
    Assert.True(DataOK(data));
  }

  // ── stale data ────────────────────────────────────────────────────────────

  [Fact]
  public void YesterdayWindow_IsRejected()
  {
    // This is exactly what happens after midnight: the old 48h window starts yesterday.
    var data = MakeWindow(DateTime.Now.Date.AddDays(-1));
    Assert.False(DataOK(data));
  }

  [Fact]
  public void TomorrowWindow_IsRejected()
  {
    var data = MakeWindow(DateTime.Now.Date.AddDays(1));
    Assert.False(DataOK(data));
  }

  // ── incomplete data (mimics sparse PV entity payload) ─────────────────────

  [Fact]
  public void OnlyToday_96Slots_IsRejected()
  {
    // Only today's half of the window — tomorrow's slots are missing.
    var data = MakeWindow(DateTime.Now.Date).Take(96).ToDictionary();
    Assert.False(DataOK(data));
  }

  [Fact]
  public void HourlyResolution_IsRejected()
  {
    // The Open-Meteo HA entity may provide hourly keys; 15-min slots would be missing.
    var data = new Dictionary<DateTime, int>();
    for (var t = DateTime.Now.Date; t < DateTime.Now.Date.AddDays(2); t = t.AddHours(1))
      data[t] = 0;
    Assert.False(DataOK(data));
  }

  [Fact]
  public void ExtraEntries_IsRejected()
  {
    // Data that covers the right window but has an extra stale entry.
    var data = MakeWindow(DateTime.Now.Date);
    data[DateTime.Now.Date.AddDays(-1)] = 99; // extra stale entry → count mismatch
    Assert.False(DataOK(data));
  }
}
