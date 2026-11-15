# TariffAdvisor

Advises which import tariff — `Market` (spot) or `Fixed` — is cheaper for the coming day. It only advises: the switch
is made at the supplier and in PVControl's `select.pv_control_import_tariff`.

## How it decides

From `StartTime` (13:00, when tomorrow's day-ahead prices are out) until midnight it judges PVControl's latest planning
situation once per quarter: the `ForecastSnapshot` in PVControl's `dp_shadow.db` (read only), which holds the
forecasts, the battery, the settings and every import tariff's planning price list.

- The DP planner (`src/Planning`, PVControl's DP options) plans the horizon (now → end of tomorrow) once per tariff:
  today's quarters with **today's tariff** in both runs, the day from the next switch time with the candidate.
  Export prices stay the same.
- A plan's cost = its grid cost over the whole horizon (tonight's charging depends on tomorrow's tariff too) less the
  energy left in the battery at the end, valued at today's tariff's refill price for both plans. The planner's
  penalties aren't money and don't count.
- The advice is relative to **today's** tariff (one change a day): a tariff already chosen for tomorrow doesn't change it.

`sensor.tariff_advisor_advice`:

| State | Meaning |
|---|---|
| `stay` | today's tariff is cheaper, or the other saves less than `MinSavingEur` |
| `switch_to_fixed` / `switch_to_market` | that tariff saves at least `MinSavingEur` |
| `pending` | the day's prices aren't published yet |
| `unavailable` | no or a stale snapshot (`MaxSnapshotAgeMinutes`), or only one tariff can be priced (PVControl.yaml: `ImportPriceFixedValue`/`ImportPriceFixedEntity`) |

Attributes: `for_day`, `current_tariff`, `cost_market_eur`, `cost_fixed_eur`, `saving_eur` (the best other tariff
against today's), `min_saving_eur`, `reason`, `snapshot_at`, `computed_at`. Before 13:00 the last advice (for today)
stays. `sensor.tariff_advisor_version` shows the deployed build.

## Configuration (`TariffAdvisor.yaml`)

| Key | Default | |
|---|---|---|
| `Enabled` | – | `false`: nothing runs |
| `SnapshotDb` | `../PVControl/dp_shadow.db` | PVControl's DP shadow DB, relative to this plugin's folder |
| `MinSavingEur` | `0.30` | a switch is advised from this saving on |
| `StartTime` | `13:00` | judged from this time of day |
| `MaxSnapshotAgeMinutes` | `30` | an older snapshot isn't judged |

## Build, test, run

A NetDaemon app plugin (`src/AppPlugin.props`), loaded by the host from `apps/plugins/TariffAdvisor/`; it compiles
against `src/Planning` (shipped by the host). Needs PVControl's DP shadow to be on (it writes the snapshots).

```bash
dotnet build src/TariffAdvisor
dotnet test tests/TariffAdvisor.Tests
```
