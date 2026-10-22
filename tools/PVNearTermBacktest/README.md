# PV near-term backtest

Checks PVControl's near-term PV correction (`NetEnergyPrediction.WithRunningAvgCorrection`)
against Home Assistant history: every minute of daylight of the last days, with the PV forecast
as it was at that minute (the recorded `wh_period_15m` snapshots) and the real PV afterwards.

```bash
HASS_SERVER=https://ha.example HASS_TOKEN=... node tools/PVNearTermBacktest/backtest.mjs [days=10]
```

Node 18+ (built-in `fetch`), no packages. Entities at the top of `backtest.mjs` (PV power, the
forecast entities — summed like PVControl does). HA keeps state history 10 days by default.

Per variant it reports the mean absolute error of the next hour's PV — split into morning (first
2 h of production), midday and evening (last 2 h) — and of the PV for the rest of the day (what
the grid-charge decision depends on), plus the p95 change of that rest-of-day PV per minute
(how much the input jumps). The day ratio is computed as in PVControl (running slot pro rata,
continuous actual energy); the variants differ in the near-term ramp.

Result 2026-10-12…22 (10 days), Wh:

| Variant | Morning | Midday | Evening | All | Rest of day | p95 change/min |
|---|---|---|---|---|---|---|
| absolute 15 min (until 2026-10-22) | 111 | 239 | 154 | 198 | 859 | 102 |
| absolute 30 min | 124 | 256 | 190 | 218 | 873 | 95 |
| no ramp | 156 | 236 | 112 | 198 | 860 | 74 |
| ratio 15 min (absolute under 50 Wh) | 130 | 214 | 76 | 173 | 836 | 101 |
| **ratio, absolute under 150 Wh forecast in the window (current)** | **106** | **214** | **77** | **168** | **827** | 103 |

The ratio live/forecast keeps the forecast's slope — in October PV goes from full to 0 in ~2 h,
an absolute live average lags that; at dawn (little forecast in the window) the ratio is noise,
the absolute average is better there.
