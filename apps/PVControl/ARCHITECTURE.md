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

**NeedToCharge grid charge:** the charge target lifts the no-charging trajectory's trough
(before PV recovers) to floor + forecast margin + `ChargeTargetBufferPct` (1 %); a running charge
stops there. A charge starts when the trough reaches floor + margin − `StartBelowPct` (1 %),
never below the floor itself — a 2 % band between start and stop. The charge goes into the
cheapest quarter plus the cheapest quarters under `ForceChargeMaxPrice` needed for the deficit.

Why the band sits around the target: with a 1 % band every SoC tick restarted the charge (28
bursts of 30–60 s on 2026-10-19). A 5 % band fixed that, but it was added on top of the start
level and was also the charge target (so the simulated charge and the live stop agreed): every
charge bought ~0.6 kWh extra, and on 2026-10-22 the morning low was planned at 29 % with a 20 %
floor (20 + 5 + 4 margin). Now the band costs no energy: a stopped charge only restarts after a
real 2 % drop of the trough. A time lock against restarts was considered and dropped — 15-min
prices can differ wildly from one quarter to the next, and a lock could push a needed top-up out
of the cheap running quarter into a more expensive one.

**Trough up to the PV peak after the critical point** (2026-10-24, heating just switched on, 44 %):
the charge target lifts the no-charging trajectory's trough before the next PV peak, and that peak
was the first maximum of the whole look-ahead. A small PV bump in the afternoon before a foggy day
was that maximum, so the trough search ended the same afternoon and the deficit was 0: the plan
showed force_charge 13:00–15:00 that took only the PV surplus, then held the floor with grid_only
from 04:00 until 13:00 the next day at ~0.42 €/kWh instead of charging at 0.353 today. The peak is
now searched from `LatestChargeTime` on (`FoggyDayTopUpTests`); without a real recovery the
window stays 24 h.

Two additions since 2026-10-22 (the 02:00 charge fell ~3 % short — the night used ~340 W instead
of ~260 W — and was topped up at 07:45 for 0.426 €/kWh):
- **Forecast margin** (`ForecastMarginPct`): floor and target are raised by +1 % per 2 h until
  the low (max 4 %), in the start/stop condition and the charge target alike — a night charge
  ~7 h before the low aims ~4 % higher; a low an hour away gets none.
- **Charge vs hold** (`ChargingBeatsHolding`): the cheapest quarter is only charged in if it's
  cheaper than the hours to bridge (latest charge time → PV takeover after it, `PvTakeoverAfter`:
  PV above house load + extra loads), round trip (efficiency²) included — or under
  `ForceChargeMaxPrice`. Otherwise the floor is held with `grid_only`, which buys only the load.
  The span used to end at the first PV takeover after *now*: at midday that's the PV running now,
  so a low tonight fell back to one hour after it. Live 2026-10-26 13:30 (44 %, low ~01:30): 0.3649
  today lost against that one cheap night hour (~0.377), while the same plan at 00:30 compared
  with the hours to tomorrow's PV incl. the morning peak (~0.415) and charged at 0.3727 — the
  battery carried the night and the morning either way, so the cheaper midday quarter is right.
  The same fix holds the floor instead of charging at the same price when only later hours are
  cheap (an afternoon EV drain: grid_only at 0.30 until a 0.05 hour, not a 0.30 charge + loss).

**Mode order** (`ComputeMode`): negative import price → opportunistic discharge → user force
charge → NeedToCharge → normal. A negative export price is not a step but a modifier
(`InverterState.ExportAllowed = false`): it used to return house_only early, which blocked the
cheap force charge and the NeedToCharge safety net during every negative-export window — and
negative export and cheap import come from the same cause (midday solar surplus), so it starved
the cheap charge on exactly the sunny days it mattered. Exporting and grid charging are
independent, so only export is suppressed.

