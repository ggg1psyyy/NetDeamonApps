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
                    └─ RunSimulation()  ──►  EnergySimulator.Simulate()
                                                   │
                                            SimulationResult
                                         (slots + pre-computed derived values)
                                                   │
                                        ProposedState  ──► Inverter command
                                        ChargeNow      ──► Schedulable load outputs
```

---

## The simulation oracle

`EnergySimulator.Simulate(SimulationInput)` is a **pure function** — no HA, no side effects.
It returns a `SimulationResult` which wraps a two-day timeline (`IReadOnlyList<SimulationSlot>`) plus pre-computed derived values so callers never have to re-derive them from the raw slot list.

Each slot contains:
- `Time` — the 15-min bucket it covers
- `SoC` — battery state of charge at the end of that slot
- `State` — the `InverterState` chosen for that slot (mode + reason)

`SimulationResult` pre-computes:
- PV window boundaries (`FirstRelevantPVEnergyToday/Tomorrow`, `LastRelevantPVEnergyToday/Tomorrow`) — first/last slot where net PV > 50 Wh
- `CurrentPVPeriod` — `BeforePV` / `InPVPeriod` / `AfterPV`
- `WillReachMaxSocToday/Tomorrow` — whether any slot hits ≥ 99 %
- `OvernightMinSocReached` — lowest SoC in the sunset → sunrise window
- `ForceChargeSlots` — set of slot times where `force_charge` was chosen
- `HasNewGridVs(baseline)` / `IsGridCheapVs(baseline)` — used by load scheduling

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
| `Prediction_NetEnergy` | PV − Load (with running-average correction) | Net surplus/deficit |
| `Prediction_BatterySoC` | Simulation output | Battery SoC % per slot |

`HourlyWeightedAverageLoadPrediction` strips historical EV/car-charging from the base load
(`excludeScheduledLoads: true`) so scheduled loads can be added back as `ExtraLoad` without
double-counting.

### PV running-average correction (`NetEnergyPrediction.WithRunningAvgCorrection`)

Applied to the PV forecast both when computing `Prediction_NetEnergy` and when building
`SimulationInput.PVPredictionWh`. Two-stage:

1. **Day-scale ratio** (all remaining today slots):
   `ratio = actual_PV_energy_today_Wh / Σ(forecast slots midnight → now)`
   Corrects for persistent over/under-prediction (e.g. cloudy day).
   Skipped when forecast sum < 50 Wh (night/dawn/dusk). Clamped to [0.0, 2.0]. Tomorrow untouched.

2. **Near-term 4-slot ramp** (slots 0–3 from now, applied after day-scale):
   Blends from the 5-minute running average down to the day-scaled forecast.
   `slot[i] = scaled[i] + (shortAvgPerSlot − scaled[i]) × (4 − i) / 4`

Load uses only the near-term ramp (no day-scale — load follows historical patterns, not weather).

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
| `Optimal` | PV only — never battery, never grid |
| `Priority` | Optimal + may draw the battery down to its configured minimum |
| `PriorityPlus` | Priority + may draw cheap grid import, but only on top of (not instead of) Priority's own rule — see below |
| `Emergency` | Run immediately, ignore battery/PV conditions |

### FindLoadWindow logic

Off/target-reached/Emergency are unconditional short-circuits (skip / skip / ChargeNow=true, no
simulation check). Optimal only ever runs during today's PV window, scheduling tomorrow's
instead when outside it — unaffected by anything below.

Priority and PriorityPlus share one ladder, climbed from the bottom rung up — every evaluation
checks the most restrictive rung first, climbing to the next one only if the one below refused:

```
Optimal rung      PV alone covers the draw.
  ↓ (if refused)
Priority rung     PriorityRung(sim) = IsOvernightMinSocOk(true) && !HasNewGridVs(baseline)
                  — may also draw the battery down to its configured minimum.
  ↓ (if refused)
