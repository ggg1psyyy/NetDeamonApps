# DataLogger
Logs energy sensor history to a local SQLite database (`apps/plugins/DataLogger/energy_history.db`, in the plugin's folder next to its YAML) at regular intervals. The data is used by PVControl's load prediction and is intended as a training dataset for future ML-based load forecasting.

One row per hour (`hourly`, stamped hh:59:59 local time with offset) and per local day (`daily`, midnight to midnight — 23 or 25 h at the clock change; the repeated hour in October gets two rows, +02:00 and +01:00): weather (outside/inside temperature, pressure, clouds, humidity), energies (house, PV, import/export, battery, EV, heat pump, warm water, as counter increases in Wh — resets and glitches handled), SoCs and prices (time-weighted means: every reading held until the next). Also the heat pump's operation and the house's state:

| Column | Source (`DataLogger.yaml`) | Value |
|---|---|---|
| `heatingmode` | `HeatPumpModeEntity` | mode held longest in the period |
| `heatingminutes`, `compressorminutes`, `ecominutes` | `HeatingActiveEntity`, `CompressorActiveEntity`, `EcoModeEntity` | minutes on |
| `hptargettemperature` | `TargetTemperatureEntity` | time-weighted mean of the heat pump's current target temperature (what eco mode lowers) |
| `flowtemperature`, `returntemperature`, `windspeed` | `FlowTemperatureEntity`, `ReturnTemperatureEntity`, `WindSpeedEntity` | time-weighted mean |
| `athomeminutes` | `PresenceEntity` | minutes `home` |
| `roomsheating` | `RoomClimateEntities` (`hvac_action`) | mean number of rooms with an open valve |
| `roomsetpoint` | `RoomClimateEntities` (setpoint) | mean room setpoint |
| `heatenergyheating`, `heatenergywarmwater` | `HeatEnergyHeatingEntity`, `HeatEnergyWarmwaterEntity` | heat output (Wh) per mode, counter increase |
| `heatpumpelecheating`, `heatpumpelecwarmwater` | `ElecEnergyHeatingEntity`, `ElecEnergyWarmwaterEntity` | heat pump electricity (Wh) under the same conditions — COP per mode = heat / electricity |
| `maxquarterimport` | `ImportTotalEntities` (the first that worked in the period: a source a later one counted ≥ 0.2 kWh more than was frozen) | highest 15-min mean grid import of the hour/day, kW |
| `pvcontrolmode`, `pvmodechanges` | `PVControlModeEntity` | PVControl mode held longest; number of mode changes |
| `feedinminutes`, `forcechargeminutes` | `PVControlModeEntity` | minutes in `feedin_priority`; in `force_charge` / `force_charge_grid_only` |
| `chargedisabledminutes` | `BatteryChargingEnabledEntity` | minutes with battery charging off |
| `invertereepromwrites` | `InverterEepromEntities` | state changes of the inverter's EEPROM settings (work mode, manual mode, charge current, export limit) = writes |

## Build, test, run

A NetDaemon app plugin (`src/AppPlugin.props`), loaded by the host from `apps/plugins/DataLogger/`; its DB lies next to
it. **Data contract:** PVControl reads this DB (`hourly`, `daily`) — a schema change is a change for PVControl too
(its tests build the DB with this project's SQL).

## Tests

`tests/DataLogger.Tests` (xUnit, no HA needed):

```bash
dotnet test tests/DataLogger.Tests
```

| Test class | What it covers |
|---|---|
| `DataLoggerPeriodTests` | DataLogger hours and days across both clock changes, backfill of an empty table |
| `DataLoggerOperationTests` | DataLogger operation columns: minutes in a state, state changes, time-weighted means, prevailing heat pump mode, migrations (v6, v7, v9) |
| `AppFilesTests` | The DB's default location: in the plugin's folder, next to its YAML |
