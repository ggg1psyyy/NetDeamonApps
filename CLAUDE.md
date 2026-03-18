# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

NetDaemon4 home automation apps written in C# (.NET 9.0) for Home Assistant. Three apps:
- **PVControl** — Solar/battery optimization with dynamic EPEX Spot pricing, load forecasting, and heatpump scheduling
- **DataLogger** — Logs energy sensor history to SQLite for ML training
- **MidiControl** — BCF2000 MIDI controller integration (side project, low priority)

## Build & Run

```bash
dotnet build                   # Debug build
dotnet build -c Release        # Release build
dotnet publish -c Release      # Publish; copy output to /config/netdaemon4 on HA
dotnet test ../NetDeamonApps.Tests/  # Run unit tests (from repo root)
```

The test project lives at `../NetDeamonApps.Tests/` (sibling directory to the repo). Most testing is done against a live Home Assistant instance. To run locally, set `ASPNETCORE_ENVIRONMENT=Development` and configure `appsettings.json` with valid HA host/token.

## Tests

`NetDeamonApps.Tests` (xUnit) covers pure-logic components that don't need HA:
- **`PredictionContainerTests`** — validates the 192-slot 15-min window check (`DataOK`)
- **`MidnightRolloverTests`** — demonstrates the midnight load-staleness bug and its fix (`TodayAndTomorrow.First()` vs `Today.First()`)
- **`SimulatorTests`** — end-to-end `PVSimulator.Simulate()` tests using injectable `SimulationInput` (no HA needed)

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
- **`HouseEnergy.cs`** — Core energy model: running averages, four prediction instances, cost tracking, charging decisions
- **`PVControl.cs`** — Registers 30+ MQTT entities in HA, subscribes to sensor state changes, runs logic every 15 minutes via cron
- **`Predictions/`** — Abstract `Prediction` base with `PredictionContainer` (validates 15-min interval data over 48h). Concrete: load (historical SQLite), PV (OpenMeteo API entities), NetEnergy (PV−Load), BatterySoC (simulation)
- **`Db/`** — LinqToDB ORM over SQLite for cost and history tables
- **`Managers/`** — `ILoadManager` interface for schedulable loads; `HeatpumpManager` handles warm-water timing

### Key Patterns
- **MQTT Entity Registration**: All dynamic HA sensors/selects/numbers are created via `IMqttEntityManager`, not static YAML — see `PVControlCommon.RegisterSensor()`
- **Reactive Extensions**: State subscriptions use Rx.LINQ with throttling; cron jobs via `INetDaemonScheduler`
- **Extensions.cs**: Central utility library — `GetEntityHistoryAsync()`, `GetUnitMultiplicator()`, enums for `InverterModes`, `BatteryStatuses`, `RunHeavyLoadsStatus`, `ForceChargeReasons`

### Configuration
`appsettings.json` holds HA connection (host, port, token) and MQTT credentials. App-specific entity IDs and parameters live in each app's `.yaml` file.
