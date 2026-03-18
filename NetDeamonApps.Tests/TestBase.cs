using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using NetDeamon.apps.PVControl;

namespace NetDeamonApps.Tests;

/// <summary>
/// Initialises the static PVControlCommon logger (private setter) with a no-op implementation
/// so that PredictionContainer and other PVControl code can log without throwing.
/// </summary>
public abstract class TestBase
{
  protected TestBase()
  {
    var setter = typeof(PVControlCommon)
      .GetProperty("PVCC_Logger", BindingFlags.Public | BindingFlags.Static)!
      .GetSetMethod(nonPublic: true)!;
    setter.Invoke(null, [NullLogger<NetDeamon.apps.PVControl.PVControl>.Instance]);
  }

  /// <summary>
  /// Builds a full 192-slot dictionary (today 00:00 → day-after-tomorrow 00:00, exclusive)
  /// with every 15-minute key present and the given constant value.
  /// </summary>
  protected static Dictionary<DateTime, int> MakeWindow(DateTime startDate, int value = 0)
  {
    var d = new Dictionary<DateTime, int>();
    for (var t = startDate; t < startDate.AddDays(2); t = t.AddMinutes(15))
      d[t] = value;
    return d;
  }
}
