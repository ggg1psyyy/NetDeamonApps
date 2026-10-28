using NetDeamon.apps;
using NetDeamon.apps.PVControl;
using Xunit;

namespace NetDeamonApps.Tests;

/// <summary>
/// Tests for PriceList.NormalizeToQuarterHourly / WithResolution — the logic that lets the price
/// pipeline accept both hourly-native and quarter-hourly-native EPEX sensors while always
/// exposing quarter-hourly PriceTableEntry data to consumers, optionally averaged per hour to
/// match hourly-billing providers.
/// </summary>
public class PriceListTests
{
  // ── NormalizeToQuarterHourly ─────────────────────────────────────────────

  [Fact]
  public void NormalizeToQuarterHourly_SplitsHourlyEntryIntoFourIdenticalPriceSlots()
  {
    var start = new DateTime(2026, 8, 10, 12, 0, 0);
    var source = new PriceList([new PriceTableEntry(start, start.AddHours(1), 10f)]);

    var result = source.NormalizeToQuarterHourly().ToList();

    Assert.Equal(4, result.Count);
    for (int i = 0; i < 4; i++)
    {
      Assert.Equal(start.AddMinutes(i * 15), result[i].StartTime);
      Assert.Equal(start.AddMinutes((i + 1) * 15), result[i].EndTime);
      Assert.Equal(10f, result[i].Price);
    }
  }

  [Fact]
  public void NormalizeToQuarterHourly_LeavesQuarterHourEntriesUnchanged()
  {
    var start = new DateTime(2026, 8, 10, 12, 0, 0);
    var source = new PriceList([
      new PriceTableEntry(start, start.AddMinutes(15), 10f),
      new PriceTableEntry(start.AddMinutes(15), start.AddMinutes(30), 20f),
      new PriceTableEntry(start.AddMinutes(30), start.AddMinutes(45), 30f),
      new PriceTableEntry(start.AddMinutes(45), start.AddMinutes(60), 40f),
    ]);

    var result = source.NormalizeToQuarterHourly().ToList();

    Assert.Equal(4, result.Count);
    Assert.Equal([10f, 20f, 30f, 40f], result.Select(r => r.Price));
  }

  // ── WithResolution ───────────────────────────────────────────────────────

  [Fact]
  public void WithResolution_Hourly_AveragesFourQuarterSlots()
  {
    var start = new DateTime(2026, 8, 10, 12, 0, 0);
    var source = new PriceList([
      new PriceTableEntry(start, start.AddMinutes(15), 10f),
      new PriceTableEntry(start.AddMinutes(15), start.AddMinutes(30), 20f),
      new PriceTableEntry(start.AddMinutes(30), start.AddMinutes(45), 30f),
      new PriceTableEntry(start.AddMinutes(45), start.AddMinutes(60), 40f),
    ]);

    var result = source.WithResolution(PriceResolution.Hourly).ToList();

    Assert.Equal(4, result.Count);
    Assert.All(result, r => Assert.Equal(25f, r.Price));
    Assert.Equal([start, start.AddMinutes(15), start.AddMinutes(30), start.AddMinutes(45)], result.Select(r => r.StartTime));
  }

  [Fact]
  public void WithResolution_QuarterHourly_LeavesEntriesUnchanged()
  {
    var start = new DateTime(2026, 8, 10, 12, 0, 0);
    var source = new PriceList([
      new PriceTableEntry(start, start.AddMinutes(15), 10f),
      new PriceTableEntry(start.AddMinutes(15), start.AddMinutes(30), 20f),
    ]);

    var result = source.WithResolution(PriceResolution.QuarterHourly).ToList();

    Assert.Equal([10f, 20f], result.Select(r => r.Price));
  }

  [Fact]
  public void WithResolution_Hourly_DoesNotConflateSameHourAcrossDays()
  {
    var day1 = new DateTime(2026, 8, 10, 12, 0, 0);
    var day2 = new DateTime(2026, 8, 11, 12, 0, 0);
    var source = new PriceList([
      new PriceTableEntry(day1, day1.AddMinutes(15), 10f),
      new PriceTableEntry(day1.AddMinutes(15), day1.AddMinutes(30), 20f),
      new PriceTableEntry(day2, day2.AddMinutes(15), 100f),
      new PriceTableEntry(day2.AddMinutes(15), day2.AddMinutes(30), 200f),
    ]);

    var result = source.WithResolution(PriceResolution.Hourly).ToList();

    Assert.Equal(15f, result.Single(r => r.StartTime == day1).Price);
    Assert.Equal(150f, result.Single(r => r.StartTime == day2).Price);
  }

