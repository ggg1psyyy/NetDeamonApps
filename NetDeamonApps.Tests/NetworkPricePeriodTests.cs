using NetDeamon.apps.PVControl;
using Xunit;

namespace NetDeamonApps.Tests;

/// <summary>
/// Tests for NetworkPricePeriod.IsActiveAt — the month/hour window match used to apply
/// time-bounded network price overrides (SNAP, WiNAP). Both ranges must support wrapping
/// across their boundary: WiNAP spans Oct-Mar (crosses the calendar year) and 22:00-04:00
/// (crosses midnight), which a naive start&lt;=value&lt;=end check cannot express.
/// </summary>
public class NetworkPricePeriodTests
{
  private static NetworkPricePeriod Winap => new()
  {
    Name = "WiNAP",
    StartMonth = 10,
    EndMonth = 3,
    StartHour = 22,
    EndHour = 4,
    Price = 0.06882f,
  };

  private static NetworkPricePeriod Snap => new()
  {
    Name = "SNAP",
    StartMonth = 4,
    EndMonth = 9,
    StartHour = 10,
    EndHour = 16,
    Price = 0.06882f,
  };

  // ── Wrapping period (WiNAP: Oct-Mar, 22:00-04:00) ──────────────────────────

  [Theory]
  [InlineData(10, 23)]  // October, late night
  [InlineData(12, 22)]  // December, exactly at start hour
  [InlineData(1, 3)]    // January, middle of the night
  [InlineData(3, 0)]    // March, just after midnight
  public void Winap_IsActive_DuringWinterNightWindow(int month, int hour)
    => Assert.True(Winap.IsActiveAt(new DateTime(2026, month, 1, hour, 0, 0)));

  [Theory]
  [InlineData(10, 21)]  // October, one hour before the window opens
  [InlineData(3, 4)]    // March, exactly at end hour (exclusive)
  [InlineData(4, 23)]   // April, outside the month range entirely
  [InlineData(9, 23)]   // September, night before WiNAP starts
  [InlineData(1, 12)]   // January, daytime (outside the hour window)
  public void Winap_IsNotActive_OutsideWinterNightWindow(int month, int hour)
    => Assert.False(Winap.IsActiveAt(new DateTime(2026, month, 1, hour, 0, 0)));

  // ── Non-wrapping period (SNAP: Apr-Sep, 10:00-16:00) — regression guard ────

  [Theory]
  [InlineData(4, 10)]   // April, exactly at start hour
  [InlineData(7, 13)]   // July, midday
  [InlineData(9, 15)]   // September, just before end hour
  public void Snap_IsActive_DuringSummerDayWindow(int month, int hour)
    => Assert.True(Snap.IsActiveAt(new DateTime(2026, month, 1, hour, 0, 0)));

  [Theory]
  [InlineData(4, 9)]    // April, one hour before the window opens
  [InlineData(9, 16)]   // September, exactly at end hour (exclusive)
  [InlineData(3, 12)]   // March, outside the month range
  [InlineData(10, 12)]  // October, outside the month range
  public void Snap_IsNotActive_OutsideSummerDayWindow(int month, int hour)
    => Assert.False(Snap.IsActiveAt(new DateTime(2026, month, 1, hour, 0, 0)));
}
