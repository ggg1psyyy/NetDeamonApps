# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

NetDaemon 6 home automation apps written in C# (.NET 10.0) for Home Assistant: a host (`src/Host`) that loads one plugin per app, each its own project under `src/` with its tests in `tests/<Project>.Tests`. **Each project has its own `CLAUDE.md`** (architecture, tests) and `README.md`. Apps:
- **PVControl** — Solar/battery optimization with dynamic EPEX Spot pricing, load forecasting, and scheduling of extra loads (EV charger) and warm-water heating (heat pump one-time heatings)
- **DataLogger** — Logs energy sensor history to SQLite for ML training
- **HeatingTest** — Automated step test of the heat pump's heating-curve level (test/reference nights) with its own SQLite log; independent of PVControl, `Enabled: false` in its YAML switches it off completely
- **MidiControl** — BCF2000 MIDI controller integration (side project, low priority)

Shared code: `src/Common` (each basic calculation once — use it instead of writing a formula again).

## Build & Run

```bash
dotnet build                          # Debug build
dotnet build -c Release               # Release build
dotnet publish src/Host -c Release    # Publish host + plugins (src/Host/bin/Release/net10.0/publish)
python deploy/deploy.py apps          # Publish, copy to the add-on, restart, verify (deploy/README.md)
dotnet test                          # Run all unit tests (from repo root; one project: dotnet test tests/PVControl.Tests)
```

One test project per project lives at `tests/<Project>.Tests/` (shared settings in `tests/Tests.props`, all in `NetDeamon.sln`). Most testing is done against a live Home Assistant instance. To run locally, set `ASPNETCORE_ENVIRONMENT=Development` and configure `src/Host/appsettings.json` with valid HA host/token.

## Testing Requirements

**Every new or changed feature/function must be accompanied by a corresponding test.** If the logic is testable without HA (pure computation, data transformation, decision logic), add or update a test in the project's `tests/<Project>.Tests`. If HA integration makes unit testing impractical, document why in a comment near the test class.

## One calculation, one function

**Absolute rule:** if two places calculate the same thing and differ only in their inputs, it must be one function/class with the differing parts as parameters — never two copies of the formula. Example: `SourceShares.Cost(importPrice, batteryPrice, pvPrice)` is the only € calculation for energy; the battery's average cost passes `pvPrice: 0` (money paid), the simulation's `NetGridCostVs` the export price (lost export).

## Documentation

- **`README.md`** describes only how things work **now** — no history, no dated incidents, no "before / used to" (that's what the commit log is for).
- **`src/PVControl/ARCHITECTURE.md`** explains **why** things are the way they are — incidents, measurements and dates that led to a design belong there.
- Every project has its own **`README.md`** (what it does now, how to build/test it); the root one is the umbrella.

## Architecture

### App Discovery
`src/Host/program.cs` bootstraps the NetDaemon host; `src/Host/PluginLoader.cs` loads every app plugin from `apps/plugins/<Name>/` (each app is its own project under `src/`). Apps are auto-discovered via reflection from any class with `[NetDaemonApp]`. A plugin is `src/<Name>/<Name>.csproj` on `src/AppPlugin.props`, listed as `AppPlugin` in `src/Host/Host.csproj`. Use `[Focus]` in DEBUG builds to run only one app.

### App Structure Pattern
Each app consists of:
- `AppName.cs` — main class implementing `IAsyncInitializable`
- `AppName.yaml` — configuration (entity IDs, parameters); copied to output dir at build
- Config model class deserialized via `IAppConfig<T>` DI
- Files the app keeps (DBs): a relative location via `AppFiles.PathOf` lies in the plugin's folder, next to its YAML
- **Namespaces stay as they are:** YAML config keys and NetDaemon app ids are full type names (DataLogger's class lives in namespace `PVControl`) — renaming one silently breaks its config

### Configuration
`src/Host/appsettings.json` (not committed) holds the HA connection (host, port, token) and MQTT credentials. App-specific entity IDs and parameters live in each app's `.yaml` file.

## Home Assistant config

The live HA config is mounted locally (path, add-on slug, deploy steps and other machine details: `CLAUDE.local.md`, not committed — identifying details like addresses never go into committed files). **Read-only by default:** writing, deploying or deleting anything there, and restarting HA or add-ons, only with the user's explicit permission for that action. `src/PVControl/HA/` holds the PVControl automations (`python deploy/deploy.py ha` merges them; dry run without `--write`) (compare them with the live ones before changing them).

## Follow-ups (remind the user when due)

- **Weekend 2026-11-07/08: evaluate the DP shadow** (branch `dp-shadow`, live since 2026-10-30 12:32, `0.5.324+58218e0`). From `apps/plugins/PVControl/dp_shadow.db` (table `shadow`, one row per quarter): how often both plans agree now (`greedy_mode`/`dp_mode`), the DP's advantage under the one model (`greedy_total − dp_total`, also `greedy_grid − dp_grid`), the lows (`greedy_min_soc`/`dp_min_soc`), the next charges; compare with what really happened (cost log). Then decide: calibrate `BandPenaltyPerPctQuarter`, keep shadowing, switch, or drop the branch. Until then: merge `master` fixes into `dp-shadow` before any deploy, or the shadow is gone.
- **From ~2026-12-05: learn the warm-water efficiency.** `WarmWater.EfficiencyChangePerK` in `PVControl.yaml` is a fixed 0.03 (one rough measurement). DataLogger v7 logs heat and electricity per mode since 2026-10-24 (`heatenergywarmwater` / `heatpumpelecwarmwater`); fit warm-water COP vs outside temperature from the runs and replace the fixed value (or learn it in `WarmWaterController`). Needs enough runs over a spread of outside temperatures.
- **Mid-December 2026: power price figures (SNE-T-V).** Also set the real WiNAP rate in `PVControl.yaml` (`ImportPriceNetworkPeriods`, a SNAP-rate placeholder until then; it applies from 2027-10-01). Once the ct/kW (≤ 10 kW / above) and Linz Netz's start date are published, build the € side of the import cap: `PowerPrice` config, `CostTracker` books Δ(month peak) × ct/kW when the peak rises, `NetGridCostVs` adds the peak increase (the month's peak so far becomes a free soft cap under 9.8 kW).
- **HeatingTest:** heating switched on 2026-10-24. Arm `switch.heating_test_armed` once the rooms hold their setpoints. **On hold (checked 2026-10-29):** Wohnen/Küche has no reserve — 0.1–0.3 K under 21.0 °C, 0.8 K under since the setpoint went to 21.5 °C (comfort, 2026-10-28), valve heating around the clock; a test night (curve −3 K) would hit its 1.0-K abort. Recheck once the user has looked at the heating curve level (or the room's setpoint).
