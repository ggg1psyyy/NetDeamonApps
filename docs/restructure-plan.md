# Restructure: one host, one plugin per app

Goal: one repository (this one) with independent projects — a small NetDaemon host ("shim") that
loads any number of app plugins, one plugin per app, each buildable, testable and runnable on its
own. Decided 2026-10-29; work happens on `master`, one tested, deployable commit per step.

## Target layout

```
src/Host            the shim: program.cs, plugin loader, appsettings; no apps. Assembly name stays
                    NetDeamonApps (the HA add-on starts that file)
src/Common          Calculations, generic Extensions, PriorityCounter, AppVersion, SqliteSchema
src/PVControl       plugin (+ HA/, ARCHITECTURE.md)
src/DataLogger      plugin
src/HeatingTest     plugin
src/MidiControl     plugin (+ ImageSharp, only here)
tests/<Project>.Tests, tests/Host.Tests
tools/              LoadBacktest (references the projects instead of linking files)
deploy/             build all → add-on layout; HA automation deploy (machine details from an
                    uncommitted deploy/local.json)
```

Deployed (`/config/netdaemon6`):

```
NetDeamonApps.dll …            host
apps/plugins/<Name>/<Name>.dll  one folder per plugin, with its YAML and its own dependencies
```

## Decisions

- **Plugins load into the default load context** (`AssemblyLoadContext.Default`), with one
  `Resolving` handler per plugin folder (`AssemblyDependencyResolver`): a plugin's own dependencies
  (ImageSharp) come from its folder, NetDaemon's types always from the host. A separate context per
  plugin would load the plugin's copy of NetDaemon.AppModel — `[NetDaemonApp]` would be another type
  and no app would be found. Central package versions make conflicting dependency versions
  impossible anyway. (A per-plugin context stays the upgrade path for third-party plugins.)
- **Version gate:** a plugin built against another NetDaemon major.minor than the host is logged
  and skipped, never loaded half.
- **Namespaces stay unchanged** — YAML config keys and NetDaemon app ids are full type names
  (DataLogger's class lives in namespace `PVControl`; that stays too).
- **AppVersion per plugin** (`AppVersion.Of(assembly)`), the GitVersion target in
  `Directory.Build.targets` for every project.
- **DBs live in their plugin's folder**, next to its YAML (`apps/plugins/<Name>/`): a relative DB location
  resolves against the app's assembly folder (`AppFiles.PathOf`, Common). PVControl reads DataLogger's DB
  as `../DataLogger/energy_history.db`; its own `PVControlCommon` paths switch to `AppFiles.PathOf`.
  Deploy of a move: stop the add-on, copy the files and the DB (with any `-wal`/`-shm`) to the new folder,
  start; once the app is seen writing the new DB, delete the old folder.
- **Order:** HeatingTest first (independent: proves YAML under `apps/plugins/`, the version sensor
  and MQTT entities without risk to the battery), then DataLogger, PVControl last, then MidiControl.
- **Move-only commits** (`git mv`) separate from content edits, so `git log --follow` keeps history.
- **Safety tag** `before-plugins` before the first plugin move.

## Steps

- [x] 1. `Directory.Packages.props` (central package versions, one Microsoft.Data.Sqlite version
      for tools too) and `Directory.Build.props` (TargetFramework, Nullable, LangVersion)
- [x] 2. `src/Common` (Calculations, generic Extensions, PriorityCounter, AppVersion, SqliteSchema)
- [x] 3. `src/Host` + plugin loader (discovery, version gate, dependency resolving) with tests;
      `--plugins <folder>` to run a single plugin; the current apps still load as before
      (`src/Host`, moved there once all apps were plugins)
- [x] 4. Tag `before-plugins`; move HeatingTest → plugin (`src/HeatingTest`, `src/AppPlugin.props`; the host publishes plugins into `apps/plugins/<Name>/`), deploy, verify (version sensor, entities)
- [x] 5. DataLogger → plugin (its DB into `apps/plugins/DataLogger/`; PVControl, still in the host, reads it there), deploy, verify (next hourly row)
- [x] 6. PVControl → plugin (cost log `energy_costs.db` in its folder, the DataLogger's DB as `../DataLogger/`), deploy mid-quarter with low import, verify: `sensor.pv_control_version`,
      health ok, `binary_sensor.pv_control_inverter_in_sync` on, the EV's `charge_now` present,
      `sensor.pv_control_mode` attributes after one cycle. If it fails to load, HA degrades safely
      (mode-8 run time lapses to Self Use, Manual Mode reverted after 5 min, health reports
      "not running").
- [x] 7. MidiControl → plugin; ImageSharp out of everything else (its fonts and icons still come from the absolute
      `/config/netdaemon5/...` paths: move them into its plugin folder when MIDI is revived). SQLite stays in the host through Common
      (`SqliteSchema`), so every plugin shares the host's SQLite; linq2db is PVControl's own. `PluginLoader` also
      resolves a plugin's own native libraries (`ResolvingUnmanagedDll`)
- [x] Tests split: `tests/<Project>.Tests` (shared `tests/Tests.props`); PVControl.Tests also references DataLogger
      (the DB contract it reads)
- [x] 8. `deploy/` (`deploy.py apps` / `ha`, machine details in the uncommitted `deploy/local.json`; `HA/` moved to
      `src/PVControl/HA`), LoadBacktest references PVControl, launch settings (`src/Host/Properties`, VS Code),
      devcontainer restores the solution
- [ ] 9. READMEs: root = umbrella (what's here, run one project alone, deploy, the plugin contract);
      one per project (purpose, build/run/test alone, config, entities, data contracts — DataLogger's
      `hourly`/`daily` schema that PVControl reads — HA files); CLAUDE.md per project with its tests
