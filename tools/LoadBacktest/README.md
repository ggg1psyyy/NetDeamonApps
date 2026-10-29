# LoadBacktest

Offline backtest of load forecast models for PVControl, against a copy of DataLogger's
`energy_history.db`. Not part of the deployed app.

```bash
dotnet run -c Release --project tools/LoadBacktest -- <path to energy_history.db> [first day, default 2025-09-15]
```

Every day at 21:00 each model forecasts the next 25 hours from the history before it (rolling
origin); scored against what happened. Main measure: the **night sum 22:00–08:00**, which sizes
the grid charge. Load = house energy without EV charging and warm water (what PVControl
forecasts), split into heat-pump heating (heat pump − warm water) and the rest.

Structurally impossible hours (negative energies, warm water above the heat pump, heat pump or EV
above the house — ~45 hours since 2024, e.g. warm water −19 kWh in one hour) count as missing.

## Models

| | |
|---|---|
| M0 | Today's `HourlyWeightedAverageLoadPrediction`: weighted mean per local hour, weight exp(−days/10) |
| M1 | M0 + level offset: mean deviation of the last N hours from M0, damped, limited to ±25 % |
| M2 | Base load = hour-of-day average + damped level; heat pump = per local hour a + s·b·HDH, HDH = max(0, T₀ − 24-h mean temperature), weighted least squares (half-life ~3 months); s scales the heating part to the last 48 h (heating off → 0) |
| M3 | ML.NET FastTree on the total load (hour, weekday, season, temperature, 24-h mean, clouds, load 48 h earlier), retrained weekly |

"forecast err" variants add a temperature forecast error (per-forecast bias σ 1 °C, noise
0.5 + 0.04 °C per hour of lead time); the others use the measured temperature.

## Result 2026-10-22 (376 nights, 2025-09-15 → 2026-10-20)

Night sum MAE (mean night 4.05 kWh; winter 5.87, transition 2.97, summer 2.98 kWh):

| Model | all | winter | transition | summer |
|---|---|---|---|---|
| M0 today | 0.82 (20 %) | 1.57 (27 %) | 0.28 | 0.56 |
| M1 level 48 h × 0.5 | 0.77 | 1.49 | 0.25 | 0.49 |
| **M2 13 °C, level 48 h × 0.5** | **0.70 (17 %)** | **1.25 (21 %)** | 0.32 | 0.49 |
| M2 same, with forecast error | 0.71 | 1.27 | 0.31 | 0.50 |
| M3 FastTree | 0.82 | 1.41 | 0.36 | 0.65 |

M2 is chosen: −15 % night error overall, −20 % in winter, barely affected by forecast error,
transparent. M3 brings no gain (and a +0.5 kWh winter bias). The remaining winter error is
mostly operation, not weather: at the same 3.4 °C the heat pump heated 3.3 to 17 kWh a day
(2025-12-04 → 06) — heating mode, setpoints, presence. The live model should use the heat pump
mode (heating off → no heating part) instead of inferring it from the last 48 h.

## What drives the error (`--analyze`, 2026-10-22)

```bash
dotnet run -c Release --project tools/LoadBacktest -- <db> 2025-09-15 --analyze
```

- **Winter: the heat pump.** 68 % of the night-error variance; with a perfect heat pump forecast
  the winter error would drop from 1.24 to 0.33 kWh. Summer: the base load (occasional loads).
- **Heat pump error is random per night**: on "heating on" nights the heat pump uses ±1.6 kWh
  (standard deviation) around the forecast, uniformly over all hours. It doesn't persist from
  night to night (r = −0.06) and isn't explained by solar gain, clouds, humidity or pressure.
- **Explainable parts**: heating switched off/stopping ("on → off" nights: +1.04 kWh bias, 19 of
  140 winter nights) → use the heat pump mode live; indoor temperature at 21:00 (r = −0.43, warm
  house → less heating) → −4 % error out-of-sample. Candidate for later: the room valves'
  heating state (logged by HeatingTest since 2026-10-20).
- **Correction during the night doesn't help**: at 02:00, using the deviation since 22:00 takes
  the 02:00–08:00 error from 0.87 to 0.84 kWh (winter); full correction makes it worse.
- **Size of under-forecasts** (actual − forecast, night): nights without heating 90 % ≤ 0.49 kWh
  (≈ 4 % SoC); heating nights 90 % ≤ 1.96 kWh (≈ 17 % SoC, ~60 % of the heat pump forecast).
  A fixed margin can't fit both — it should follow the forecast heating energy.
