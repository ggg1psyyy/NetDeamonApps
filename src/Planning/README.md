# Planning

The battery planner the app plugins share: given a planning situation, it simulates the house quarter by quarter and
plans the battery. No Home Assistant and no app state — a plan is a function of its input.

| Part | What |
|---|---|
| `Simulator/` | `EnergySimulator` (15-min slot simulation, now → end of tomorrow), `SimulationInput` / `SimulationResult`, `SessionPlanner` (extra-load sessions), `LoadPricing` |
| `Optimizer/` | `BatteryDpPlanner` (the DP plan), `PlanEvaluation` (the one cost model both plans are judged by), `DpSteering`, `DpLoadPricer`, `ForecastSnapshot` (a planning situation, saved and replayed) |
| `PriceList.cs` | A price list (per 15 min): lookups, resolution, estimates for unpublished quarters |
| `SourceShares.cs` | What a kWh costs by its source (PV, grid, battery) |
| `PlanningTypes.cs`, `PVControlExtensions.cs` | The types it plans with (modes, reasons, price entry) and the prediction-window helpers |

A `ForecastSnapshot` holds everything a `SimulationInput` needs (`ToInput()`); PVControl saves one per quarter in its
`dp_shadow.db`, so another plugin can plan "what if" on PVControl's current situation without PVControl's code.

**Shipped by the host**, like Common: the host references it, a plugin compiles against it only
(`Private="false" ExcludeAssets="runtime"`), so one copy is loaded for all plugins. Namespaces are PVControl's
(`NetDeamon.apps.PVControl.Simulator`, `…Optimizer`), unchanged by the move.

## Build & test

```bash
dotnet build src/Planning
dotnet test tests/Planning.Tests
```
