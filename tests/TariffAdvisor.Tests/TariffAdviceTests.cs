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
  static readonly AdviceSettings Settings = new(0.30f, 30);

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
    }, ["Fixed"]),
  };

  static Advice Decide(ForecastSnapshot s, AdviceSettings? settings = null, Func<SimulationInput, DpOptions?, DpPlan>? plan = null) =>
    TariffAdvice.Decide((Quarter, s), Quarter.AddMinutes(1), settings ?? Settings, plan);

  [Fact]
  public void AFixedPriceFarUnderTheMarket_Fixed()
  {
    var a = Decide(With(0.10f));
    Assert.Equal("Fixed", a.State);
    Assert.Equal("decided", a.Status);
    Assert.Equal(DateOnly.FromDateTime(Tomorrow), a.ForDay);
    Assert.Equal("Market", a.CurrentTariff);
    Assert.True(a.SavingEur > 0.30f, a.Reason);
    Assert.Equal(a.CostEur!["Market"] - a.CostEur["Fixed"], a.SavingEur!.Value, 4);
  }

  [Fact]
  public void AFixedPriceOverTheMarket_Market()
  {
    var a = Decide(With(0.80f));
    Assert.Equal("Market", a.State);
    Assert.True(a.SavingEur < 0, a.Reason);
  }

  [Fact]
  public void TodayFixed_TheMarketCheaper_Market()
  {
    var a = Decide(With(0.80f, active: "Fixed"));
    Assert.Equal("Market", a.State);
    Assert.Equal("Fixed", a.CurrentTariff);
  }

  [Fact]
  public void ASavingUnderTheMinimum_TodaysTariff()
  {
    var a = Decide(With(0.10f), Settings with { MinSavingEur = 1000 });
    Assert.Equal("Market", a.State);
    Assert.True(a.SavingEur > 0);
    Assert.Contains("only", a.Reason);
  }

  [Fact]
  public void TheSamePricesUnderBothTariffs_NoSaving()
  {
    var a = Decide(With(0) with { ImportTariffs = new TariffPrices("Market", Tomorrow, new() { ["Market"] = Live.ImportPrices, ["Fixed"] = Live.ImportPrices }) });
    Assert.Equal("Market", a.State);
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
    // The fixed tariff's rules where it's in force: only the Fixed run, from the switch on.
    Assert.Single(seen, i => i.FixedImportAt(Tomorrow) && !i.FixedImportAt(Tomorrow.AddMinutes(-15)));
    Assert.Single(seen, i => i.FixedImportPeriods.Count == 0);
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

  [Theory]
  [InlineData("Market")]
  [InlineData("Fixed")]
  public void BeforeTheDaysPricesArePublished_TodaysTariff(string today)
  {
    var a = Decide(With(0.10f, active: today, publishedUntil: Tomorrow));
    Assert.Equal(today, a.State);
    Assert.Equal("pending", a.Status);
    Assert.Null(a.SavingEur);
  }

  [Fact]
  public void NoSnapshot_AStaleOne_OneWithoutTariffs_OrOnlyOneTariff_Unavailable()
  {
    Assert.Equal("Unavailable", TariffAdvice.Decide(null, Quarter, Settings).State);
    Assert.Equal("Unavailable", TariffAdvice.Decide((Quarter, With(0.10f)), Quarter.AddMinutes(31), Settings).State);
    Assert.Equal("Unavailable", Decide(Live with { ImportTariffs = null }).State);
    var onlyMarket = Decide(With(0) with { ImportTariffs = new TariffPrices("Market", Tomorrow, new() { ["Market"] = Live.ImportPrices }) });
    Assert.Equal("Unavailable", onlyMarket.State);
    Assert.Contains("Market", onlyMarket.Reason);
  }

  [Fact]
  public void AFailure_Unavailable_WithTheError()
  {
    var a = TariffAdvice.Failed(new InvalidOperationException("database is locked"));
    Assert.Equal("Error", a.State);
    Assert.Contains("database is locked", a.Reason);
  }

  [Theory]
  [InlineData(12, 59, false)]
  [InlineData(13, 0, true)]
  [InlineData(19, 59, true)]
  [InlineData(20, 0, false)]
  [InlineData(0, 0, false)]
  public void JudgedFrom13Until20(int h, int m, bool due) =>
    Assert.Equal(due, TariffAdvice.Due(Quarter.Date.AddHours(h).AddMinutes(m)));

  static readonly DateTime Evening = Quarter.Date.AddHours(19.75);
  static readonly Advice Decided = new("Fixed", "decided", "Fixed saves 0.50 €", DateOnly.FromDateTime(Tomorrow));

  [Fact]
  public void ADecisionStays_AgainstALaterFailureOrStaleSnapshot_ButNotAgainstANewDecision()
  {
    Assert.Same(Decided, TariffAdvice.Shown(Decided, TariffAdvice.Failed(new Exception("x")), Evening));
    Assert.Same(Decided, TariffAdvice.Shown(Decided, new Advice("Unavailable", "unavailable", "stale"), Evening));
    var market = Decided with { State = "Market" };
    Assert.Same(market, TariffAdvice.Shown(Decided, market, Evening));
  }

  [Fact]
  public void WithoutADecisionForAComingDay_TheNewResultShows()
  {
    var error = TariffAdvice.Failed(new Exception("x"));
    Assert.Same(error, TariffAdvice.Shown(null, error, Evening));
    var pending = new Advice("Market", "pending", "not published", DateOnly.FromDateTime(Tomorrow.AddDays(1)));
    Assert.Same(pending, TariffAdvice.Shown(Decided, pending, Tomorrow.AddHours(13)));   // yesterday's decision was for today
  }

  [Fact]
  public void Config_TheYamlsValues()
  {
    foreach (var file in ConfigKeys.Files("TariffAdvisor"))
    {
      var yaml = File.ReadAllText(file);
      Assert.Contains("MinSavingEur: 0.30", yaml);
      Assert.DoesNotContain("StartTime", yaml);   // the window is in code (TariffAdvice.WindowStart/WindowEnd)
      Assert.Empty(ConfigKeys.Unmatched(yaml, typeof(NetDeamon.apps.TariffAdvisor.TariffAdvisorConfig)));
    }
  }
}
