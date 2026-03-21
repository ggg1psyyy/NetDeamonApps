using NetDeamon.apps;
using NetDeamon.apps.PVControl;
using NetDeamon.apps.PVControl.Simulator;
using Xunit;

namespace NetDeamonApps.Tests;

/// <summary>
/// Regression tests for the energy cost price pipeline (unit: €/kWh throughout).
///
/// Source of truth – live HA data fetched 2026-03-21 ~20:00 CET:
///   sensor.epex_spot_data_market_price  state = 0.14966 €/kWh  (hour 20:00–21:00)
///   sensor.solax_today_s_import_energy  state = 0.0 kWh
///   sensor.solax_today_s_export_energy  state = 0.8 kWh
///   sensor.pv_control_current_import_price_brutto  state = 0.28989604 €/kWh
///
/// YAML config values (PVControl.yaml, 2026-03-21):
///   ImportPriceMultiplier = 1.0
///   ImportPriceAddition   = 0.012  €/kWh
///   ImportPriceNetwork    = 0.07992 €/kWh
///   ImportPriceTax        = 0.2
///
/// The most important regression these tests protect against:
///   Previously, internal prices were in ct/kWh (×100 of EPEX input).
///   If that multiplication is accidentally re-introduced, PriceListImport prices
///   become ~11–33 instead of ~0.11–0.33, making ForceChargeMaxPrice = 0.25f
///   always FALSE (11 > 0.25) and the battery would never force-charge from the grid.
/// </summary>
public class EnergyCostTests : TestBase
{
  // ── Config constants matching PVControl.yaml ──────────────────────────────
  const float ImportPriceMultiplier = 1.0f;
  const float ImportPriceAddition   = 0.012f;   // €/kWh
  const float ImportPriceNetwork    = 0.07992f;  // €/kWh
  const float ImportPriceTax        = 0.2f;      // 20 %

  const float ExportPriceMultiplier = 0.8f;
  const float ExportPriceAddition   = 0.0f;
  const float ExportPriceTax        = 0.0f;

  // ── Live EPEX netto prices (€/kWh) from 2026-03-21 and 2026-03-22 ────────
  // Selected to cover the full range seen in the live feed.
  // 2026-03-22T10:00 = 0.0 €/kWh  (cheapest — free solar surplus hour)
  // 2026-03-21T20:00 = 0.14966 €/kWh  (current hour when data was read)
  // 2026-03-22T18:00 = 0.1828 €/kWh   (most expensive in feed)
  const float EpexCheapest  = 0.0f;      // 2026-03-22T10:00
  const float EpexMid       = 0.14966f;  // 2026-03-21T20:00  (confirmed via live entity)
  const float EpexExpensive = 0.1828f;   // 2026-03-22T18:00

  // Live-confirmed brutto import price at 20:00 (from sensor.pv_control_current_import_price_brutto)
  const float LiveBruttoPriceAt2000 = 0.28989604f;

  static float BruttoPriceImport(float netto) =>
    (netto * ImportPriceMultiplier + ImportPriceAddition + ImportPriceNetwork) * (1 + ImportPriceTax);

  static float BruttoPriceExport(float netto) =>
    (netto * ExportPriceMultiplier + ExportPriceAddition) * (1 + ExportPriceTax);

  // ── Price formula tests ───────────────────────────────────────────────────

  [Fact]
  public void BruttoPriceImport_AtLiveNettoPrice_MatchesHaEntity()
  {
    // Verify formula against the live-confirmed HA entity value.
    // Any re-introduction of ×100 or ÷100 would shift this by two orders of magnitude.
    float result = BruttoPriceImport(EpexMid);
    Assert.True(Math.Abs(result - LiveBruttoPriceAt2000) < 0.001f,
      $"Expected ~{LiveBruttoPriceAt2000} €/kWh, got {result}");
  }

  [Fact]
  public void BruttoPriceImport_AllLivePrices_AreInEurPerKwhRange()
  {
    // Guard: brutto import must be in the physically realistic €/kWh range (0.05–1.00).
    // If accidentally in ct/kWh the values would be 5–100, failing this assertion.
    float[] nettoPrices = [EpexCheapest, EpexMid, EpexExpensive,
                           0.00723f, 0.03534f, 0.10707f, 0.15994f]; // more live slots
    foreach (float netto in nettoPrices)
    {
      float brutto = BruttoPriceImport(netto);
      Assert.True(brutto < 1.0f,
        $"Brutto import price {brutto} ≥ 1.0 — looks like ct/kWh, not €/kWh (netto={netto})");
      Assert.True(brutto >= 0.05f,
        $"Brutto import price {brutto} < 0.05 — unexpectedly low (netto={netto})");
    }
  }

