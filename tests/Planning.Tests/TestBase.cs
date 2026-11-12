namespace NetDeamonApps.Tests;

/// <summary>Helpers of the planner tests. PVControl.Tests links this file and adds its part (the PVControl logger).</summary>
public abstract partial class TestBase
{
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