**Room for negative export** (`NegativeExportFillsBattery`): with import positive but export
negative (EPEX between about −9 ct and 0 with this tariff), a battery filled from PV in the
morning has no room left when export turns negative — the PV surplus is curtailed. The valley
logic of opportunistic discharge (feed in first, charge in the cheapest export window) covers
this, but only with `enable_opportunistic_export` on, which is off here. So before a negative
block later today PV surplus is fed in while 70 % (`NegativeExportSurplusShare`, forecast margin)
of the block's surplus, capped at the charge rate, covers the room to 100 %. It only reorders PV
use, never discharges to the grid. History: 2026-04-25/26 exported 16 kWh at −9 to −48 ct with a
full battery (before export suppression); since 2026-09-27 export is suppressed in those hours.

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
  Switched to `Model` on 2026-10-24, right after the heating was switched on: the average knows
  nothing about the heating and only follows it over days (20-day age weighting), so the first
  nights' grid charges would have been sized ~1 kWh short (night forecast 2.99 vs 4.05 kWh). The
  model had 2 scored nights then, mean error 0.11 kWh vs 0.28 kWh for the average.
- `HeatPumpModeTracker` — heating on/off and since when: an unavailable mode (KNX dropouts) keeps
  the last known one, and only a real change to a heating mode is "switched on" — HA's
  LastChanged also moves on "unavailable → heating", which would have switched the 48-h scaling
  off for two days after every dropout (audit 2026-10-22). Seeded at start from DataLogger's
  `heatingmode` column (last 72 h), so a restart keeps the switch time. A mode change triggers an
  immediate recalculation.
- Weather fetch failures are retried after 5 min, not every 15-s cycle.
- `NightForecastTracker` — shadow comparison: at 21:00 both night forecasts are noted, after 08:00
  scored against DataLogger's rows; the last 30 nights in the sensor's `nights` attribute (restored
  after a restart). The actual night total (`WeatherLoadPrediction.NightTotal`) counts real hours:
  the repeated hour at the end of DST twice, the skipped one in March not as missing.

### PV running-average correction (`NetEnergyPrediction.WithRunningAvgCorrection`)

Applied to the PV forecast both when computing `Prediction_NetEnergy` and when building
`SimulationInput.PVPredictionWh`. Two-stage:

1. **Day-scale ratio** (all today slots from the running one on):
   `ratio = actual_PV_energy_today_Wh / Σ(forecast midnight → now, running slot pro rata)`
   Corrects for persistent over/under-prediction (e.g. cloudy day).
   Skipped when forecast sum < 50 Wh (night/dawn/dusk). Clamped to [0.0, 2.0]. Tomorrow untouched.

2. **Near-term 4-slot ramp** (slot 0 = the running slot, applied after day-scale): the live
   15-minute PV average as a ratio to the forecast energy of those same 15 minutes, ramping to the
   day ratio: `slot[i] = forecast[i] × (dayRatio + (shortRatio − dayRatio) × (4 − i) / 4)`.
   With less than `NearTermRatioMinWh` (150 Wh) forecast in the window (dawn) the absolute
   average instead: `slot[i] = scaled[i] + (shortAvgPerSlot − scaled[i]) × (4 − i) / 4`.

"Actual PV energy today" is the inverter counter (0.1 kWh steps) made continuous with the PV
power since its last step (`CounterInterpolator`).

Load uses only the near-term ramp (no day-scale — load follows historical patterns, not weather).

**Why — quarter-boundary jumps and noise (2026-10-22).** A NeedToCharge grid charge stopped at
14:12 started again at 14:15:15: the trough moved ~5 % across a quarter boundary, more than the
5 % hysteresis band of the time. Two causes:
- The simulation starts with the running quarter (`RoundToNearestQuarterHour` floors) and
  simulated it in full from the current SoC — 12 min into a quarter with ~2 kW PV surplus that's
  ~+4 % SoC that had already flowed, gone at the next boundary. Now the running slot only counts
  its rest (`SimulationInput.SlotFraction`, in the slot loop and the baseline trajectory alike).
- The day-scale ratio counted the just-started quarter in full against ~0 actual energy, and that
  quarter itself got the raw forecast (the live average went to the slot after): ratio
  0.90 → 0.85, −163 Wh PV. Now pro rata, and slot 0 is the running slot.

