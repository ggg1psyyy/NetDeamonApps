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

**NeedToCharge grid charge:** starts when the no-charging trajectory's trough (before PV
recovers) reaches the SoC floor; once charging it continues until that trough is
`NeedToChargeHysteresisPct` (5 %) above the floor, and the charge target lifts the trough to
exactly that level. The band must clearly exceed the live SoC's 1 % resolution — with 1 % every
SoC tick restarted the charge (28 bursts of 30–60 s on 2026-10-19). The charge goes into the
cheapest quarter plus the cheapest quarters under `ForceChargeMaxPrice` needed for the deficit.

Two additions since 2026-10-22 (the 02:00 charge fell ~3 % short — the night used ~340 W instead
of ~260 W — and was topped up at 07:45 for 0.426 €/kWh):
- **Forecast margin** (`ForecastMarginPct`): floor and target are raised by +1 % per 2 h until
  the low (max 4 %), in the start/stop condition and the charge target alike — a night charge
  ~7 h before the low aims ~4 % higher; a low an hour away gets none.
- **Charge vs hold** (`ChargingBeatsHolding`): the cheapest quarter is only charged in if it's
  cheaper than the hours to bridge (latest charge time → PV takeover), round trip (efficiency²)
  included — or under `ForceChargeMaxPrice`. Otherwise the floor is held with `grid_only`,
  which buys only the load.

**This same function is called many times per cycle** — once as a baseline, then once per
schedulable load candidate window, then once more for the final combined result.

---

## Predictions (4 parallel forecasts)

All predictions produce a `Dictionary<DateTime, int>` indexed in 15-min steps over today + tomorrow.
Values are in **Wh per 15-min slot**.

| Name | Source | What it forecasts |
|---|---|---|
| `Prediction_Load` | SQLite history (avg per local hour of day, recent days weighted more) | House base load |
| `Prediction_PV` | OpenMeteo via HA sensors (today + tomorrow kWh) | Solar production |
| `Prediction_NetEnergy` | PV − Load (with running-average correction) | Net surplus/deficit |
| `Prediction_BatterySoC` | Simulation output | Battery SoC % per slot |

`HourlyWeightedAverageLoadPrediction` strips historical EV/car-charging from the base load
(`excludeScheduledLoads: true`) so scheduled loads can be added back as `ExtraLoad` without
double-counting.

### Weather-aware load forecast (`LoadModel/`, `PVControl.yaml` → `LoadForecast`)

Chosen by backtest (`tools/LoadBacktest`, 376 nights 2025-09 → 2026-10): −15 % night error
(22:00–08:00, which sizes the grid charge) against the hour-of-day average, −20 % in winter.
- `WeatherForecast` — hourly forecast via `weather.get_forecasts` (Pirate Weather, 168 h), fetched
  every `WeatherRefreshMinutes`, kept when a fetch fails; health `degraded` after 3 h.
- `HeatDemandModel` — the house's heat demand (reusable, e.g. for a heating planner): heat pump
  energy without warm water per local hour = a_h + s·b_h·HDH, HDH = max(0, 13 °C − 24-h mean
  outside temperature), weighted least squares (half-life ~130 days). s scales the heating part to
  the last 48 h (curve changes, heating winding down) — not within 48 h after heating was switched
  on. Heat pump mode not in `HeatingModes` → only the recent standby level.
- `WeatherLoadModel` — base load (hour-of-day average of the load without the heat pump, exp(−days/10))
  + half its mean deviation over the last 48 h (±25 %) + `HeatDemandModel`. Identical to the
  backtested M2 (the tool compiles `LoadModels.cs` itself).
- `LoadHour.FromRow` — DataLogger rows → load hours; structurally impossible rows (negative energies,
  warm water above the heat pump, …) are skipped.
- `WeatherLoadPrediction` — the `Prediction`: fits on 400 days of DataLogger history, measured
  temperatures for past hours and the forecast after, recomputed hourly and after a new weather
  forecast. Without weather forecast or history it delivers the average it wraps (`Source`).
