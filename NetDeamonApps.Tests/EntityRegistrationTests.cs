using NetDeamon.apps.PVControl;
using Xunit;

namespace NetDeamonApps.Tests;

/// <summary>
/// When PVControl publishes an entity's MQTT discovery config (<c>PVControlCommon.RegisterSensor</c>; the
/// publishing itself needs HA). Option lists follow the code: a mode retired from <c>InverterModes</c>
/// (house_only, 2026-09) stayed selectable in HA and the override fell back to automatic.
/// </summary>
public class EntityRegistrationTests
{
  [Fact]
  public void NewEntity_PublishedWithItsDefault()
    => Assert.Equal((true, true), PVControlCommon.Registration(exists: false, reRegister: false, republish: false, hasOptions: false));

  [Fact]
  public void ExistingEntity_LeftAlone()
    => Assert.Equal((false, false), PVControlCommon.Registration(exists: true, reRegister: false, republish: false, hasOptions: false));

  [Fact]
  public void ExistingSelect_OptionsRepublished_StateKept()
    // The override select keeps the user's choice across a restart.
    => Assert.Equal((true, false), PVControlCommon.Registration(exists: true, reRegister: false, republish: false, hasOptions: true));

  [Fact]
  public void Republish_KeepsTheState_ReRegister_SetsTheDefault()
  {
    Assert.Equal((true, false), PVControlCommon.Registration(exists: true, reRegister: false, republish: true, hasOptions: false));
    Assert.Equal((true, true), PVControlCommon.Registration(exists: true, reRegister: true, republish: false, hasOptions: false));
  }
}