The counter interpolation halved the 15-s noise of the PV ahead that afternoon (p95 102 → 55 Wh).

**Why the near-term ramp is a ratio.** In October PV goes from full to 0 in ~2 h; a trailing
average of the absolute power lags that slope — too much PV ahead in the evening, too little in
the morning. Backtest over 10 full days (2026-10-12…22, every minute of daylight, the forecast
as it was at that minute, real PV), mean error of the next hour's PV (Wh):

| Near-term variant | Morning (first 2 h) | Midday | Evening (last 2 h) | All | PV rest of day |
|---|---|---|---|---|---|
| Absolute 15-min average (before) | 111 | 239 | 154 | 198 | 859 |
| Absolute 30-min average | 124 | 256 | 190 | 218 | 873 |
| No ramp | 156 | 236 | 112 | 198 | 860 |
| Ratio live/forecast, absolute under 50 Wh in the window | 130 | 214 | 76 | 173 | 836 |
| **Ratio, absolute under 150 Wh forecast in the window** | **106** | **214** | **77** | **168** | **827** |

Reproducible with `tools/PVNearTermBacktest` (Node, reads HA history).

The 30-min average had first been chosen from a single afternoon (11:00–15:05, no steep edges)
— over full days it was worse everywhere. Remaining swings are real: a cloud cutting PV from 1.9
to 0.4 kW for 15 min still moves the PV ahead by ~1 kWh — information, not noise.

`RunningIntAverage` also expires values when nothing new arrives (keeping the latest one, the
current state). Sensors that only report on change kept old values otherwise: the EV CT went to
0 W at 14:13:46 the same day and stayed silent, the EV's ~1.7 kW stayed in its average and was
subtracted from the house load (`avg_house_load_now` down to −1.2 kW) until the next restart.

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
Level and attributes are two MQTT messages: published level-first, the automation (triggered by the
level) read the previous summary — 2026-10-28 "degraded: All checks ok", then "ok: DataLogger: last
hourly row …". The attributes go first now, and the automation waits 2 s before reading. Not `for:`
on the trigger: a hiccup shorter than it would send only the "ok" back. The level also flipped
because a restart started the monitor empty (12:19:46, NetDaemon re-initialised the app on HA's
reconnect: ok, 45 s later degraded again) — the issues that counted are restored from the sensor now
(`HealthMonitor.Restore`): a lasting problem stays, a gone one clears after its minute.

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

**Cheap window shorter than the session** (`StartChoice.Partial`): if no full-length start passes,
the earliest start whose session, shortened by `FindMaxSessionEnd` (the same binary search
Priority uses) to at least `MinWindowMinutes`, passes is taken; the follow-up preview plans the
rest. Live 2026-10-25 08:00: EV 18 → 25 % (2 h 37 min), house in force_charge in the only hours
under the 35 ct limit (08–10 h, 32 ct). Every full session ran past 10:00 on the house battery,
which the house bought back overnight at 36–38 ct, so nothing passed and the EV stood still
during the day's cheapest grid window. Charging just 08:00–10:00 adds import only at 32 ct.

