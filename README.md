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
- Runs a slot-by-slot simulation (`EnergySimulator`) to find when the battery will drop below the minimum SoC
- If grid charging is needed, schedules it at the cheapest available price window before PV takes over
- Optionally discharges to the grid opportunistically during high-price periods

PVControl itself only sets entity states — you use HA automations to act on them.

#### Main entity for automations

**`sensor.pv_control_mode`** — current inverter operating mode

| Value | Meaning |
|---|---|
| `automatic` | Default/transitional state |
| `normal` | Normal self-consumption operation |
| `force_charge` | Charge battery from grid — scheduled cheap window |
| `force_charge_grid_only` | Grid-charging with battery also powering house |
| `grid_only` | Grid powers house; battery discharge disabled (e.g. negative import price) |
| `force_discharge` | Opportunistic export to grid during high-price periods |
| `feedin_priority` | Feed all available energy to grid |
| `house_only` | Battery + PV power house only, no grid interaction |
| `reset` | Transient reset state |

#### Control entities

| Entity | Description |
|---|---|
| `select.pv_control_mode_override` | Manually override the current mode |
| `switch.pv_control_enforce_preferred_soc` | Keep SoC above preferred minimum at all times (grid backup mode) |
| `switch.pv_control_force_charge_at_cheapest_period` | Always charge at cheapest daily window, even if not strictly needed |
| `switch.pv_control_enable_opportunistic_export` | Enable opportunistic discharge to grid during high-price periods |
| `number.pv_control_preferredbatterycharge` | Preferred minimum SoC (%) |
| `number.pv_control_max_price_for_forcecharge` | Price ceiling (ct/kWh) for grid import during force-charge |
| `number.pv_control_forcecharge_target_soc` | Target SoC (%) when force-charging at cheapest period |

#### Status entities

| Entity | Description |
|---|---|
| `binary_sensor.pv_control_need_to_charge_from_grid_today` | Grid charging needed before next PV period |
| `binary_sensor.pv_control_battery_charging_enabled` | Whether battery charging is currently enabled |
| `sensor.pv_control_battery_status` | Battery status: `idle`, `charging`, `discharging`, `unknown` |
| `sensor.pv_control_active_network_price_period` | Current SNAP (Structured Network Access Pricing) period name |

#### Price entities

| Entity | Description |
|---|---|
| `sensor.pv_control_current_import_price_brutto` | Current gross import price (ct/kWh) incl. taxes, network, markup |
| `sensor.pv_control_current_export_price_brutto` | Current gross export/feed-in price (ct/kWh) |
| `sensor.pv_control_best_import_price_today` | Cheapest import price slot today (ct/kWh) |
| `sensor.pv_control_best_export_price_today` | Highest export price slot today (ct/kWh) |

#### Cost tracking entities

| Entity | Description |
|---|---|
| `sensor.pv_control_sum_import_cost_brutto` | Cumulative gross import cost (€) |
| `sensor.pv_control_sum_import_cost_energy_only` | Cumulative import cost — energy component only |
| `sensor.pv_control_sum_import_cost_network_only` | Cumulative import cost — network/grid fee only |
| `sensor.pv_control_sum_export_earnings_brutto` | Cumulative gross export earnings (€) |
| `sensor.pv_control_sum_import_export_net_cost` | Net cost = import − export (€) |
| `sensor.pv_control_battery_avg_cost_per_kwh` | Rolling average cost per kWh stored in battery |

#### Battery / forecast entities

| Entity | Description |
|---|---|
| `sensor.pv_control_battery_remainingenergy` | Estimated remaining usable battery energy (Wh) |
| `sensor.pv_control_battery_remainingtime` | Estimated time until battery depleted at current discharge rate |
| `sensor.pv_control_info_predicted_soc` | Full 48-h SoC timeline (JSON attribute, used for Plotly chart) |
| `sensor.pv_control_info_soc_snapshot` | Point-in-time SoC snapshot used for dashboard display |
| `sensor.pv_control_info_max_soc_today` | Predicted peak SoC today (%) |
| `sensor.pv_control_info_min_soc_today` | Predicted minimum SoC today (%) |
| `sensor.pv_control_info_max_soc_tomorrow` | Predicted peak SoC tomorrow (%) |
| `sensor.pv_control_info_min_soc_tomorrow` | Predicted minimum SoC tomorrow (%) |
| `sensor.pv_control_info_predicted_charge` | Predicted total charge energy today (Wh) |
| `sensor.pv_control_info_predicted_discharge` | Predicted total discharge energy today (Wh) |
| `sensor.pv_control_estimated_remaining_charge_today` | Remaining charge expected today (Wh) |
| `sensor.pv_control_estimated_charge_tomorrow` | Total charge expected tomorrow (Wh) |
| `sensor.pv_control_estimated_remaining_discharge_today` | Remaining discharge expected today (Wh) |
| `sensor.pv_control_estimated_discharge_tomorrow` | Total discharge expected tomorrow (Wh) |

#### Per schedulable load entities

For each load defined in YAML (e.g. `"EV Charger"` → slug `ev_charger`):

| Entity | Description |
|---|---|
| `select.pv_control_<slug>_mode` | Scheduling mode: `Off` / `Optimal` / `Priority` / `PriorityPlus` / `Emergency` |
| `number.pv_control_<slug>_target_level` | Target level (%, °C, …) to charge/heat to |
| `binary_sensor.pv_control_<slug>_charge_now` | Whether to run this load right now |
| `sensor.pv_control_<slug>_total_energy` | Cumulative energy consumed by this load (kWh) |
| `sensor.pv_control_<slug>_total_cost` | Cumulative cost for this load (€) |

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
| `SimulatorTests` | End-to-end `EnergySimulator.Simulate()` with injectable `SimulationInput` |
| `LoadSchedulingDecisionTests` | Schmitt-trigger start/keep semantics for schedulable load decisions |
| `RunningAvgCorrectionTests` | PV running-average correction: day-scale ratio + 4-slot near-term ramp |

---

## Development notes

- All dynamic HA entities (sensors, selects, numbers) are registered via MQTT (`IMqttEntityManager`), not static YAML
- App-specific configuration (entity IDs, thresholds) lives in each app's `.yaml` file
- `PVControlCommon` is a static singleton that holds shared dependencies; initialized once in the constructor