  [Fact]
  public void WithResolution_Hourly_OnNormalizedHourlySource_IsANoOp()
  {
    var start = new DateTime(2026, 8, 10, 12, 0, 0);
    var source = new PriceList([new PriceTableEntry(start, start.AddHours(1), 42f)]);

    var result = source.NormalizeToQuarterHourly().WithResolution(PriceResolution.Hourly).ToList();

    Assert.Equal(4, result.Count);
    Assert.All(result, r => Assert.Equal(42f, r.Price));
  }

  [Fact]
  public void SameAs_SameTimesAndPrices_ElseNot()
  {
    // A price entity changes state every quarter and with every fetch; only prices that really differ replan the loads.
    var t = new DateTime(2026, 10, 29, 0, 0, 0);
    PriceList a = new([new PriceTableEntry(t, t.AddMinutes(15), 0.30f), new PriceTableEntry(t.AddMinutes(15), t.AddMinutes(30), 0.31f)]);
    Assert.True(a.SameAs(new PriceList(a)));
    Assert.False(a.SameAs(new PriceList([new PriceTableEntry(t, t.AddMinutes(15), 0.30f)])));                                      // fewer entries
    Assert.False(a.SameAs(new PriceList([a.First(), new PriceTableEntry(t.AddMinutes(15), t.AddMinutes(30), 0.32f)])));           // a price differs
    Assert.False(a.SameAs(new PriceList([a.First(), new PriceTableEntry(t.AddMinutes(30), t.AddMinutes(45), 0.31f)])));           // a time differs
    Assert.True(new PriceList().SameAs(new PriceList()));
  }

  // ── GetPriceRank / GetPricePercentage regression (hour-bucket bug) ──────

  [Fact]
  public void GetPriceRank_WithQuarterHourEntries_PicksMatchingSlot()
  {
    var start = new DateTime(2026, 8, 10, 12, 0, 0);
    var list = new PriceList([
      new PriceTableEntry(start, start.AddMinutes(15), 40f),           // rank 4
      new PriceTableEntry(start.AddMinutes(15), start.AddMinutes(30), 10f), // rank 1
      new PriceTableEntry(start.AddMinutes(30), start.AddMinutes(45), 30f), // rank 3
      new PriceTableEntry(start.AddMinutes(45), start.AddMinutes(60), 20f), // rank 2
    ]);

    Assert.Equal(4, list.GetPriceRank(start));
    Assert.Equal(1, list.GetPriceRank(start.AddMinutes(15)));
    Assert.Equal(3, list.GetPriceRank(start.AddMinutes(30)));
    Assert.Equal(2, list.GetPriceRank(start.AddMinutes(45)));
  }

  [Fact]
  public void GetPricePercentage_WithQuarterHourEntries_PicksMatchingSlot()
  {
    var start = new DateTime(2026, 8, 10, 12, 0, 0);
    var list = new PriceList([
      new PriceTableEntry(start, start.AddMinutes(15), 0f),
      new PriceTableEntry(start.AddMinutes(15), start.AddMinutes(30), 50f),
      new PriceTableEntry(start.AddMinutes(30), start.AddMinutes(45), 100f),
    ]);

    Assert.Equal(0, list.GetPricePercentage(start));
    Assert.Equal(50, list.GetPricePercentage(start.AddMinutes(15)));
    Assert.Equal(100, list.GetPricePercentage(start.AddMinutes(30)));
  }

  // ── GetBestChargeWindow ──────────────────────────────────────────────────