**Ready-by deadline** (`time.pv_control_<load>_ready_by` + `switch.pv_control_<load>_enforce_ready_by`;
MQTT time entities need HA ≥ 2026.5; PriorityPlus only; off unless the switch is on):
only starts that finish by the next occurrence of that time are considered, and the deadline
beats the price threshold — if none passes, the same choice (`SessionPlanner.Choose`) is made among
all starts that still finish in time (`DeadlineCheapest` for a later one, else `DeadlineEarliest`),
and if even now is too late, it starts now (`DeadlineNow`). A change bypasses the min-on latch like
the other settings in `SessionStartSettings`. The ready-by branch used to take the cheapest start
alone, ignoring both the running session (never stopped for a later start) and the sticky plan:
2026-10-27/28, ready by 06:30, all night prices 0.355–0.40 over the 0.35 limit — the cheapest start
flipped with forecast noise between 23:00 and 01:45, and whenever "now" won the EV started, only to
be stopped by the next cycle after its 15-min min-on: ten starts between 21:30 and 03:34 (the target
was still reached at 05:00). The supply choice flipped too (03:53–05:00 grid_only/normal every
~10 min, the EV partly from the battery a 03:15 night charge had filled): a session running directly
from the grid now stays so (`ChooseSupply(keepFromGrid)`).
Candidates are compared at equal energy: a session starting in the running slot lasts its duration
from now (`SessionPlanner.SessionEnd`). The simulation counts only the rest of the running slot, so
"start 21:15, end 22:45" at 21:28 charged ~77 of 90 min — 0.4 kWh less than every later start — and
won the scan as the cheapest at the evening's highest price (live 2026-10-28: 0.455 €/kWh against
0.376–0.384 at night).

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
never below 0 (negative-price handling only follows published prices), with a risk premium
(`EstimatedPricePremium`, default 5 %: import ×1.05, export ×0.95; not compounded along the chain)
— live 2026-10-22 13:15 a charge moved from today 14:00 (0.3327, published) to an estimated
02:00 (0.3202, 3.9 % cheaper); with the premium the published slot wins.
`SimulationInput.PublishedPricesUntil` keeps the real end: the EV session planner
(`PriceHorizonEnd`) doesn't commit to estimated prices, and the health check reads the published
lists directly.

`sensor.pv_control_price_publication` records when tomorrow's prices first show up
(`PricePublicationTracker`, last 14 days): HA's recorder drops the price sensors' attributes
(the price list is too large), so the publication times weren't recorded anywhere (2026-10-22: at
13:24 none of the three sources had tomorrow yet, against an assumed ~13:00–13:30). A time is only
noted after "not published" was seen the same day — not the time of a restart. PVControl reloads
the price list on any change of the price entities (`StateAllChanges`), attributes included.

When ha_epex_spot's data shows up (v4.1.0, `__init__.py`): each entry fetches once an hour at
hh:50 + a random 0–9 min, but the entities only take the fetched data at the next refresh —
hh:00 for an hourly source, also :15/:30/:45 for a 15-min one. The action `epex_spot.fetch_data`
fetches and refreshes at once (2026-10-22 13:59 was such a manual call). So an hourly source
always shows tomorrow on a full hour.

`HA/PriceFetch.yaml` therefore calls `epex_spot.fetch_data` every 5 min from 12:45 until a source
reaches the end of tomorrow. 2026-10-26: Energy-Charts showed tomorrow at 13:15 by itself, Awattar
only after a manual `fetch_data` at 13:24 (smartENERGY and ENTSO-E not yet then), while
`sensor.pv_control_price_publication` had read ~14:00 on the four days before. Until then the
13:00–14:00 quarters were decided on estimates — 2026-10-26 a charge flipped between 13:00 (0.3649,
published) and an estimated night (0.3665) for an hour; with the real prices the night was
0.373–0.386.

Price source choice: the sources used to be a priority list (first with data wins). On
2026-10-22 at 14:11 Awattar (hourly) and Energy-Charts (15 min, not configured then) had
tomorrow, smartENERGY didn't — a source that has tomorrow should win, whichever it is and
wherever it's listed. Now the source whose data reaches furthest is used (`PriceManager.FurthestSource`,
tie → list order). This is safe because the sources carry the same EPEX prices: hour means of
Energy-Charts' quarters vs Awattar's hours differed by at most 0.001 ct over 48 h, and the
provider bills hourly (`ImportPriceResolution: Hourly`), so a 15-min source never brings finer
prices. The health check's `price_source` now means "a source has no data at all" instead of
"not the first one is used".

