# PVControl Architecture

## Big picture

Every **15 seconds** (cron `*/15 * * * * *`), `PVControl.cs` runs `ScheduledOperations()`,
which calls `RunSimulation()` in `HouseEnergy.cs`, then reads `ProposedState` and sends the
resulting inverter command to the Solax inverter via MQTT.
The 15-second cadence keeps inverter commands responsive, but the simulation is expensive
(multiple `PVSimulator.Simulate()` calls). Caching to avoid redundant recalculations within
the same 15-minute slot is a known future improvement.

```
[HA sensors] ──► HouseEnergy
                    │
                    ├─ 4 × Prediction  ──► SimulationInput
                    │                              │
                    └─ RunSimulation()  ──►  PVSimulator.Simulate()
                                                   │
                                              SimulationSlot[]
                                         (one slot per 15 min, 2 days)
                                                   │
                                        ProposedState  ──► Inverter command
                                        ChargeNow      ──► Schedulable load outputs
```

---

## The simulation oracle

`PVSimulator.Simulate(SimulationInput)` is a **pure function** — no HA, no side effects.
It returns a two-day timeline (`List<SimulationSlot>`) with one entry every 15 minutes.

Each slot contains:
- `Time` — the 15-min bucket it covers
- `SoC` — battery state of charge at the end of that slot
- `State` — the `InverterState` chosen for that slot (mode + reason)

The simulator decides slot by slot: should the inverter force-charge from grid, export,
or run normally? All price/forecast data is baked into `SimulationInput` upfront.

**This same function is called many times per cycle** — once as a baseline, then once per
schedulable load candidate window, then once more for the final combined result.

---

## Predictions (4 parallel forecasts)

All predictions produce a `Dictionary<DateTime, int>` indexed in 15-min steps over today + tomorrow.
Values are in **Wh per 15-min slot**.

| Name | Source | What it forecasts |
|---|---|---|
| `Prediction_Load` | SQLite history (weighted avg by day-of-week) | House base load |
| `Prediction_PV` | OpenMeteo via HA sensors (today + tomorrow kWh) | Solar production |
| `Prediction_NetEnergy` | PV − Load | Net surplus/deficit |
| `Prediction_BatterySoC` | Simulation output | Battery SoC % per slot |

`HourlyWeightedAverageLoadPrediction` strips historical EV/car-charging from the base load
(`excludeScheduledLoads: true`) so scheduled loads can be added back as `ExtraLoad` without
double-counting.

---

## RunSimulation() step by step

```
1. Refresh predictions (Load if date changed, PV always, NetEnergy always)

2. Build baseInput  ← all predictions + prices + battery state + user settings

3. baseline = Simulate(baseInput)
   baseForceChargeSlots = slots where baseline already force_charges from grid
   (these are "free" — the system would grid-charge here anyway)

4. For each schedulable load (highest Priority first):
       FindLoadWindow(load, baseInput, baseForceChargeSlots)
       → writes load.ChargeNow / ChargeReason / PredictedEnd / ExtraLoads

5. finalInput = baseInput + all found ExtraLoads

6. _simulationResult = Simulate(finalInput)
   → this is what ProposedState reads from
   → also used to update Prediction_BatterySoC
```

---

## Schedulable loads

A "schedulable load" is anything that:
- has a current **level** (EV SoC %, water temp °C, …)
- has a **target level** to reach
- draws a roughly-known **power** (W) while running
- should run during **cheap/solar** time

### Config (YAML → `SchedulableLoadConfig`)

```yaml
SchedulableLoads:
  - Name: "EV Charger"
    Priority: 10                  # higher wins when slots compete
    EnergyPerLevelUnitKwh: 0.6    # 60 kWh battery / 100% = 0.6 kWh per %
    AvgPowerW: 1800               # fallback power before real measurement
    MinActivePowerW: 500          # below this = standby / noise, ignored
    CurrentLevelEntity: sensor.byd_atto_2_ladezustand
    ActualPowerEntity: sensor.esp_ct8_carport   # optional: for running average
    # Mode / TargetLevel omitted → auto-created as HA select / number entities
```

