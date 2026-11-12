# CLAUDE.md — Planning

The planner shared by the plugins (`README.md`); its design notes are PVControl's (`../PVControl/ARCHITECTURE.md`),
where it grew. Pure: no `PVCC_*` statics, no HA — keep it that way (a plugin that plans must not need PVControl's
state). `InternalsVisibleTo` PVControl and PVControl.Tests only (internals they used before the move); other plugins
use the public API.

## Tests (`tests/Planning.Tests`)

Scenario tests of the simulator and the DP that need nothing of PVControl: `SimulatorTests`, `PreferredMinimumTests`,
`TopUpDpTests`, `DpLoadPricerTests`, `DpDeviationTests`, `ChargeAbovePriceLimitTests`, `CheapWindowShorterThanSessionTests`,
`DailyTargetChargeTests`, `EvDirectFromGridTests`, `FindWindowConditionTests`, `FoggyDayTopUpTests`,
`GreedyBatteryLossesTests`, `LoadSchedulingDecisionTests`, `MiddayChargeVsNightTests`, `MorningTopUpTests`,
`NeedChargeHysteresisTests`, `NeedToChargeHysteresisTests` (what each covers: `../PVControl/CLAUDE.md`). The live
snapshots (`Data/forecast_snapshot_*.json.gz`) and the helpers (`TestBase.cs`, partial) live here; PVControl.Tests links
both. `tests/Host.Tests/SharedLibraryTests` checks that the host ships it and plugins only compile against it.
