# CLAUDE.md — HeatingTest

Guidance for working on `src/HeatingTest`; the repository-wide rules (tests for every change, one calculation one
function, documentation, HA config) are in the root `CLAUDE.md`.

Independent of every other app; `Enabled: false` in its YAML switches it off completely. Its DB lies in its plugin
folder (`DBLocation`, `AppFiles.PathOf`).

## Tests (`tests/HeatingTest.Tests`)

- **`HeatingTestTests`** — HeatingTest app: start/skip/alternation, end/abort/disarm (`HeatingTestLogic`), SQLite log and restart recovery (`HeatingTestDb` on a temp file)
- **`AppFilesTests`** — the DB's default location in the plugin folder
