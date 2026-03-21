# NetDeamonApps

NetDaemon4 home automation apps for Home Assistant, written in C# (.NET 9.0).

> Developed with assistance from [Claude Code](https://claude.ai/code) (Anthropic) — used throughout for architecture decisions, bug diagnosis, code generation, and test authoring.

---

## Apps

### PVControl
Optimizes battery usage and grid import costs using dynamic EPEX Spot pricing, PV forecasts (OpenMeteo), and historical load data.

The app runs a forward simulation every 15 minutes to predict battery SoC over the next 48 hours and decides the optimal inverter mode. All decisions are derived from this simulation — no separate heuristics.

**How it works:**
- Forecasts house load from historical SQLite data (weighted average)
- Fetches PV forecast from OpenMeteo HA entities
- Runs a slot-by-slot simulation (`PVSimulator`) to find when the battery will drop below the minimum SoC
- If grid charging is needed, schedules it at the cheapest available price window before PV takes over
- Optionally discharges to the grid opportunistically during high-price periods

PVControl itself only sets entity states — you use HA automations to act on them.

#### Main entities for automations

**`sensor.pv_control_mode`**
| Value | Meaning |
|---|---|
| `normal` | Normal operation |
| `force_charge` | Charge battery from grid now — cheapest window before SoC hits minimum |
| `grid_only` | Use grid only, disable battery discharge (active during negative prices) |

**`sensor.pv_control_run_heavyloads_now`**
| Value | Meaning |
|---|---|
| `Yes` | PV surplus expected — run heavy loads freely |
| `IfNecessary` | Marginal — only run essential loads |
| `No` | Battery predicted to drop below absolute minimum — defer loads |

#### Other useful entities

- **`binary_sensor.pv_control_need_to_charge_from_grid_today`** — whether grid charging is needed before the next PV period

#### Configuration entities

| Entity | Description |
|---|---|
| `select.pv_control_mode_override` | Manually override the current mode |
| `number.pv_control_preferredbatterycharge` | Preferred minimum SoC (%) |
| `switch.pv_control_enforce_preferred_soc` | Keep SoC above preferred minimum at all times (grid backup mode) |
| `switch.pv_control_force_charge_at_cheapest_period` | Always charge at cheapest daily window, even if not strictly needed |
| `number.pv_control_max_price_for_forcecharge` | Price ceiling (ct/kWh) for opportunistic force-charge |
| `number.pv_control_forcecharge_target_soc` | Target SoC (%) when force-charging at cheapest period |

Most remaining entities are informational (simulation timeline, price list, SoC forecast, etc.).

---

### DataLogger
Logs energy sensor history to a local SQLite database at regular intervals. The data is used by PVControl's load prediction and is intended as a training dataset for future ML-based load forecasting.

---

### MidiControl
Integrates a BCF2000 MIDI controller with Home Assistant — maps faders/buttons to HA entities and renders live state back to the controller's display. Side project, low priority.

---

## Build & Deploy

```bash
dotnet build                  # Debug build
dotnet build -c Release       # Release build
dotnet publish -c Release     # Publish for deployment
```

Copy the published output to `/config/netdaemon4` on your Home Assistant instance (NetDaemon4 add-on) or your custom deployment folder.

For local development, set `ASPNETCORE_ENVIRONMENT=Development` and configure `appsettings.json` with your HA host, port, and token.

## Tests

The xUnit test project lives at `NetDeamonApps.Tests/` and covers pure-logic components without needing a live HA connection:

```bash
dotnet test NetDeamonApps.Tests/
```

| Test class | What it covers |
|---|---|
| `PredictionContainerTests` | 192-slot 15-min window validation (`DataOK`) |
| `MidnightRolloverTests` | Load data staleness detection after midnight |
| `SimulatorTests` | End-to-end `PVSimulator.Simulate()` with injectable inputs |

---

## Development notes

- All dynamic HA entities (sensors, selects, numbers) are registered via MQTT (`IMqttEntityManager`), not static YAML
- App-specific configuration (entity IDs, thresholds) lives in each app's `.yaml` file
- `PVControlCommon` is a static singleton that holds shared dependencies; initialized once in the constructor
