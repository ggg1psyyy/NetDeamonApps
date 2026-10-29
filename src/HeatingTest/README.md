# HeatingTest
Automated step test of the heat pump's space heating, to learn how the house reacts before any heating optimization is built. Fully independent of PVControl: own config (`HeatingTest.yaml`; `Enabled: false` → nothing runs at all), own HA entities (device "Heating Test"), own SQLite DB.

While enabled it logs the heat pump and every room controller every minute. While armed it runs one night at a time, alternating:
- **Test night:** at the start time the heating-curve level (`number.wp_heizkennlinie_niveau`) is lowered by the offset for the duration, then the original level is restored.
- **Reference night:** nothing is changed — the normal night the test nights are compared with.

A night only starts if the heat pump is in heating mode (`select.warmepumpe_modus` = `Warmwasser & Heizung`), heated the house ≥ 2 h in the last 24 h and it's ≤ 6 °C outside at the start time; otherwise it's skipped. A night ends early when disarmed or when the heat pump leaves heating mode; a test also when a room falls more than its `AbortBelowK` below its setpoint (bathroom 0.5 K, other rooms 1 K). The original level is kept in the DB and restored also after a NetDaemon restart; the app keeps setting it until the heat pump reports it back.

| Entity | Description |
|---|---|
| `switch.heating_test_armed` | Run test/reference nights (off by default); switching off ends a running test and restores the level |
| `number.heating_test_offset` | Level offset of a test night (default −3 K) |
| `time.heating_test_start` | Start of a night (default 22:00) |
| `number.heating_test_duration` | Length of a night (default 12 h) |
| `sensor.heating_test_status` | `Idle` / `Armed` / `Test` / `Reference` / `Restoring`; attributes: last decision (e.g. why a night was skipped), running run, original level, next kind, last runs |
| `sensor.heating_test_version` | Deployed build, also the "Firmware" of the Heating Test device |

**DB** (`apps/plugins/HeatingTest/heating_test.db`, in the plugin's folder next to its YAML; times as local ISO-8601 text with offset — open with DB Browser for SQLite, Python, Excel, …):

| Table | Content |
|---|---|
| `runs` | One row per night: `kind` (Test/Reference), `start`, `planned_end`, `actual_end`, `offset_k`, `original_level`, `restored`, `end_reason`, `outdoor_at_start` |
| `samples` | Every minute: `run_id`/`phase` (Test, Reference, Normal), outdoor (heat pump and house sensor), flow, flow setpoint, return, compressor %, power, COP, level, slope, heating/warm-water flags, heat pump mode |
| `room_samples` | Every minute per room: current temperature, setpoint, `hvac_action`, preset |

## Build, test, run

A NetDaemon app plugin (`src/AppPlugin.props`), loaded by the host from `apps/plugins/HeatingTest/`; its DB lies next to it.
Independent of every other app.

## Tests

`tests/HeatingTest.Tests` (xUnit, no HA needed):

```bash
dotnet test tests/HeatingTest.Tests
```

| Test class | What it covers |
|---|---|
| `HeatingTestTests` | Heating step test: start/skip/alternation, end/abort/disarm, SQLite log incl. restart recovery |
| `AppFilesTests` | The DB's default location: in the plugin's folder, next to its YAML |