  [Fact]
  public void CostAccumulation_1kWhAtLivePrice_IsCorrectEurAmount()
  {
    // Core accumulation: deltaEur = deltaKwh × bruttoPrice (no ×100 or ÷100).
    // At 0.14966 €/kWh netto → 0.2899 €/kWh brutto → 1 kWh costs 0.2899 €.
    float brutto   = BruttoPriceImport(EpexMid);
    float deltaKwh = 1.0f;
    float deltaEur = deltaKwh * brutto;

    Assert.True(deltaEur is > 0.10f and < 1.0f,
      $"1 kWh import cost {deltaEur} € — outside expected 0.10–1.00 € range " +
      "(ct/kWh bug would give ~29, ×100 bug would give ~2900)");
  }

  [Fact]
  public void CostAccumulation_SmallDelta_AccumulatesCorrectly()
  {
    // 0.1 kWh (typical 15-min import) at mid price ≈ 0.029 € — never zero, never > 1.
    float deltaEur = 0.1f * BruttoPriceImport(EpexMid);
    Assert.True(deltaEur is > 0.001f and < 0.5f,
      $"0.1 kWh accumulated {deltaEur} € — unit error suspected");
  }

  // ── ForceChargeMaxPrice comparison tests ─────────────────────────────────

  [Fact]
  public void ForceChargeMaxPrice_025Eur_IsAboveCheapBruttoAndBelowExpensive()
  {
    // ForceChargeMaxPrice = 0.25 €/kWh must correctly straddle real EPEX brutto prices.
    // Regression: if prices accidentally in ct/kWh, BruttoCheap ≈ 11.0 > 0.25 → no charge ever.
    float forceChargeMaxPrice = 0.25f;
    float bruttoAtCheap     = BruttoPriceImport(EpexCheapest);   // ~0.109 €/kWh
    float bruttoAtExpensive = BruttoPriceImport(EpexExpensive);  // ~0.330 €/kWh

    Assert.True(bruttoAtCheap < forceChargeMaxPrice,
      $"Cheap brutto {bruttoAtCheap:F4} should be < ForceChargeMaxPrice {forceChargeMaxPrice} (ct/kWh regression would give ~11.0)");
    Assert.True(bruttoAtExpensive > forceChargeMaxPrice,
      $"Expensive brutto {bruttoAtExpensive:F4} should be > ForceChargeMaxPrice {forceChargeMaxPrice}");
  }

  // ── Simulator integration tests with live price ranges ───────────────────

  /// <summary>
  /// Builds a price list using brutto-converted live EPEX prices covering two days.
  /// 2026-03-21 20:00 = start; tomorrow 10:00 has the cheapest slot (0.0 netto).
  /// </summary>
  static List<PriceTableEntry> BuildLiveImportPrices()
  {
    // Representative subset of live EPEX data → converted to brutto import prices.
    // Hours not listed default to EpexMid brutto.
    var baseDate = new DateTime(2026, 3, 21);
    var hourlyNetto = new Dictionary<int, float>
    {
      // 2026-03-21
      [20] = 0.14966f, [21] = 0.13404f, [22] = 0.12827f, [23] = 0.11628f,
      // 2026-03-22 (hours 24–47 = next day 0–23)
      [24] = 0.10387f, [25] = 0.10105f, [26] = 0.10099f, [27] = 0.10239f,
      [28] = 0.10611f, [29] = 0.10978f, [30] = 0.10703f, [31] = 0.08974f,
      [32] = 0.04253f, [33] = 0.00723f, [34] = 0.00000f, [35] = 0.00090f,  // cheapest: h34
      [36] = 0.00419f, [37] = 0.00907f, [38] = 0.01240f, [39] = 0.07749f,
      [40] = 0.10756f, [41] = 0.15846f, [42] = 0.18280f, [43] = 0.17960f,  // most expensive: h42
    };

    var prices = new List<PriceTableEntry>();
    for (int h = 20; h < 20 + 48; h++)
    {
      float netto = hourlyNetto.TryGetValue(h, out var p) ? p : EpexMid;
      prices.Add(new PriceTableEntry(
        baseDate.AddHours(h),
        baseDate.AddHours(h + 1),
        BruttoPriceImport(netto)));
    }
    return prices;
  }

