using NetDaemon.HassModel;
using NetDaemon.HassModel.Entities;
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;

namespace NetDeamon.apps.PVControl
{
  /// <summary>
  /// PVControl's view of HA's states: NetDaemon's state cache, corrected where a re-read from HA (<see cref="Settle"/>)
  /// is newer. NetDaemon fills its cache on every (re)connect from one snapshot after subscribing to the events and
  /// overwrites every entity with it, so a change between HA's snapshot and the cache's write is lost: the entity keeps
  /// the older value until its next change (2026-11-09 07:34: HA restarted, binary_sensor.pv_control_inverter_in_sync
  /// read "off" in PVControl until 09:13 while HA had "on" since 07:34:00 — a false health alarm for 1.5 h). A
  /// correction lasts until the cache has the entity newer (its next event), so nothing has to be cleared.
  /// </summary>
  /// <param name="inner">NetDaemon's app context.</param>
  public sealed class SettledHaContext(IHaContext inner) : IHaContext
  {
    private readonly ConcurrentDictionary<string, EntityState> _fresh = new();

    /// <summary>NetDaemon's context.</summary>
    public IHaContext Inner => inner;

    /// <summary>Entities corrected right now (for the log and tests).</summary>
    public int Corrections => _fresh.Count;

    /// <inheritdoc/>
    public EntityState? GetState(string entityId)
    {
      var cached = inner.GetState(entityId);
      if (!_fresh.TryGetValue(entityId, out var fresh)) return cached;
      if (IsNewer(fresh, cached)) return fresh;
      _fresh.TryRemove(new KeyValuePair<string, EntityState>(entityId, fresh));   // the cache caught up
      return cached;
    }

    /// <summary>
    /// Takes the states read from HA where they are newer than the cache's and differ from it.
    /// </summary>
    /// <param name="states">The states as HA has them now (GET /api/states).</param>
    /// <returns>The entities corrected, with the cache's and HA's state.</returns>
    public IReadOnlyList<(string EntityId, string? Cached, string? Fresh)> Settle(IEnumerable<EntityState> states)
    {
      var corrected = new List<(string, string?, string?)>();
      foreach (var fresh in states)
      {
        if (string.IsNullOrEmpty(fresh.EntityId) || !Informative(fresh)) continue;
        var cached = inner.GetState(fresh.EntityId);
        if (!IsNewer(fresh, cached) || SameAs(fresh, cached)) continue;
        _fresh[fresh.EntityId] = fresh;
        corrected.Add((fresh.EntityId, cached?.State, fresh.State));
      }
      return corrected;
    }

    /// <summary>A state HA updated after the cached one (no cached one: newer).</summary>
    /// <param name="fresh">Read from HA.</param>
    /// <param name="cached">The cache's.</param>
    public static bool IsNewer(EntityState fresh, EntityState? cached) =>
      cached?.LastUpdated is not { } c || fresh.LastUpdated is { } f && f.ToUniversalTime() > c.ToUniversalTime();   // websocket and REST parse alike; UTC whatever the Kind

    /// <summary>"unknown"/"unavailable" from HA is no information (an entity during HA's start): the cache's stays.</summary>
    /// <param name="state">Read from HA.</param>
    public static bool Informative(EntityState state) => state.State is { } s && s != "unknown" && s != "unavailable";

    private static bool SameAs(EntityState fresh, EntityState? cached) =>
      cached is not null && cached.State == fresh.State && Raw(cached.AttributesJson) == Raw(fresh.AttributesJson);

    private static string Raw(JsonElement? json) => json is { } j ? j.GetRawText() : "";

    /// <inheritdoc/>
    public IObservable<Event> Events => inner.Events;
    /// <inheritdoc/>
    public IObservable<StateChange> StateAllChanges() => inner.StateAllChanges();
    /// <inheritdoc/>
    public IReadOnlyList<Entity> GetAllEntities() => inner.GetAllEntities();
    /// <inheritdoc/>
    public void CallService(string domain, string service, ServiceTarget? target = null, object? data = null) =>
      inner.CallService(domain, service, target, data);
    /// <inheritdoc/>
    public Task<JsonElement?> CallServiceWithResponseAsync(string domain, string service, ServiceTarget? target = null, object? data = null) =>
      inner.CallServiceWithResponseAsync(domain, service, target, data);
    /// <inheritdoc/>
    public Area? GetAreaFromEntityId(string entityId) => inner.GetAreaFromEntityId(entityId);
    /// <inheritdoc/>
    public EntityRegistration? GetEntityRegistration(string entityId) => inner.GetEntityRegistration(entityId);
    /// <inheritdoc/>
    public void SendEvent(string eventType, object? data = null) => inner.SendEvent(eventType, data);
  }

  /// <summary>
  /// Points the entities of a configuration (NetDaemon binds them to its own context) at another context: every
  /// <see cref="Entity"/> property, in lists and nested configuration objects of PVControl's own types.
  /// </summary>
  public static class EntityRebinder
  {
    /// <summary>Rebinds the entities under <paramref name="root"/>.</summary>
    /// <param name="root">The configuration.</param>
    /// <param name="context">The context they read from.</param>
    /// <returns>How many entities were rebound.</returns>
    public static int Rebind(object root, IHaContext context) => Walk(root, context, new HashSet<object>(ReferenceEqualityComparer.Instance));

    private static bool Ours(Type t) => t.IsClass && t != typeof(string) && t.Namespace?.StartsWith("NetDeamon.apps", StringComparison.Ordinal) == true;

    private static int Walk(object node, IHaContext context, HashSet<object> seen)
    {
      if (!seen.Add(node)) return 0;
      int count = 0;
      foreach (var p in node.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
      {
        // Only what the configuration binding sets: computed properties (a path, a derived value) are no configuration.
        if (p.GetIndexParameters().Length > 0 || !p.CanRead || p.SetMethod is not { IsPublic: true }) continue;
        var value = p.GetValue(node);
        if (value is null) continue;
        if (value is Entity e)
        {
          if (p.PropertyType.IsAssignableFrom(typeof(Entity)) && e.GetType() == typeof(Entity))
          {
            p.SetValue(node, new Entity(context, e.EntityId));
            count++;
          }
        }
        else if (value is IList list && value is not string)
        {
          for (int i = 0; i < list.Count; i++)
          {
            if (list[i] is Entity le && le.GetType() == typeof(Entity) && !list.IsReadOnly)
            {
              list[i] = new Entity(context, le.EntityId);
              count++;
            }
            else if (list[i] is { } item && Ours(item.GetType()))
              count += Walk(item, context, seen);
          }
        }
        else if (Ours(value.GetType()))
          count += Walk(value, context, seen);
      }
      return count;
    }
  }
}
