# CLAUDE.md — Planning

The planner shared by the plugins (`README.md`); its design notes are PVControl's (`../PVControl/ARCHITECTURE.md`),
where it grew. Pure: no `PVCC_*` statics, no HA — keep it that way (the one I/O: `ForecastSnapshotStore`, the snapshot table) (a plugin that plans must not need PVControl's
state). `InternalsVisibleTo` PVControl and PVControl.Tests only (internals they used before the move); other plugins
use the public API.

## Tests (`tests/Planning.Tests`)

Scenario tests of the simulator and the DP that need nothing of PVControl: `SimulatorTests`, `PreferredMinimumTests`,
`TopUpDpTests`, `DpLoadPricerTests`, `DpDeviationTests`, `ChargeAbovePriceLimitTests`, `CheapWindowShorterThanSessionTests`,
`DailyTargetChargeTests`, `EvDirectFromGridTests`, `FindWindowConditionTests`, `FoggyDayTopUpTests`,
`GreedyBatteryLossesTests`, `LoadSchedulingDecisionTests`, `MiddayChargeVsNightTests`, `MorningTopUpTests`,
`NeedChargeHysteresisTests`, `NeedToChargeHysteresisTests`, `FixedTariffRulesTests` (under a fixed price on live snapshots: no hold, no charge, mostly normal, down to the absolute floor; enforced keeps the preferred, holds only at the floor (`HoldOnlyAtFloor`, live 2026-11-17 07:30: holds at 30–61 % between normal quarters); market today/fixed from midnight; `PreferredMinSocAt`; `TariffPrices.Input`'s fixed quarters, kept in the snapshot; no euro step at the floor — the DP's steps rounded up, the exact SoC; `NextEnergyWh` ends a discharge at the floor, a real deficit still under; `ForLoadPlanning`: the EV on the market's prices, the limit at the price paid; holds only during a grid-fed load; before a switch to the market holds allowed (`FixedImportUntilEnd`); the end rule off with nothing above the floor; `ReasonOf` judges the preferred minimum in force at the low; the charge-now rule looks 4 h ahead only — live 2026-11-17 18:00: tomorrow's 23:30 tie charge pulled to now every 1–3 min; one price throughout is no block — live 2026-11-24 14:30/12:30: no charge above the floor at any whole percent or minute, also without the 4-h limit; a hold at the floor stays for the running quarter — the floor check on the whole quarter, live 2026-11-19 19:30: normal 6 min in; a running load starting now counts over the whole quarter — live 2026-11-21 10:30: the EV at the floor, normal 9–11 min in; the plan never sits under the floor where a hold is allowed, no phantom floor penalty — the forward pass at the exact energy, live 2026-11-21 10:30: 0.625 €), `TariffPricesTests` (`DpPlan.EndEnergyWh`, the snapshot's tariff lists — today's tariff until the switch, the other from it —, old rows without them still load, the store's latest row, no DB or table = none) (what each covers: `../PVControl/CLAUDE.md`). The live
snapshots (`Data/forecast_snapshot_*.json.gz`) and the helpers (`TestBase.cs`, partial) live here; PVControl.Tests links
both. `tests/Host.Tests/SharedLibraryTests` checks that the host ships it and plugins only compile against it.
