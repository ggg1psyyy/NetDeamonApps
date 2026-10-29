using NetDeamon.apps.PVControl;
using Xunit;

namespace NetDeamonApps.Tests;

/// <summary>
/// The battery SoC held through sensor dropouts (BatteryState.BatterySoc / IsSoCUnavailable). Live
/// 2026-10-21: sensor.solax_battery_capacity 39 → unknown 13:28:02 → 39 at 13:28:17; the cycle at
/// 13:28:15 read 0 % and switched to force_charge. BatteryState and the cycle's fallback to normal
/// mode read HA entities and publish via MQTT, so they aren't unit tested — the hold/stale decision
/// they rely on is.
/// </summary>
public class HeldReadingTests
{
  static readonly DateTime T0 = new(2026, 10, 21, 13, 27, 47);

  static HeldReading Soc() => new(TimeSpan.FromMinutes(2));

  [Fact]
  public void Dropout_LiveCycleAt132815_UsesLastGoodSoc()
  {
    var soc = Soc();
    soc.Update(T0, 39);                                     // 13:27:47
    soc.Update(new DateTime(2026, 10, 21, 13, 28, 2), null); // unknown
    var cycle = new DateTime(2026, 10, 21, 13, 28, 15);
    soc.Update(cycle, null);
    Assert.Equal(39, soc.Value); // was 0 → empty battery → force_charge
    Assert.False(soc.IsStale(cycle));
  }

  [Fact]
  public void UnknownLongerThanTwoMinutes_IsStale()
  {
    var soc = Soc();
    soc.Update(T0, 39);
    Assert.False(soc.IsStale(T0.AddMinutes(2)));
    Assert.True(soc.IsStale(T0.AddMinutes(2).AddSeconds(1)));
    Assert.Equal(39, soc.Value); // still held, but not to be used for decisions any more
  }

  [Fact]
  public void ReadingBack_NotStaleAnyMore()
  {
    var soc = Soc();
    soc.Update(T0, 39);
    var back = T0.AddMinutes(10);
    soc.Update(back, 40);
    Assert.False(soc.IsStale(back));
    Assert.Equal(40, soc.Value);
  }

  [Fact]
  public void NoReadingYet_IsStale()
  {
    var soc = Soc();
    soc.Update(T0, null);
    Assert.True(soc.IsStale(T0));
    Assert.Null(soc.Value);
  }
}
