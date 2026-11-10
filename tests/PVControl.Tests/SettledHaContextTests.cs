using NetDaemon.HassModel;
using NetDaemon.HassModel.Entities;
using NetDeamon.apps.PVControl;
using NetDeamon.apps.PVControl.Managers;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace NetDeamonApps.Tests;

/// <summary>
/// PVControl's states after a (re)connect (<see cref="SettledHaContext"/>): NetDaemon's cache, corrected where a re-read
/// from HA is newer. Live 2026-11-09 07:34: HA restarted, the in-sync template went off 07:33:47 → on 07:34:00, the cache
/// kept "off" until the next change at 09:13 — health "not in the commanded mode" for 1.5 h. The REST call itself
/// (GET /api/states in <c>PVControl.SettleStatesAsync</c>) needs live HA and isn't unit tested.
/// </summary>
public class SettledHaContextTests
{
  const string InSync = "binary_sensor.pv_control_inverter_in_sync";
  static readonly DateTime T0 = new(2026, 11, 9, 6, 33, 47, DateTimeKind.Utc);   // 07:33:47 local

  /// <summary>NetDaemon's cache: a state per entity, set by the test.</summary>
  sealed class Cache : IHaContext
  {
    public readonly Dictionary<string, EntityState> States = [];
    public EntityState? GetState(string entityId) => States.GetValueOrDefault(entityId);
    public IObservable<Event> Events => Observable.Empty<Event>();
    public IObservable<StateChange> StateAllChanges() => Observable.Empty<StateChange>();
    public IReadOnlyList<Entity> GetAllEntities() => [];
    public void CallService(string domain, string service, ServiceTarget? target = null, object? data = null) { }
    public Task<JsonElement?> CallServiceWithResponseAsync(string domain, string service, ServiceTarget? target = null, object? data = null) => Task.FromResult<JsonElement?>(null);
    public Area? GetAreaFromEntityId(string entityId) => null;
    public EntityRegistration? GetEntityRegistration(string entityId) => null;
    public void SendEvent(string eventType, object? data = null) { }
  }

  static EntityState State(string id, string state, DateTime updated, string attributes = "{}") => new()
  {
    EntityId = id, State = state, LastUpdated = updated, LastChanged = updated, AttributesJson = JsonDocument.Parse(attributes).RootElement,
  };

  [Fact]
  public void TheInverterSyncOfNov9_TheCachesOlderOffIsCorrectedToHAsOn()
  {
    var cache = new Cache();
    cache.States[InSync] = State(InSync, "off", T0);
    var settled = new SettledHaContext(cache);
    var entity = new Entity(settled, InSync);
    Assert.Equal("off", entity.State);

    var corrected = settled.Settle([State(InSync, "on", T0.AddSeconds(13))]);
    Assert.Equal((InSync, "off", "on"), Assert.Single(corrected));
    Assert.Equal("on", entity.State);
  }

  [Fact]
  public void TheCacheNewer_OrTheSame_StaysAsItIs()
  {
    var cache = new Cache();
    cache.States["sensor.a"] = State("sensor.a", "5", T0.AddSeconds(10));   // an event after HA's read
    cache.States["sensor.b"] = State("sensor.b", "7", T0);
    var settled = new SettledHaContext(cache);
    Assert.Empty(settled.Settle([State("sensor.a", "4", T0), State("sensor.b", "7", T0), State("sensor.b", "7", T0.AddSeconds(5))]));
    Assert.Equal("5", new Entity(settled, "sensor.a").State);
    Assert.Equal(0, settled.Corrections);
  }

  [Fact]
  public void AttributesOnly_AreACorrectionToo()
  {
    // The restore reads sensor.pv_control_mode's attributes (bulk, reason): a newer attribute set counts.
    var cache = new Cache();
    cache.States["sensor.pv_control_mode"] = State("sensor.pv_control_mode", "force_charge", T0, """{"bulk":false}""");
    var settled = new SettledHaContext(cache);
    Assert.Single(settled.Settle([State("sensor.pv_control_mode", "force_charge", T0.AddSeconds(2), """{"bulk":true}""")]));
    Assert.True(new Entity(settled, "sensor.pv_control_mode").EntityState!.AttributesJson!.Value.GetProperty("bulk").GetBoolean());
  }

  [Fact]
  public void UnknownFromHA_IsNoInformation()
  {
    // During HA's start its entities read "unknown" for a second: the cache's value stays.
    var cache = new Cache();
    cache.States["sensor.soc"] = State("sensor.soc", "80", T0);
    var settled = new SettledHaContext(cache);
    Assert.Empty(settled.Settle([State("sensor.soc", "unknown", T0.AddSeconds(1)), State("sensor.x", "unavailable", T0)]));
    Assert.Equal("80", new Entity(settled, "sensor.soc").State);
  }

  [Fact]
  public void NotInTheCache_TakesHAs()
  {
    var settled = new SettledHaContext(new Cache());
    Assert.Single(settled.Settle([State("sensor.new", "1", T0)]));
    Assert.Equal("1", new Entity(settled, "sensor.new").State);
  }

  [Fact]
  public void TheNextEvent_EndsTheCorrection()
  {
    var cache = new Cache();
    cache.States[InSync] = State(InSync, "off", T0);
    var settled = new SettledHaContext(cache);
    settled.Settle([State(InSync, "on", T0.AddSeconds(13))]);
    cache.States[InSync] = State(InSync, "off", T0.AddMinutes(30));   // a real change later: the cache's again
    Assert.Equal("off", new Entity(settled, InSync).State);
    Assert.Equal(0, settled.Corrections);
  }

  [Fact]
  public void TheConfigurationsEntities_ReadThroughTheSettledContext()
  {
    // NetDaemon binds the YAML's entities to its own context: rebound, single entities, lists and nested configurations.
    var cache = new Cache();
    var config = new PVConfig
    {
      BatterySoCEntity = new Entity(cache, "sensor.soc"),
      InverterSyncEntity = new Entity(cache, InSync),
      TotalImportEnergyEntities = [new Entity(cache, "sensor.meter"), new Entity(cache, "sensor.solax_import")],
      SchedulableLoads = [new SchedulableLoadConfig { Name = "EV", CurrentLevelEntity = new Entity(cache, "sensor.ev_soc") }],
    };
    var settled = new SettledHaContext(cache);
    int n = EntityRebinder.Rebind(config, settled);
    Assert.True(n >= 5, $"{n} rebound");
    Assert.Same(settled, config.BatterySoCEntity.HaContext);
    Assert.Same(settled, config.InverterSyncEntity!.HaContext);
    Assert.All(config.TotalImportEnergyEntities, e => Assert.Same(settled, e.HaContext));
    Assert.Same(settled, config.SchedulableLoads.Single().CurrentLevelEntity.HaContext);
    Assert.Equal("sensor.meter", config.TotalImportEnergyEntities[0].EntityId);
  }
}
