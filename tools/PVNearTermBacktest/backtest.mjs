// Backtest of PVControl's near-term PV correction (NetEnergyPrediction.WithRunningAvgCorrection)
// against Home Assistant history: every minute of daylight of the last days, with the forecast
// as it was at that minute and the real PV afterwards. See README.md.
//
//   HASS_SERVER=https://ha.example HASS_TOKEN=... node backtest.mjs [days=10]

const server = process.env.HASS_SERVER, token = process.env.HASS_TOKEN;
if (!server || !token) { console.error('Set HASS_SERVER and HASS_TOKEN'); process.exit(1); }
const days = Number(process.argv[2] ?? 10);

// Entities as in PVControl.yaml: PV power and the forecast entities (summed, like
// CombineForecastLists — here two roof sides).
const PV_POWER = 'sensor.solax_pv_power_total';
const FORECASTS = ['sensor.energy_production_today_3', 'sensor.energy_production_today_4'];

const Q = 15 * 60_000, STEP = 10_000;
const from = new Date(Date.now() - (days + 1) * 86_400_000).toISOString(), to = new Date().toISOString();
const history = async e => (await (await fetch(
  `${server}/api/history/period/${from}?end_time=${to}&significant_changes_only=0&filter_entity_id=${e}`,
  { headers: { Authorization: `Bearer ${token}` } })).json())[0] ?? [];

// PV power on a 10-s grid (sample and hold) and its running energy (Wh).
const pvRows = (await history(PV_POWER)).map(x => [Date.parse(x.last_changed), +x.state]).filter(x => !isNaN(x[1]));
const t0 = Math.floor(pvRows[0][0] / STEP) * STEP, n = Math.floor((Date.now() - t0) / STEP);
const cumWh = new Float64Array(n + 1);
for (let i = 0, j = 0, w = 0; i < n; i++) {
  while (j < pvRows.length && pvRows[j][0] <= t0 + i * STEP) w = pvRows[j++][1];
  cumWh[i + 1] = cumWh[i] + Math.max(0, w) * STEP / 3_600_000;
}
const at = t => Math.min(n, Math.max(0, Math.round((t - t0) / STEP)));
const energy = (a, b) => cumWh[at(b)] - cumWh[at(a)];

// Forecast snapshots (wh_period_15m) as recorded; the one valid at t, summed over the entities.
const snaps = await Promise.all(FORECASTS.map(async e => (await history(e))
  .filter(x => x.attributes?.wh_period_15m)
  .map(x => [Date.parse(x.last_updated), Object.fromEntries(Object.entries(x.attributes.wh_period_15m).map(([k, v]) => [Date.parse(k), v]))])));
const cache = new Map();
function forecastAt(t) {
  const picked = snaps.map(list => { let i = -1; while (i + 1 < list.length && list[i + 1][0] <= t) i++; return i; });
  if (picked.some(i => i < 0)) return null;
  const day = new Date(t); day.setHours(0, 0, 0, 0);
  const key = day.getTime() + '|' + picked.join(',');
  if (!cache.has(key)) {
    const fc = {};
    picked.forEach((i, s) => { for (const [k, v] of Object.entries(snaps[s][i][1])) if (+k >= day.getTime() && +k < day.getTime() + 86_400_000) fc[k] = (fc[k] ?? 0) + v; });
    cache.set(key, fc);
  }
  return cache.get(key);
}
const overlapWh = (slots, a, b) => {
  let s = 0;
  for (const k in slots) { const o = Math.max(0, Math.min(b, +k + Q) - Math.max(a, +k)); if (o > 0) s += slots[k] * o / Q; }
  return s;
};