### Runtime (`SchedulableLoadRuntime`)

Wraps the config with live state:
- `Mode` — from YAML or HA select entity
- `TargetLevel` / `CurrentLevel` — from YAML or HA sensor
- `EffectivePowerW` — running average of actual power (or `AvgPowerW` as fallback)
- `EnergyNeededWh` / `DurationMinutes` — derived from level gap × kWh/unit
- `ChargeNow` / `ChargeReason` / `PredictedEnd` — written by `FindLoadWindow`

### Modes (`LoadSchedulingMode`)

| Mode | Behaviour |
|---|---|
| `Off` | Never schedule |
| `Optimal` | Only run if battery would reach 100% today anyway (pure solar excess) |
| `Priority` | Run during solar hours as long as overnight battery stays above minimum |
| `PriorityPlus` | Like Priority + allow cheap grid import slots after sunset |
| `Emergency` | Run immediately, ignore battery/PV conditions |

### FindLoadWindow logic

```
Off          → skip
Level done   → skip
Emergency    → ChargeNow=true, no simulation check

PriorityPlus → build windows: PV-hours now→sunset + cheap-grid slots after sunset
               run ONE test simulation with those windows
               accept if: overnight SoC ok AND any new force_charge is only in cheap slots

Optimal/Priority → scan start slots from now to sunset (15-min steps)
                   for each: run test simulation with a single ExtraLoad block
                   accept first slot where:
                     needsGrid = false   (no NEW force_charge tonight vs. baseline)
                     Optimal: battery also reaches ≥99% today
                   ChargeNow = true if the accepted slot is the current slot
```

**Key constraint — `needsGrid`:** only looks at the overnight window
(`LastRelevantPVEnergyToday` → `FirstRelevantPVEnergyTomorrow`), not the full 2-day
simulation. This prevents tomorrow's force_charge (caused indirectly by a slightly lower
battery today) from incorrectly rejecting a valid solar window today.

---

## Key files

| File | Role |
|---|---|
| `PVControl.cs` | NetDaemon app entry point; registers HA entities; 15-second cron |
| `PVControlCommon.cs` | Static singleton holding shared dependencies (IHaContext, EntityManager, Config, Logger) |
| `HouseEnergy.cs` | Core model: predictions, simulation, scheduling decisions, prices |
| `Simulator/PVSimulator.cs` | Pure simulation engine |
| `Simulator/SimulationInput.cs` | All inputs to the simulation (snapshot in time) |
| `Simulator/ExtraLoad.cs` | A load injected into a simulation (name, power, time window) |
| `Managers/SchedulableLoadConfig.cs` | YAML config + `PVConfig` partial |
| `Managers/SchedulableLoadRuntime.cs` | Live runtime state per load |
| `Predictions/` | 4 prediction classes + abstract base `Prediction` |
| `Simulator/LoadSchedulingDecision.cs` | Schmitt-trigger logic (used only via unit tests; main flow uses simulation oracle) |

---

## Price pipeline

```
EPEX Spot (HA sensor, ct/kWh net)
  │
  ├─ × ImportPriceMultiplier + ImportPriceAddition + ImportPriceNetwork
  │  × (1 + ImportPriceTax)
  └─► PriceListImport  (ct/kWh gross, used by simulator + forced-charge decisions)

EPEX Spot (same or different entity)
  │
  └─► PriceListExport  (gross, used for opportunistic discharge decisions)
```

`ForceChargeMaxPrice` (set via HA number entity) is the threshold used in `PriorityPlus`
and in force-charge decisions — grid import is allowed if the price is at or below this.

---

## HA entity auto-creation

`PVControl.RegisterControlSensors()` creates all dynamic sensors/selects/numbers via MQTT.
For each schedulable load:
- If `Mode` not in YAML → creates `select.pv_control_<slug>_mode`
- If `TargetLevel` not in YAML → creates `number.pv_control_<slug>_target_level`
- Always creates `binary_sensor.pv_control_<slug>_charge_now`

`<slug>` = `Name.ToLower().Replace(' ', '_')`, e.g. `"EV Charger"` → `ev_charger`.
