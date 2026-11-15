# CLAUDE.md — TariffAdvisor

Guidance for working on `src/TariffAdvisor`; the repository-wide rules (tests for every change, one calculation one
function, documentation, HA config) are in the root `CLAUDE.md`.

Reads PVControl's `dp_shadow.db` only through `ForecastSnapshotStore` (src/Planning) and plans only with Planning's
public API — never PVControl's code or state. `TariffAdvice` is the pure decision, `TariffAdvisor` the HA side (timer,
sensor). Advises only; never switches anything.

## Tests (`tests/TariffAdvisor.Tests`)

- **`TariffAdviceTests`** — on a live snapshot (2026-11-02 06:00) with the real DP: a fixed price far under the market → `Fixed`, over it → `Market`, today Fixed → `Market`, a saving under `MinSavingEur` → today's tariff, the same prices → no saving; today's quarters keep today's tariff in both runs, export unchanged, the fixed tariff's rules only in the Fixed run from the switch; with fixed plans: the end energy valued at today's refill price for both, penalties left out; before publication today's tariff (`pending`), `Unavailable` (no/stale snapshot, no tariffs, one tariff), judged 13:00–20:00 (`Due`), a failure → `Error` (`Failed`), a decision stays against a later failure but not a new decision (`Shown`), the YAML's values