- `LoadForecast.Mode`: `Average` (off), `Shadow` (default: computed, published in
  `sensor.pv_control_load_forecast`, not used), `Model` (becomes `Prediction_Load`, so simulation,
  net energy and charge sizing use it; a fallback to the average is a health issue).
- `NightForecastTracker` — shadow comparison: at 21:00 both night forecasts are noted, after 08:00
  scored against DataLogger's rows; the last 30 nights in the sensor's `nights` attribute (restored
  after a restart).

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
   Load's day rollover (NeedsDayRollover) is logged as information; only a failed
   recalculation is an error (once), retried every cycle — or every 5 min while invalid.
   All predictions get the same `now` (UpdateData(now) → PopulateData(now), validated against
   it): with separate clock reads a cycle at midnight built windows for different days

2. Build baseInput  ← all predictions + prices + battery state + user settings

3. baseline = Simulate(baseInput)
   (each load's test simulations are compared against its grid import — only the import a
   load ADDS counts as that load's grid use)

4. Warm water (if configured): replan when due (WarmWaterController.ReplanReason), then
   baseInput += its heating runs and baseline = Simulate(baseInput) again — the tank needs
   heating whatever the other loads do, so they plan against a baseline that contains it

5. For each schedulable load (highest Priority first):
       FindLoadWindow(load, baseInput, baseline)
       → writes load.ChargeNow / ChargeReason / PredictedEnd / ExtraLoads

6. finalInput = baseInput + all found ExtraLoads

7. _simulationResult = Simulate(finalInput)
   → this is what ProposedState reads from
   → also used to update Prediction_BatterySoC
```

After RunSimulation, `PVControl.ScheduledOperationsCore` lets the warm-water controller act on
its plan (`ActAsync`: start a one-time heating, end a boost) and publish it.

**Battery SoC dropouts.** `BatteryState.BatterySoc` keeps the last good reading while the SoC
sensor is unknown (`HeldReading`), like battery capacity and minimum SoC. Before, an unknown SoC
read as 0 % — an empty battery to the simulation: live 2026-10-21 13:28:02–13:28:17 the SolaX
sensors dropped out, the 13:28:15 cycle went `force_charge` and the inverter drew 6.4 kW from the
grid. After `SoCMaxHold` (2 min) without a reading the cycle decides nothing from the battery:
it publishes mode `normal` (the inverter's own automatic, `charge_Reason: SoCUnavailable`,
battery charging and export enabled) and skips the rest — schedulable loads and warm water keep
their last state — until the SoC is back.

**Health.** After every cycle (also a failed one or the SoC fallback) `PVControl.PublishHealthAsync`
collects the problems of this cycle (`HealthChecks`: SoC, inverter data, prices, forecasts, cycle
failures, warm-water heat pump, DataLogger's newest hourly row — read every 5 min) and
`HealthMonitor` debounces them: counted after 30 s present, cleared after 1 min gone, an issue
with `EscalateAfter` becomes an error after that long. Published as `sensor.pv_control_health`
(worst level, issues, `last_cycle`); the HA notification automation is `HA/Health.yaml`.

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

**PriorityPlus's bonus ruleset** (`RunPriorityPlusBonusRuleset`/`SessionPlanner.FindBestPriceAwareStart`):
tried only once Priority's ruleset above has already concluded it can't charge this cycle. Day
or night doesn't matter here — only price. It simulates the EV at every 15-minute start from
"now". A start passes when it needs no new grid (vs. baseline) or new grid only at/under the
price threshold. Of the passing starts the earliest wins, unless a later one is cheaper overall
by at least 1 % (`DefaultMinSavingFraction` — keeps it from waiting on forecast noise). "Cheaper overall" is `NetGridCostVs`: extra import
cost minus export earnings over the whole simulation, plus battery energy missing at the end
valued at the mean import price — so PV energy the EV uses counts at the export price it would
otherwise have earned. A running session is never stopped to wait for a cheaper start.
Without a deadline, starts are bounded by the published price horizon and sessions must end
`ConsequenceMarginHours` (12 h) before the simulation end (their grid effects must be visible).

**Ready-by deadline** (`time.pv_control_<load>_ready_by` + `switch.pv_control_<load>_enforce_ready_by`;
MQTT time entities need HA ≥ 2026.5; PriorityPlus only; off unless the switch is on):
only starts that finish by the next occurrence of that time are considered, and the deadline
beats the price threshold — if none passes, the cheapest start that still finishes in time is
taken (`DeadlineCheapest`), and if even now is too late, it starts now (`DeadlineNow`). A change
bypasses the min-on latch like the other settings in `SessionStartSettings`.

**Planned follow-up sessions** (`SessionPlanner.PlanFollowUpSessions`, PriorityPlus only): after the
climb, if the current/next session doesn't reach the target level, the same price scan plans the
following session(s) — each judged against a baseline that already contains the earlier ones — up
to 3, within the price horizon. Stored in `SchedulableLoadRuntime.PlannedSessions` and published as
the `planned_sessions` attribute (also reflected in `zz_charge_timeline`, `charge_end_predicted` and
`charge_end_predicted_soc`). Preview only: never part of the control simulation; every cycle
re-decides `ChargeNow` from scratch.

`HasNewGridVs`/`IsGridCheapVs` compare grid import energy slot by slot over the full simulated
timeline, not inverter modes: a load can add import to a slot the baseline already supplies from
the grid (force_charge, or grid_only holding the battery at its floor while waiting for the
charge window), and that added import is the load's own grid use at that slot's price.

**Min-off latch** (`MinOffMinutes`, counterpart of the min-on latch `MinWindowMinutes`): in
`FindLoadWindow`, a restart within MinOffMinutes of the last stop is held back — ChargeNow stays
false and the planned session moves to the end of the off-time ("Off-latched …"). Stops are
never delayed. Bypassed by a mode change, a change of `SessionStartSettings`, or an availability
change. The end of either latch triggers a replan.

**Replanning** (`ReplanPolicy`, per-load `ReplanIntervalMinutes`, default 5): every 15-s cycle
still runs the house simulation, the final simulation and the inverter command, but a load's
`FindLoadWindow` (the expensive part) only runs when due — interval elapsed, a requested replan
(any user change, availability change, new prices, house load off its forecast for 5 min via
`LoadDeviationTracker`), a new quarter-hour, house battery SoC or the load's level changed, or
the load started/stopped drawing power. In between, its last decision (ChargeNow, sessions)
stays and is simulated as is. The load's live safety checks live inside `FindLoadWindow`, so
each of their inputs is a trigger. `last_plan` / `last_plan_reason` attributes show when and why.

**Learned load characteristics** (`number.pv_control_<load>_energy_per_level`,
`number.pv_control_<load>_charge_power`): both start from the YAML values (`EnergyPerLevelUnitKwh`,
`AvgPowerW`), are learned while the load draws power (`ActualPowerEntity` above
`MinActivePowerW`), persisted in those entities, and can be set by hand (e.g. new wallbox).
Energy per level is meter-side (charger losses included): `LevelEnergyLearner` measures kWh
between observed whole level steps (≥ 3 levels) and blends it with the stored value weighted as
20 levels. Charge power is a 10-minute time-constant average of the measured power and replaces
`AvgPowerW` as the planning power when the load isn't running.

The min-on latch and the PriorityPlus scan read last cycle's session (`previousLoads`/
`previousEnd`, captured at the start of `DecideLoadWindow`), never `load.ExtraLoads` mid-climb:
a lower ruleset may already have overwritten it this cycle (Optimal's "scheduled tomorrow").
The scan prefers last cycle's planned start while it stays cheaper than the earliest start, so
forecast noise can't flip a deferred plan to "start now" for a single cycle.

Session bookkeeping (`SessionStartTime`/`Mode`/`Settings`, which the min-on latch reads) is
updated once per cycle from the FINAL decision (`FindLoadWindow` wraps `DecideLoadWindow`), so a
lower ruleset's "no" that a higher one overrides in the same cycle doesn't end the session.

---

## Warm water (`WarmWater/`)

The warm-water tank isn't a schedulable load: its level doesn't stay where it's left — it loses
heat all the time, faster the warmer it is. So the question isn't "reach a target by a time" but
"be warm enough in every comfort window at the lowest cost". The heat pump's own automatic is
meant to be set low (e.g. 20 °C) as a safety net; PVControl heats with one-time heatings
(`number.wp_solltemperatur_warmwasser_einmalig` + `switch.wp_einmalige_warmwasser_erzeugung`;
the heat pump stops heating at the setpoint, but the switch stays on until switched off).

**`TankModel`** — learned daily from 10 days of HA history (`WarmWaterController.RefitAsync`):
- heating rate (K/h): total rise / total time of the heating runs ≥ 8 min (measured ~17 K/h at any temperature)
- standby loss `k·(T − ambient)`, at least 0.2 K/h: linear fit of the drop over one-hour windows
  away from heating runs (+40 min sensor settling). Measured 0.75 K/h at 41 °C, 1.57 K/h at 55 °C
- usage per hour of day: the mean extra drop of that hour beyond the standby loss (≈ 0 so far —
  the sensor barely shows tapping)
- power, linear over the run temperature (measured 1.65 kW at 42 °C, ~1.9 kW at 50 °C) — from
  the heat pump power during heating runs, time-weighted
- any part with too little data keeps the measured defaults (`TankModel.Default`)

**`WarmWaterPlanner`** (pure):
- `HeatingPricesPerKwh` — what a kWh of heating costs in each slot, from the house simulation:
  the heat pump drawing in just that slot vs the baseline, by `NetGridCostVs` (lost export for PV
  surplus, refill cost for battery energy, import price otherwise). Probed per 30 min (96
  simulations, ~0.6 s).
- Unpublished hours (tomorrow before ~13:00) are priced from the simulation's estimated prices
  (see Price pipeline); the plan sensor's `prices_known_until` shows where published prices end.
- `WithOutsideEfficiency` — × (1 + `EfficiencyChangePerK` · (mean − outside temperature)), from
  PVControl's weather forecast (default 3 %/K, measured COP 3.9 → 6.3 over 15 K): a warm
  afternoon beats a cold evening; relative to the horizon's mean, so the cost level stays.
- `Plan` — dynamic program over the 15-min slots × tank temperature (0.1 K buckets) × "heated
  the whole last slot". Per slot: off, or heat for 1/10 … 10/10 of it (so a run can stop between
  slot boundaries). Cost = heating energy × slot price + `RunStartCostEur` per run start + 10 €/K
  per slot below a comfort window's minimum (so a window is only missed when it can't be met).
  Each state keeps its path's exact temperature, so bucket rounding never accumulates. A run that
  can't be stopped is first heated to its target (`ForcedTarget`, see below). Heating early only wins
  when the price difference beats the extra standby loss at the higher temperature.
- `PredictAutomatic` — the heat pump's own automatic (setpoint − 5 K → setpoint), the forecast
  while PVControl doesn't control the tank, and the baseline the plan's cost is compared with.

**`WarmWaterControl.Decide`** (pure) — the one-time switch is the command, the "active" sensor
the feedback. Every compressor start wears the heat pump, so **a heating that runs is never
stopped**: it continues to its setpoint, which may only be raised. In `Auto`:
- inside a planned run, switch off, the run raises the tank ≥ 3 K (`MinRunK`) and the last
  heating stopped ≥ 60 min ago (`MinOffTime`) → **Start** (setpoint = target rounded up to 0.5 K,
  then switch on)
- heating, the plan's run wants a higher target → **SetSetpoint** (never lower)
- heating, whatever the plan says now → nothing (no stop mid-run)
- switch on but nothing heats → the heating finished (the switch stays on after the setpoint is
  reached) or our start isn't taken yet: **Stop** — but only 10 min after PVControl's own start
  (measured: "active" follows the switch in 5–7 s, the compressor ~2.5 min later)

Boost (any mode): start towards the boost temperature now (not held back by the off-time), or
raise a running heating's setpoint; ends when reached, when the heat pump stopped after running,
or after 3 h — a heating going higher than the boost keeps running. `Off`/`PlanOnly` don't touch
the switch otherwise. At most one command per 3 min; nothing while the heat pump entities are
unavailable.

In the plan, any heating that runs is forced to its target first (`ForcedTarget`: the boost, the
one-time setpoint, or the automatic setpoint), so the plan matches what will happen.

The control acts with the clock read *after* the replan (`ActAsync(DateTime.Now)`): the plan's
current run starts at the planning time, and with the cycle's earlier `now` (live 2026-10-20
16:26: 16:26:14.0 vs a run starting 16:26:14.9) it found no current run and switched off a
heating 3 min after starting it. The plan's run times also don't include the start-up delay, so a
run still heating past its planned end triggers a replan ("run outlasted its plan").

**Modes:** `Off` (heat pump automatic, PVControl only forecasts it), `PlanOnly` (default — plans
and publishes next to the automatic forecast, doesn't touch the heat pump), `Auto`.

**House simulation:** the automatic forecast (Off/PlanOnly) or the plan (Auto, boost) goes into
the simulation as ExtraLoads; the DataLogger `warmwaterenergy` column is subtracted from the
house load forecast, and the measured heat pump power while warm water heats from the house load
running average — same as a schedulable load.

**Replanning:** first plan, setting change, price update, new quarter-hour, heating start/stop,
tank model update, at the latest every `ReplanIntervalMinutes` (15).

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
| `WarmWater/TankModel.cs` | Learned tank model (heating rate, standby loss, usage, power) and its fit |
| `WarmWater/WarmWaterPlanner.cs` | Heating prices per slot, the dynamic-program plan, the automatic forecast |
| `WarmWater/WarmWaterControl.cs` | Pure control decision (start a run, boost) and the daily comfort windows |
| `WarmWater/WarmWaterController.cs` | HA side: history fit, entities, replanning, service calls |
| `WarmWater/WarmWaterConfig.cs` | YAML config + `PVConfig.WarmWater` |
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

**Unpublished hours.** The simulation runs to the end of tomorrow, but tomorrow's EPEX prices
arrive ~13:00. Until then the simulator's price scans (NeedToCharge window, cheapest quarter, …)
saw no tomorrow at all, and the cost comparisons (`NetGridCostVs`: warm-water heating prices, EV)
priced it with the flat mean of the known prices. The simulation input therefore gets `PriceListImport/Export.WithEstimatedPrices(end of tomorrow)`:
missing quarters take the price 24 h earlier (chained; the daily pattern repeats well enough),
never below 0 (negative-price handling only follows published prices).
`SimulationInput.PublishedPricesUntil` keeps the real end: the EV session planner
(`PriceHorizonEnd`) doesn't commit to estimated prices, and the health check reads the published
lists directly.

---

## HA entity auto-creation

`PVControl.RegisterControlSensors()` creates all dynamic sensors/selects/numbers via MQTT.
For each schedulable load:
- If `Mode` not in YAML → creates `select.pv_control_<slug>_mode`
- If `TargetLevel` not in YAML → creates `number.pv_control_<slug>_target_level`
- Always creates `binary_sensor.pv_control_<slug>_charge_now`

`<slug>` = `Name.ToLower().Replace(' ', '_')`, e.g. `"EV Charger"` → `ev_charger`.

For the warm-water tank (`WarmWaterController.RegisterEntitiesAsync`, slug from `WarmWater.Name`,
`"Warm Water"` → `warm_water`):
- `select.pv_control_warm_water_mode` (Off / PlanOnly / Auto, default PlanOnly)
- `number.pv_control_warm_water_comfort_temp`, `number.pv_control_warm_water_boost_temp`
- `button.pv_control_warm_water_boost` (until 2026-10-20 a switch, removed on startup; boost state in `sensor.pv_control_warm_water_plan`)
- per comfort period: `time.pv_control_warm_water_<period>` and `switch.pv_control_warm_water_<period>_enabled`
- `sensor.pv_control_warm_water_plan` — next heating as state; plan, automatic forecast, costs,
  temperature forecast, model and control reason as attributes (see README)

All command entities are handled by `WarmWaterController.HandleCommandAsync` (called from
`PVControl.UserStateChanged`), which echoes the value and requests a replan.
