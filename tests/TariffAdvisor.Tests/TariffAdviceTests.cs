using NetDeamon.apps;
using NetDeamon.apps.PVControl.Optimizer;
using NetDeamon.apps.PVControl.Simulator;
using NetDeamon.apps.TariffAdvisor;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace NetDeamonApps.Tests;

/// <summary>
/// The tariff advice (<see cref="TariffAdvice.Decide"/>) on a live snapshot (2026-11-02 06:00: a sunny day, the night
/// charge done) with the real DP, and with fixed plans for the cost valuation. The HA side (<see cref="TariffAdvisor"/>:
/// the timer, reading the DB, publishing) needs HA and is checked live (sensor.tariff_advisor_advice).
/// </summary>
public class TariffAdviceTests
{
  static readonly AdviceSettings Settings = new(0.30f, TimeSpan.FromHours(13), 30);

  static readonly ForecastSnapshot Live = ForecastSnapshot.FromBytes(File.ReadAllBytes(
    Path.Combine(AppContext.BaseDirectory, "Data", "forecast_snapshot_2026-11-02_0600.json.gz")));

  static readonly DateTime Quarter = new(2026, 11, 2, 6, 0, 0);
  static readonly DateTime Tomorrow = Quarter.Date.AddDays(1);

  /// <summary>The live snapshot with tomorrow published and both tariffs: the market as recorded, a flat fixed price.</summary>
  static ForecastSnapshot With(float fixedPrice, string active = "Market", DateTime? publishedUntil = null) => Live with
  {
    PublishedPricesUntil = publishedUntil ?? Tomorrow.AddDays(1),
    ImportTariffs = new TariffPrices(active, Tomorrow, new Dictionary<string, List<PricePoint>>
    {
      ["Market"] = Live.ImportPrices,
      ["Fixed"] = Live.ImportPrices.Select(p => p with { Price = fixedPrice }).ToList(),
    }),
  };

  static Advice Decide(ForecastSnapshot s, AdviceSettings? settings = null, Func<SimulationInput, DpOptions?, DpPlan>? plan = null) =>
    TariffAdvice.Decide((Quarter, s), Quarter.AddMinutes(1), settings ?? Settings, plan);

  [Fact]
  public void AFixedPriceFarUnderTheMarket_SwitchToFixed()
  {
    var a = Decide(With(0.10f));
    Assert.Equal("switch_to_fixed", a.State);
    Assert.Equal(DateOnly.FromDateTime(Tomorrow), a.ForDay);
    Assert.Equal("Market", a.CurrentTariff);
    Assert.True(a.SavingEur > 0.30f, a.Reason);
    Assert.Equal(a.CostEur!["Market"] - a.CostEur["Fixed"], a.SavingEur!.Value, 4);
  }

  [Fact]
  public void AFixedPriceOverTheMarket_Stay()
  {
    var a = Decide(With(0.80f));
    Assert.Equal("stay", a.State);
    Assert.True(a.SavingEur < 0, a.Reason);
  }

  [Fact]
  public void TodayFixed_TheMarketCheaper_SwitchToMarket()
  {
    var a = Decide(With(0.80f, active: "Fixed"));
    Assert.Equal("switch_to_market", a.State);
    Assert.Equal("Fixed", a.CurrentTariff);
  }

  [Fact]
  public void ASavingUnderTheMinimum_Stay()
  {
    var a = Decide(With(0.10f), Settings with { MinSavingEur = 1000 });
    Assert.Equal("stay", a.State);
    Assert.True(a.SavingEur > 0);
    Assert.Contains("only", a.Reason);
  }

  [Fact]
  public void TheSamePricesUnderBothTariffs_NoSaving()
  {
    var a = Decide(With(0) with { ImportTariffs = new TariffPrices("Market", Tomorrow, new() { ["Market"] = Live.ImportPrices, ["Fixed"] = Live.ImportPrices }) });
    Assert.Equal("stay", a.State);
    Assert.Equal(0, a.SavingEur!.Value, 4);
  }

