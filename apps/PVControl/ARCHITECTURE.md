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
- `HasNewGridVs(baseline)` / `IsGridCheapVs(baseline)` — used by load scheduling: slots where
  this simulation imports more grid energy than the baseline, and whether all of them are priced
  at/under `ForceChargeMaxPrice`

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
   (each load's test simulations are compared against its grid import — only the import a
   load ADDS counts as that load's grid use)

4. For each schedulable load (highest Priority first):
       FindLoadWindow(load, baseInput, baseline)
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
| `Optimal` (= 10) | PV only — never battery, never grid |
| `Priority` (= 20) | Optimal + may draw the battery down to its configured minimum |
| `PriorityPlus` (= 30) | Priority + one additional bonus ruleset for cheap grid import — see below |
| `Emergency` | Run immediately, ignore battery/PV conditions |

Optimal/Priority/PriorityPlus are deliberately numbered with gaps (10/20/30), not sequential —
room for a future mode to slot in between without renumbering anything else. `Off`/`Emergency`
are unrelated, independent gates and don't need to fit this scheme.

### FindLoadWindow logic

Off/target-reached/Emergency are unconditional short-circuits (skip / skip / ChargeNow=true, no
simulation check).

Optimal, Priority, and PriorityPlus are each a **complete, self-contained ruleset** — not
separate functions with bonus clauses blended in. They climb a numbered ladder, from the bottom:

```
if (mode >= Optimal)      run Optimal's complete ruleset; if ChargeNow, done.
if (mode >= Priority)     run Priority's complete ruleset; if ChargeNow, done.
if (mode >= PriorityPlus) run PriorityPlus's one bonus ruleset; if ChargeNow, done.
if (nothing above ever called SetResult) fall back to the generic "No valid window".
```

Each ruleset runs in full and produces a result (`ChargeNow` true or false, with or without a
deferred prediction) exactly as if it were the only rung that mattered. `ChargeNow == true` stops
the climb immediately. Otherwise every tier the mode is entitled to try still runs to completion
— a tier's own "no" (even just a deferred prediction, e.g. "expected start ~06:30") is never
silently discarded in favour of the next tier's attempt, and the generic fallback at the very
end only fires if literally nothing above it ever called `SetResult` — tracked by a single
`decided` flag set inside `SetResult` itself, checked once at the end, rather than by comparing
`mode` against each tier's own number a second time.

Optimal's ruleset doesn't decide anything *about*
Optimal mode — it just answers "would PV alone cover this," which is equally meaningful
regardless of which outer mode invoked it, so Priority and PriorityPlus genuinely run it first,
unmodified (only its old `if (mode == Optimal)` wrapper is gone, replaced by the climb itself).
Priority's ruleset is shared verbatim by PriorityPlus too — same floor, same late-start/dusk-stop
timing discipline, same binary-search session length — PriorityPlus never sees a looser version
of it.

**Priority's ruleset** (used identically by both Priority and PriorityPlus): outside PV hours
(`CurrentPVPeriod` is `AfterPV` or `BeforePV`), a NEW session searches for the latest start
before the next sunrise that stays safe (minimizing how long the battery sits drawn down
overnight, since nothing recharges it before sunrise), and an already-active session is stopped
at dusk once past its own anti-oscillation latch. During PV hours, session length is found via
binary search, requiring `IsOvernightMinSocOk() && !HasNewGridVs(baseline)` — no new grid
import, ever, under Priority's own rule. The SoC floor (live gate and overnight check) follows
`EnforcePreferredSoC` like every mode: preferred minimum when on, absolute minimum when off.

**PriorityPlus's bonus ruleset** (`RunPriorityPlusBonusRuleset`/`SessionPlanner.FindEarliestPriceAwareStart`):
tried only once Priority's ruleset above has already concluded it can't charge this cycle. Day
or night doesn't matter here — only price. It scans forward from "now" in 15-minute steps,
re-simulating the EV at each advancing candidate start, for the earliest one where the candidate
either needs no new grid at all (vs. baseline — same comparison Priority's rule uses) or needs
new grid at a price at/under the configured threshold. Advancing matters because delaying the
EV's own simulated start changes what the simulator's `NeedToCharge` logic computes for it (a
different deadline, possibly a different, cheaper slot) — checking once at "now" isn't enough.
Bounded by the published import-price horizon (today + tomorrow once published); once prices run
out there's nothing left to evaluate, and the scan gives up.

**Planned follow-up sessions** (`SessionPlanner.PlanFollowUpSessions`, PriorityPlus only): after the
climb, if the current/next session doesn't reach the target level, the same price scan plans the
following session(s) — each judged against a baseline that already contains the earlier ones — up
to 3, within the price horizon. Stored in `SchedulableLoadRuntime.PlannedSessions` and published as
the `planned_sessions` attribute (also reflected in `charge_timeline`, `charge_end_predicted` and
`charge_end_predicted_soc`). Preview only: never part of the control simulation; every cycle
re-decides `ChargeNow` from scratch.

`HasNewGridVs`/`IsGridCheapVs` compare grid import energy slot by slot over the full simulated
timeline, not inverter modes: a load can add import to a slot the baseline already supplies from
the grid (force_charge, or grid_only holding the battery at its floor while waiting for the
charge window), and that added import is the load's own grid use at that slot's price.

Session bookkeeping (`SessionStartTime`/`Mode`/`Settings`, which the min-on latch reads) is
updated once per cycle from the FINAL decision (`FindLoadWindow` wraps `DecideLoadWindow`), so a
lower ruleset's "no" that a higher one overrides in the same cycle doesn't end the session.

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
