# NetDeamonApps

NetDaemon 6 home automation apps for Home Assistant, written in C# (.NET 10.0): a small host that loads any number
of app plugins, one plugin per app — each its own project, buildable and testable on its own.

> Developed with assistance from [Claude Code](https://claude.ai/code) (Anthropic) — used throughout for architecture decisions, bug diagnosis, code generation, and test authoring.

---

## What's here

| Project | What it is |
|---|---|
| [`src/Host`](src/Host/README.md) | The host: starts NetDaemon and loads the app plugins (`NetDeamonApps.dll`, what the add-on starts) |
| [`src/Common`](src/Common/README.md) | Shared code: calculations, HA helpers, SQLite schema helper, app files and version |
| [`src/PVControl`](src/PVControl/README.md) | Solar/battery optimisation with dynamic EPEX Spot prices, load and PV forecasts, extra loads (EV) and warm water |
| [`src/DataLogger`](src/DataLogger/README.md) | Logs energy sensor history hourly/daily to SQLite (PVControl's load forecast reads it) |
| [`src/HeatingTest`](src/HeatingTest/README.md) | Automated step test of the heat pump's heating curve, with its own SQLite log |
| [`src/MidiControl`](src/MidiControl/README.md) | BCF2000 MIDI controller integration (side project) |
| `tests/<Project>.Tests` | xUnit tests per project |
| [`deploy/`](deploy/README.md) | Deploy to the NetDaemon add-on and PVControl's HA automations |
| [`tools/LoadBacktest`](tools/LoadBacktest/README.md) | Offline backtest of load forecast models |
| [`docs/`](docs/restructure-plan.md) | The host/plugin restructure plan |

Deployed (`/config/netdaemon6`):

```
NetDeamonApps.dll …             the host
apps/plugins/<Name>/<Name>.dll   one folder per plugin: its YAML, its DB(s), its own dependencies
```

## Build & Deploy

```bash
dotnet build                          # Debug build (whole solution)
dotnet build -c Release               # Release build
dotnet publish src/Host -c Release    # Publish the host with every app plugin (apps/plugins/<Name>/)
dotnet test                           # All tests (one project: dotnet test tests/PVControl.Tests)
```

Deploy with `python deploy/deploy.py apps` (see [`deploy/README.md`](deploy/README.md)), or copy `src/Host/bin/Release/net10.0/publish/` to `/config/netdaemon6` on your Home Assistant instance (NetDaemon 6 add-on) or your custom deployment folder.

The build gets its version from git: `0.5.<commit count>+<short hash>`, with `.dirty` appended when it was built with uncommitted changes (e.g. `0.5.233+1bdea38`, `0.5.233+1bdea38.dirty`). After a deploy, `sensor.pv_control_version` / `sensor.heating_test_version` and the devices' "Firmware" show it; their history shows every deploy. Building needs `git` on the path, otherwise the version is `1.0.0`.

For local development, set `ASPNETCORE_ENVIRONMENT=Development` and configure `src/Host/appsettings.json` with your HA host, port, and token.

### Run one plugin alone

```bash
dotnet build src/HeatingTest
dotnet run --project src/Host -- --plugins "$PWD/src/HeatingTest/bin/Debug/net10.0"
```

(A full path: `dotnet run` starts the host in `src/Host`, where it finds `appsettings.json`.) `--plugins <folder>` loads a plugin's build output (or a folder of plugin folders) in addition to the deployed
`apps/plugins/`; its YAML is read from there. Careful: a local run against the live Home Assistant runs the apps a
second time next to the add-on — PVControl would then command the inverter twice.

## Writing a plugin

1. `src/<Name>/<Name>.csproj` importing `../AppPlugin.props` (NetDaemon packages, Common, `EnableDynamicLoading`,
   YAML copied to the output); add own packages to it — they stay in the plugin's folder.
2. Apps as usual: a class with `[NetDaemonApp]`, its config from `<Name>.yaml` (`IAppConfig<T>`).
3. Files the app keeps (DBs): a relative location resolved with `AppFiles.PathOf(location, GetType().Assembly)` lies
   in the plugin's folder, next to its YAML.
4. Add it as `<AppPlugin Include="..\<Name>\<Name>.csproj" />` in `src/Host/Host.csproj` (published into
   `apps/plugins/<Name>/`), its tests as `tests/<Name>.Tests` on `../Tests.props`, both to `NetDeamon.sln`.

The host provides NetDaemon, Common and SQLite; a plugin built against another NetDaemon major.minor is skipped
(logged). Details: [`src/Host/README.md`](src/Host/README.md).

## Development notes

- All dynamic HA entities (sensors, selects, numbers, switches, times) are registered via MQTT (`IMqttEntityManager`), not static YAML
- App-specific configuration (entity IDs, thresholds) lives in each app's `.yaml` file
- Central package versions in `Directory.Packages.props`; shared build settings and the git version in `Directory.Build.props` / `.targets`
