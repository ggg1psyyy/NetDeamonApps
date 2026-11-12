using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using NetDeamon.apps.PVControl;

namespace NetDeamonApps.Tests;

/// <summary>
/// Initialises the static PVControlCommon logger (private setter) with a no-op implementation
/// so that PredictionContainer and other PVControl code can log without throwing. The helpers
/// (MakeWindow) are the planner tests' part (../Planning.Tests/TestBase.cs, linked).
/// </summary>
public abstract partial class TestBase
{
  protected TestBase()
  {
    var setter = typeof(PVControlCommon)
      .GetProperty("PVCC_Logger", BindingFlags.Public | BindingFlags.Static)!
      .GetSetMethod(nonPublic: true)!;
    setter.Invoke(null, [NullLogger<NetDeamon.apps.PVControl.PVControl>.Instance]);
  }
}
