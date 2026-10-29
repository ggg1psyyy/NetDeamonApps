# deploy

Deploys the host with every app plugin to the NetDaemon add-on, and PVControl's Home Assistant files
(`src/PVControl/HA/`) into the HA config. Python 3, standard library only (PyYAML for `ha`).

Setup: copy `local.example.json` to `local.json` (not committed) and fill in the add-on folder and slug and the
mounted HA config folder. The HA URL and token come from `src/Host/appsettings.json`.

```bash
python deploy/deploy.py apps            # publish, copy, restart the add-on, verify the version sensors
python deploy/deploy.py apps --dry-run  # publish only
python deploy/deploy.py ha              # merge src/PVControl/HA into the HA config: dry run
python deploy/deploy.py ha --write      # apply (backups next to the files), check the config, reload
python -m unittest discover deploy      # the script's tests
```

`apps`:
- publishes `src/Host` (`dotnet publish src/Host -c Release`: the host plus `apps/plugins/<Name>/`)
- never restarts from 2 min before until 15 s after a quarter boundary (:00/:15/:30/:45: the DataLogger rows,
  the cost log and the quarter tracking write then) — it waits
- refuses while the grid import is high or a force charge runs (after a restart the import cap guard only
  estimates the running quarter); `--force` overrides
- copies the publish output into the add-on folder, keeping the live `appsettings*.json` and every DB; files
  that no longer exist in the build stay there (remove them by hand)
- restarts the add-on and waits until `version_entities` show the deployed commit

`ha`: replaces PVControl's automations in `automations.yaml` by `id` (appends missing ones), the
`pv_control_apply_mode` script in `scripts.yaml` and the `packages/pv_control_inverter.yaml` package; checks the
result is still valid YAML with unique ids and nothing else lost, and with `--write` writes only what changed,
runs HA's config check and reloads automations, scripts and templates.