  /// <summary>
  /// Regression test for a stale-tie-breaking bug: when several consecutive quarter-hour price
  /// entries tie at the same (cheapest) price — the normal case for an hourly-native source
  /// normalized to quarter-hourly, e.g. 13:00-13:45 all at the same price before a genuinely
  /// different price at 14:00 — `OrderBy(Price)` is a stable sort, so ties always resolve to the
  /// EARLIEST entry. `GetBestChargeWindow` used to filter candidates by
  /// `StartTime >= now.Date.AddHours(now.Hour)` (floored to the calendar hour), so once "now"
  /// passed the earliest tied entry's own end time (here, 13:15) but stayed within the same
  /// hour, that already-elapsed 13:00-13:15 entry remained in the candidate set and kept winning
  /// the tie — so the returned window no longer contained "now" at all, and stayed that way for
  /// the rest of the tied price bracket (13:15-13:45), since nothing beats a tied cheapest price
  /// until a genuinely different (pricier) bracket arrives. The caller's `inWindow` check
  /// (`now >= StartTime && now < EndTime`) then fails for the whole rest of that bracket, so the
  /// charge doesn't start until the NEXT, more expensive price bracket, even though several
  /// equally-cheap quarter-hours were still available right now.
  /// </summary>
  [Theory]
  [InlineData(0)]  // now = 13:00 (the earliest tied entry itself) — always worked
  [InlineData(15)] // now = 13:15 — first minute the bug could show
  [InlineData(30)] // now = 13:30
  [InlineData(45)] // now = 13:45 — last minute still in the tied-cheapest bracket
  public void GetBestChargeWindow_TiedCheapestBracket_ReturnsWindowContainingNow(int minutesPastStart)
  {
    var start = new DateTime(2026, 9, 27, 13, 0, 0);
    var list = new PriceList([
      new PriceTableEntry(start,                     start.AddMinutes(15), 0.09396f),
      new PriceTableEntry(start.AddMinutes(15),       start.AddMinutes(30), 0.09396f),
      new PriceTableEntry(start.AddMinutes(30),       start.AddMinutes(45), 0.09396f),
      new PriceTableEntry(start.AddMinutes(45),       start.AddMinutes(60), 0.09396f),
      new PriceTableEntry(start.AddMinutes(60),       start.AddMinutes(75), 0.09612f), // 14:00, genuinely pricier
    ]);
    var now = start.AddMinutes(minutesPastStart);
    var need = new NeedToChargeResult(estimatedSoc: 20, latestChargeTime: start.AddHours(8), needToCharge: true);

    var result = list.GetBestChargeWindow(need, now);

    Assert.True(result.StartTime <= now && result.EndTime > now,
      $"now={now:HH:mm} should fall within the returned window [{result.StartTime:HH:mm}, {result.EndTime:HH:mm}) " +
      $"— a tied-cheapest price bracket must not go stale before it actually ends.");
  }

  /// <summary>
  /// Once "now" reaches a genuinely different (pricier) bracket, GetBestChargeWindow must not
  /// keep reporting an earlier, now-impossible-to-use window either — same property, later slot.
  /// </summary>
  [Fact]
  public void GetBestChargeWindow_PricierBracket_ReturnsWindowContainingNow()
  {
    var start = new DateTime(2026, 9, 27, 13, 0, 0);
    var list = new PriceList([
      new PriceTableEntry(start,               start.AddMinutes(15), 0.09396f),
      new PriceTableEntry(start.AddMinutes(15), start.AddMinutes(30), 0.09396f),
      new PriceTableEntry(start.AddMinutes(30), start.AddMinutes(45), 0.09396f),
      new PriceTableEntry(start.AddMinutes(45), start.AddMinutes(60), 0.09396f),
      new PriceTableEntry(start.AddMinutes(60), start.AddMinutes(75), 0.09612f), // 14:00
    ]);
    var now = start.AddMinutes(60); // 14:00 — first minute of the pricier bracket
    var need = new NeedToChargeResult(estimatedSoc: 20, latestChargeTime: start.AddHours(8), needToCharge: true);

    var result = list.GetBestChargeWindow(need, now);

    Assert.True(result.StartTime <= now && result.EndTime > now,
      $"now={now:HH:mm} should fall within the returned window [{result.StartTime:HH:mm}, {result.EndTime:HH:mm})");
  }

  // ── WithEstimatedPrices (unpublished hours, live 2026-10-22) ──────────────────────────────

  static PriceList Today(DateTime day, Func<int, float> priceOfHour) =>
    new(Enumerable.Range(0, 24).Select(h => new PriceTableEntry(day.AddHours(h), day.AddHours(h + 1), priceOfHour(h))));

