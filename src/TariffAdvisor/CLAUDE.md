# CLAUDE.md — TariffAdvisor

Guidance for working on `src/TariffAdvisor`; the repository-wide rules (tests for every change, one calculation one
function, documentation, HA config) are in the root `CLAUDE.md`.

Reads PVControl's `dp_shadow.db` only through `ForecastSnapshotStore` (src/Planning) and plans only with Planning's
public API — never PVControl's code or state. `TariffAdvice` is the pure decision, `TariffAdvisor` the HA side (timer,
sensor). Advises only; never switches anything.

## Tests (`tests/TariffAdvisor.Tests`)

- **`TariffAdviceTests`** — on a live snapshot (2026-11-02 06:00) with the real DP: a fixed price far under the market → switch, over it → stay, today Fixed → switch to market, a saving under `MinSavingEur` → stay, the same prices → no saving; today's quarters keep today's tariff in both runs, export unchanged; with fixed plans: the end energy valued at today's refill price for both, penalties left out; `pending` before publication, `unavailable` (no/stale snapshot, no tariffs, one tariff), judged from `StartTime` until midnight, the YAML's values
