using System;
using System.Collections.Generic;

namespace NetDeamon;

/// <summary>
/// The subscriptions an app has to end itself when NetDaemon disposes it. NetDaemon ends the app's HA state
/// subscriptions and its scheduler with the app, but not those on singletons — the MQTT entity manager's command
/// subscriptions live on: after a reload (e.g. a reconnect to Home Assistant) the old app instance still handled
/// every command with its stale state (live 2026-10-30: a cost row booked twice, an old inverter command published).
/// </summary>
public sealed class AppSubscriptions : IDisposable
{
  private readonly List<IDisposable> _items = [];
  private readonly object _lock = new();

  /// <summary>Whether the app was disposed: handlers return at once (a command already on its way).</summary>
  public bool IsDisposed { get; private set; }

  /// <summary>Keeps a subscription until <see cref="Dispose"/>; one added after it is ended at once.</summary>
  /// <param name="subscription">The subscription.</param>
  /// <returns>The subscription.</returns>
  public T Add<T>(T subscription) where T : IDisposable
  {
    lock (_lock)
    {
      if (!IsDisposed)
      {
        _items.Add(subscription);
        return subscription;
      }
    }
    subscription.Dispose();
    return subscription;
  }

  /// <summary>Ends every subscription (each on its own: one failing doesn't keep the others alive).</summary>
  public void Dispose()
  {
    List<IDisposable> items;
    lock (_lock)
    {
      if (IsDisposed) return;
      IsDisposed = true;
      items = [.. _items];
      _items.Clear();
    }
    foreach (var item in items)
    {
      try { item.Dispose(); }
      catch (Exception) { /* the next one still has to end */ }
    }
  }
}
