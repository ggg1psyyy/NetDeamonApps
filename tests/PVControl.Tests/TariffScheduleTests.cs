using NetDeamon.apps;
using NetDeamon.apps.PVControl;
using Xunit;

namespace NetDeamonApps.Tests;

/// <summary>Tariff switching (select.pv_control_import_tariff / export_tariff): a choice applies from the switch time,
/// each quarter is priced by the tariff in force then. The selects themselves (MQTT, restore from HA) need HA and
/// aren't unit tested; their logic is <see cref="TariffSchedule{T}"/>.</summary>
public class TariffScheduleTests
{
  static readonly DateTime Day = new(2026, 11, 17);
  static readonly TimeSpan Midnight = TimeSpan.Zero;

  [Fact]
  public void NextSwitch_TodayIfAhead_ElseTomorrow()
  {
    Assert.Equal(Day.AddDays(1), TariffSchedule<ImportTariff>.NextSwitch(Day.AddHours(20), Midnight));
    Assert.Equal(Day.AddDays(1), TariffSchedule<ImportTariff>.NextSwitch(Day, Midnight));            // exactly at it: the next one
    Assert.Equal(Day.AddHours(6), TariffSchedule<ImportTariff>.NextSwitch(Day.AddHours(5), TimeSpan.FromHours(6)));
  }

  [Fact]
  public void Choose_AppliesFromTheSwitch_QuartersBeforeKeepTheActiveTariff()
  {
    var s = new TariffSchedule<ImportTariff>(ImportTariff.Fixed);
    Assert.True(s.Choose(ImportTariff.Market, Day.AddHours(20), Midnight));
    Assert.Equal(ImportTariff.Fixed, s.Active);
    Assert.Equal(ImportTariff.Market, s.Chosen);
    Assert.Equal(Day.AddDays(1), s.EffectiveFrom);
    Assert.Equal(ImportTariff.Fixed, s.At(Day.AddHours(23.75)));
    Assert.Equal(ImportTariff.Market, s.At(Day.AddDays(1)));
  }

  [Fact]
  public void Choose_TheActiveOneAgain_DropsThePendingChange()
  {
    var s = new TariffSchedule<ImportTariff>(ImportTariff.Fixed);
    s.Choose(ImportTariff.Market, Day.AddHours(20), Midnight);
    Assert.True(s.Choose(ImportTariff.Fixed, Day.AddHours(21), Midnight));
    Assert.Null(s.EffectiveFrom);
    Assert.Equal(ImportTariff.Fixed, s.At(Day.AddDays(1)));
    Assert.False(s.Choose(ImportTariff.Fixed, Day.AddHours(22), Midnight));   // no change
  }

  [Fact]
  public void Roll_OnlyFromTheSwitchTime()
  {
    var s = new TariffSchedule<ExportTariff>(ExportTariff.SameAsImport);
    s.Choose(ExportTariff.Fixed, Day.AddHours(20), Midnight);
    Assert.False(s.Roll(Day.AddHours(23.99)));
    Assert.True(s.Roll(Day.AddDays(1).AddSeconds(5)));
    Assert.Equal(ExportTariff.Fixed, s.Active);
    Assert.Null(s.EffectiveFrom);
    Assert.False(s.Roll(Day.AddDays(1).AddMinutes(1)));
  }

  [Fact]
  public void Restore_FirstStart_TheYamlValue()
  {
    var s = TariffSchedule<ImportTariff>.Restore(ImportTariff.Market, null, null, null, Day.AddHours(20), Midnight);
    Assert.Equal(ImportTariff.Market, s.Active);
    Assert.Null(s.EffectiveFrom);
    // An unknown state (renamed option) falls back to the YAML's value too.
    Assert.Equal(ImportTariff.Market, TariffSchedule<ImportTariff>.Restore(ImportTariff.Market, "bogus", null, null, Day, Midnight).Active);
  }

  [Fact]
  public void Restore_APendingChoice_KeepsItsTime_OrRollsIfItPassedWhileDown()
  {
    string tomorrow = Day.AddDays(1).ToString("o");
    // The state is parsed ignoring case (the command path lowercases it).
    var pending = TariffSchedule<ImportTariff>.Restore(ImportTariff.Market, "market", "Fixed", tomorrow, Day.AddHours(21), Midnight);
    Assert.Equal(ImportTariff.Fixed, pending.Active);
    Assert.Equal(ImportTariff.Market, pending.Chosen);
    Assert.Equal(Day.AddDays(1), pending.EffectiveFrom);

    var passed = TariffSchedule<ImportTariff>.Restore(ImportTariff.Market, "Market", "Fixed", tomorrow, Day.AddDays(1).AddHours(1), Midnight);
    Assert.Equal(ImportTariff.Market, passed.Active);
    Assert.Null(passed.EffectiveFrom);

    // Lost effective_from: from the next switch, as if chosen now.
    var lost = TariffSchedule<ImportTariff>.Restore(ImportTariff.Market, "Market", "Fixed", null, Day.AddHours(21), Midnight);
    Assert.Equal(Day.AddDays(1), lost.EffectiveFrom);
  }

  [Fact]
  public void Parse_IgnoresCase_RejectsUnknownAndNumbers()
  {
    Assert.Equal(ExportTariff.SameAsImport, TariffSchedule<ExportTariff>.Parse("sameasimport"));
    Assert.Null(TariffSchedule<ExportTariff>.Parse("Entities"));
    Assert.Null(TariffSchedule<ExportTariff>.Parse("7"));
    Assert.Null(TariffSchedule<ExportTariff>.Parse(null));
  }

