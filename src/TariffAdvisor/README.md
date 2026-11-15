# TariffAdvisor

Shows which import tariff — `Market` (spot) or `Fixed` — is cheaper for the coming day. It only advises: the switch
is made at the supplier and in PVControl's `select.pv_control_import_tariff`.

## How it decides

Every quarter from 13:00 (tomorrow's day-ahead prices are out) until 20:00 (`TariffAdvice.WindowStart`/`WindowEnd`,
in code) it judges PVControl's latest planning situation: the `ForecastSnapshot` in PVControl's `dp_shadow.db` (read only), which holds the
forecasts, the battery, the settings and every import tariff's planning price list.

- The DP planner (`src/Planning`, PVControl's DP options) plans the horizon (now → end of tomorrow) once per tariff:
  today's quarters with **today's tariff** in both runs, the day from the next switch time with the candidate.
  Export prices stay the same. Where the fixed tariff is in force the DP plans by its rules (no hold, no preferred
  reserve unless enforced — PVControl's, `TariffPrices.Input`).
- A plan's cost = its grid cost over the whole horizon (tonight's charging depends on tomorrow's tariff too) less the
  energy left in the battery at the end, valued at today's tariff's refill price for both plans. The planner's
  penalties aren't money and don't count.
- Planned extra loads stay where PVControl scheduled them: EV sessions always by the market's prices, also under Fixed
  (their timing costs nothing there), so the Market run finds them in the cheap quarters; warm water by the price paid
  (its standby loss is real), so from Fixed the Market run has it where Fair put it.
- The advice is relative to **today's** tariff (one change a day): a tariff already chosen for tomorrow doesn't change it.

`sensor.tariff_advisor_advice`:

| State | Meaning |
|---|---|
| `Market` / `Fixed` | the tariff for the coming day: the other one if it saves at least `MinSavingEur`, else today's; until the day's prices are published, today's tariff (attribute `status`: `pending`, else `decided`) |
| `Unavailable` | no or a stale snapshot (`MaxSnapshotAgeMinutes`), or only one tariff can be priced (PVControl.yaml: `ImportPriceFixedValue`/`ImportPriceFixedEntity`) |
| `Error` | judging failed (the log has the error) |

Outside the window the state stays: after 20:00 it is the decision for tomorrow (also through the night and the
morning, until the next window). Once tomorrow is decided, a later `Unavailable` or `Error` in the window doesn't replace
it (it is only logged); a new decision does.

Attributes: `status`, `for_day`, `current_tariff`, `cost_market_eur`, `cost_fixed_eur`, `saving_eur` (the best other
tariff against today's), `min_saving_eur`, `reason`, `snapshot_at`, `computed_at`. `sensor.tariff_advisor_version`
shows the deployed build.

## Configuration (`TariffAdvisor.yaml`)

| Key | Default | |
|---|---|---|
| `Enabled` | – | `false`: nothing runs |
| `SnapshotDb` | `../PVControl/dp_shadow.db` | PVControl's DP shadow DB, relative to this plugin's folder |
| `MinSavingEur` | `0.30` | the other tariff is the result from this saving on |
| `MaxSnapshotAgeMinutes` | `30` | an older snapshot isn't judged |

## Build, test, run

A NetDaemon app plugin (`src/AppPlugin.props`), loaded by the host from `apps/plugins/TariffAdvisor/`; it compiles
against `src/Planning` (shipped by the host). Needs PVControl's DP shadow to be on (it writes the snapshots).

```bash
dotnet build src/TariffAdvisor
dotnet test tests/TariffAdvisor.Tests
```
