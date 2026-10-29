# Host

The NetDaemon host ("shim"): starts NetDaemon (runtime, MQTT entity manager, scheduler, TTS) and loads the app
plugins. It has no apps of its own. The HA add-on starts `NetDeamonApps.dll`, so the assembly keeps that name.

## Plugins

`PluginLoader` (called from `program.cs`) loads at start:

- every `apps/plugins/<Name>/<Name>.dll` next to the host (where `dotnet publish src/Host` puts every `AppPlugin` of
  `Host.csproj`, with its YAML and its own dependencies),
- and every folder given with `--plugins <folder>`: a plugin folder, a folder of plugin folders, or a project's build
  output (`bin/Debug/net10.0` — its assembly is the one with a `.deps.json`); the YAML there is read too.

Each plugin's apps are registered like the host's own (`AddAppsFromAssembly`). Plugins load into the default load
context: NetDaemon's types, Common and SQLite always come from the host, so `[NetDaemonApp]` is the same type in every
plugin. What only a plugin has (ImageSharp, linq2db; native libraries under its `runtimes/`) is resolved from its
folder. A plugin built against another NetDaemon major.minor than the host is skipped, as is one that fails to load —
the host and the other plugins start anyway; the add-on log shows `Plugin <Name> loaded (<version>)` or why it was
skipped.

## Configuration

`appsettings.json` (not committed): the HA connection (host, port, token) and MQTT. `appsettings.Development.json` for
local runs (`ASPNETCORE_ENVIRONMENT=Development`). The apps' own settings are in each plugin's YAML.

## Tests

```bash
dotnet test tests/Host.Tests
```

| Test class | What it covers |
|---|---|
| `PluginLoaderTests` | Finding plugin folders (deployed, a folder given directly, a build output by its `.deps.json`), the NetDaemon version gate, `--plugins` arguments |

Loading a plugin into the running process is proven live (each plugin's `loaded` line and its entities), not in the
tests — it would load assemblies into the test runner's own process.
