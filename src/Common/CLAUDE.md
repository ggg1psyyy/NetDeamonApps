# CLAUDE.md — Common

Guidance for working on `src/Common`; the repository-wide rules (tests for every change, one calculation one
function, documentation, HA config) are in the root `CLAUDE.md`.

Shared by every app and provided by the host (plugins never carry their own copy): a change here is a change for
every plugin — run all tests (`dotnet test`).

## What's here

- **Calculations.cs**: the basic calculations shared by all apps, each once — `TimeRange` (overlap, slot fraction, floor), `Energy` (W/Wh/minutes), `Soc` (SoC/Wh, charge time), `Mean` (weighted, age weight, hour-of-day profile), `Regression` (least squares), `StepFunction` (HA history held values, time-weighted mean). `SqliteSchema` (version, columns) for the apps' own DBs; `Extensions.ToISO8601`/`FromISO8601` for every stored or HA time. Use these instead of writing the formula again
- **Extensions.cs**: generic HA helpers — `GetEntityHistoryAsync()`, `GetUnitMultiplicator()`, `TryGetStateValue()` (any enum by name via `TryParseEnum`), attributes, ISO 8601, `DayLog`; PVControl's own types (`PriceTableEntry`, `InverterModes`, `BatteryStatuses`, `ForceChargeReasons`, `LoadSchedulingMode`, prediction-window helpers, `RunningIntAverage`) are in `src/PVControl/PVControlExtensions.cs`
- **`AppSubscriptions`**: the subscriptions an app ends itself on dispose (MQTT command subscriptions outlive the app — the entity manager is a singleton); every app with command entities registers them and implements `IAsyncDisposable`
- **`AppFiles.PathOf`**: an app's relative file location (DB) lies in its plugin folder; **`AppVersion.Of`**: the git version of an app's assembly

## Tests (`tests/Common.Tests`)

- **`CalculationsTests`** — the shared basic calculations (`src/Common/Calculations.cs`: time ranges/slot fractions/rounding, W/Wh/minutes, SoC/Wh and charge time, weighted and age-weighted means, hour-of-day profile, least squares, step functions), `DayLog`, `ToSlug` (`GetNextQuarterHour` is PVControl's: `PVControlExtensionsTests`); `Energy.CounterIncrease` (every meter's increase — DataLogger, import cap, cost tracker; resets and glitches) is pinned in `HistoryTimestampTests`
- **`ExtensionsTests`**, **`ParseExtensionsTests`** — `GetUnitMultiplicator`, `TryParseToIntList`, `TryParseEnum` (any enum by name)
- **`AppFilesTests`** — `AppFiles.PathOf`: a relative location lies in the app assembly's (plugin) folder, an absolute one stays
- **`AppVersionTests`** — build version from git (`Directory.Build.targets` target `GitVersion`, `src/Common/AppVersion.cs`: each app reads its own assembly), shown as `sensor.pv_control_version` / `sensor.heating_test_version` and the devices' firmware
- **`AppSubscriptionsTests`** — every subscription ended once on dispose, one added afterwards at once, a failing one doesn't keep the others
