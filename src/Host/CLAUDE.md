# CLAUDE.md — Host

Guidance for working on `src/Host`; the repository-wide rules (tests for every change, one calculation one
function, documentation, HA config) are in the root `CLAUDE.md`.

The host starts NetDaemon and loads the plugins (`PluginLoader`: default load context, a `Resolving` and a
`ResolvingUnmanagedDll` handler per plugin, the NetDaemon major.minor gate). It has no apps; the assembly stays
`NetDeamonApps` (the add-on starts that file). Why plugins load into the default context: `docs/restructure-plan.md`.

## Tests (`tests/Host.Tests`)

- **`PluginLoaderTests`** — the host's plugin loader: finding plugin folders, the NetDaemon version gate, `--plugins`