  static PriceList Quarters(DateTime from, DateTime until, Func<DateTime, float> price)
  {
    var l = new List<PriceTableEntry>();
    for (var t = from; t < until; t = t.AddMinutes(15)) l.Add(new PriceTableEntry(t, t.AddMinutes(15), price(t)));
    return new PriceList(l);
  }

  [Fact]
  public void Prices_NothingPending_TheActiveTariffsListUnchanged()
  {
    var market = Quarters(Day, Day.AddDays(1), t => 0.20f + t.Hour / 100f);
    var s = new TariffSchedule<ImportTariff>(ImportTariff.Market);
    var plan = s.Prices(t => market.WithEstimatedPrices(Day.AddDays(2), 0.05f));
    var before = market.WithEstimatedPrices(Day.AddDays(2), 0.05f);   // what the plan got before tariffs could switch
    Assert.Equal(before.Select(p => (p.StartTime, p.Price)), plan.Select(p => (p.StartTime, p.Price)));
  }

  [Fact]
  public void Prices_FixedTodayMarketTomorrow_TomorrowEstimatedFromTheMarketNotFromTheFixedPrice()
  {
    // Published until midnight (tomorrow's prices aren't out yet): tomorrow's quarters are estimates.
    var market = Quarters(Day, Day.AddDays(1), t => 0.20f + t.Hour / 100f);
    var fixedPrice = Quarters(Day, Day.AddDays(1), _ => 0.30f);
    var s = new TariffSchedule<ImportTariff>(ImportTariff.Fixed);
    s.Choose(ImportTariff.Market, Day.AddHours(20), Midnight);

    var plan = s.Prices(t => (t == ImportTariff.Fixed ? fixedPrice : market).WithEstimatedPrices(Day.AddDays(2), t == ImportTariff.Fixed ? 0 : 0.05f));

    Assert.Equal(0.30f, plan.GetPrice(Day.AddHours(23.75)), 5);                    // today: still the fixed price
    Assert.Equal((0.20f + 0.10f) * 1.05f, plan.GetPrice(Day.AddDays(1).AddHours(10)), 5);   // tomorrow 10:00: today's market 10:00 + premium
    Assert.Equal(2 * 96, plan.Count());                                              // every quarter once
  }

  [Fact]
  public void Prices_MarketTodayFixedTomorrow_TomorrowFixedWithoutPremium()
  {
    var market = Quarters(Day, Day.AddDays(1), t => 0.20f + t.Hour / 100f);
    var fixedPrice = Quarters(Day, Day.AddDays(1), _ => 0.30f);
    var s = new TariffSchedule<ImportTariff>(ImportTariff.Market);
    s.Choose(ImportTariff.Fixed, Day.AddHours(20), Midnight);

    var plan = s.Prices(t => (t == ImportTariff.Fixed ? fixedPrice : market).WithEstimatedPrices(Day.AddDays(2), t == ImportTariff.Fixed ? 0 : 0.05f));

    Assert.Equal(0.20f + 0.23f, plan.GetPrice(Day.AddHours(23.75)), 5);
    Assert.Equal(0.30f, plan.GetPrice(Day.AddDays(1).AddHours(10)), 5);
  }

  [Fact]
  public void Sources_AChoiceWithoutItsPriceSetting_IsRefused()
  {
    Assert.NotNull(TariffSources.ImportMissing(ImportTariff.Fixed, hasFixedEntity: false, fixedValue: 0));
    Assert.Null(TariffSources.ImportMissing(ImportTariff.Fixed, hasFixedEntity: true, fixedValue: 0));
    Assert.Null(TariffSources.ImportMissing(ImportTariff.Fixed, hasFixedEntity: false, fixedValue: 0.14125f));
    Assert.Null(TariffSources.ImportMissing(ImportTariff.Market, false, 0));

    Assert.NotNull(TariffSources.ExportMissing(ExportTariff.Market, false, 0, marketEntities: 0));
    Assert.Null(TariffSources.ExportMissing(ExportTariff.Market, false, 0, marketEntities: 1));
    Assert.NotNull(TariffSources.ExportMissing(ExportTariff.Fixed, false, 0, 0));
    Assert.Null(TariffSources.ExportMissing(ExportTariff.Fixed, true, 0, 0));
    Assert.Null(TariffSources.ExportMissing(ExportTariff.SameAsImport, false, 0, 0));
  }

  [Theory]
  [InlineData("00:00", 0)]
  [InlineData("06:30", 6.5)]
  [InlineData("25:00", 0)]
  [InlineData("later", 0)]
  public void SwitchTime_ParsedOrMidnight(string value, double hours) =>
    Assert.Equal(TimeSpan.FromHours(hours), new PVConfig { TariffSwitchTime = value }.TariffSwitchTimeOfDay);

  [Fact]
  public void Config_TariffsAsTodayAndNoOldKeys()
  {
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir != null && !File.Exists(Path.Combine(dir.FullName, "src", "PVControl", "PVControl.yaml")))
      dir = dir.Parent;
    Assert.NotNull(dir);
    string[] lines = File.ReadAllLines(Path.Combine(dir.FullName, "src", "PVControl", "PVControl.yaml"))
      .Select(l => l.Trim()).Where(l => !l.StartsWith('#')).ToArray();
    Assert.Contains("ImportTariff: Market", lines);
    Assert.Contains("ExportTariff: SameAsImport", lines);
    Assert.Contains("TariffSwitchTime: \"00:00\"", lines);
    foreach (var old in new[] { "ImportPriceFixed:", "ExportPriceSameAsImport:", "ExportPriceFixed:" })
      Assert.DoesNotContain(lines, l => l.StartsWith(old));
  }
}
