using System.Text.Json;
using NetDeamon.apps.PVControl;
using NetDeamon.apps.PVControl.LoadModel;
using Xunit;

namespace NetDeamonApps.Tests;

/// <summary>
/// Hourly weather forecast for the load model: parsing a real weather.get_forecasts response of
/// weather.pirateweather (Data/pirateweather_hourly_2026-10-22.json, 48 of its 168 hours) and the
/// hour lookup. Fetching it (CallServiceWithResponseAsync) needs a live HA.
/// </summary>
public class WeatherForecastTests : TestBase
{
  static JsonElement Fixture() =>
    JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Data", "pirateweather_hourly_2026-10-22.json"))).RootElement;

  [Fact]
  public void Parse_RealResponse_HoursInLocalTime()
  {
    var hours = WeatherForecast.Parse(Fixture(), "weather.pirateweather");
    Assert.Equal(48, hours.Count);
    // 2026-10-22T09:00:00+00:00 = 11:00 CEST: 9.9 °C, 99 % clouds, wind 7.42 km/h.
    Assert.Equal(new DateTime(2026, 10, 22, 11, 0, 0), hours[0].Local);
    Assert.Equal(9.9, hours[0].Temperature);
    Assert.Equal(99, hours[0].CloudCoverage);
    Assert.Equal(7.42, hours[0].WindSpeed);
  }

  [Fact]
  public void Parse_WrappedInResponse_SameResult()
  {
    var wrapped = JsonDocument.Parse($"{{\"response\": {Fixture().GetRawText()}}}").RootElement;
    Assert.Equal(48, WeatherForecast.Parse(wrapped, "weather.pirateweather").Count);
  }

  [Fact]
  public void Parse_OtherEntityOrNoForecast_Empty()
  {
    Assert.Empty(WeatherForecast.Parse(Fixture(), "weather.zuhause"));
    Assert.Empty(WeatherForecast.Parse(JsonDocument.Parse("{}").RootElement, "weather.pirateweather"));
  }

  [Fact]
  public void TemperatureAt_HourContainingTheTime_OrNull()
  {
    var hours = WeatherForecast.Parse(Fixture(), "weather.pirateweather");
    Assert.Equal(10.6, WeatherForecast.TemperatureAt(hours, new DateTime(2026, 10, 22, 12, 45, 0)));
    Assert.Null(WeatherForecast.TemperatureAt(hours, new DateTime(2026, 10, 30, 12, 0, 0)));
  }

  [Fact]
  public void Health_NoOrOldForecast_Degraded()
  {
    var now = new DateTime(2026, 10, 22, 14, 0, 0);
    Assert.Empty(HealthChecks.WeatherForecast(false, null, now, WeatherForecast.MaxAge));
    Assert.Single(HealthChecks.WeatherForecast(true, null, now, WeatherForecast.MaxAge));
    Assert.Empty(HealthChecks.WeatherForecast(true, now.AddHours(-3), now, WeatherForecast.MaxAge));
    Assert.Single(HealthChecks.WeatherForecast(true, now.AddHours(-3).AddMinutes(-1), now, WeatherForecast.MaxAge));
  }
}
