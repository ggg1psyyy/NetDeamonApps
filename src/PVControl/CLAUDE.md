# CLAUDE.md — PVControl

Guidance for working on `src/PVControl`; the repository-wide rules (tests for every change, one calculation one
function, documentation, HA config) are in the root `CLAUDE.md`.

Design notes and their reasons: `ARCHITECTURE.md` (keep it updated with behaviour changes); the HA automations,
script and package it relies on: `HA/` (`python deploy/deploy.py ha`). Reads the DataLogger's DB.

## Architecture

- **`PVControlCommon.cs`** — Static singleton holding shared dependencies (`IHaContext`, `IMqttEntityManager`, `ILogger`, `IAppConfig<PVConfig>`, scheduler). Initialize once in constructor.
- **`HouseEnergy.cs`** — Core energy model: running averages, four prediction instances, cost tracking, `RunSimulation`, and `FindLoadWindow` → `LoadWindowSearch` (one load's decision per cycle: the gates, then the rulesets as methods — numbered mode ladder Optimal 10 → Priority 20 → PriorityPlus 30 — over shared fields)
- **`PVControl.cs`** — Registers the MQTT entities in HA, subscribes to sensor and command changes, runs the cycle every 15 seconds via cron (load schedule searches only when due, see `ReplanPolicy`)
- **`Simulator/`** — `EnergySimulator` (pure 15-min slot simulation, now → end of tomorrow), `SimulationInput`/`SimulationResult` (incl. `HasNewGridVs`/`IsGridCheapVs`/`NetGridCostVs`), `SessionPlanner` (PriorityPlus price scan and follow-up sessions), `LoadSchedulingDecision`
- **`Predictions/`** — Abstract `Prediction` base with `PredictionContainer` (validates 15-min interval data over 48h). Concrete: load (historical SQLite), PV (OpenMeteo API entities), NetEnergy (PV−Load), BatterySoC (simulation)
- **`Db/`** — LinqToDB ORM over SQLite for cost and history tables
- **`LoadModel/`** — weather-aware load forecast: `WeatherForecast` (Pirate Weather hourly), `HeatDemandModel` (heat pump energy signature, reusable), `WeatherLoadModel` (= backtested M2), `WeatherLoadPrediction` (Shadow/Model, average as fallback), `NightForecastTracker`; chosen by `tools/LoadBacktest`
- **`WarmWater/`** — warm-water tank planner: `TankModel` (learned from HA history), `WarmWaterPlanner` (dynamic program over slots × temperature, prices from the house simulation), `WarmWaterControl` (start one-time heatings, boost), `WarmWaterController` (HA side)
- **`ImportCap/`** — 15-min import cap: `PriorityCounter` (import counters in priority order), `QuarterImport`, `MonthPeakTracker`, `ImportCapGuard` (live decision; the limit goes out as `sensor.pv_control_battery_charge_current_limit`, an HA automation applies it)
- **`Managers/`** — `SchedulableLoadConfig` (YAML) / `SchedulableLoadRuntime` (live state) per extra load, `ReplanPolicy`, `LevelEnergyLearner`/`ChargePowerLearner`, `VppChargeCurve` (SoC-dependent VPP charge limit)
- **`ARCHITECTURE.md`** — detailed design notes for PVControl; keep it updated with behaviour changes

## Key Patterns

- **MQTT Entity Registration**: All dynamic HA sensors/selects/numbers/switches/times are created via `IMqttEntityManager`, not static YAML — see `PVControlCommon.RegisterSensor()`
- **Reactive Extensions**: State subscriptions use Rx.LINQ with throttling; cron jobs via `INetDaemonScheduler`
- PVControl's own types (`PriceTableEntry`, `InverterModes`, `BatteryStatuses`, `ForceChargeReasons`, `LoadSchedulingMode`, prediction-window helpers, `RunningIntAverage`) are in `PVControlExtensions.cs`; the generic helpers in Common

## Tests (`tests/PVControl.Tests`)

Also references DataLogger: PVControl reads its DB — schema and file name.
- **`PredictionContainerTests`** — validates the 192-slot 15-min window check (`DataOK`)
- **`MidnightRolloverTests`** — the load prediction's day rollover (`HouseEnergy.NeedsDayRollover`: the start of the 48-h window decides) and the PV forecast's window cut (`CombineForecastLists`)
- **`SimulatorTests`** — end-to-end `EnergySimulator.Simulate()` tests using injectable `SimulationInput` (no HA needed)
- **`FindWindowConditionTests`** — simulation conditions behind the load scheduling rules (`FindLoadWindow` itself is private and reads live HA state, so its predicates are tested here)
- **`SessionPlannerTests`** — PriorityPlus price scan (cheapest/earliest start, min saving, sticky plan, ready-by deadline — over the price limit with the same choice rules: a running session keeps running, the plan stays; too close to finish counted from now in the running slot —, follow-ups, net grid cost; split sessions: `CheapestTwoBlocks`, `FindSplit` with the 5 % / 10 ct threshold)
- **`CheapWindowShorterThanSessionTests`** — PriorityPlus when the cheap window is shorter than the session (live 2026-10-25 08:00): shortened session (`StartChoice.Partial`), longest passing end, min window, follow-up
- **`LoadSchedulingDecisionTests`** — Priority/PriorityPlus live SoC gate with restart hysteresis
- **`ReplanPolicyTests`** — when a load's schedule search reruns, house-load deviation trigger, min-off latch (`DelayUntil`: a moved session keeps its energy and supply)
- **`LevelEnergyLearnerTests`** — learning energy per level unit (`Blend`: the weighted mean with the prior) and charge power
- **`PreferredMinimumTests`** — the preferred minimum (live 2026-10-30): `EnergySimulator.SoftBandPct` (the preferred minimum above the absolute one, or the forecast margin on an enforced floor), the night charge to it in cheap hours, no purchase at the peak while above the hard floor, the hard-floor hold and its release margin (`HoldReleasePct`), flat prices, enforced, planned loads overnight (`IsOvernightMinSocOk`); the older scenario tests (MorningTopUp, NeedToChargeHysteresis, VppChargePath, EvDirectFromGrid, CheapWindowShorterThanSession) pin a hard 12 % floor (enforced) — the semantics they were written for
- **`MorningTopUpTests`** — NeedToCharge grid charge with the real 2026-10-22 night: forecast margin of the night charge, no top-up at peak price when holding the floor is as cheap
- **`FoggyDayTopUpTests`** — NeedToCharge with a small PV bump before a foggy day (live 2026-10-24): the charge target's trough search runs to the PV peak after the critical point, so the cheap hours really charge from the grid
- **`TankModelTests`**, **`WarmWaterPlannerTests`**, **`WarmWaterControlTests`** — warm-water tank model fit (real history in `Data/`), the dynamic-program plan and heating prices, the control decision, the refit schedule (`WarmWaterController.RefitDue`; its HA calls aren't unit tested)
- **`NetEnergyPredictionTests`**, **`PriceListTests`**, **`NetworkPricePeriodTests`** (incl. first/last year), **`EnergyCostTests`**, **`PVControlExtensionsTests`** — PV running-average correction, price normalization and comparison (`SameAs`: a replan only on a real price change), network price periods, gross price composition, unit conversion
- **`LoadModelTests`**, **`WeatherForecastTests`** — weather-aware load forecast (`src/PVControl/LoadModel`): cleaning, heat demand model, base level, night scoring, `WeatherLoadPrediction` on a temporary DB; Pirate Weather parsing (real response in `Data/`)
- **`MiddayChargeVsNightTests`** — NeedToCharge's charge-vs-hold comparison uses the hours from the low until PV after it (live 2026-10-26 13:30: the midday quarter at 0.3649 instead of the night at 0.3727; holding still wins when the bridged hours are cheap)
- **`ChargeAbovePriceLimitTests`** — above `ForceChargeMaxPrice` a NeedToCharge charge only protects the floor (no forecast margin)
- **`DailyTargetChargeTests`** — the daily target charge (`switch.pv_control_force_charge_at_cheapest_period`): the target by the end of the day's PV (`SimulationInput.DailyTargetDeadline`), only what PV won't deliver, in the cheapest quarters under the limit, tomorrow's deadline after sunset
- **`EvDirectFromGridTests`** — `SessionPlanner.ChooseSupply`: PriorityPlus sessions use PV + battery if no extra grid is needed, otherwise run directly from the grid (a running grid session stays on the grid) (`ExtraLoad.FromGrid`, battery held while PV doesn't cover them, or alongside a force_charge) — live 2026-10-25: EV 06:00 from the battery, fed by a 00:00 night charge; `NetGridCostVs` counts the round-trip loss of battery use
- **`NeedChargeHysteresisTests`** — a running NeedToCharge charge never stops where starting one would begin it (no force_charge/normal flip-flop between plans); no grid charge right before a PV peak that fills the battery anyway (capped to the room PV leaves)
- **`InverterCommandTests`** — `InverterCommand` (the attributes of sensor.pv_control_mode): version (`seq`) only on change — not on a voltage step —, the published command restored after a restart (`FromPublished`), battery power per mode, the planned charge (`NextCharge`: energy, target SoC, end), `BulkFor` (plan, user-forced — the import cap doesn't change the path), the 1-minute minimum run (`HoldMinimumRun`), the steadied target SoC (`SteadyTarget`), `HealthChecks.InverterSync` (the HA script/reconciler in `HA/` aren't unit tested)
- **`VppChargePathTests`** — the SoC-dependent VPP charge limit (`VppChargeCurve`: default line, learning and its thresholds, persistence) and the charge path (`EnergySimulator.ChargesBulk`: VPP within the same-price quarters — hourly vs quarter-hourly prices — else bulk if it delivers more energy, both as the import cap leaves them; the battery at the path's limit in the plan); `ChargeStartAlignmentTests`: an earlier VPP start in the same price block beats a later bulk one (`VppNowBeatsBulkLater`), a later start VPP manages stays
- **`RunningSlotSessionTests`** — sessions coupled to their energy (`SessionPlanner.EndForEnergy`: from now in the running slot; live 2026-10-28 21:28 the EV started at the evening's highest price), `ExtraLoad.EnergyFromWh`, follow-ups cover the real rest, `FindMaxSessionEnd` returns an energy's exact end, the `price_scan` line
- **`ImportCapTests`** — import cap (SNE-G-V power price): `PriorityCounter` (list order = priority, fallback while the first fails, frozen detection live and per period — `FirstWorking` for the DataLogger), `QuarterImport` (quarters as UTC instants across the clock changes; a finished quarter scaled to exactly 15 min, none without a start reading), `MonthPeakTracker` (restore from sensor + DataLogger, the higher wins; an older month's quarter never replaces a later peak), `ImportCapGuard` (battery throttled first, then extra loads paused, reaction time; an extra load running outside PVControl's switch counts; `NextLimit`: lowered at once, raised only with twice the headroom — the 2026-10-29 bulk + EV night), `HealthChecks.ImportCap` (none configured = error)
- **`ImportCapSimulationTests`** — the cap in the plan: `SimulationInput.GridChargeWhPerSlot`, force charge with the EV under the cap, `ExceedsImportCapVs` in the PriorityPlus price check, `GridChargeRoomW` (a charge starts at the room its planned EV session leaves), warm-water start waits for room, DataLogger v8 `maxquarterimport`
- **`PricePublicationTrackerTests`** — when tomorrow's prices first appear (`sensor.pv_control_price_publication`, also per source)
- **`HeldReadingTests`** — battery SoC held through sensor dropouts, stale after 2 min (PVControl then falls back to normal mode)
- **`RunningSlotTests`** — no jumps at quarter boundaries: the simulation's running slot counts only its rest (`SimulationInput.SlotFraction`), the 0.1-kWh PV energy counter made continuous (`CounterInterpolator`)
- **`SourceSharesTests`** — what a kWh costs by its source (`SourceShares`: PV free, grid, battery) for `sensor.pv_control_battery_avg_cost_per_kwh` and the loads' cost totals, `CostTracker.BlendAverageCost`, `CostTracker.CounterBooking` (the cost log's totals: an unavailable counter keeps the last total), the shared price lookup `PriceList.GetPriceOrMean`
- **`HealthTests`** — `sensor.pv_control_health`: `HealthMonitor` debounce/escalation, restore after a restart, and `HealthChecks` (incl. `Instances`: a second live PVControl instance is an error) (reading HA/DB and publishing in `PVControl` aren't unit tested)
- **`EntityRegistrationTests`** — when `RegisterSensor` publishes an entity's MQTT discovery config: option lists (selects/enums) on every start so HA follows `InverterModes` etc., the state kept on a republish
- **`SqliteOptionsTests`** — every LinqToDB connection names Microsoft.Data.Sqlite (`SqliteOptions`), never LinqToDB's driver detection (live 2026-10-29: it failed once the driver lay only in the host's folder)
- **`AppFilesTests`** — PVControl finds the DataLogger's DB from its own plugin folder (`../DataLogger/`), its cost DB in its folder

`TestBase` (PVControl.Tests) initialises `PVControlCommon.PVCC_Logger` via reflection (private setter) so PVControl code can log without throwing.
