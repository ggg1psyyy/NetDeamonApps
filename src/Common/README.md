# Common

Shared code of all apps (`NetDeamonApps.Common.dll`, provided by the host — every plugin references it, none
carries its own copy). Each calculation exists once here; callers differ only in their parameters.

| File | What it offers |
|---|---|
| `Calculations.cs` | `TimeRange` (overlap, slot fraction, floor/ceiling), `Energy` (W/Wh/minutes, counter increase), `Soc` (SoC/Wh, charge time), `Mean` (weighted, age weight, hour-of-day profile), `Regression` (least squares), `StepFunction` (HA history held values, time-weighted mean) |
| `Extensions.cs` | Generic HA helpers: entity history, unit multiplicators, `TryGetStateValue` (numbers, enums by name), attributes, ISO 8601 times, `DayLog`, `ToSlug` |
| `SqliteSchema.cs` | Schema version and columns of the apps' own SQLite DBs |
| `PriorityCounter.cs` | Energy counters in priority order (the first while it works, then the fallbacks) |
| `AppFiles.cs` | `PathOf`: an app's relative file location (DB) lies in its plugin folder |
| `AppVersion.cs` | `Of(assembly)`: the git build version of an app's assembly |

## Tests

```bash
dotnet test tests/Common.Tests
```

| Test class | What it covers |
|---|---|
| `CalculationsTests` | The shared basic calculations: time ranges, W/Wh, SoC/Wh, means and hour-of-day profile, least squares, step functions, day log |
| `ExtensionsTests`, `ParseExtensionsTests` | Unit conversion of `unit_of_measurement` strings (kWh, MWh, ct, …), number lists, enums by name |
| `AppFilesTests` | `AppFiles.PathOf`: relative in the app's folder, absolute unchanged |
| `AppVersionTests` | Build version from git (commit count, hash, `.dirty`) |
