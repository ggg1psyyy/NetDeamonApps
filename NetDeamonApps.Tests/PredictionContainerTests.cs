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

  // ── one clock read per cycle (live 2026-10-20 00:00:00) ─────────────────
  // The predictions read the clock separately: a window built at 23:59:59 (19.10.–20.10.) was
  // validated or combined at 00:00:00 (20.10.–21.10.) → "Prediction could not be validated",
  // then "Could not find SolarForeCast" for every 19.10. slot.

  static readonly DateTime BeforeMidnight = new(2026, 10, 19, 23, 59, 59, 900);
  static readonly DateTime AfterMidnight = new(2026, 10, 20, 0, 0, 0);

  [Fact]
  public void WindowBuiltBeforeMidnight_ValidatedWithALaterClock_Rejected() =>
    Assert.False(new PredictionContainer().UpdateData(MakeWindow(BeforeMidnight.Date), AfterMidnight));

  [Fact]
  public void WindowBuiltBeforeMidnight_ValidatedWithTheSameNow_Accepted() =>
    Assert.True(new PredictionContainer().UpdateData(MakeWindow(BeforeMidnight.Date), BeforeMidnight));

  sealed class WindowPrediction : Prediction
  {
    public WindowPrediction() => Initialize("Test");
    protected override Dictionary<DateTime, int> PopulateData(DateTime now) => MakeWindow(now.Date, 1);
  }

  [Fact]
  public void Prediction_UpdateData_UsesTheGivenNowNotTheClock()
  {
    var p = new WindowPrediction();
    p.UpdateData(BeforeMidnight);
    Assert.True(p.DataOK);
    Assert.Equal(BeforeMidnight.Date, p.TodayAndTomorrow.Keys.Min());
    p.UpdateData(AfterMidnight);
    Assert.Equal(AfterMidnight.Date, p.TodayAndTomorrow.Keys.Min());
  }

  [Fact]
  public void InvalidData_EmptyWindowOfTheGivenDay()
  {
    var c = new PredictionContainer();
    c.UpdateData(MakeWindow(BeforeMidnight.Date), AfterMidnight);
    Assert.Equal(AfterMidnight.Date, c.PredictionData.Keys.Min());
    Assert.Equal(192, c.PredictionData.Count);
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
