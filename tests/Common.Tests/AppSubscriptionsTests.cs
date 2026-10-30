using NetDeamon;
using Xunit;

namespace NetDeamonApps.Tests;

/// <summary>
/// <see cref="AppSubscriptions"/>: an app ends its MQTT command subscriptions when NetDaemon disposes it (live
/// 2026-10-30: after a reconnect to Home Assistant the old PVControl instance still handled every command). That the
/// apps register theirs with it and dispose it (PVControl, HeatingTest: IAsyncDisposable) needs HA and MQTT — the
/// add-on log's "PVControl disposed" line after a reload shows it live.
/// </summary>
public class AppSubscriptionsTests
{
  sealed class Probe : IDisposable
  {
    public int Disposed;
    public void Dispose() => Disposed++;
  }

  sealed class Failing : IDisposable
  {
    public void Dispose() => throw new InvalidOperationException("broken");
  }

  [Fact]
  public void Dispose_EndsEverySubscription_Once()
  {
    var subs = new AppSubscriptions();
    var a = subs.Add(new Probe());
    var b = subs.Add(new Probe());
    Assert.False(subs.IsDisposed);
    subs.Dispose();
    subs.Dispose();
    Assert.True(subs.IsDisposed);
    Assert.Equal(1, a.Disposed);
    Assert.Equal(1, b.Disposed);
  }

  [Fact]
  public void AddedAfterDispose_EndedAtOnce()
  {
    // A subscription still being set up while the app is disposed (InitializeAsync runs on) never stays alive.
    var subs = new AppSubscriptions();
    subs.Dispose();
    Assert.Equal(1, subs.Add(new Probe()).Disposed);
  }

  [Fact]
  public void OneFailing_TheOthersStillEnd()
  {
    var subs = new AppSubscriptions();
    var before = subs.Add(new Probe());
    subs.Add(new Failing());
    var after = subs.Add(new Probe());
    subs.Dispose();
    Assert.Equal(1, before.Disposed);
    Assert.Equal(1, after.Disposed);
  }
}
