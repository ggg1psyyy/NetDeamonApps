# deploy

Deploys the host with every app plugin to the NetDaemon add-on, and PVControl's Home Assistant files
(`src/PVControl/HA/`) into the HA config. Python 3, standard library only (PyYAML for `ha`).

Setup: copy `local.example.json` to `local.json` (not committed) and fill in the add-on folder and slug and the
mounted HA config folder. The HA URL and token come from `src/Host/appsettings.json`.

```bash
python deploy/deploy.py apps            # publish, stop the add-on, copy, start it, verify the version sensors
python deploy/deploy.py apps --dry-run  # publish only
python deploy/deploy.py ha              # merge src/PVControl/HA into the HA config: dry run
python deploy/deploy.py ha --write      # apply (backups next to the files), check the config, reload
python -m unittest discover deploy      # the script's tests
```

`apps`:
- publishes `src/Host` (`dotnet publish src/Host -c Release`: the host plus `apps/plugins/<Name>/`)
- never restarts from 2 min before until 15 s after a quarter boundary (:00/:15/:30/:45: the DataLogger rows,
  the cost log and the quarter tracking write then) — it waits
- waits while the Supervisor runs a backup or restore (`ha jobs info` over SSH — `ssh` in `local.json`, default
  `root@<HA host>`, the SSH add-on), up to 15 min: during one it refuses to start an add-on, and a stopped NetDaemon
  stayed down; if the jobs can't be read it warns and goes on
- refuses while the grid import is high or a force charge runs (after a restart the import cap guard only
  estimates the running quarter); `--force` overrides
- stops the add-on, copies the publish output into its folder (keeping the live `appsettings*.json` and every DB;
  files the build no longer has are removed from the plugin folders) and starts it again — also if the copy fails;
  never copies over the running add-on (it would rewrite the DLLs under the process); a refused start (a backup began after the check)
  is retried every 15 s for up to 10 min
- waits until `version_entities` show the deployed commit

`ha`: replaces PVControl's automations in `automations.yaml` by `id` (appends missing ones), the
`pv_control_apply_mode` script in `scripts.yaml` and the `packages/pv_control_inverter.yaml` package; checks the
result is still valid YAML with unique ids and nothing else lost, and with `--write` writes only what changed,
runs HA's config check and reloads automations, scripts and templates.
