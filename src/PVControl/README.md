# PVControl
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

Design details live in [`src/PVControl/ARCHITECTURE.md`](ARCHITECTURE.md).

Deployed, PVControl lies in `apps/plugins/PVControl/` with `PVControl.yaml` and its cost log `energy_costs.db` (`EnergyCostDBLocation`); the load forecast reads the DataLogger's DB from there as `../DataLogger/energy_history.db` (`DBLocation`, the default).

## Main entity for automations

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

Whether PV surplus may currently be exported is a separate, orthogonal decision — see
`binary_sensor.pv_control_export_allowed` below. It's independent of `sensor.pv_control_mode`
so a cheap grid-charge or safety-net top-up can still run even while export is suppressed
(e.g. during a negative export price window).

Before negative export prices later the same day, PV surplus is fed in while the price is still
positive (battery charging off, reason `RoomForNegativeExport`) as long as 70 % of the PV surplus
forecast in the negative hours would fill the battery anyway — the battery then charges in the
negative hours instead of the PV being curtailed. Independent of opportunistic export.

## Control entities

| Entity | Description |
|---|---|
| `select.pv_control_mode_override` | Manually override the current mode |
| `switch.pv_control_enforce_preferred_soc` | The preferred minimum becomes the hard floor: never drained below, not even by an unforeseen load (reserve for power outages) |
| `switch.pv_control_force_charge_at_cheapest_period` | Daily target charge: reach `number.pv_control_forcecharge_target_soc` by the end of the day's PV (headroom for the evening and the night), buying only what PV won't deliver by then, in the cheapest quarters under the price limit before it — tomorrow's PV end once today's has passed |
| `switch.pv_control_enable_opportunistic_export` | Enable opportunistic discharge to grid during high-price periods |
| `number.pv_control_preferredbatterycharge` | Preferred minimum SoC (%): what planning keeps (see below) |
| `number.pv_control_max_price_for_forcecharge` | Price ceiling (ct/kWh) for grid import during force-charge; also the "cheap grid" threshold for PriorityPlus loads |
| `number.pv_control_forcecharge_target_soc` | Target SoC (%) of the daily target charge |

## Status entities