  [Fact]
  public void WithEstimatedPrices_TomorrowNotPublished_SameHourOfToday()
  {
    // 12:48: today published, tomorrow not (arrives ~13:00) — GetPrice read 0 there.
    var day = new DateTime(2026, 10, 22);
    var prices = Today(day, h => h == 14 ? 0.32f : 0.42f);
    Assert.Equal(0f, prices.GetPrice(day.AddDays(1).AddHours(14.75)));

    var est = prices.WithEstimatedPrices(day.AddDays(2));
    Assert.Equal(0.32f, est.GetPrice(day.AddDays(1).AddHours(14.75)));
    Assert.Equal(0.42f, est.GetPrice(day.AddDays(1).AddHours(3)));
    Assert.Equal(day.AddDays(2), est.End);
    Assert.Equal(day.AddDays(1), prices.End); // the published list stays as it is
  }

  [Fact]
  public void WithEstimatedPrices_GapLongerThanADay_Chained()
  {
    // Only today's first 12 hours: today 12–23 h from the hour means (none) → overall mean, and
    // tomorrow repeats today.
    var day = new DateTime(2026, 10, 22);
    var prices = new PriceList(Today(day, h => h).Where(p => p.StartTime.Hour < 12));
    var est = prices.WithEstimatedPrices(day.AddDays(2));
    Assert.Equal(6f, est.GetPrice(day.AddDays(1).AddHours(6)));      // = today 06:00
    Assert.Equal(5.5f, est.GetPrice(day.AddHours(18)));              // mean of 0…11
    Assert.Equal(5.5f, est.GetPrice(day.AddDays(1).AddHours(18)));   // = today's estimate
  }

  [Fact]
  public void WithEstimatedPrices_NegativePrices_NotEstimatedNegative()
  {
    var day = new DateTime(2026, 10, 22);
    var est = Today(day, h => h == 13 ? -0.05f : 0.20f).WithEstimatedPrices(day.AddDays(2));
    Assert.Equal(0f, est.GetPrice(day.AddDays(1).AddHours(13)));
    Assert.Equal(-0.05f, est.GetPrice(day.AddHours(13))); // published: kept
  }

  [Fact]
  public void WithEstimatedPrices_AlreadyComplete_Unchanged()
  {
    var day = new DateTime(2026, 10, 22);
    var prices = Today(day, _ => 0.3f);
    Assert.Same(prices, prices.WithEstimatedPrices(day.AddDays(1)));
  }

  // ── Risk premium on estimates ────────────────────────────────────────────────────────────

  [Fact]
  public void WithEstimatedPrices_Premium_OnlyOnEstimates_NotCompounded()
  {
    var day = new DateTime(2026, 10, 22);
    var est = Today(day, _ => 0.30f).WithEstimatedPrices(day.AddDays(3), 0.05f);
    Assert.Equal(0.30f, est.GetPrice(day.AddHours(14)));                       // published: as is
    Assert.Equal(0.315f, est.GetPrice(day.AddDays(1).AddHours(14)), 4);        // estimated: +5 %
    Assert.Equal(0.315f, est.GetPrice(day.AddDays(2).AddHours(14)), 4);        // chained: still +5 %, not +10 %
    Assert.Equal(0.285f, Today(day, _ => 0.30f).WithEstimatedPrices(day.AddDays(2), -0.05f).GetPrice(day.AddDays(1).AddHours(3)), 4); // export
  }

