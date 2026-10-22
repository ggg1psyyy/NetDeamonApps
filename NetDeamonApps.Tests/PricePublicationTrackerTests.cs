using NetDeamon.apps.PVControl;
using Xunit;

namespace NetDeamonApps.Tests;

/// <summary>
/// sensor.pv_control_price_publication: when tomorrow's prices first appear in PVControl's price
/// list (HA's recorder doesn't keep the list itself). Reading the list and publishing the sensor
/// need a live HA.
/// </summary>
public class PricePublicationTrackerTests
{
  static readonly DateTime Day = new(2026, 10, 22);
  static readonly DateTime TodayOnly = Day.AddDays(1), WithTomorrow = Day.AddDays(2);

  [Fact]
  public void PendingThenPublished_TimeNoted()
  {
    var t = new PricePublicationTracker();
    Assert.False(t.Observe(Day.AddHours(13.4), TodayOnly));
    Assert.Null(t.PublishedToday(Day.AddHours(13.4)));
    Assert.True(t.Observe(Day.AddHours(13.75), WithTomorrow));
    Assert.Equal(Day.AddHours(13.75), t.PublishedToday(Day.AddHours(14)));
    Assert.False(t.Observe(Day.AddHours(14), WithTomorrow)); // once per day
  }

  [Fact]
  public void RestartAfterPublication_NotNotedAsPublicationTime()
  {
    // NetDaemon restarted at 15:00, the prices were already there: 15:00 isn't their publication time.
    var t = new PricePublicationTracker();
    Assert.False(t.Observe(Day.AddHours(15), WithTomorrow));
    Assert.Empty(t.Days);
  }

  [Fact]
  public void NextDay_PendingAgain_NewEntry_KeepsFourteenDays()
  {
    var t = new PricePublicationTracker();
    for (int d = 0; d < 20; d++)
    {
      var day = Day.AddDays(d);
      t.Observe(day.AddHours(10), day.AddDays(1));
      Assert.True(t.Observe(day.AddHours(13 + d % 3), day.AddDays(2)));
    }
    Assert.Equal(PricePublicationTracker.KeepDays, t.Days.Count);
    Assert.Equal(Day.AddDays(19), t.Days[^1].Day);
  }
}