| Entity | Description |
|---|---|
| `binary_sensor.pv_control_need_to_charge_from_grid_today` | Grid charging needed before next PV period |
| `binary_sensor.pv_control_battery_charging_enabled` | Whether battery charging is currently enabled |
| `binary_sensor.pv_control_export_allowed` | Whether PV surplus may currently be exported to the grid (off during negative export price) |
| `sensor.pv_control_battery_status` | Battery status: `idle`, `charging`, `discharging`, `unknown` |
| `sensor.pv_control_active_network_price_period` | Current network price override period name (SNAP Apr–Sep 10–16; WiNAP Oct–Mar 23–05 from October 2027), or "Standard". A period in `ImportPriceNetworkPeriods` applies between `StartMonth` of `StartYear` and `EndMonth` of `EndYear` (0 = no limit) |
| `sensor.pv_control_version` | Deployed build (see [Build & Deploy](../../README.md#build--deploy)), also the "Firmware" of the PV Control device |
| `sensor.pv_control_health` | `ok` / `degraded` / `error` — see [Health](#health) |
| `sensor.pv_control_load_forecast` | Coming night's load (22:00–08:00, kWh) by the weather-aware model — see [Load forecast](#load-forecast) |

**Minimum SoC.** Two floors:
- **Absolute minimum** (the inverter's minimum + 2 %): the hard floor, never drained below — held with `grid_only` (the grid covers the load) once the battery reaches it, released only 2 % above it.
- **Preferred minimum** (`number.pv_control_preferredbatterycharge`): what planning keeps — when PV doesn't, a grid charge lifts the coming low (before PV takes over) to it, and planned loads (EV, warm water) never use the band below it. An unforeseen load (e.g. a heat pump run) may drain the battery from there down to the absolute minimum: that band is the reserve. The plan never plans below it: a coming dip is prevented in the cheapest quarters before it — independent of `number.pv_control_max_price_for_forcecharge`, but never in a quarter above the coming 24 h's average price —, buying only what keeps the minimum until PV refills the battery or an as cheap quarter comes after the dip (e.g. midday 0.40 for the evening, the night after at 0.39 for the rest). Once below it (an unforeseen load), it is refilled in the cheapest quarters of the coming 24 h, not while PV charges the battery anyway (if the absolute minimum would be reached before, the hard floor's rules apply).
- With `switch.pv_control_enforce_preferred_soc` the preferred minimum is the hard floor itself, with a forecast margin on top (+1 % per 2 h until the low, at most 4 %).

Grid charge (`need_to_charge_from_grid_today`): the target for the low is the hard floor + the band above it (the preferred minimum, or the forecast margin when enforced) + 1 %. A charge starts when the low would fall 2 % below that target (never below the hard floor itself) and charges just up to it, in the cheapest quarters before the low — or the hard floor is held with `grid_only` when charging isn't cheaper than buying the load. A running charge continues as long as starting one would begin it. Before a PV peak on the way to the low, the charge only fills the room PV leaves (100 % − that peak) and doesn't start for 2 % or less; the rest follows after the peak. Above the price limit a grid charge only protects the hard floor itself (up to it + 1 %), never an enforced floor's forecast margin alone. `sensor.pv_control_mode` has the reason in `charge_Reason`, the running or next charge quarter in `next_charge_window_start/end`.

## Load forecast

The house load forecast is the hour-of-day average of the last days (recent days weighted more). With `LoadForecast:` in `PVControl.yaml` a weather-aware model is added: base load (that average without the heat pump, plus half of its deviation over the last 48 h) plus the heat pump's space heating from the outside temperature forecast (Pirate Weather hourly) — learned from DataLogger's history as an energy signature (Wh per heating degree below 13 °C, per hour of the day). The heat pump mode decides whether it heats at all. In the backtest over the last year it missed the night load 15 % less than the average, in winter 20 % less (`tools/LoadBacktest/README.md`).

`Mode: Model` (configured) makes PVControl use it, with the average as fallback when the weather forecast or history is missing; `Mode: Shadow` (code default) computes it next to the average without using it. `sensor.pv_control_load_forecast` shows tonight's forecast and its parts (`average_kwh`, `night_base_kwh`, `night_heat_pump_kwh`, `night_temp24`, `heating_on`, `heat_pump_scale`, `source`, `used_by_pvcontrol`), the next 24 h of both forecasts (`next_24h`), and the nights scored so far (`nights`, `model_mae_kwh` vs `average_mae_kwh`) — the comparison keeps running in both modes.

## Health

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
| Import cap | first import counter failed (fallback in use) / a quarter over the cap in the last hour | no import counter works, or none configured |
| Inverter sync | inverter not in the commanded mode for 2 min | HA reconciler stopped retrying |

Attributes: `summary` (the problems in one line), `issues` (`key`, `level`, `message`, `since`), `last_cycle`, `version`. If NetDaemon itself stops, the sensor keeps its last state — `last_cycle` then gets old. The automation [`HA/Health.yaml`](HA/Health.yaml) notifies on every level change (PVControl publishes the attributes before the level, so the message carries the matching summary; after a restart the issues that counted are restored, so a lasting problem doesn't read "ok" first), when PVControl stopped running (`last_cycle` older than 2 min) and when it runs again (ntfy to `notify.ha_warnings`; replace it with your notify action).

## Import cap

The network power price (from 2027) counts the month's highest 15-min mean grid import; above 10 kW it costs extra. PVControl keeps every 15-min block (clock quarters, as the meter bills them) under `MaxGridImportW` (9.8 kW):

1. **Battery grid charging is throttled first:** the charge current of a grid charge in `sensor.pv_control_mode` (`battery_current_a`, `battery_power_w` = current × battery voltage) is what the battery may charge so the quarter's mean stays under the cap, in whole amps. It is lowered at once as far as needed and raised only with twice the headroom (from L to R only while the room covers R + (R − L)). A charge the plan runs starts at the lowest room over its remaining quarters, planned EV sessions and warm water included, so it doesn't have to be lowered when the EV comes on. A bulk charge stays bulk: it follows the limit with its charge current.
2. **Then the extra loads are paused** (`charge_now` off, reason "Import cap: paused"), until there is room for them again with a margin. Their plan stays. [`HA/CarCharger.yaml`](HA/CarCharger.yaml) switches the EV on `charge_now`.
3. **Then warm-water heatings wait** until there is room for the heat pump.

Nothing reacts instantly (automation delays such as the EV's 1 min, inverter and car ramps), so the guard assumes the current power keeps flowing for `ImportCapReactionSeconds` (90 s) after any change. A change late in a quarter only takes effect in the next one. The plan respects the cap too: grid charging only gets what the load leaves under it, so NeedToCharge takes more cheap quarters at lower power, and PriorityPlus never plans a load over the cap.

The import is counted from `TotalImportEnergyEntities` in list order. The first counter (the utility meter via wM-Bus) is used while it works. The next one (the inverter) takes over only while the first is unavailable, or frozen while the next keeps counting. A counter that doesn't change is no failure: no import. The quarters are tracked as UTC instants, so the clock changes in March and October lose none. An extra load (EV) counts against the budget whether PVControl switched it on or not — a charger started by hand draws import all the same.

| Entity | Content |
|---|---|
| `sensor.pv_control_mode` (command attributes) | The mode is also the inverter command; attributes `battery_power_w` (+ charge, − discharge: `battery_current_a` × the measured battery voltage — the hardware maximum, lower only while the import cap limits below what the path charges; the voltage is taken when the command changes, a 5-V step alone is no new command), `bulk` (grid charge at full power, see below), `charge_energy_wh`, `charge_target_soc`, `charge_start`, `charge_until` (the running grid charge in the plan, else the next one: energy into the battery, the SoC it reaches — shown anew only on a change of 2 % or more or for another charge —, its slots; `next_charge_window_start/end` and `price` are the same charge), `vpp_charge_limit_w` (what a VPP charge reaches at the current SoC), `seq` (version, grows with every change — not with a restart: the published command is restored and only a different one gets a new version), `issued` (when `seq` last changed), `import_cap_reason`, `cap_kw`, `allowed_kw` (mean allowed for the rest of the quarter), `extra_loads_paused`, `headroom_w` (room for a new load), `voltage_v` (the battery voltage used), `max_current_a` (the hardware maximum); `grid_eur`: the plan's grid cost over the horizon (import − export, €, whole cents) |
| `sensor.pv_control_vpp_charge_limit` | Battery charge power a remote-controlled (VPP) grid charge reaches at the current SoC (W); attributes `at_0_pct_w`, `slope_w_per_pct`, `learned`, `at_50_pct_w`, `buckets` (the learned readings, restored after a restart) |
| `sensor.pv_control_import_quarter` | The running quarter's mean import if the current power continues (kW); attributes `quarter_start`, `imported_kwh`, `complete` (tracked from its start), `counter` (the counter in use) |
| `sensor.pv_control_import_month_peak` | The month's highest 15-min import so far (kW, each quarter scaled to exactly 15 min — what the meter bills); attribute `quarter_start`. After a restart restored from this sensor and the DataLogger's `maxquarterimport` (the higher one) |

| Setting | Meaning |
|---|---|
| `MaxGridImportW` | Highest mean grid import of any 15-min block, W; 0 = off |
| `ImportCapReactionSeconds` | How long a requested change takes to show at the meter, s |
| `TotalImportEnergyEntities` | Grid import counters (kWh) in priority order (also the cost log's import) |
| `BatteryVoltageEntity` | Battery voltage: W per A of the charge current, in the plan, the import cap and the command (230 V without it) |
| `MaxBatteryChargeCurrrentValue` | Hardware charge current limit, A |
| `InverterSyncEntity` | HA's feedback whether the inverter runs the command (`binary_sensor.pv_control_inverter_in_sync`) |
| `VppChargePowerAt0PctW`, `VppChargePowerSlopeWPerPct` | Default line of the VPP charge power by SoC (W = At0 + Slope × SoC) until the curve is learned |

## Inverter control (HA side)

PVControl only publishes `sensor.pv_control_mode` (state = mode, attributes = how to run it); all inverter writes are HA automations, so another inverter only needs another script. For the SolaX X3-Hybrid G4 the modes run in RAM as VPP mode 8 ("Individual Setting – Duration Mode") through the solax-modbus integration's `*_direct` entities — no EEPROM writes for mode changes or charge power. The inverter limits VPP charging, falling with the SoC (~5.1 kW at 20 %, ~3.3 kW at 63 %; Manual Force Charge ~7.45 kW); PVControl learns that curve from VPP charges that run below the command (`sensor.pv_control_vpp_charge_limit`). The plan chooses the path when a grid charge starts: VPP if it puts the energy to the target into the battery within the quarters that cost the same — with hourly prices up to the rest of the hour, with quarter-hourly prices usually one quarter — else `bulk` if that delivers more energy over those quarters (both paths as the import cap leaves them per quarter, VPP falling with the SoC), else VPP still. Bulk is Manual Mode + Force Charge (30 A, or the import cap's current), two EEPROM writes per charge session. A planned charge may start anywhere in its price block, but not so late that it needs full power where starting earlier in the same block (same price) would have stayed on VPP. The plan models the battery at the path's power (VPP by the curve, PV included). A `force_charge` set with `select.pv_control_mode_override` is always bulk; the import cap doesn't change the path — a bulk charge runs at `battery_current_a` (written to the battery charge current before Manual Mode starts, back to the maximum after it), and `force_charge_grid_only` is never bulk. A started grid charge runs at least 1 minute unless the user changes the mode.

| File | Content |
|---|---|
| [`HA/InverterHelpers.yaml`](HA/InverterHelpers.yaml) | Package: applied `seq`, applied time, re-apply counter, blocked flag, `binary_sensor.pv_control_inverter_in_sync` |
| [`HA/ApplyMode.yaml`](HA/ApplyMode.yaml) | `script.pv_control_apply_mode`, the only writer of the inverter state: base work mode (only when it changes), Manual Force Charge for `bulk`, else the mode 8 block (PV limit, battery push power, run time 1200 s, NextMotion back to the base mode) or VPP off; records the applied `seq` |
| [`HA/InverterSync.yaml`](HA/InverterSync.yaml) | Reconciler: applies a new `seq`, re-applies when the inverter left the commanded state (at most 5 times per version, then blocked + notification), refreshes the mode-8 run time after 600 s; only while PV Control runs (mode 8 then returns to Self Use by itself; Manual Mode is switched back to Self Use after 5 min without PV Control) |
| [`HA/Mode.yaml`](HA/Mode.yaml) | Mode change notifications |
| [`HA/Battery.yaml`](HA/Battery.yaml) | Charging on/off: maximum (`max_current_a`, 30 A) / 0 A — not during a bulk charge, whose current the apply script sets |

| PVControl mode | Inverter |
|---|---|
| `normal` | Self Use, VPP off |
| `feedin_priority` | Feedin Priority, VPP off |
| `force_charge`, `bulk` | Manual Mode + Force Charge (30 A), VPP off |
| `force_charge` | Self Use + mode 8: PV unlimited, battery charges at `battery_power_w` (the inverter limits it by the SoC) |
| `force_charge_grid_only` | Self Use + mode 8: PV 0 W, battery charges at `battery_power_w` (the inverter limits it by the SoC) |
| `grid_only` | Self Use + mode 8: PV unlimited, battery 0 W (held) |
| `force_discharge` | Self Use + mode 8: battery discharges at `battery_power_w` |

## Price entities

| Entity | Description |
|---|---|
| `sensor.pv_control_current_import_price_brutto` | Current gross import price (€/kWh) incl. taxes, network, markup; `data` attribute holds the full price list |
| `sensor.pv_control_current_export_price_brutto` | Current gross export/feed-in price (€/kWh); `data` attribute holds the full price list |
| `sensor.pv_control_best_import_price_today` | Cheapest import price slot today (€/kWh) |
| `sensor.pv_control_best_export_price_today` | Highest export price slot today (€/kWh) |
| `sensor.pv_control_price_publication` | When tomorrow's prices arrived today (`HH:mm`, `pending`, or `published` after a restart); attributes `prices_until` and `days` (the last 14 days' publication times, each with `sources`: when every price entity first had tomorrow) |

Price sources (`CurrentImportPriceEntities`, ha_epex_spot sensors) are all equal: PVControl uses the one whose price data reaches furthest, so whichever publishes tomorrow first wins (on a tie the earlier one in the list). `ImportPriceResolution` applies to every source — with `Hourly`, a 15-min source gives the same hourly prices as an hourly one. A configured source without any data shows up as `price_source` in the health sensor.

The sources are the integration's **market price** sensors (raw EPEX price; its surcharge and tax stay 0 — PVControl adds markup, network fee and tax itself). The integration polls its sources on its own schedule; [`HA/PriceFetch.yaml`](HA/PriceFetch.yaml) calls `epex_spot.fetch_data` every 5 min from 12:45 until one source reaches the end of tomorrow, so the prices arrive minutes after the auction results instead of in the afternoon.

The simulation plans until the end of tomorrow, but tomorrow's EPEX prices only arrive in the afternoon. Until then the missing hours are estimated with the price of the same hour a day earlier (the daily pattern repeats well enough; estimates are never negative). Estimates carry a risk premium (`EstimatedPricePremium`, default 5 %: import × 1.05, export × 0.95), so an estimated slot only wins over a published one when it's clearly better. Price-driven commitments — EV session starts — still only use published prices.

## Cost tracking entities

| Entity | Description |
|---|---|
| `sensor.pv_control_sum_import_cost_brutto` | Cumulative gross import cost (€) |
| `sensor.pv_control_sum_import_cost_energy_only` | Cumulative import cost — energy component only |
| `sensor.pv_control_sum_import_cost_network_only` | Cumulative import cost — network/grid fee only |
| `sensor.pv_control_sum_export_earnings_brutto` | Cumulative gross export earnings (€) |
| `sensor.pv_control_sum_import_export_net_cost` | Net cost = import − export (€) |
| `sensor.pv_control_battery_avg_cost_per_kwh` | Average cost per kWh stored in the battery: each charged kWh split by source (PV free, grid at the import price), blended into the stored energy — the same calculation as the loads' `total_cost` |

## Battery / forecast entities

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

## Per schedulable load entities

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

Attributes of `charge_now`: `charge_status_reason`, `mode`, `target_level`, `current_level`, `power_avg_w`, `net_pv_w`, `charge_start_predicted`, `charge_end_predicted`, `charge_end_predicted_soc`, `planned_sessions` (current/next session plus planned follow-ups), `zz_charge_timeline` (prefixed so it sorts last in the HA GUI), `ready_by`, `energy_per_level_kwh`, `learned_power_w`, `last_plan`, `last_plan_reason`, `price_scan` (PriorityPlus: earliest / cheapest / last plan start with their extra cost, and whether any start was under the price limit). A session window's length comes from the energy still needed (counted from now in the running slot); follow-up sessions cover the rest.

## Schedulable loads

Extra loads (the EV charger) are scheduled by simulating them as an extra load in the house simulation and checking the result. Warm water has its own planner (see below).

**Modes** form a ladder — each mode runs its own ruleset plus all lower ones, bottom up, and the first one that allows charging wins:

| Mode | Behaviour |
|---|---|
| `Off` | Never runs |
| `Emergency` | Always runs now, no simulation |
| `Optimal` (10) | Only on PV surplus: the house battery must still reach ~100 % today, hold its overnight minimum, and no grid is needed |
| `Priority` (20) | May draw the house battery down to its minimum, but never causes grid use. Evening/night sessions start as late as possible before sunrise; a running session stops at dusk |
| `PriorityPlus` (30) | Like Priority, plus grid use when it's cheap (day or night) |

Planned loads keep the house battery at the preferred minimum in every mode (the band below it is the reserve for unforeseen loads). Grid use is compared by energy: any 15-min slot where the load adds grid import counts, at that slot's price — including the grid covering the house while the battery is held at its minimum.

**PriorityPlus price rule:** every 15-min start is simulated; a start passes when the load adds no grid import, or only at or below `number.pv_control_max_price_for_forcecharge`. Of the passing starts, the earliest wins unless a later one is at least 1 % cheaper overall (extra import cost minus lost export earnings — battery energy the load uses tonight is refilled by tomorrow's PV that would otherwise be exported — plus the round-trip loss of extra battery use). Each start is supplied like Priority — PV and the house battery — if that needs no extra grid (e.g. a ready-by target in the morning from the battery). Once it needs grid energy, the session takes it directly from the grid, not via the house battery (wear, losses): while it runs and PV doesn't cover house + load, the battery is held (`grid_only`, PV surplus feeds the load first) — or, if the house charges from the grid in that quarter anyway, the load runs along the `force_charge`. If the cheap window is shorter than the session, so no full-length start passes, the load charges just that window (the earliest shortened session that passes, at least `MinWindowMinutes`); the rest follows later. If the best unbroken session runs through dearer quarters between two cheap stretches, the session is split into two blocks with a pause (at least `MinWindowMinutes` each, at least `MinOffMinutes` between them) when that saves at least 5 % and 10 ct: the charger switches once more only where it pays. A planned pause is kept while the session runs; it only resumes in its second block. A planned later start sticks while it stays cheaper than starting now, so forecast noise can't flip it; a running session is never stopped to wait for a cheaper start. Follow-up sessions up to the target are planned as a preview (`planned_sessions`).

**Ready-by deadline** (PriorityPlus only, when enforced): only starts that finish by the next occurrence of the ready-by time count, and the deadline beats the price threshold — if no start passes, the same choice is made among all starts that still finish in time (the earliest unless a later one saves at least the minimum, a running session keeps running, last cycle's plan stays unless clearly beaten); if even now is too late, it starts now. A session running directly from the grid stays on the grid until it ends.

**Latches** against on/off cycling: once started, a session runs at least `MinWindowMinutes`; once stopped, it restarts no earlier than `MinOffMinutes` later (stops themselves are never delayed). Both are bypassed by a mode change or a change of the settings the decision depends on (minimum SoC, enforce switch, price threshold, target, ready-by); the off-latch also by an availability change.

**Replanning:** the house simulation and inverter mode update every 15 s, but a load's schedule search (the expensive part) only runs every `ReplanIntervalMinutes` or on a trigger — any setting change, availability change, prices that really changed (not every state change of the price entities), a new quarter-hour, house battery SoC or the load's level changing, the load starting/stopping, the house load staying > 500 W and > 30 % off its forecast for 5 min, or a latch ending.

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

## Warm water

The heat pump heats the warm-water tank with one-time heatings at the cheapest times that keep the comfort periods warm (e.g. ≥ 42 °C from 06:30 for 1 h and from 19:00 for 2 h); outside them the tank may cool down. In `Auto`, PVControl switches the one-time heating on, and off again once the heat pump has reached the setpoint (its switch stays on otherwise). A heating that runs is never stopped — compressor starts wear the heat pump — the plan may only raise its setpoint; a new planned run needs ≥ 3 K of rise and 60 min since the last heating ended. Set the heat pump's own warm-water setpoint low (e.g. 20 °C) as a safety net before switching to `Auto`.

How it decides: a tank model learned daily from 10 days of HA history (in the background, retried hourly while the history is unavailable) (heating ~17 K/h, standby loss growing with the temperature, ~1.7 kW) and what a kWh of heating costs in every 15-min slot according to the house simulation (PV surplus = lost export, battery energy = what refilling it costs, otherwise the import price). A dynamic program picks the cheapest heating slots. Heating early to a higher temperature (up to 57 °C) only wins when the price difference beats the extra heat loss — measured, a tank at 57 °C loses about twice as much per hour as one at 42 °C. The heat pump's efficiency follows the outside temperature (air/water, ~3 % per K, `EfficiencyChangePerK`, from the weather forecast of `LoadForecast`), so a warm afternoon counts cheaper than a cold evening. Hours without a published price yet (tomorrow, before ~13:00) are priced with estimates (see "Price entities"); the plan sensor shows `prices_known_until` and `outside_efficiency`.

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

## Build, test, run

A NetDaemon app plugin (`src/AppPlugin.props`), loaded by the host from `apps/plugins/PVControl/`. Built, published and
deployed with the others ([root README](../../README.md#build--deploy)); its Home Assistant automations, script and package
are in [`HA/`](HA) (`python deploy/deploy.py ha`). Design and the reasons behind it: [`ARCHITECTURE.md`](ARCHITECTURE.md).

Reads the DataLogger's DB (`../DataLogger/energy_history.db` from its plugin folder: the `hourly` table for the load
forecast, `maxquarterimport` for the month peak) — the DataLogger plugin must run too.

## Tests

`tests/PVControl.Tests` (xUnit, no HA needed); it also references DataLogger, whose DB it reads:

```bash
dotnet test tests/PVControl.Tests
```

| Test class | What it covers |
|---|---|
| `PredictionContainerTests` | 192-slot 15-min window validation (`DataOK`) |
| `MidnightRolloverTests` | The load prediction's day rollover after midnight, the PV forecast's window cut |
| `SimulatorTests` | End-to-end `EnergySimulator.Simulate()` with injectable `SimulationInput` |
| `FindWindowConditionTests` | Simulation conditions behind the load scheduling rules (PV surplus, overnight minimum, new/cheap grid, grid charge sized into the cheap band) |
| `CheapWindowShorterThanSessionTests` | PriorityPlus with a cheap window shorter than the session (live 2026-10-25): charges the window, longest passing end, min window, follow-up |
| `SessionPlannerTests` | PriorityPlus price scan: cheapest vs earliest start, minimum saving, sticky plan, ready-by deadline (same choice rules over the price limit), follow-up sessions, net grid cost |
| `LoadSchedulingDecisionTests` | Priority/PriorityPlus live SoC gate with restart hysteresis |
| `ReplanPolicyTests` | When a load's schedule search runs again, house-load deviation trigger, min-off latch (a moved session keeps its energy and supply) |
| `LevelEnergyLearnerTests` | Learning energy per level and charge power |
| `PreferredMinimumTests` | The preferred minimum: the night charge lifts the morning low to it in the cheap hours; the live 2026-10-30 day (all prices above the limit) charges at midday to keep the evening at it; a late dip with only peak quarters before it isn't bought there; below it but above the hard floor nothing is bought at the peak; the hard floor is held (and released only 2 % above it); flat prices keep it; enforced it is the hard floor; planned loads keep it overnight |
| `MorningTopUpTests` | Grid charge on a real night (prices, load forecast): night charge with forecast margin, no top-up at peak price before PV (grid_only holds the floor) |
| `FoggyDayTopUpTests` | Grid charge before a foggy day: the trough is searched up to the PV peak after the critical point, not a small bump before it |
| `TankModelTests` | Warm-water tank model fit on 10 days of real history, history helpers |
| `WarmWaterPlannerTests` | Warm-water plan: heat late vs preheat in cheap slots, max temperature, run in progress, heating prices from the house simulation, outside-temperature efficiency |
| `WarmWaterControlTests` | Starting, never stopping mid-run, raising the setpoint, off-time and minimum run of the one-time heating, boost lifecycle, comfort windows |
| `NetEnergyPredictionTests` | PV running-average correction: near-term ramp |
| `PricePublicationTrackerTests` | When tomorrow's prices first appear (once per day, not after a restart) |
| `PriceListTests` | Hourly/quarter-hourly price normalization, list comparison (a replan only on a real price change), hourly billing averages, estimates for unpublished hours with risk premium (a published cheap slot vs a slightly cheaper estimated one) |
| `DailyTargetChargeTests` | Daily target charge: the target by the end of the day's PV, only what PV won't deliver, in the cheapest quarters under the limit |
| `NetworkPricePeriodTests` | Month/hour windows of network price overrides (SNAP, WiNAP) and their first/last year |
| `EnergyCostTests` | Gross import price composition (markup, network fee, tax) |
| `PVControlExtensionsTests` | PVControl's helpers: prediction windows, quarter hours, daily running sums, running averages |
| `LoadModelTests`, `WeatherForecastTests` | Weather-aware load forecast: data cleaning, heat demand (energy signature, 48-h scaling, heating off / just switched on), base level, night scoring, model vs fallback on a temporary DB; parsing a real Pirate Weather forecast |
| `ChargeAbovePriceLimitTests` | Above the force-charge price limit a grid charge only protects the floor, never the forecast margin alone |
| `EvDirectFromGridTests` | PriorityPlus: battery if no grid is needed, otherwise directly from the grid (battery held, or alongside a force_charge), round-trip loss in the cost |
| `NeedChargeHysteresisTests` | A running grid charge never stops where starting one would begin it (no force_charge/normal flip-flop); no grid charge right before a PV peak that fills the battery anyway |
| `InverterCommandTests` | Versioned inverter command: version only on change, the published command restored after a restart, battery power per mode, sync health |
| `ImportCapTests` | Import cap: counters in priority order with fallback, the running quarter, the month's peak, the guard (battery first, then extra loads, reaction time), health |
| `ImportCapSimulationTests` | Import cap in the plan: grid charging yields to the load, NeedToCharge under the cap, PriorityPlus never over it, warm-water starts wait, DataLogger v8 |
| `HeldReadingTests` | Battery SoC held through sensor dropouts, stale after 2 min → normal mode |
| `RunningSlotTests` | No jumps at quarter boundaries: the running slot counts only its rest, the PV energy counter made continuous |
| `SourceSharesTests` | What a kWh costs by source (battery average cost, loads' cost totals) and the shared price lookup |
| `HealthTests` | Health sensor: debounce (single blip stays ok, clear after 1 min, escalation), checks (SoC, prices incl. the disabled primary source, tomorrow's prices, cycle failures, DataLogger, forecasts, warm water) |