PriorityPlus rung PriorityRung(sim) || (HasNewGridVs(baseline) && IsGridCheapVs(baseline))
                  — may also draw a NEW grid-assisted charge, but only when the EV itself
                  causes that need AND it's at/under the configured price threshold; reached
                  only for PriorityPlus, and only once the Priority rung has already refused.
```

PriorityPlus's predicate literally calls Priority's own rung function first, every time — not
an approximation that merely looks equivalent — so whenever the EV doesn't need (or can't
cheaply get) new grid, PriorityPlus gets Priority's exact floor and timing discipline, never a
separate, looser path.

**Outside PV hours** (`CurrentPVPeriod` is `AfterPV` or `BeforePV`), this ladder governs
real-time scheduling directly: a NEW session searches for the latest start before the next
sunrise that still satisfies it (minimizing how long the battery sits drawn down overnight,
since nothing recharges it before sunrise), and an already-ACTIVE session is stopped at dusk
once past its own anti-oscillation latch — unless, for PriorityPlus, it's genuinely drawing a
new, cheap grid charge right now (re-checked every cycle, not assumed). The Optimal rung is
never checked explicitly in this path: PV is zero by definition outside PV hours, so "PV alone
covers it" can never apply there regardless of input.

**During PV hours**, a new session's length is found via `FindMax`'s binary search: Step 2
(`PriorityRung`, shared by both modes) runs first; PriorityPlus skips it in favour of Step 3
(`IsGridCheapVs(baseline)` alone) whenever the baseline survives overnight unassisted — proven
safe because `!HasNewGridVs(T) ⟹ IsGridCheapVs(T)` for any candidate `T`, so Step 3 can never
return a shorter session than Step 2 would have. **Known gap:** Step 3's predicate never calls
`PriorityRung`/`IsOvernightMinSocOk` on the candidate itself, unlike the evening/pre-dawn path
above — unexercised so far (daytime margins are typically generous) but the same class of issue
the evening-path fix addressed; flagged, not yet fixed, since folding Step 2/3 into the same
`FindMax` call needs its own check that `HasNewGridVs`/`IsGridCheapVs` stay monotone in the
END-search direction (they're known NOT to be monotone in the START-search direction the
evening path uses, which is why that path is a linear scan, not a binary search).

`HasNewGridVs`/`IsGridCheapVs` compare against the full simulated timeline, not just the
overnight window — a slot either side of sunset counts as "new" if it wasn't in baseline.

---

## Key files

| File | Role |
|---|---|
| `PVControl.cs` | NetDaemon app entry point; registers HA entities; 15-second cron |
| `PVControlCommon.cs` | Static singleton holding shared dependencies (IHaContext, EntityManager, Config, Logger) |
| `HouseEnergy.cs` | Core model: predictions, simulation, scheduling decisions, prices |
| `Simulator/EnergySimulator.cs` | Pure simulation engine |
| `Simulator/SimulationInput.cs` | All inputs to the simulation (snapshot in time) + PV-window helpers |
| `Simulator/SimulationResult.cs` | All outputs: slot timeline + pre-computed PV windows, SoC peaks, force-charge slots |
| `Simulator/ExtraLoad.cs` | A load injected into a simulation (name, power, time window) |
| `Managers/SchedulableLoadConfig.cs` | YAML config + `PVConfig` partial |
| `Managers/SchedulableLoadRuntime.cs` | Live runtime state per load |
| `Predictions/NetEnergyPrediction.cs` | PV − load + `WithRunningAvgCorrection` (day-scale + near-term ramp) |
| `Predictions/` | 4 prediction classes + abstract base `Prediction` + `PredictionContainer` |
| `Simulator/LoadSchedulingDecision.cs` | Schmitt-trigger logic. `Decide()` is used only via unit tests (main flow uses the simulation oracle); `PrioritySoCGateOk()` is wired into `HouseEnergy.FindLoadWindow`'s live Priority/PriorityPlus SoC gate |

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
