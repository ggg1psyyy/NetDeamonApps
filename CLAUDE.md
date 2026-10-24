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
- **`LoadSchedulingDecisionTests`** — Priority/PriorityPlus live SoC gate with restart hysteresis
- **`ReplanPolicyTests`** — when a load's schedule search reruns, house-load deviation trigger, min-off latch
- **`LevelEnergyLearnerTests`** — learning energy per level unit and charge power
- **`MorningTopUpTests`** — NeedToCharge grid charge with the real 2026-10-22 night: forecast margin of the night charge, no top-up at peak price when holding the floor is as cheap
- **`FoggyDayTopUpTests`** — NeedToCharge with a small PV bump before a foggy day (live 2026-10-24): the charge target's trough search runs to the PV peak after the critical point, so the cheap hours really charge from the grid
- **`TankModelTests`**, **`WarmWaterPlannerTests`**, **`WarmWaterControlTests`** — warm-water tank model fit (real history in `Data/`), the dynamic-program plan and heating prices, the control decision (`WarmWaterController`'s HA calls aren't unit tested)
- **`HeatingTestTests`** — HeatingTest app: start/skip/alternation, end/abort/disarm (`HeatingTestLogic`), SQLite log and restart recovery (`HeatingTestDb` on a temp file)
- **`NetEnergyPredictionTests`**, **`PriceListTests`**, **`NetworkPricePeriodTests`**, **`EnergyCostTests`**, **`ExtensionsTests`** — PV running-average correction, price normalization, network price periods, gross price composition, unit conversion
- **`LoadModelTests`**, **`WeatherForecastTests`** — weather-aware load forecast (`apps/PVControl/LoadModel`): cleaning, heat demand model, base level, night scoring, `WeatherLoadPrediction` on a temporary DB; Pirate Weather parsing (real response in `Data/`)
- **`DataLoggerOperationTests`** — DataLogger schema v6: minutes in a state, time-weighted means, prevailing mode, the v6 migration
- **`PricePublicationTrackerTests`** — when tomorrow's prices first appear (`sensor.pv_control_price_publication`)
- **`HeldReadingTests`** — battery SoC held through sensor dropouts, stale after 2 min (PVControl then falls back to normal mode)
- **`RunningSlotTests`** — no jumps at quarter boundaries: the simulation's running slot counts only its rest (`SimulationInput.SlotFraction`), the 0.1-kWh PV energy counter made continuous (`CounterInterpolator`)
- **`CalculationsTests`** — the shared basic calculations (`apps/Calculations.cs`: time ranges/slot fractions/rounding, W/Wh/minutes, SoC/Wh and charge time, weighted and age-weighted means, hour-of-day profile, least squares, step functions), `DayLog`, `ToSlug`
- **`SourceSharesTests`** — what a kWh costs by its source (`SourceShares`: PV free, grid, battery) for `sensor.pv_control_battery_avg_cost_per_kwh` and the loads' cost totals, `CostTracker.BlendAverageCost`, the shared price lookup `PriceList.GetPriceOrMean`
- **`HealthTests`** — `sensor.pv_control_health`: `HealthMonitor` debounce/escalation and `HealthChecks` (reading HA/DB and publishing in `PVControl` aren't unit tested)
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
- **`Managers/`** — `SchedulableLoadConfig` (YAML) / `SchedulableLoadRuntime` (live state) per extra load, `ReplanPolicy`, `LevelEnergyLearner`/`ChargePowerLearner`
- **`ARCHITECTURE.md`** — detailed design notes for PVControl; keep it updated with behaviour changes

### Key Patterns
- **MQTT Entity Registration**: All dynamic HA sensors/selects/numbers/switches/times are created via `IMqttEntityManager`, not static YAML — see `PVControlCommon.RegisterSensor()`
- **Reactive Extensions**: State subscriptions use Rx.LINQ with throttling; cron jobs via `INetDaemonScheduler`
- **Calculations.cs**: the basic calculations shared by all apps, each once — `TimeRange` (overlap, slot fraction, floor), `Energy` (W/Wh/minutes), `Soc` (SoC/Wh, charge time), `Mean` (weighted, age weight, hour-of-day profile), `Regression` (least squares), `StepFunction` (HA history held values, time-weighted mean). `SqliteSchema` (version, columns) for the apps' own DBs; `Extensions.ToISO8601`/`FromISO8601` for every stored or HA time. Use these instead of writing the formula again
- **Extensions.cs**: Central utility library — `GetEntityHistoryAsync()`, `GetUnitMultiplicator()`, `TryGetStateValue()`, enums for `InverterModes`, `BatteryStatuses`, `ForceChargeReasons`, `LoadSchedulingMode`

### Configuration
`appsettings.json` holds HA connection (host, port, token) and MQTT credentials. App-specific entity IDs and parameters live in each app's `.yaml` file.
