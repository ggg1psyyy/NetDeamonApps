# CLAUDE.md — DataLogger

Guidance for working on `src/DataLogger`; the repository-wide rules (tests for every change, one calculation one
function, documentation, HA config) are in the root `CLAUDE.md`.

**Data contract:** PVControl reads this app's DB (`hourly`/`daily`, from its plugin folder as
`../DataLogger/energy_history.db`). A schema change is a change for PVControl too: PVControl.Tests reference this
project and build the DB with its SQL — run them as well.

## Tests (`tests/DataLogger.Tests`)

- **`DataLoggerPeriodTests`** — DataLogger periods as UTC instants: both repeated hours in October, no row for the skipped March hour, 23/25-h days, backfill
- **`DataLoggerOperationTests`** — DataLogger schema v6/v7/v9: the period's value (`PeriodValue`: time-weighted mean, counter increase; `Number`: the one state parser), minutes in a state, state changes, time-weighted means, prevailing mode, the v6, v7 (heat and electricity per mode) and v9 (PVControl mode, inverter EEPROM writes) migrations
- **`AppFilesTests`** — the DB's default location in the plugin folder
