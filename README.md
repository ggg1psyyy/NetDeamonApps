# NetDeamonApps

NetDaemon 6 home automation apps for Home Assistant, written in C# (.NET 10.0).

> Developed with assistance from [Claude Code](https://claude.ai/code) (Anthropic) — used throughout for architecture decisions, bug diagnosis, code generation, and test authoring.

---

## Apps

### PVControl
Optimizes battery usage and grid import costs using dynamic EPEX Spot pricing, PV forecasts (OpenMeteo), and historical load data.

Every 15 seconds the app runs a forward simulation in 15-minute slots (now → end of tomorrow) to predict battery SoC and decide the inverter mode. All decisions are derived from this simulation — no separate heuristics.

**How it works:**
- Forecasts house load from historical SQLite data (weighted average)
- Fetches PV forecast from OpenMeteo HA entities, corrected by today's actual PV (day ratio) and, for the next hour, by the live PV relative to what the forecast expected in the last 15 min (`tools/PVNearTermBacktest`)
- Runs a slot-by-slot simulation (`EnergySimulator`) to find when the battery will drop below the minimum SoC
- If grid charging is needed, charges just enough to clear the coming trough, in the cheapest quarter-hour plus the cheapest quarters under the max force-charge price — so a larger need moves earlier into a cheap band instead of spilling into expensive slots after it
- Optionally discharges to the grid opportunistically during high-price periods
- Schedules extra loads such as an EV charger (see [Schedulable loads](#schedulable-loads))

PVControl itself only sets entity states — you use HA automations to act on them.

Design details live in [`apps/PVControl/ARCHITECTURE.md`](apps/PVControl/ARCHITECTURE.md).

#### Main entity for automations

**`sensor.pv_control_mode`** — current inverter operating mode

| Value | Meaning |
|---|---|
| `automatic` | Default/transitional state |
| `normal` | Normal self-consumption operation |
| `force_charge` | Charge battery from grid — scheduled cheap window |
| `force_charge_grid_only` | Negative import price: PV disconnected, grid charges the battery and powers the house |
| `grid_only` | Grid powers the house, battery idle — holds the battery at its minimum until the scheduled charge window, or while a PriorityPlus session that needs grid runs without enough PV (`charge_Reason` `LoadFromGrid`) |
| `force_discharge` | Opportunistic export to grid during high-price periods |
| `feedin_priority` | Feed all available energy to grid |
| `reset` | Transient reset state |

Whether PV surplus may currently be exported is a separate, orthogonal decision — see
`binary_sensor.pv_control_export_allowed` below. It's independent of `sensor.pv_control_mode`
so a cheap grid-charge or safety-net top-up can still run even while export is suppressed
(e.g. during a negative export price window).

Before negative export prices later the same day, PV surplus is fed in while the price is still
positive (battery charging off, reason `RoomForNegativeExport`) as long as 70 % of the PV surplus
forecast in the negative hours would fill the battery anyway — the battery then charges in the
negative hours instead of the PV being curtailed. Independent of opportunistic export.

#### Control entities

| Entity | Description |
|---|---|
| `select.pv_control_mode_override` | Manually override the current mode |
| `switch.pv_control_enforce_preferred_soc` | Keep SoC above preferred minimum at all times (grid backup mode) |
| `switch.pv_control_force_charge_at_cheapest_period` | Always charge at cheapest daily window, even if not strictly needed |
| `switch.pv_control_enable_opportunistic_export` | Enable opportunistic discharge to grid during high-price periods |
| `number.pv_control_preferredbatterycharge` | Preferred minimum SoC (%) |
| `number.pv_control_max_price_for_forcecharge` | Price ceiling (ct/kWh) for grid import during force-charge; also the "cheap grid" threshold for PriorityPlus loads |
| `number.pv_control_forcecharge_target_soc` | Target SoC (%) when force-charging at cheapest period |

#### Status entities

| Entity | Description |
|---|---|
| `binary_sensor.pv_control_need_to_charge_from_grid_today` | Grid charging needed before next PV period |
| `binary_sensor.pv_control_battery_charging_enabled` | Whether battery charging is currently enabled |
| `binary_sensor.pv_control_export_allowed` | Whether PV surplus may currently be exported to the grid (off during negative export price) |
| `sensor.pv_control_battery_status` | Battery status: `idle`, `charging`, `discharging`, `unknown` |
| `sensor.pv_control_active_network_price_period` | Current network price override period name (e.g. SNAP in summer, WiNAP in winter), or "Standard" |
| `sensor.pv_control_version` | Deployed build (see [Build & Deploy](#build--deploy)), also the "Firmware" of the PV Control device |
| `sensor.pv_control_health` | `ok` / `degraded` / `error` — see [Health](#health) |
| `sensor.pv_control_load_forecast` | Coming night's load (22:00–08:00, kWh) by the weather-aware model — see [Load forecast](#load-forecast) |

Grid charge for the minimum (`need_to_charge_from_grid_today`): the target for the low before PV takes over is the minimum SoC + a forecast margin (+1 % per 2 h until that low, at most 4 %) + 1 % — e.g. minimum 20 %, low 17 h away: 25 %. A charge starts when the low would fall 2 % below that target (never below the minimum itself) and charges just up to it, in the cheapest quarters before the low — or the minimum is held with `grid_only` when charging isn't cheaper than buying the load. A running charge continues as long as starting one would begin it. Before a PV peak on the way to the low, the charge only fills the room PV leaves (100 % − that peak) and doesn't start for 2 % or less; the rest follows after the peak. Above `number.pv_control_max_price_for_forcecharge` a grid charge only protects the minimum itself (up to minimum + 1 %), never the forecast margin alone. `sensor.pv_control_mode` has the reason in `charge_Reason`, the running or next charge quarter in `next_charge_window_start/end`.

#### Load forecast

The house load forecast is the hour-of-day average of the last days (recent days weighted more). With `LoadForecast:` in `PVControl.yaml` a weather-aware model is added: base load (that average without the heat pump, plus half of its deviation over the last 48 h) plus the heat pump's space heating from the outside temperature forecast (Pirate Weather hourly) — learned from DataLogger's history as an energy signature (Wh per heating degree below 13 °C, per hour of the day). The heat pump mode decides whether it heats at all. In the backtest over the last year it missed the night load 15 % less than the average, in winter 20 % less (`tools/LoadBacktest/README.md`).

`Mode: Model` (configured) makes PVControl use it, with the average as fallback when the weather forecast or history is missing; `Mode: Shadow` (code default) computes it next to the average without using it. `sensor.pv_control_load_forecast` shows tonight's forecast and its parts (`average_kwh`, `night_base_kwh`, `night_heat_pump_kwh`, `night_temp24`, `heating_on`, `heat_pump_scale`, `source`, `used_by_pvcontrol`), the next 24 h of both forecasts (`next_24h`), and the nights scored so far (`nights`, `model_mae_kwh` vs `average_mae_kwh`) — the comparison keeps running in both modes.

#### Health

`sensor.pv_control_health` sums up the checks below every 15-s cycle. A problem counts after 30 s (a single 15-s sensor blip doesn't) and until it has been gone for 1 min; the state is the worst counting problem.

| Check | `degraded` | `error` |
|---|---|---|
| Battery SoC | unknown, last value used (≤ 2 min) | unavailable > 2 min → inverter in normal mode |
| Inverter data (run mode, battery/grid/PV power, house load) | unknown | unknown > 2 min |
| Import prices | first configured source has no data / tomorrow's prices missing after 16:00 | no price for now |
| PV forecast, load prediction | a PV forecast source unavailable | prediction invalid |
| 15-s cycle | one failed | 3+ failed in a row |
| Warm water | heat pump entities unavailable (control paused) | — |
| DataLogger | newest hourly row older than 2 h | — |
| Weather forecast (load model) | none yet or older than 3 h | — |
| Load forecast (`Mode: Model`) | fell back to the average | — |
| Import cap | first import counter failed (fallback in use) / a quarter over the cap in the last hour | no import counter works |
| Inverter sync | inverter not in the commanded mode for 2 min | HA reconciler stopped retrying |

Attributes: `summary` (the problems in one line), `issues` (`key`, `level`, `message`, `since`), `last_cycle`, `version`. If NetDaemon itself stops, the sensor keeps its last state — `last_cycle` then gets old. The automation [`HA/Health.yaml`](HA/Health.yaml) notifies on every level change (PVControl publishes the attributes before the level, so the message carries the matching summary; after a restart the issues that counted are restored, so a lasting problem doesn't read "ok" first), when PVControl stopped running (`last_cycle` older than 2 min) and when it runs again (ntfy to `notify.ha_warnings`; replace it with your notify action).

#### Import cap

The network power price (from 2027) counts the month's highest 15-min mean grid import; above 10 kW it costs extra. PVControl keeps every 15-min block (clock quarters, as the meter bills them) under `MaxGridImportW` (9.8 kW):

1. **Battery grid charging is throttled first:** the battery power of a grid charge in `sensor.pv_control_mode` (`battery_power_w`) is what the battery may charge so the quarter's mean stays under the cap, in whole amps × battery voltage (lowered when needed, raised only by 2 A or back to the maximum).
2. **Then the extra loads are paused** (`charge_now` off, reason "Import cap: paused"), until there is room for them again with a margin. Their plan stays. [`HA/CarCharger.yaml`](HA/CarCharger.yaml) switches the EV on `charge_now`.
3. **Then warm-water heatings wait** until there is room for the heat pump.

Nothing reacts instantly (automation delays such as the EV's 1 min, inverter and car ramps), so the guard assumes the current power keeps flowing for `ImportCapReactionSeconds` (90 s) after any change. A change late in a quarter only takes effect in the next one. The plan respects the cap too: grid charging only gets what the load leaves under it, so NeedToCharge takes more cheap quarters at lower power, and PriorityPlus never plans a load over the cap.

The import is counted from `TotalImportEnergyEntities` in list order. The first counter (the utility meter via wM-Bus) is used while it works. The next one (the inverter) takes over only while the first is unavailable, or frozen while the next keeps counting. A counter that doesn't change is no failure: no import.

| Entity | Content |
|---|---|
| `sensor.pv_control_mode` (command attributes) | The mode is also the inverter command; attributes `battery_power_w` (+ charge, − discharge: hardware maximum × `BatteryNominalVoltageV`, lower only while the import cap limits below what the path charges), `bulk` (grid charge at full power, see below), `charge_energy_wh`, `charge_target_soc`, `charge_start`, `charge_until` (the running grid charge in the plan, else the next one: energy into the battery, the SoC it reaches, its slots), `vpp_charge_limit_w` (what a VPP charge reaches at the current SoC), `seq` (version, grows with every change), `issued` (when `seq` last changed), `import_cap_reason`, `cap_kw`, `allowed_kw` (mean allowed for the rest of the quarter), `extra_loads_paused`, `headroom_w` (room for a new load), `voltage_v` (the nominal voltage used) |
| `sensor.pv_control_vpp_charge_limit` | Battery charge power a remote-controlled (VPP) grid charge reaches at the current SoC (W); attributes `at_0_pct_w`, `slope_w_per_pct`, `learned`, `at_50_pct_w`, `buckets` (the learned readings, restored after a restart) |
| `sensor.pv_control_import_quarter` | The running quarter's mean import if the current power continues (kW); attributes `quarter_start`, `imported_kwh`, `complete` (tracked from its start), `counter` (the counter in use) |
| `sensor.pv_control_import_month_peak` | The month's highest 15-min import so far (kW); attribute `quarter_start`. After a restart restored from this sensor and the DataLogger's `maxquarterimport` (the higher one) |

| Setting | Meaning |
|---|---|
| `MaxGridImportW` | Highest mean grid import of any 15-min block, W; 0 = off |
| `ImportCapReactionSeconds` | How long a requested change takes to show at the meter, s |
| `TotalImportEnergyEntities` | Grid import counters (kWh) in priority order (also the cost log's import) |
| `BatteryVoltageEntity` | Battery voltage while charging: W per A of the charge current (230 V without it) |
| `MaxBatteryChargeCurrrentValue` | Hardware charge current limit, A |
| `InverterSyncEntity` | HA's feedback whether the inverter runs the command (`binary_sensor.pv_control_inverter_in_sync`) |
| `VppChargePowerAt0PctW`, `VppChargePowerSlopeWPerPct` | Default line of the VPP charge power by SoC (W = At0 + Slope × SoC) until the curve is learned |
| `BatteryNominalVoltageV` | Voltage for the command's battery power (A × V) |

#### Inverter control (HA side)

PVControl only publishes `sensor.pv_control_mode` (state = mode, attributes = how to run it); all inverter writes are HA automations, so another inverter only needs another script. For the SolaX X3-Hybrid G4 the modes run in RAM as VPP mode 8 ("Individual Setting – Duration Mode") through the solax-modbus integration's `*_direct` entities — no EEPROM writes for mode changes or charge power. The inverter limits VPP charging, falling with the SoC (~5.1 kW at 20 %, ~3.3 kW at 63 %; Manual Force Charge ~7.45 kW); PVControl learns that curve from VPP charges that run below the command (`sensor.pv_control_vpp_charge_limit`). The plan chooses the path when a grid charge starts: VPP if it puts the energy to the target into the battery within the quarters that cost the same — with hourly prices up to the rest of the hour, with quarter-hourly prices usually one quarter — else `bulk`: Manual Mode + Force Charge (30 A), two EEPROM writes per charge session. A planned charge may start anywhere in its price block, but not so late that it needs full power where starting earlier in the same block (same price) would have stayed on VPP. The plan models the battery at the path's power (VPP by the curve, PV included). A `force_charge` set with `select.pv_control_mode_override` is always bulk; `bulk` is off while the import cap throttles the charge (Manual has no power setting), and `force_charge_grid_only` is never bulk. A started grid charge runs at least 1 minute unless the user changes the mode.

| File | Content |
|---|---|
| [`HA/InverterHelpers.yaml`](HA/InverterHelpers.yaml) | Package: applied `seq`, applied time, re-apply counter, blocked flag, `binary_sensor.pv_control_inverter_in_sync` |
| [`HA/ApplyMode.yaml`](HA/ApplyMode.yaml) | `script.pv_control_apply_mode`, the only writer of the inverter state: base work mode (only when it changes), Manual Force Charge for `bulk`, else the mode 8 block (PV limit, battery push power, run time 1200 s, NextMotion back to the base mode) or VPP off; records the applied `seq` |
| [`HA/InverterSync.yaml`](HA/InverterSync.yaml) | Reconciler: applies a new `seq`, re-applies when the inverter left the commanded state (at most 5 times per version, then blocked + notification), refreshes the mode-8 run time after 600 s; only while PV Control runs (mode 8 then returns to Self Use by itself; Manual Mode is switched back to Self Use after 5 min without PV Control) |
| [`HA/Mode.yaml`](HA/Mode.yaml) | Notifications and the extras of `reset` |
| [`HA/Battery.yaml`](HA/Battery.yaml) | Charging on/off: 30 A / 0 A |

| PVControl mode | Inverter |
|---|---|
| `normal`, `reset` | Self Use, VPP off |
| `feedin_priority` | Feedin Priority, VPP off |
| `force_charge`, `bulk` | Manual Mode + Force Charge (30 A), VPP off |
| `force_charge` | Self Use + mode 8: PV unlimited, battery charges at `battery_power_w` (the inverter limits it by the SoC) |
| `force_charge_grid_only` | Self Use + mode 8: PV 0 W, battery charges at `battery_power_w` (the inverter limits it by the SoC) |
| `grid_only` | Self Use + mode 8: PV unlimited, battery 0 W (held) |
| `force_discharge` | Self Use + mode 8: battery discharges at `battery_power_w` |

#### Price entities

| Entity | Description |
|---|---|
| `sensor.pv_control_current_import_price_brutto` | Current gross import price (€/kWh) incl. taxes, network, markup; `data` attribute holds the full price list |
| `sensor.pv_control_current_export_price_brutto` | Current gross export/feed-in price (€/kWh); `data` attribute holds the full price list |
| `sensor.pv_control_best_import_price_today` | Cheapest import price slot today (€/kWh) |
| `sensor.pv_control_best_export_price_today` | Highest export price slot today (€/kWh) |
| `sensor.pv_control_price_publication` | When tomorrow's prices arrived today (`HH:mm`, `pending`, or `published` after a restart); attributes `prices_until` and `days` (the last 14 days' publication times) |

Price sources (`CurrentImportPriceEntities`, ha_epex_spot sensors) are all equal: PVControl uses the one whose price data reaches furthest, so whichever publishes tomorrow first wins (on a tie the earlier one in the list). `ImportPriceResolution` applies to every source — with `Hourly`, a 15-min source gives the same hourly prices as an hourly one. A configured source without any data shows up as `price_source` in the health sensor.

The sources are the integration's **market price** sensors (raw EPEX price; its surcharge and tax stay 0 — PVControl adds markup, network fee and tax itself). The integration polls its sources on its own schedule; [`HA/PriceFetch.yaml`](HA/PriceFetch.yaml) calls `epex_spot.fetch_data` every 5 min from 12:45 until one source reaches the end of tomorrow, so the prices arrive minutes after the auction results instead of in the afternoon.

The simulation plans until the end of tomorrow, but tomorrow's EPEX prices only arrive in the afternoon. Until then the missing hours are estimated with the price of the same hour a day earlier (the daily pattern repeats well enough; estimates are never negative). Estimates carry a risk premium (`EstimatedPricePremium`, default 5 %: import × 1.05, export × 0.95), so an estimated slot only wins over a published one when it's clearly better. Price-driven commitments — EV session starts — still only use published prices.

#### Cost tracking entities

| Entity | Description |
|---|---|
| `sensor.pv_control_sum_import_cost_brutto` | Cumulative gross import cost (€) |
| `sensor.pv_control_sum_import_cost_energy_only` | Cumulative import cost — energy component only |
| `sensor.pv_control_sum_import_cost_network_only` | Cumulative import cost — network/grid fee only |
| `sensor.pv_control_sum_export_earnings_brutto` | Cumulative gross export earnings (€) |
| `sensor.pv_control_sum_import_export_net_cost` | Net cost = import − export (€) |
| `sensor.pv_control_battery_avg_cost_per_kwh` | Average cost per kWh stored in the battery: each charged kWh split by source (PV free, grid at the import price), blended into the stored energy — the same calculation as the loads' `total_cost` |

#### Battery / forecast entities

| Entity | Description |
|---|---|
| `sensor.pv_control_battery_remainingenergy` | Estimated remaining usable battery energy (Wh) |
| `sensor.pv_control_battery_remainingtime` | Estimated minutes until the battery is full or depleted at the current rate |
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
| `select.pv_control_<slug>_mode` | Scheduling mode: `Off` / `Emergency` / `Optimal` / `Priority` / `PriorityPlus` (only if `Mode` isn't set in YAML) |
| `number.pv_control_<slug>_target_level` | Target level (%, °C, …) to charge/heat to (only if `TargetLevel` isn't set in YAML) |
| `time.pv_control_<slug>_ready_by` | "Ready by" time of day (needs HA ≥ 2026.5 for MQTT time entities) |
| `switch.pv_control_<slug>_enforce_ready_by` | Enables the ready-by deadline (off by default) |
| `number.pv_control_<slug>_energy_per_level` | Energy per level unit (kWh/% …) at the meter, charger losses included — learned, editable |
| `number.pv_control_<slug>_charge_power` | Typical power while running (W) — learned, editable |
| `binary_sensor.pv_control_<slug>_charge_now` | Whether to run this load right now — the entity your automation acts on |
| `sensor.pv_control_<slug>_total_energy` | Cumulative energy consumed by this load (kWh; needs `ActualEnergyEntity`) |
| `sensor.pv_control_<slug>_total_cost` | Cumulative cost for this load (€; needs `ActualEnergyEntity`) |

Attributes of `charge_now`: `charge_status_reason`, `mode`, `target_level`, `current_level`, `power_avg_w`, `net_pv_w`, `charge_start_predicted`, `charge_end_predicted`, `charge_end_predicted_soc`, `planned_sessions` (current/next session plus planned follow-ups), `zz_charge_timeline` (prefixed so it sorts last in the HA GUI), `ready_by`, `energy_per_level_kwh`, `learned_power_w`, `last_plan`, `last_plan_reason`.

#### Schedulable loads

Extra loads (the EV charger) are scheduled by simulating them as an extra load in the house simulation and checking the result. Warm water has its own planner (see below).

**Modes** form a ladder — each mode runs its own ruleset plus all lower ones, bottom up, and the first one that allows charging wins:

| Mode | Behaviour |
|---|---|
| `Off` | Never runs |
| `Emergency` | Always runs now, no simulation |
| `Optimal` (10) | Only on PV surplus: the house battery must still reach ~100 % today, hold its overnight minimum, and no grid is needed |
| `Priority` (20) | May draw the house battery down to its minimum, but never causes grid use. Evening/night sessions start as late as possible before sunrise; a running session stops at dusk |
| `PriorityPlus` (30) | Like Priority, plus grid use when it's cheap (day or night) |

The house battery minimum follows `switch.pv_control_enforce_preferred_soc` in every mode (preferred minimum when on, absolute minimum when off). Grid use is compared by energy: any 15-min slot where the load adds grid import counts, at that slot's price — including the grid covering the house while the battery is held at its minimum.

**PriorityPlus price rule:** every 15-min start is simulated; a start passes when the load adds no grid import, or only at or below `number.pv_control_max_price_for_forcecharge`. Of the passing starts, the earliest wins unless a later one is at least 1 % cheaper overall (extra import cost minus lost export earnings — battery energy the load uses tonight is refilled by tomorrow's PV that would otherwise be exported — plus the round-trip loss of extra battery use). Each start is supplied like Priority — PV and the house battery — if that needs no extra grid (e.g. a ready-by target in the morning from the battery). Once it needs grid energy, the session takes it directly from the grid, not via the house battery (wear, losses): while it runs and PV doesn't cover house + load, the battery is held (`grid_only`, PV surplus feeds the load first) — or, if the house charges from the grid in that quarter anyway, the load runs along the `force_charge`. If the cheap window is shorter than the session, so no full-length start passes, the load charges just that window (the earliest shortened session that passes, at least `MinWindowMinutes`); the rest follows later. A planned later start sticks while it stays cheaper than starting now, so forecast noise can't flip it; a running session is never stopped to wait for a cheaper start. Follow-up sessions up to the target are planned as a preview (`planned_sessions`).

**Ready-by deadline** (PriorityPlus only, when enforced): only starts that finish by the next occurrence of the ready-by time count, and the deadline beats the price threshold — if no start passes, the same choice is made among all starts that still finish in time (the earliest unless a later one saves at least the minimum, a running session keeps running, last cycle's plan stays unless clearly beaten); if even now is too late, it starts now. A session running directly from the grid stays on the grid until it ends.

**Latches** against on/off cycling: once started, a session runs at least `MinWindowMinutes`; once stopped, it restarts no earlier than `MinOffMinutes` later (stops themselves are never delayed). Both are bypassed by a mode change or a change of the settings the decision depends on (minimum SoC, enforce switch, price threshold, target, ready-by); the off-latch also by an availability change.

**Replanning:** the house simulation and inverter mode update every 15 s, but a load's schedule search (the expensive part) only runs every `ReplanIntervalMinutes` or on a trigger — any setting change, availability change, new prices, a new quarter-hour, house battery SoC or the load's level changing, the load starting/stopping, the house load staying > 500 W and > 30 % off its forecast for 5 min, or a latch ending.

**Learning:** while the load draws power, `energy_per_level` is learned from the energy meter between whole level steps (at least 3 levels within one session, weighted against the stored value as if it had been measured over 20 levels), and `charge_power` follows the measured power (10-min average, only after 2 min of continuous draw). Both survive restarts and can be set by hand, e.g. after installing a new wallbox.

**YAML configuration** (`PVControl.yaml` → `SchedulableLoads`):

| Key | Meaning |
|---|---|
| `Name`, `Priority` | Display name (→ entity slug) and priority (higher is planned first) |
| `Mode`, `TargetLevel` | Fixed mode / target; omit to get the HA select / number entity |
| `CurrentLevelEntity` | Sensor with the current level (EV SoC %, water temperature, …) |
| `ActualPowerEntity`, `ActualEnergyEntity` | Measured power (W) and energy counter (kWh) of the load — used for learning and cost tracking |
| `MinActivePowerW` | Power above which the load counts as running |
| `AvailabilityEntity` | Optional: only plan while this is on (e.g. car at home) |
| `HistoryDbColumn` | DataLogger column with this load's history, subtracted from the house load forecast |
| `EnergyPerLevelUnitKwh`, `AvgPowerW` | Starting values for the learned energy per level and charge power |
| `MinWindowMinutes`, `MinOffMinutes` | Minimum on-time (also the shortest window worth starting) and minimum off-time |
| `ReplanIntervalMinutes` | Max minutes between schedule searches (default 5) |
| `Icon`, `ChargeNowDeviceClass`, `LevelUnit`, `TargetLevelMin/Max/Step/Default` | Display settings of the auto-created entities |

#### Warm water

The heat pump heats the warm-water tank with one-time heatings at the cheapest times that keep the comfort periods warm (e.g. ≥ 42 °C from 06:30 for 1 h and from 19:00 for 2 h); outside them the tank may cool down. In `Auto`, PVControl switches the one-time heating on, and off again once the heat pump has reached the setpoint (its switch stays on otherwise). A heating that runs is never stopped — compressor starts wear the heat pump — the plan may only raise its setpoint; a new planned run needs ≥ 3 K of rise and 60 min since the last heating ended. Set the heat pump's own warm-water setpoint low (e.g. 20 °C) as a safety net before switching to `Auto`.

How it decides: a tank model learned daily from 10 days of HA history (heating ~17 K/h, standby loss growing with the temperature, ~1.7 kW) and what a kWh of heating costs in every 15-min slot according to the house simulation (PV surplus = lost export, battery energy = what refilling it costs, otherwise the import price). A dynamic program picks the cheapest heating slots. Heating early to a higher temperature (up to 57 °C) only wins when the price difference beats the extra heat loss — measured, a tank at 57 °C loses about twice as much per hour as one at 42 °C. The heat pump's efficiency follows the outside temperature (air/water, ~3 % per K, `EfficiencyChangePerK`, from the weather forecast of `LoadForecast`), so a warm afternoon counts cheaper than a cold evening. Hours without a published price yet (tomorrow, before ~13:00) are priced with estimates (see "Price entities"); the plan sensor shows `prices_known_until` and `outside_efficiency`.

| Entity | Description |
|---|---|
| `select.pv_control_warm_water_mode` | `Off` (heat pump automatic, PVControl only forecasts it), `PlanOnly` (default: plans and publishes, never touches the heat pump), `Auto` (PVControl runs the one-time heatings) |
| `number.pv_control_warm_water_comfort_temp` | Comfort temperature, default 42 °C — the plan aims 1 K above it (`ComfortMarginK`) for model error |
| `time.pv_control_warm_water_morning` | Start of the morning comfort period (default 06:30, lasts 60 min) |
| `switch.pv_control_warm_water_morning_enabled` | Morning comfort period on/off (default on) |
| `time.pv_control_warm_water_evening` | Start of the evening comfort period (default 19:00, lasts 120 min) |
| `switch.pv_control_warm_water_evening_enabled` | Evening comfort period on/off (default on) |
| `button.pv_control_warm_water_boost` | "Warm water now": heats to the boost temperature right away in any mode (a running heating keeps going, at least to the boost temperature). The boost ends when reached, when the heat pump stopped, or after 3 h; the plan sensor shows it (`boost to … °C`, attributes `boost`, `boost_since`). ~3 K take ~11 min, from 30 °C to 45 °C ~50 min |
| `number.pv_control_warm_water_boost_temp` | Boost temperature, default 45 °C |
| `sensor.pv_control_warm_water_plan` | State: the next heating (`06:15 → 44.5 °C`), `heating to … °C` or `no heating planned`. Attributes below |

Attributes of `sensor.pv_control_warm_water_plan`:

| Attribute | Meaning |
|---|---|
| `mode`, `boost`, `boost_since` | Current mode, whether a boost is going and since when |
| `control` | What the control did or waits for this cycle (e.g. `Plan: start heating to 44.5 °C`, `Waiting for the heat pump to start (2 min)`, `Plan only (not controlling)`) |
| `temperature`, `comfort_temp` | Tank temperature now, comfort temperature |
| `runs` | Planned heatings: `start`, `end`, `from_temp`, `to_temp`, `power_w` |
| `automatic_runs` | What the heat pump's own automatic would do (from its setpoint, 5 K hysteresis) |
| `plan_cost_eur`, `automatic_cost_eur` | Heating cost of both until the end of tomorrow — the difference is the expected saving |
| `comfort_deficit_k`, `automatic_comfort_deficit_k` | Largest shortfall below the comfort minimum (0 = all comfort periods met) |
| `temp_forecast` | Planned tank temperature every 30 min, `[time, °C]` |
| `model` | Learned tank model: `heating_k_per_h`, `loss_k_per_h_at_42` / `_at_55`, `power_w_at_45`, `usage_k_per_h` (per hour of day), `fitted` |
| `last_plan`, `last_plan_reason` | When and why the plan was last made |

**Getting started:** deploy (mode starts as `PlanOnly`), compare `runs` with `automatic_runs` and `plan_cost_eur` with `automatic_cost_eur` for a day or two and check `model` looks plausible; then set the heat pump's own warm-water setpoint low (e.g. 20 °C) and switch to `Auto`, watching `control`.

YAML (`PVControl.yaml` → `WarmWater`): the heat pump entities (temperature, heating/one-time active, one-time setpoint and switch, automatic setpoint, power), `HistoryDbColumn`, `MaxTemp`, `ComfortTempDefault`, `ComfortMarginK`, `BoostTempDefault`, `ComfortPeriods` (name, default time, duration), `RunStartCostEur`, `ReplanIntervalMinutes`, `HistoryDays`.

---

### DataLogger
Logs energy sensor history to a local SQLite database at regular intervals. The data is used by PVControl's load prediction and is intended as a training dataset for future ML-based load forecasting.

One row per hour (`hourly`, stamped hh:59:59 local time with offset) and per local day (`daily`, midnight to midnight — 23 or 25 h at the clock change; the repeated hour in October gets two rows, +02:00 and +01:00): weather (outside/inside temperature, pressure, clouds, humidity), energies (house, PV, import/export, battery, EV, heat pump, warm water, as counter increases — resets and glitches handled), SoCs and prices. Also the heat pump's operation and the house's state:

| Column | Source (`DataLogger.yaml`) | Value |
|---|---|---|
| `heatingmode` | `HeatPumpModeEntity` | mode held longest in the period |
| `heatingminutes`, `compressorminutes`, `ecominutes` | `HeatingActiveEntity`, `CompressorActiveEntity`, `EcoModeEntity` | minutes on |
| `hptargettemperature` | `TargetTemperatureEntity` | time-weighted mean of the heat pump's current target temperature (what eco mode lowers) |
| `flowtemperature`, `returntemperature`, `windspeed` | `FlowTemperatureEntity`, `ReturnTemperatureEntity`, `WindSpeedEntity` | time-weighted mean |
| `athomeminutes` | `PresenceEntity` | minutes `home` |
| `roomsheating` | `RoomClimateEntities` (`hvac_action`) | mean number of rooms with an open valve |
| `roomsetpoint` | `RoomClimateEntities` (setpoint) | mean room setpoint |
| `heatenergyheating`, `heatenergywarmwater` | `HeatEnergyHeatingEntity`, `HeatEnergyWarmwaterEntity` | heat output (Wh) per mode, counter increase |
| `heatpumpelecheating`, `heatpumpelecwarmwater` | `ElecEnergyHeatingEntity`, `ElecEnergyWarmwaterEntity` | heat pump electricity (Wh) under the same conditions — COP per mode = heat / electricity |
| `maxquarterimport` | `ImportTotalEntities` (the first that worked in the period: a source a later one counted ≥ 0.2 kWh more than was frozen) | highest 15-min mean grid import of the hour/day, kW |
| `pvcontrolmode`, `pvmodechanges` | `PVControlModeEntity` | PVControl mode held longest; number of mode changes |
| `feedinminutes`, `forcechargeminutes` | `PVControlModeEntity` | minutes in `feedin_priority`; in `force_charge` / `force_charge_grid_only` |
| `chargedisabledminutes` | `BatteryChargingEnabledEntity` | minutes with battery charging off |
| `invertereepromwrites` | `InverterEepromEntities` | state changes of the inverter's EEPROM settings (work mode, manual mode, charge current, export limit) = writes |

---

### HeatingTest
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

**DB** (`apps/HeatingTest/heating_test.db`, times as local ISO-8601 text with offset — open with DB Browser for SQLite, Python, Excel, …):

| Table | Content |
|---|---|
| `runs` | One row per night: `kind` (Test/Reference), `start`, `planned_end`, `actual_end`, `offset_k`, `original_level`, `restored`, `end_reason`, `outdoor_at_start` |
| `samples` | Every minute: `run_id`/`phase` (Test, Reference, Normal), outdoor (heat pump and house sensor), flow, flow setpoint, return, compressor %, power, COP, level, slope, heating/warm-water flags, heat pump mode |
| `room_samples` | Every minute per room: current temperature, setpoint, `hvac_action`, preset |

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

Copy the published output to `/config/netdaemon6` on your Home Assistant instance (NetDaemon 6 add-on) or your custom deployment folder.

The build gets its version from git: `0.5.<commit count>+<short hash>`, with `.dirty` appended when it was built with uncommitted changes (e.g. `0.5.233+1bdea38`, `0.5.233+1bdea38.dirty`). After a deploy, `sensor.pv_control_version` / `sensor.heating_test_version` and the devices' "Firmware" show it; their history shows every deploy. Building needs `git` on the path, otherwise the version is `1.0.0`.

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
| `FindWindowConditionTests` | Simulation conditions behind the load scheduling rules (PV surplus, overnight minimum, new/cheap grid, grid charge sized into the cheap band) |
| `CheapWindowShorterThanSessionTests` | PriorityPlus with a cheap window shorter than the session (live 2026-10-25): charges the window, longest passing end, min window, follow-up |
| `SessionPlannerTests` | PriorityPlus price scan: cheapest vs earliest start, minimum saving, sticky plan, ready-by deadline (same choice rules over the price limit), follow-up sessions, net grid cost |
| `LoadSchedulingDecisionTests` | Priority/PriorityPlus live SoC gate with restart hysteresis |
| `ReplanPolicyTests` | When a load's schedule search runs again, house-load deviation trigger, min-off latch |
| `LevelEnergyLearnerTests` | Learning energy per level and charge power |
| `MorningTopUpTests` | Grid charge on a real night (prices, load forecast): night charge with forecast margin, no top-up at peak price before PV (grid_only holds the floor) |
| `FoggyDayTopUpTests` | Grid charge before a foggy day: the trough is searched up to the PV peak after the critical point, not a small bump before it |
| `TankModelTests` | Warm-water tank model fit on 10 days of real history, history helpers |
| `WarmWaterPlannerTests` | Warm-water plan: heat late vs preheat in cheap slots, max temperature, run in progress, heating prices from the house simulation, outside-temperature efficiency |
| `HeatingTestTests` | Heating step test: start/skip/alternation, end/abort/disarm, SQLite log incl. restart recovery |
| `WarmWaterControlTests` | Starting, never stopping mid-run, raising the setpoint, off-time and minimum run of the one-time heating, boost lifecycle, comfort windows |
| `NetEnergyPredictionTests` | PV running-average correction: near-term ramp |
| `PricePublicationTrackerTests` | When tomorrow's prices first appear (once per day, not after a restart) |
| `PriceListTests` | Hourly/quarter-hourly price normalization, hourly billing averages, estimates for unpublished hours with risk premium (a published cheap slot vs a slightly cheaper estimated one) |
| `NetworkPricePeriodTests` | Month/hour windows of network price overrides (SNAP, WiNAP) |
| `EnergyCostTests` | Gross import price composition (markup, network fee, tax) |
| `ExtensionsTests` | Unit conversion of `unit_of_measurement` strings (kWh, MWh, ct, …) |
| `LoadModelTests`, `WeatherForecastTests` | Weather-aware load forecast: data cleaning, heat demand (energy signature, 48-h scaling, heating off / just switched on), base level, night scoring, model vs fallback on a temporary DB; parsing a real Pirate Weather forecast |
| `DataLoggerPeriodTests` | DataLogger hours and days across both clock changes, backfill of an empty table |
| `ChargeAbovePriceLimitTests` | Above the force-charge price limit a grid charge only protects the floor, never the forecast margin alone |
| `EvDirectFromGridTests` | PriorityPlus: battery if no grid is needed, otherwise directly from the grid (battery held, or alongside a force_charge), round-trip loss in the cost |
| `NeedChargeHysteresisTests` | A running grid charge never stops where starting one would begin it (no force_charge/normal flip-flop); no grid charge right before a PV peak that fills the battery anyway |
| `InverterCommandTests` | Versioned inverter command: version only on change, continued after a restart, battery power per mode, sync health |
| `ImportCapTests` | Import cap: counters in priority order with fallback, the running quarter, the month's peak, the guard (battery first, then extra loads, reaction time), health |
| `ImportCapSimulationTests` | Import cap in the plan: grid charging yields to the load, NeedToCharge under the cap, PriorityPlus never over it, warm-water starts wait, DataLogger v8 |
| `DataLoggerOperationTests` | DataLogger operation columns: minutes in a state, state changes, time-weighted means, prevailing heat pump mode, migrations (v6, v7, v9) |
| `HeldReadingTests` | Battery SoC held through sensor dropouts, stale after 2 min → normal mode |
| `RunningSlotTests` | No jumps at quarter boundaries: the running slot counts only its rest, the PV energy counter made continuous |
| `CalculationsTests` | The shared basic calculations: time ranges, W/Wh, SoC/Wh, means and hour-of-day profile, least squares, step functions, day log |
| `SourceSharesTests` | What a kWh costs by source (battery average cost, loads' cost totals) and the shared price lookup |
| `HealthTests` | Health sensor: debounce (single blip stays ok, clear after 1 min, escalation), checks (SoC, prices incl. the disabled primary source, tomorrow's prices, cycle failures, DataLogger, forecasts, warm water) |
| `AppVersionTests` | Build version from git (commit count, hash, `.dirty`) |

---

## Development notes

- All dynamic HA entities (sensors, selects, numbers, switches, times) are registered via MQTT (`IMqttEntityManager`), not static YAML
- App-specific configuration (entity IDs, thresholds) lives in each app's `.yaml` file
- `PVControlCommon` is a static singleton that holds shared dependencies; initialized once in the constructor