  [Fact]
  public void TodaysQuartersKeepTodaysTariff_InBothRuns()
  {
    var seen = new List<SimulationInput>();
    Decide(With(0.10f), plan: (input, _) => { seen.Add(input); return Plan(1, 0); });
    Assert.Equal(2, seen.Count);
    var today = seen.Select(i => i.ImportPrices.Where(p => p.StartTime < Tomorrow).Select(p => p.Price).ToList()).ToList();
    Assert.Equal(today[0], today[1]);
    Assert.Contains(seen, i => i.ImportPrices.Where(p => p.StartTime >= Tomorrow).All(p => p.Price == 0.10f));
    Assert.All(seen, i => Assert.Equal(Live.ExportPrices.Count, i.ExportPrices.Count()));   // export unchanged
  }

  static DpPlan Plan(float gridCost, int endWh) => new([], new PlanCost(gridCost, 5, 5, 3), 0, EndEnergyWh: endWh);

  [Fact]
  public void TheEnergyLeft_ValuedAtTodaysRefillPrice_ForBothPlans_PenaltiesLeftOut()
  {
    // Fixed: dearer on the grid but ends with 4 kWh more; the planner's own costs (penalties, its end value) don't count.
    var s = With(0.10f);
    var a = Decide(s, plan: (input, _) => input.ImportPrices.Last().Price == 0.10f ? Plan(2.0f, 9000) : Plan(1.5f, 5000));
    var today = s.ToInput().WithPrices(s.ImportTariffs!.From("Market")!, s.ToInput().ExportPrices);
    float refill = PlanEvaluation.RefillPrice(today);
    Assert.Equal(1.5f - PlanEvaluation.EndValue(today, 5000, refill), a.CostEur!["Market"], 4);
    Assert.Equal(2.0f - PlanEvaluation.EndValue(today, 9000, refill), a.CostEur["Fixed"], 4);
  }

  [Fact]
  public void BeforeTheDaysPricesArePublished_Pending()
  {
    var a = Decide(With(0.10f, publishedUntil: Tomorrow));
    Assert.Equal("pending", a.State);
    Assert.Null(a.SavingEur);
  }

  [Fact]
  public void NoSnapshot_AStaleOne_OneWithoutTariffs_OrOnlyOneTariff_Unavailable()
  {
    Assert.Equal("unavailable", TariffAdvice.Decide(null, Quarter, Settings).State);
    Assert.Equal("unavailable", TariffAdvice.Decide((Quarter, With(0.10f)), Quarter.AddMinutes(31), Settings).State);
    Assert.Equal("unavailable", Decide(Live with { ImportTariffs = null }).State);
    var onlyMarket = Decide(With(0) with { ImportTariffs = new TariffPrices("Market", Tomorrow, new() { ["Market"] = Live.ImportPrices }) });
    Assert.Equal("unavailable", onlyMarket.State);
    Assert.Contains("Market", onlyMarket.Reason);
  }

  [Theory]
  [InlineData(12, 59, false)]
  [InlineData(13, 0, true)]
  [InlineData(23, 59, true)]
  [InlineData(0, 0, false)]
  public void JudgedFromTheStartTimeUntilMidnight(int h, int m, bool due) =>
    Assert.Equal(due, TariffAdvice.Due(Quarter.Date.AddHours(h).AddMinutes(m), Settings));

  [Fact]
  public void Config_TheYamlsValues()
  {
    var yaml = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TariffAdvisor.yaml"));
    Assert.Contains("MinSavingEur: 0.30", yaml);
    Assert.Contains("StartTime: \"13:00\"", yaml);
    Assert.Equal(TimeSpan.FromHours(13), new TariffAdvisorConfig { StartTime = "later" }.Settings.StartTime);
    Assert.Equal("switch_to_fixed", TariffAdvice.SwitchTo("Fixed"));
  }
}