// The correction, mirroring WithRunningAvgCorrection: day ratio (running slot pro rata,
// confidence over the first 6 solar slots), then the near-term ramp over 4 slots from the
// running one — absolute (live average) or as a ratio live/forecast.
function corrected(t, fc, v) {
  const start = Math.floor(t / Q) * Q, day = new Date(t); day.setHours(0, 0, 0, 0);
  const sum = overlapWh(fc, day.getTime(), t);
  let solar = 0; for (const k in fc) if (+k < t && fc[k] >= 100) solar++;
  let r = sum >= 50 ? Math.min(2, energy(day.getTime(), t) / sum) : 1;
  const conf = Math.min(1, solar / 6); r = conf >= 1 ? r : 1 + (r - 1) * conf;
  const w = v.window * 60_000, liveWh = energy(t - w, t), windowFc = overlapWh(fc, t - w, t);
  const ratioOk = v.kind !== 'abs' && windowFc >= v.minWh * v.window / 15;
  const shortRatio = ratioOk ? Math.min(3, liveWh / windowFc) : r, avgPerSlot = liveWh / (w / Q);
  const out = {}; let rem = v.ramp;
  for (const k of Object.keys(fc).map(Number).filter(k => k >= start).sort((a, b) => a - b)) {
    let x = fc[k] * r;
    if (rem > 0) {
      const weight = rem / v.ramp;
      x = ratioOk ? fc[k] * (r + (shortRatio - r) * weight) : (x > 0 ? x + (avgPerSlot - x) * weight : x);
      rem--;
    }
    out[k] = x;
  }
  return out;
}

const variants = {
  'absolute 15 min (until 2026-10-22)': { kind: 'abs', window: 15, ramp: 4 },
  'absolute 30 min': { kind: 'abs', window: 30, ramp: 4 },
  'no ramp': { kind: 'abs', window: 15, ramp: 0 },
  'ratio 15 min': { kind: 'ratio', window: 15, ramp: 4, minWh: 50 },
  'ratio 15 min, absolute under 150 Wh (current)': { kind: 'ratio', window: 15, ramp: 4, minWh: 150 },
};

const stats = Object.fromEntries(Object.keys(variants).map(k => [k, { morning: [], midday: [], evening: [], rest: [], step: [] }]));
let fullDays = 0;
for (let d = new Date(from); d < new Date(); d.setDate(d.getDate() + 1)) {
  const d0 = new Date(d).setHours(0, 0, 0, 0), noon = forecastAt(d0 + 13 * 3_600_000);
  if (!noon) continue;
  const producing = Object.entries(noon).filter(([, v]) => v >= 20).map(([k]) => +k);
  if (!producing.length) continue;
  const first = Math.min(...producing), last = Math.max(...producing) + Q;
  if (last + 3_600_000 > Date.now()) continue; // only finished days
  fullDays++;
  const previous = {};
  for (let t = first; t <= last - Q; t += 60_000) {
    const fc = forecastAt(t); if (!fc) continue;
    const part = t < first + 2 * 3_600_000 ? 'morning' : t > last - 2 * 3_600_000 ? 'evening' : 'midday';
    const nextHour = energy(t, t + 3_600_000), restOfDay = energy(t, d0 + 86_400_000);
    for (const [name, v] of Object.entries(variants)) {
      const slots = corrected(t, fc, v), s = stats[name];
      s[part].push(Math.abs(overlapWh(slots, t, t + 3_600_000) - nextHour));
      const rest = overlapWh(slots, t, d0 + 86_400_000);
      s.rest.push(Math.abs(rest - restOfDay));
      if (previous[name] !== undefined) s.step.push(Math.abs(rest - previous[name]));
      previous[name] = rest;
    }
  }
}
const mean = a => a.reduce((x, y) => x + y, 0) / a.length;
const p95 = a => [...a].sort((x, y) => x - y)[Math.floor(a.length * 0.95)];
console.log(`${fullDays} full days, mean absolute error of the PV prediction (Wh)`);
console.table(Object.fromEntries(Object.entries(stats).map(([k, s]) => [k, {
  'next h morning': Math.round(mean(s.morning)), 'next h midday': Math.round(mean(s.midday)),
  'next h evening': Math.round(mean(s.evening)), 'next h all': Math.round(mean([...s.morning, ...s.midday, ...s.evening])),
  'rest of day': Math.round(mean(s.rest)), 'p95 change/min': Math.round(p95(s.step)),
}])));
