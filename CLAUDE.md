# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

NetDaemon 6 home automation apps written in C# (.NET 10.0) for Home Assistant. Four apps:
- **PVControl** — Solar/battery optimization with dynamic EPEX Spot pricing, load forecasting, and scheduling of extra loads (EV charger) and warm-water heating (heat pump one-time heatings)
- **DataLogger** — Logs energy sensor history to SQLite for ML training
- **HeatingTest** — Automated step test of the heat pump's heating-curve level (test/reference nights) with its own SQLite log; independent of PVControl, `Enabled: false` in its YAML switches it off completely
- **MidiControl** — BCF2000 MIDI controller integration (side project, low priority)

## Build & Run

```bash
dotnet build                   # Debug build
dotnet build -c Release        # Release build
dotnet publish -c Release      # Publish; copy output to /config/netdaemon6 on HA
dotnet test NetDeamonApps.Tests/     # Run unit tests (from repo root)
```

The test project lives at `NetDeamonApps.Tests/` inside the repo (tracked in `NetDeamon.sln`). Most testing is done against a live Home Assistant instance. To run locally, set `ASPNETCORE_ENVIRONMENT=Development` and configure `appsettings.json` with valid HA host/token.

## Testing Requirements

**Every new or changed feature/function must be accompanied by a corresponding test.** If the logic is testable without HA (pure computation, data transformation, decision logic), add or update a test in `NetDeamonApps.Tests`. If HA integration makes unit testing impractical, document why in a comment near the test class.

## One calculation, one function

**Absolute rule:** if two places calculate the same thing and differ only in their inputs, it must be one function/class with the differing parts as parameters — never two copies of the formula. Example: `SourceShares.Cost(importPrice, batteryPrice, pvPrice)` is the only € calculation for energy; the battery's average cost passes `pvPrice: 0` (money paid), the simulation's `NetGridCostVs` the export price (lost export).

## Documentation