**One calculation, one function** (2026-10-23). A sweep for formulas written more than once
moved them into `apps/Calculations.cs` (time ranges, W/Wh, SoC/Wh, charge time, weighted mean,
step functions) or onto the type they belong to (`PriceList.TryGetPrice`, `ExtraLoad.Between`/
`EnergyWh`, `SimulationInput.SlotEnergyWh`/`RelevantPVEdge`/`MaxChargeWhPerSlot`/`SimulateWith`).
The simulator's inverter modes are now parameter sets of two flows: `grid_only` = `Normal` with
no charge rate, `feedin_priority` = `Normal` without battery charging, `force_charge_grid_only`
= `ForceCharge` without PV. The separate `FeedinPriority` copy lacked `Normal`'s
`Max(0, energy − floor)`, so below the floor it "discharged" a negative amount.
`IsInPVPeriod` (decides `feedin_priority`) and `CurrentPVPeriod` are one calculation,
`SimulationInput.PVPeriodAt`: between the day's first and last relevant PV slot. `IsInPVPeriod`
took the first edge from the slot itself on, which made it "this slot has net PV" — a cloudy
slot at midday counted as outside the PV period.
The second pass (all apps) merged: gross import/export price (`PriceManager.GrossPrice`), the age
weighting and hour-of-day profile of both load forecasts and the backtest (`Mean.AgeWeight`/
`HourOfDayProfile`; the average forecast took the age from the time difference, so yesterday
afternoon counted as today), least squares of the heat demand and tank models (`Regression`),
the excluded load columns, the night window, the price-publication and night-score day lists
(`DayLog`), ISO timestamps (five parsers, three formats), the SQLite schema version/columns of
DataLogger and HeatingTest (`SqliteSchema`), DataLogger's hourly/daily catch-up loops, the next
time of day (`TimeRange.NextOccurrence`, ready-by and heating test) and rounding up to a quarter.
`tools/LoadBacktest` links `Calculations.cs`, so its "App" variant computes exactly what the app does.

**A running NeedToCharge charge never stops where starting one would begin it** (`EnergySimulator.Simulate`).
Each plan starts from the mode of the previous plan's first slot. While charging, NeedToCharge
uses the full forecast margin (else 1 % less) — a hysteresis meant to make continuing easier.
But the margin also moves the latest charge time and with it the price window, and in that other
window the running quarter could lose. Live 2026-10-25 11:30 (and 2026-10-22 09:00) force_charge
and normal then alternated every 15-s cycle, switching the inverter between manual and self-use
mode each time. A charge that would stop is now checked as if idle too; if that would start it, it continues.
That fix alone would have held force_charge at 95 % there, right before PV filled the battery: the
low was tomorrow 07:00 (floor + margin), and with tomorrow's prices still estimated (+5 ct) the
running quarter looked cheapest. So a NeedToCharge charge before a PV peak on the way to the low
is now capped to the room PV leaves (100 % − the look-ahead's peak; more would be clipped and push
PV out to the grid), and doesn't start at all if that leaves 2 % or less (`MinPvRoomPct`) — the rest
is bought after the peak.
Still at 12:15 the plan charged at 35.4 ct (limit 35) for a low of 13 % tomorrow 07:00 — above the
12 % floor, only inside the 4 % forecast margin; then an EV session (PriorityPlus) drew the battery
and kept it going. NeedToCharge always takes the single cheapest quarter, even over the limit; with
tomorrow's prices estimated (+5 ct) that is often "now". Above `ForceChargeMaxPrice` a charge now
only protects the floor itself (need and target without the margin); the margin is only bought at
or under the limit. Any load drawing the battery is covered by the same rule.

Live the same day, the first import cap build read the counters as Wh treated as kWh: 7 Wh in a
quarter counted as 7 kWh, the guard set the charge limit to 0 A, the battery stood at 95 % and the
PV went to the grid. Since then an impossible counter reading (over 45 kW mean) is replaced by
the estimate from the grid power, and the month's peak ignores it.