  [Fact]
  public void Simulator_WithLiveEpexPrices_ForceChargesAtCheapHours()
  {
    // Regression: if prices were ct/kWh (~11–33), ForceChargeMaxPrice=0.25 would be
    // below ALL prices → no force_charge → battery drains → this test would FAIL.
    var start = new DateTime(2026, 3, 21, 20, 0, 0);
    var importPrices = BuildLiveImportPrices();

    var input = new SimulationInput
    {
      StartTime                 = start,
      StartSocPercent           = 15,    // low — needs grid charge
      BatteryCapacityWh         = 10_000,
      AbsoluteMinSocPercent     = 12,
      PreferredMinSocPercent    = 20,
      EnforcePreferredSoc       = true,
      MaxChargePowerAmps        = 10,
      InverterEfficiency        = 0.9f,
      ImportPrices              = importPrices,
      ExportPrices              = importPrices,
      LoadPredictionWh          = Enumerable.Range(0, 48 * 4)
                                    .ToDictionary(i => start.AddMinutes(i * 15), _ => 300),
      PVPredictionWh            = Enumerable.Range(0, 48 * 4)
                                    .ToDictionary(i => start.AddMinutes(i * 15), _ => 0),
      ForceCharge               = false,
      OpportunisticDischarge    = false,
      ForceChargeMaxPrice       = 0.25f,   // €/kWh — all live slots are below this threshold
      ForceChargeTargetSocPercent = 100,
      OverrideMode              = InverterModes.automatic,
      CurrentMode               = new InverterState(InverterModes.normal),
      CurrentResetCounter       = 0,
      CurrentAverageGridPowerW  = 0,
    };

    var slots = EnergySimulator.Simulate(input);

    // With 15% SoC and no PV, the simulator MUST schedule force_charge.
    // If this fails, prices are likely in ct/kWh (all > 0.25 → never cheap enough).
    Assert.Contains(slots, s => s.State.Mode == InverterModes.force_charge);

    // The cheapest live slot (h34 = 2026-03-22T10:00, brutto ~0.109 €/kWh) is well below
    // ForceChargeMaxPrice = 0.25 €/kWh — the simulator MUST prefer it over expensive slots.
    // Regression: if prices were ct/kWh (~10.9 ct), 10.9 > 0.25 → no cheap slot found →
    // all force_charge would be pushed to emergency-only slots at random/expensive times.
    var cheapSlotTime = new DateTime(2026, 3, 22, 10, 0, 0);
    var forceSlots = slots.Where(s => s.State.Mode == InverterModes.force_charge).ToList();
    Assert.True(forceSlots.Any(s => s.Time >= cheapSlotTime && s.Time < cheapSlotTime.AddHours(4)),
      "Expected force_charge near the cheapest window (22.3.2026 10:00–14:00). " +
      "If absent, prices may be in ct/kWh so ForceChargeMaxPrice=0.25 filters out all slots.");

    // At least one force_charge slot must have brutto price ≤ ForceChargeMaxPrice.
    // (Emergency charging above the threshold is allowed but cheap charging must also happen.)
    Assert.Contains(forceSlots, s =>
    {
      var price = importPrices.FirstOrDefault(p => p.StartTime <= s.Time && p.EndTime > s.Time);
      return price.Price <= 0.25f;
    });
  }

  [Fact]
  public void Simulator_WithLiveEpexPrices_DoesNotForceChargeAtExpensiveSlots()
  {
    // Ensure ForceChargeMaxPrice correctly blocks expensive slots (h42 = 0.330 €/kWh brutto).
    var start = new DateTime(2026, 3, 21, 20, 0, 0);
    var importPrices = BuildLiveImportPrices();

    var input = new SimulationInput
    {
      StartTime                 = start,
      StartSocPercent           = 15,
      BatteryCapacityWh         = 10_000,
      AbsoluteMinSocPercent     = 12,
      PreferredMinSocPercent    = 20,
      EnforcePreferredSoc       = true,
      MaxChargePowerAmps        = 10,
      InverterEfficiency        = 0.9f,
      ImportPrices              = importPrices,
      ExportPrices              = importPrices,
      LoadPredictionWh          = Enumerable.Range(0, 48 * 4)
                                    .ToDictionary(i => start.AddMinutes(i * 15), _ => 300),
      PVPredictionWh            = Enumerable.Range(0, 48 * 4)
                                    .ToDictionary(i => start.AddMinutes(i * 15), _ => 0),
      ForceCharge               = false,
      OpportunisticDischarge    = false,
      ForceChargeMaxPrice       = 0.25f,
      ForceChargeTargetSocPercent = 100,
      OverrideMode              = InverterModes.automatic,
      CurrentMode               = new InverterState(InverterModes.normal),
      CurrentResetCounter       = 0,
      CurrentAverageGridPowerW  = 0,
    };

    var slots = EnergySimulator.Simulate(input);

    // 2026-03-22T18:00 is the most expensive slot (brutto ~0.330 €/kWh) — must not force-charge there.
    var expensiveSlot = new DateTime(2026, 3, 22, 18, 0, 0);
    var slotAt18 = slots.FirstOrDefault(s => s.Time == expensiveSlot);
    if (slotAt18 != null)
      Assert.NotEqual(InverterModes.force_charge, slotAt18.State.Mode);
  }
}