- **`README.md`** describes only how things work **now** — no history, no dated incidents, no "before / used to" (that's what the commit log is for).
- **`apps/PVControl/ARCHITECTURE.md`** explains **why** things are the way they are — incidents, measurements and dates that led to a design belong there.

## Tests

`NetDeamonApps.Tests` (xUnit) covers pure-logic components that don't need HA:
- **`PredictionContainerTests`** — validates the 192-slot 15-min window check (`DataOK`)
- **`MidnightRolloverTests`** — demonstrates the midnight load-staleness bug and its fix (`TodayAndTomorrow.First()` vs `Today.First()`)
- **`SimulatorTests`** — end-to-end `EnergySimulator.Simulate()` tests using injectable `SimulationInput` (no HA needed)
- **`FindWindowConditionTests`** — simulation conditions behind the load scheduling rules (`FindLoadWindow` itself is private and reads live HA state, so its predicates are tested here)
- **`SessionPlannerTests`** — PriorityPlus price scan (cheapest/earliest start, min saving, sticky plan, ready-by deadline, follow-ups, net grid cost)
- **`CheapWindowShorterThanSessionTests`** — PriorityPlus when the cheap window is shorter than the session (live 2026-10-25 08:00): shortened session (`StartChoice.Partial`), longest passing end, min window, follow-up
- **`LoadSchedulingDecisionTests`** — Priority/PriorityPlus live SoC gate with restart hysteresis
- **`ReplanPolicyTests`** — when a load's schedule search reruns, house-load deviation trigger, min-off latch
- **`LevelEnergyLearnerTests`** — learning energy per level unit and charge power
- **`MorningTopUpTests`** — NeedToCharge grid charge with the real 2026-10-22 night: forecast margin of the night charge, no top-up at peak price when holding the floor is as cheap
- **`FoggyDayTopUpTests`** — NeedToCharge with a small PV bump before a foggy day (live 2026-10-24): the charge target's trough search runs to the PV peak after the critical point, so the cheap hours really charge from the grid
- **`TankModelTests`**, **`WarmWaterPlannerTests`**, **`WarmWaterControlTests`** — warm-water tank model fit (real history in `Data/`), the dynamic-program plan and heating prices, the control decision (`WarmWaterController`'s HA calls aren't unit tested)
- **`HeatingTestTests`** — HeatingTest app: start/skip/alternation, end/abort/disarm (`HeatingTestLogic`), SQLite log and restart recovery (`HeatingTestDb` on a temp file)
- **`NetEnergyPredictionTests`**, **`PriceListTests`**, **`NetworkPricePeriodTests`**, **`EnergyCostTests`**, **`ExtensionsTests`** — PV running-average correction, price normalization, network price periods, gross price composition, unit conversion
- **`LoadModelTests`**, **`WeatherForecastTests`** — weather-aware load forecast (`apps/PVControl/LoadModel`): cleaning, heat demand model, base level, night scoring, `WeatherLoadPrediction` on a temporary DB; Pirate Weather parsing (real response in `Data/`)
- **`DataLoggerPeriodTests`** — DataLogger periods as UTC instants: both repeated hours in October, no row for the skipped March hour, 23/25-h days, backfill
- **`MiddayChargeVsNightTests`** — NeedToCharge's charge-vs-hold comparison uses the hours from the low until PV after it (live 2026-10-26 13:30: the midday quarter at 0.3649 instead of the night at 0.3727; holding still wins when the bridged hours are cheap)
- **`ChargeAbovePriceLimitTests`** — above `ForceChargeMaxPrice` a NeedToCharge charge only protects the floor (no forecast margin)
- **`EvDirectFromGridTests`** — `SessionPlanner.ChooseSupply`: PriorityPlus sessions use PV + battery if no extra grid is needed, otherwise run directly from the grid (`ExtraLoad.FromGrid`, battery held while PV doesn't cover them, or alongside a force_charge) — live 2026-10-25: EV 06:00 from the battery, fed by a 00:00 night charge; `NetGridCostVs` counts the round-trip loss of battery use
- **`NeedChargeHysteresisTests`** — a running NeedToCharge charge never stops where starting one would begin it (no force_charge/normal flip-flop between plans); no grid charge right before a PV peak that fills the battery anyway (capped to the room PV leaves)
- **`InverterCommandTests`** — `InverterCommand` (the attributes of sensor.pv_control_mode): version (`seq`) only on change and continued after a restart, battery power per mode, the planned charge (`NextCharge`: energy, target SoC, end), `BulkFor` (plan, user-forced, throttled), the 1-minute minimum run (`HoldMinimumRun`), `HealthChecks.InverterSync` (the HA script/reconciler in `HA/` aren't unit tested)
- **`VppChargePathTests`** — the SoC-dependent VPP charge limit (`VppChargeCurve`: default line, learning, persistence) and the charge path (`EnergySimulator.ChargesBulk`: VPP within the same-price quarters — hourly vs quarter-hourly prices — else bulk; the battery at the path's limit in the plan); `ChargeStartAlignmentTests`: planned charges start with their price block, a restart within a block never at full power
- **`ImportCapTests`** — import cap (SNE-G-V power price): `PriorityCounter` (list order = priority, fallback while the first fails, frozen detection), `QuarterImport`, `MonthPeakTracker`, `ImportCapGuard` (battery throttled first, then extra loads paused, reaction time), `HealthChecks.ImportCap`
- **`ImportCapSimulationTests`** — the cap in the plan: `SimulationInput.GridChargeWhPerSlot`, force charge with the EV under the cap, `ExceedsImportCapVs` in the PriorityPlus price check, warm-water start waits for room, DataLogger v8 `maxquarterimport`
- **`DataLoggerOperationTests`** — DataLogger schema v6/v7/v9: minutes in a state, state changes, time-weighted means, prevailing mode, the v6, v7 (heat and electricity per mode) and v9 (PVControl mode, inverter EEPROM writes) migrations
- **`PricePublicationTrackerTests`** — when tomorrow's prices first appear (`sensor.pv_control_price_publication`)
- **`HeldReadingTests`** — battery SoC held through sensor dropouts, stale after 2 min (PVControl then falls back to normal mode)
- **`RunningSlotTests`** — no jumps at quarter boundaries: the simulation's running slot counts only its rest (`SimulationInput.SlotFraction`), the 0.1-kWh PV energy counter made continuous (`CounterInterpolator`)
- **`CalculationsTests`** — the shared basic calculations (`apps/Calculations.cs`: time ranges/slot fractions/rounding, W/Wh/minutes, SoC/Wh and charge time, weighted and age-weighted means, hour-of-day profile, least squares, step functions), `DayLog`, `ToSlug`
- **`SourceSharesTests`** — what a kWh costs by its source (`SourceShares`: PV free, grid, battery) for `sensor.pv_control_battery_avg_cost_per_kwh` and the loads' cost totals, `CostTracker.BlendAverageCost`, the shared price lookup `PriceList.GetPriceOrMean`
- **`HealthTests`** — `sensor.pv_control_health`: `HealthMonitor` debounce/escalation and `HealthChecks` (reading HA/DB and publishing in `PVControl` aren't unit tested)
- **`EntityRegistrationTests`** — when `RegisterSensor` publishes an entity's MQTT discovery config: option lists (selects/enums) on every start so HA follows `InverterModes` etc., the state kept on a republish
- **`AppVersionTests`** — build version from git (`NetDeamonApps.csproj` target `GitVersion`, `apps/AppVersion.cs`), shown as `sensor.pv_control_version` / `sensor.heating_test_version` and the devices' firmware

`TestBase` initialises `PVControlCommon.PVCC_Logger` via reflection (private setter) so PVControl code can log without throwing.

## Architecture

### App Discovery
`program.cs` bootstraps the NetDaemon host. Apps are auto-discovered via reflection from any class with `[NetDaemonApp]`. Use `[Focus]` in DEBUG builds to run only one app.

### App Structure Pattern
Each app consists of:
- `AppName.cs` — main class implementing `IAsyncInitializable`
- `AppName.yaml` — configuration (entity IDs, parameters); copied to output dir at build
- Config model class deserialized via `IAppConfig<T>` DI

### PVControl Architecture
- **`PVControlCommon.cs`** — Static singleton holding shared dependencies (`IHaContext`, `IMqttEntityManager`, `ILogger`, `IAppConfig<PVConfig>`, scheduler). Initialize once in constructor.
- **`HouseEnergy.cs`** — Core energy model: running averages, four prediction instances, cost tracking, `RunSimulation`, and `FindLoadWindow` (schedulable load decisions: numbered mode ladder Optimal 10 → Priority 20 → PriorityPlus 30)
- **`PVControl.cs`** — Registers the MQTT entities in HA, subscribes to sensor and command changes, runs the cycle every 15 seconds via cron (load schedule searches only when due, see `ReplanPolicy`)
- **`Simulator/`** — `EnergySimulator` (pure 15-min slot simulation, now → end of tomorrow), `SimulationInput`/`SimulationResult` (incl. `HasNewGridVs`/`IsGridCheapVs`/`NetGridCostVs`), `SessionPlanner` (PriorityPlus price scan and follow-up sessions), `LoadSchedulingDecision`
- **`Predictions/`** — Abstract `Prediction` base with `PredictionContainer` (validates 15-min interval data over 48h). Concrete: load (historical SQLite), PV (OpenMeteo API entities), NetEnergy (PV−Load), BatterySoC (simulation)
- **`Db/`** — LinqToDB ORM over SQLite for cost and history tables
- **`LoadModel/`** — weather-aware load forecast: `WeatherForecast` (Pirate Weather hourly), `HeatDemandModel` (heat pump energy signature, reusable), `WeatherLoadModel` (= backtested M2), `WeatherLoadPrediction` (Shadow/Model, average as fallback), `NightForecastTracker`; chosen by `tools/LoadBacktest`
- **`WarmWater/`** — warm-water tank planner: `TankModel` (learned from HA history), `WarmWaterPlanner` (dynamic program over slots × temperature, prices from the house simulation), `WarmWaterControl` (start one-time heatings, boost), `WarmWaterController` (HA side)
- **`ImportCap/`** — 15-min import cap: `PriorityCounter` (import counters in priority order), `QuarterImport`, `MonthPeakTracker`, `ImportCapGuard` (live decision; the limit goes out as `sensor.pv_control_battery_charge_current_limit`, an HA automation applies it)
- **`Managers/`** — `SchedulableLoadConfig` (YAML) / `SchedulableLoadRuntime` (live state) per extra load, `ReplanPolicy`, `LevelEnergyLearner`/`ChargePowerLearner`, `VppChargeCurve` (SoC-dependent VPP charge limit)
- **`ARCHITECTURE.md`** — detailed design notes for PVControl; keep it updated with behaviour changes

### Key Patterns
- **MQTT Entity Registration**: All dynamic HA sensors/selects/numbers/switches/times are created via `IMqttEntityManager`, not static YAML — see `PVControlCommon.RegisterSensor()`
- **Reactive Extensions**: State subscriptions use Rx.LINQ with throttling; cron jobs via `INetDaemonScheduler`
- **Calculations.cs**: the basic calculations shared by all apps, each once — `TimeRange` (overlap, slot fraction, floor), `Energy` (W/Wh/minutes), `Soc` (SoC/Wh, charge time), `Mean` (weighted, age weight, hour-of-day profile), `Regression` (least squares), `StepFunction` (HA history held values, time-weighted mean). `SqliteSchema` (version, columns) for the apps' own DBs; `Extensions.ToISO8601`/`FromISO8601` for every stored or HA time. Use these instead of writing the formula again
- **Extensions.cs**: Central utility library — `GetEntityHistoryAsync()`, `GetUnitMultiplicator()`, `TryGetStateValue()`, enums for `InverterModes`, `BatteryStatuses`, `ForceChargeReasons`, `LoadSchedulingMode`

### Configuration
`appsettings.json` holds HA connection (host, port, token) and MQTT credentials. App-specific entity IDs and parameters live in each app's `.yaml` file.

## Home Assistant config

The live HA config is mounted locally (path, add-on slug, deploy steps and other machine details: `CLAUDE.local.md`, not committed — identifying details like addresses never go into committed files). **Read-only by default:** writing, deploying or deleting anything there, and restarting HA or add-ons, only with the user's explicit permission for that action. The repo's `HA/` folder holds the PVControl automations (compare them with the live ones before changing them).

## Follow-ups (remind the user when due)

- **From ~2026-12-05: learn the warm-water efficiency.** `WarmWater.EfficiencyChangePerK` in `PVControl.yaml` is a fixed 0.03 (one rough measurement). DataLogger v7 logs heat and electricity per mode since 2026-10-24 (`heatenergywarmwater` / `heatpumpelecwarmwater`); fit warm-water COP vs outside temperature from the runs and replace the fixed value (or learn it in `WarmWaterController`). Needs enough runs over a spread of outside temperatures.
- **Mid-December 2026: power price figures (SNE-T-V).** Once the ct/kW (≤ 10 kW / above) and Linz Netz's start date are published, build the € side of the import cap: `PowerPrice` config, `CostTracker` books Δ(month peak) × ct/kW when the peak rises, `NetGridCostVs` adds the peak increase (the month's peak so far becomes a free soft cap under 9.8 kW).
- **HeatingTest:** heating switched on 2026-10-24. Arm `switch.heating_test_armed` once the rooms hold their setpoints (a few days later).