  /// <summary>
  /// Live 2026-10-22 13:15, tomorrow not yet published: the NeedToCharge grid charge moved from
  /// today 14:00 (0.3327 €/kWh, published) to tomorrow 02:00 (estimated 0.3202 from today 02:00,
  /// 3.9 % cheaper). With the 5 % premium the published slot wins.
  /// </summary>
  [Theory]
  [InlineData(0f, "23 02:00")]
  [InlineData(0.05f, "22 14:00")]
  public void EstimatedPricePremium_Live20261022_PublishedCheapSlotWins(float premium, string firstCharge)
  {
    float[] gross = [0.3299f, 0.3219f, 0.3202f, 0.3250f, 0.3325f, 0.3460f, 0.3997f, 0.4264f, 0.4368f, 0.4326f, 0.3900f, 0.3588f,
      0.3480f, 0.3432f, 0.3327f, 0.3528f, 0.3660f, 0.4223f, 0.4411f, 0.4366f, 0.4059f, 0.3801f, 0.3764f, 0.3505f];
    float[] export = [0.1552f, 0.1500f, 0.1488f, 0.1520f, 0.1481f, 0.1571f, 0.1929f, 0.2107f, 0.2176f, 0.2148f, 0.1865f, 0.1656f,
      0.1584f, 0.1552f, 0.1483f, 0.1617f, 0.1705f, 0.2080f, 0.2206f, 0.2176f, 0.1970f, 0.1799f, 0.1863f, 0.1690f];
    var start = new DateTime(2026, 10, 22, 13, 15, 0);
    var day = start.Date;
    PriceList Day(float[] p) => new(Enumerable.Range(0, 24).Select(h => new PriceTableEntry(day.AddHours(h), day.AddHours(h + 1), p[h])));
    var load = new Dictionary<DateTime, int>();
    var pv = new Dictionary<DateTime, int>();
    for (var t = day; t < day.AddDays(3); t = t.AddMinutes(15)) { load[t] = 80; pv[t] = t.Hour is >= 9 and < 16 && t.Date > day ? 150 : 0; }
    var input = new NetDeamon.apps.PVControl.Simulator.SimulationInput
    {
      StartTime = start, StartSocPercent = 30, BatteryCapacityWh = 11_520, AbsoluteMinSocPercent = 12, PreferredMinSocPercent = 20,
      EnforcePreferredSoc = true, MaxChargePowerAmps = 30, InverterEfficiency = 0.95f,
      ImportPrices = Day(gross).WithEstimatedPrices(day.AddDays(2), premium),
      ExportPrices = Day(export).WithEstimatedPrices(day.AddDays(2), -premium),
      PublishedPricesUntil = day.AddDays(1),
      LoadPredictionWh = load, PVPredictionWh = pv, EnableCheapForceCharge = false, OpportunisticDischarge = false,
      ForceChargeMaxPrice = 0.25f, ForceChargeTargetSocPercent = 100, CurrentMode = new InverterState(InverterModes.normal),
    };
    var first = NetDeamon.apps.PVControl.Simulator.EnergySimulator.Simulate(input).First(s => s.State.Mode == InverterModes.force_charge);
    Assert.Equal(firstCharge, first.Time.ToString("dd HH:mm"));
  }

  // ── Price source selection (PriceManager.FurthestSource) ──────────────────

  [Fact]
  public void FurthestSource_Live20261022_FirstToPublishTomorrowWins()
  {
    // 14:11: Awattar (hourly) and Energy-Charts (15 min) already had tomorrow, smartENERGY not.
    var today = new DateTime(2026, 10, 23, 0, 0, 0);
    var tomorrow = today.AddDays(1);
    // [smartENERGY, Awattar, Energy-Charts]
    Assert.Equal(1, PriceManager.FurthestSource([today, tomorrow, tomorrow]));   // tie → earlier in the list
    Assert.Equal(2, PriceManager.FurthestSource([today, today, tomorrow]));      // only Energy-Charts has tomorrow
    Assert.Equal(0, PriceManager.FurthestSource([today, null, today]));          // no data counts as nothing, tie → first
    Assert.Null(PriceManager.FurthestSource([null, null]));
    Assert.Null(PriceManager.FurthestSource([]));
  }

  [Fact]
  public void WithResolution_Hourly_QuarterSourceEqualsHourlySource()
  {
    // Same EPEX hour from two sources: hourly (Awattar) and four quarters (Energy-Charts) —
    // with Hourly billing both give the same price in every slot, so switching sources changes nothing.
    var h = new DateTime(2026, 10, 23, 4, 0, 0);
    var hourly = new PriceList([new PriceTableEntry(h, h.AddHours(1), 0.26598f)]);
    var quarters = new PriceList([
      new PriceTableEntry(h, h.AddMinutes(15), 0.25000f), new PriceTableEntry(h.AddMinutes(15), h.AddMinutes(30), 0.26000f),
      new PriceTableEntry(h.AddMinutes(30), h.AddMinutes(45), 0.27000f), new PriceTableEntry(h.AddMinutes(45), h.AddHours(1), 0.28392f)]);
    var a = hourly.NormalizeToQuarterHourly().WithResolution(PriceResolution.Hourly).Select(p => p.Price).ToList();
    var b = quarters.NormalizeToQuarterHourly().WithResolution(PriceResolution.Hourly).Select(p => p.Price).ToList();
    Assert.Equal(4, b.Count);
    for (int i = 0; i < 4; i++) Assert.Equal(a[i], b[i], 5);
  }
}