**PriorityPlus grid sessions run directly from the grid** (`ExtraLoad.FromGrid`, `SimulationInput.BatteryHeldForLoad`).
Live 2026-10-25 14:00 the EV (PriorityPlus, 2.2 kWh) was planned 06:00–07:08 from the house battery
(Priority's late start before sunrise, kept as the preferred start), and the house planned a night
charge 00:00–00:30 at 34.9 ct only to feed it: ~0.87 € with the ~10 % round-trip loss against ~0.77 €
directly from the grid — plus battery wear for charging one battery from another. The simulation
moved energy through the battery without loss, so both paths cost the same and whole-percent rounding
decided (3 ct for 06:00). Now each start of PriorityPlus's price scan and follow-up preview is supplied
by `SessionPlanner.ChooseSupply`: PV + battery if that needs no extra grid (like Priority — e.g. a
ready-by target in the morning from the battery), otherwise FromGrid: while it runs and PV doesn't cover
house + load, the battery is held (`grid_only`; the inverter can't feed the house and not the EV), or
the load runs along a force_charge the house needs in that quarter anyway; the look-ahead doesn't count
it against the battery. `NetGridCostVs` adds the round-trip loss of extra battery discharge
(`SimulationInput.RoundTripEfficiency`, also used by `ChargingBeatsHolding`).

**The charge limit changes as rarely as possible.** The Solax charge current register
(`battery_charge_max_current`) is stored in EEPROM (~100,000 write cycles, per the solax-modbus
integration's docs). On the first night (2026-10-26 05:37–06:06) the limit moved ~40 times in 0.1-A
steps — in normal mode, where the battery only charges from PV surplus and the limit has no effect.
It now only applies while the battery charges from the grid and moves in whole amps (raise ≥ 2 A).
The clean solution is the inverter's own peak shaving: remote control (`remotecontrol_*`, RAM, no
wear, needs autorepeat) with `remotecontrol_import_limit` caps the import itself.

**The inverter runs PVControl's modes in RAM** (`InverterCommand`, `HA/ApplyMode.yaml`, `HA/InverterSync.yaml`).
Every mode change used to rewrite the SolaX base work mode and manual mode (Manual Mode + Force
Charge / Stop / Discharge), and the import cap rewrote the charge current — all EEPROM registers
(~100,000 writes). The first night (2026-10-26) had ~20 mode changes, some only 15 s long, plus
~40 charge-current changes. SolaX's VPP protocol (official "SolaX_VPP function Definition of ESS",
registers 0xA0–0xA7 from ARM V1.45 on the X3-Hybrid G4; here ARM 1.47) is RAM-only. All modes now
use one VPP mode — 8, "Individual Setting – Duration Mode": PV limit + battery power for a run
time — not mode 9 (target SoC) as `force_charge_grid_only` did before: with the inverter ending a
charge itself at a SoC, "it left the mode" would be ambiguous (target reached, or a fallback; and
a target PVControl raised meanwhile). So PVControl alone decides when a mode ends; the run time
(1200 s, refreshed after 600 s while PVControl runs, NextMotion back to the base mode) only makes
the inverter fall back to Self Use by itself if HA or PVControl stop. The command carries a version
(`seq`); the script stores the version it applied, so the reconciler can tell a new command from a
fallback (re-applied at most 5 times per version, then blocked, notified, health error). The
writes go through the integration's `*_direct` entities, not its autorepeat loops (solax-modbus
2026.10.1 broke those, issue #2394). PVControl stays manufacturer-independent: it publishes the
command and reads the in-sync feedback; the register writes live in HA. The command is the
attributes of `sensor.pv_control_mode`, not a sensor of its own: the mode is the command, a second
sensor only duplicated it (the first draft had one, 2026-10-26).

**Bulk grid charges still use Manual Force Charge** (`InverterCommand.BulkFor`, `BulkChargeMinPercent`).
Measured 2026-10-26: VPP charges the battery exactly as commanded up to 4 kW, but caps at
5.07–5.11 kW (20.8–21 A) above it — mode 8 and mode 4 alike (mode 4: ~4.95 kW) — while Manual
Force Charge right after gave 30 A (7.4–7.6 kW). Every negative-price charge since spring (mode 9)
also stopped at ~5.1 kW, the Manual charges of the same period reached 7.46–7.48 kW. So it's the
firmware's VPP limit, not the integration. A night charge of half the battery at 5 kW takes 50 %
longer and reaches into dearer quarters, so a charge planned at ≥ 50 % of the battery (the
contiguous `force_charge` slots from its start) takes the Manual path: two EEPROM writes per session
(work mode + manual mode), not one per mode or current change. The choice is made at the start
and kept for the session, so a shrinking remainder doesn't switch paths mid-charge; it drops to
VPP while the import cap throttles (Manual has no power setting, only the EEPROM current).
`force_charge_grid_only` stays VPP: it only happens at negative prices with lots of PV, where
~5 kW from the grid with PV off beats 7.5 kW with PV running. Manual Mode has no run time, so the
reconciler switches it back to Self Use itself after 5 min without PVControl.

**The VPP limit falls with the SoC** (`Managers/VppChargeCurve`, `EnergySimulator.ChargesBulk`). The
"~5.1 kW cap" of 2026-10-26 was measured at 21 % SoC. 2026-10-27: 4.64 kW at 33 %, 3.95 kW at 48 %,
3.33 kW at 63 % (night and noon alike — with PV the grid only delivered ~2.2 kW), while 15 Manual
Force Charge sessions of the ten days before held ~7.45 kW from 10 % to 89 % and the BMS reported
35 A — so the inverter's remote-control path limits it, undocumented and without a register to
raise it. The plan assumed 7.35 kW per quarter, so a 1.7-kWh charge needed two quarters instead of
one and the live control restarted it in short bursts (six between 00:43 and 03:45). The path is now
chosen when a charge starts, by the power it needs: VPP if it gets the energy in within the quarters
of the same price, else bulk. The provider bills hourly (`ImportPriceResolution: Hourly`), so four
quarters at ~4 kW cost the same as one at 7.4 kW — VPP covers most charges without EEPROM writes;
with quarter-hourly prices the one cheap quarter usually needs the full power. The battery limit
counts PV in (VPP limits the battery power, not the grid's share). The command's power uses a
nominal voltage: the measured one in 5-V steps made 2–3 new versions per charge (7050/7200/7350 W).
A started charge runs at least a minute — RAM writes don't wear, but a stop 15 s after the start
(live 01:31:15–01:31:30) is noise.
An earlier VPP start beats a later bulk one (`VppNowBeatsBulkLater`): the N-cheapest-quarters rule
counts the energy needed from the SoC of each quarter, so with every quarter of discharge N can cross
the next whole number mid-hour — a sweep of 2,400 night plans found starts at :15/:30/:45 — and that
late in the block VPP may have too few quarters left. A start later in the block is fine while VPP
still manages it; when waiting one more quarter would need full power but starting now VPP gets the
energy in, the charge starts now (applied quarter by quarter: the latest start that stays on VPP).
Always starting with the block was the first version — it moved charges earlier that VPP managed
later as well. A charge that needs full power anyway keeps its natural start (live-like case: 38 %
at 00:00, target 100 % until PV — bulk from 00:00 or 00:15 alike).

`feedin_priority` and "battery charging off" stay EEPROM work-mode / charge-current writes: both
mean "cover the load from the battery, never charge it", which a fixed VPP power can't follow and
no SolaX VPP mode offers (6 charges and discharges, 7 only charges). HA keeps state history for
~10 days only, and no log held the mode, so how often they run over a year was unknown; in
2026-10-17…26 neither ran once, while the work mode was written 249 times (117 force charges, up
to 71 writes on a seesaw day). DataLogger v9 logs the mode, its minutes and the EEPROM writes per
hour/day, so that question has an answer next year.

**Import cap** (`ImportCap/`, `SimulationInput.MaxGridImportW`). The SNE-G-V (BGBl. II 299/2026, from
1.1.2027; Linz Netz starts between then and 30.6.2028) bills a monthly power price on the month's
highest 15-min mean import (§3, §6): one ct/kW in Jan–Nov 2027, then separate rates for the
part up to 10 kW and above it (§6(2)). A single quarter-hour therefore sets the month's power charge. The connection capacity of an existing connection is max(10 kW, contract, mean of the two
highest monthly 15-min peaks of 2026) (§34); exceeding it can cost a one-time fee per kW (§18(3)).
Measured 2026-10-25 09:30: 9.50 kW on the utility meter (the inverter's counter: 9.20 — its 0.1-kWh steps, hence the wM-Bus meter first)
from force charge 7.45 kW + EV 1.92 kW + house; last winter's *hourly* means reached 10.3–13.6 kW.
The battery's grid charge is the one big load that can be turned down smoothly, so it yields first,
then the EV (on/off only), then warm-water starts. The limit goes out as a sensor and the user's
automation applies it, like every other PVControl output; `MaxBatteryChargeCurrrentValue` stays the
hardware maximum (the Solax number now carries the limit; the old `MaxBatteryChargeCurrentEntity`
YAML key never bound because of the property's spelling). The guard works on the meter's energy
so far, not on power: only the quarter's mean counts, so a short spike can be balanced within it.
Nothing reacts at once (EV automation 1 min, inverter write, ramps): one reaction time instead
of per-device rules. A counter that doesn't change is valid (no import), so a source only fails
when unavailable or frozen while a later one counts (NetDaemon has no `last_reported`).
The month's peak is restored at start from its sensor and from the DataLogger (`maxquarterimport`
of the month, the higher one): the sensor alone was read in the base unit (2.82 kW → 2820), failed the
plausibility check and every restart started the month over — 2026-10-28 it showed 0.00 kW while the
real peak was 9.60 kW (26 Oct 00:00–01:00, close to the cap).
A wM-Bus receiver that loses reception stays online and keeps its last value (2026-10-28: antenna
broken, telegrams down from every 5 s to every 10–20 s, gaps up to 200 s — still counting). Live, the
inverter's counter takes over once it counted 0.2 kWh more (`PriorityCounter.Failed`, health
"degraded"); the DataLogger's `maxquarterimport` took the first source with any reading and would have
logged 0 kW for a frozen meter — it now uses the same rule per period (`PriorityCounter.FirstWorking`).

**DataLogger periods are UTC instants** (`DataLogger.DuePeriods`). At the end of summer time
(2026-10-25) the logger stepped in local clock time: 01:59:59+02:00 + 1 h is the naive 02:59:59,
which .NET reads as winter time, so the summer-time 02:00 hour was never logged and the
winter-time row came out empty (0 Wh with real 0.8 kWh heat pump load). The night score and the
load model's training lost that hour. Hours are now UTC hours, days run from local midnight to
local midnight (23/25 h), history queries and `LastChanged` comparisons are in UTC, and
`ToISO8601` formats the instant with the offset valid then (the two repeated hours get +02:00 and
+01:00 stamps).

**What a kWh cost** (`SourceShares`, `PriceList.GetPriceOrMean`). The battery's average stored
cost and the extra loads' cost totals used two separate source splits: the battery counted a
whole charge delta at the import price as soon as the grid meter showed import (else free), the
EV split PV → grid → battery with the 15-min PV average against a 1-min grid average. Now both go
through `SourceShares` (PV first, then grid, the rest battery), and the PV surplus comes from the
meters' balance PV − load = battery charge − grid import, so only the 1-min battery and grid
averages are needed — the PV average lags the steep evening ramp, a grid charge at dusk would
have counted partly as PV. The import price is looked up with the same `GetPriceOrMean` as the
simulation's `NetGridCostVs` (mean outside the prices), and the € come from one function,
`SourceShares.Cost`, for all three: the only difference is its `pvPrice` parameter. Money paid
(battery average cost, loads' cost totals) passes 0 — PV is free. `NetGridCostVs` (behind the
slot prices of the warm water and the EV) passes the export price: per slot it costs the import
and earns the export (export = PV not used, a negative PV amount), which values PV by the export
it displaces — what using a kWh costs, rather than what was paid for it.
The battery input counter is read in Wh (base unit) like the import/export counters, and all
three go through `CostTracker.CounterDeltaKwh`. The battery used its own copy that took the Wh
value as kWh: each 0.1-kWh step counted as 100 kWh, so the average swung between the grid price
(2026-10-21 00:00, 0.0001 → 0.317 €/kWh in one step) and 0 (07:33 → 0.0056) instead of blending.

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
