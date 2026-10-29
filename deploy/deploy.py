#!/usr/bin/env python3
"""Deploys the NetDaemon host with its app plugins, and PVControl's Home Assistant files.

  python deploy/deploy.py apps [--dry-run] [--force]   publish, copy to the add-on folder, restart, verify
  python deploy/deploy.py ha [--write]                  merge src/PVControl/HA into the HA config (dry run without --write)

Machine details come from deploy/local.json (not committed, see local.example.json); the HA URL and token from
src/Host/appsettings.json. Only Python's standard library, plus PyYAML for `ha` (validates the merged files).
"""
import argparse, datetime, fnmatch, io, json, os, re, shutil, subprocess, sys, time, urllib.request

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PUBLISH = os.path.join(ROOT, 'src', 'Host', 'bin', 'Release', 'net10.0', 'publish')
HA_FILES = os.path.join(ROOT, 'src', 'PVControl', 'HA')
# Never overwritten in the add-on folder: the live settings and every app's data.
KEEP = ['appsettings*.json', '*.db', '*.db-shm', '*.db-wal', '*.db.bak*']
# PVControl's automations in automations.yaml, by id: replaced, or appended when missing.
AUTOMATIONS = {'1726401911432': 'Mode.yaml', '1746880755128': 'Battery.yaml', '1792585055022': 'Health.yaml',
               '1793042000001': 'InverterSync.yaml', '1793042000002': 'PriceFetch.yaml'}
SCRIPT = ('pv_control_apply_mode', 'ApplyMode.yaml')              # in scripts.yaml
PACKAGE = ('packages/pv_control_inverter.yaml', 'InverterHelpers.yaml')
# Writes at every quarter boundary (DataLogger rows, the cost log, the quarter tracking): no restart from
# BEFORE_S before a boundary until AFTER_S after it.
BEFORE_S, AFTER_S = 120, 15


def load_config():
    path = os.path.join(ROOT, 'deploy', 'local.json')
    if not os.path.exists(path):
        sys.exit(f'{path} missing: copy deploy/local.example.json and fill it in')
    cfg = json.load(io.open(path, encoding='utf-8'))
    ha = json.load(io.open(os.path.join(ROOT, 'src', 'Host', 'appsettings.json'), encoding='utf-8-sig'))['HomeAssistant']
    cfg['url'] = f"http://{ha['Host']}:{ha.get('Port', 8123)}/api/"
    cfg['token'] = ha['Token']
    return cfg


class Ha:
    def __init__(self, cfg): self.url, self.token = cfg['url'], cfg['token']

    def call(self, path, data=None):
        req = urllib.request.Request(self.url + path, data=None if data is None else json.dumps(data).encode(),
                                     headers={'Authorization': 'Bearer ' + self.token, 'Content-Type': 'application/json'},
                                     method='GET' if data is None else 'POST')
        with urllib.request.urlopen(req, timeout=180) as r:
            return r.read().decode('utf-8', 'replace')

    def state(self, entity):
        try:
            return json.loads(self.call('states/' + entity))
        except Exception:
            return None


def now(): return datetime.datetime.now().strftime('%H:%M:%S')


def seconds_until_allowed(t):
    """0 if a restart may run at t, else the seconds to wait (the quarter-boundary write window)."""
    into = (t.minute % 15) * 60 + t.second
    if into < AFTER_S: return AFTER_S - into
    if into >= 900 - BEFORE_S: return 900 - into + AFTER_S
    return 0


def stale_files(published, deployed):
    """Files of a deployed plugin folder that its new build no longer has (e.g. dependencies the host now provides),
    as paths relative to the folder; never the KEEP files (settings, DBs)."""
    keep = lambda f: any(fnmatch.fnmatch(os.path.basename(f), p) for p in KEEP)
    return sorted(f for f in deployed if f not in set(published) and not keep(f))


def files_in(folder):
    return [os.path.relpath(os.path.join(d, f), folder).replace(os.sep, '/') for d, _, fs in os.walk(folder) for f in fs]


def git(*args): return subprocess.run(['git', '-C', ROOT] + list(args), capture_output=True, text=True).stdout.strip()


def apps(cfg, dry_run, force):
    ha = Ha(cfg)
    if git('status', '--porcelain', '--untracked-files=no'):
        print('warning: uncommitted changes, the version will end in .dirty')
    commit = git('rev-parse', '--short', 'HEAD')
    print(now(), 'publish', commit)
    if os.path.isdir(PUBLISH): shutil.rmtree(PUBLISH)
    r = subprocess.run(['dotnet', 'publish', os.path.join(ROOT, 'src', 'Host'), '-c', 'Release'], capture_output=True, text=True)
    if r.returncode: sys.exit(r.stdout[-3000:] + r.stderr[-2000:])
    plugins = sorted(os.listdir(os.path.join(PUBLISH, 'apps', 'plugins')))
    print('plugins:', ', '.join(plugins))
    if dry_run:
        print('dry run: nothing copied, no restart'); return

    wait = seconds_until_allowed(datetime.datetime.now())
    if wait:
        print(now(), f'waiting {wait} s (quarter-boundary writes)'); time.sleep(wait)
    imp, mode = ha.state(cfg['grid_power_entity']), ha.state(cfg['mode_entity'])
    grid_w = float(imp['state']) if imp else 0.0
    import_w = -grid_w if cfg.get('grid_power_import_negative', True) else grid_w
    print(now(), f"grid import {import_w:.0f} W, mode {mode['state'] if mode else '?'}")
    if not force and (import_w > cfg.get('max_import_w', 2500) or (mode and mode['state'] in ('force_charge', 'force_charge_grid_only'))):
        sys.exit('not deployed: high import or a charge is running (the import cap guard would only estimate the running '
                 'quarter after a restart); retry later or use --force')

    target = cfg['addon_folder']
    ignore = lambda d, names: [n for n in names if any(fnmatch.fnmatch(n, p) for p in KEEP)]
    shutil.copytree(PUBLISH, target, dirs_exist_ok=True, ignore=ignore)
    print(now(), 'copied to', target)
    for name in plugins:   # a plugin folder holds exactly its build (plus its data): drop what the build no longer has
        new, old = os.path.join(PUBLISH, 'apps', 'plugins', name), os.path.join(target, 'apps', 'plugins', name)
        stale = stale_files(files_in(new), files_in(old))
        for f in stale: os.remove(os.path.join(old, f))
        for d, _, _ in sorted(os.walk(old), key=lambda x: -len(x[0])):
            if d != old and not os.listdir(d): os.rmdir(d)
        if stale: print(f'  {name}: removed {len(stale)} files the build no longer has')
    ha.call('services/hassio/addon_restart', {'addon': cfg['addon']})
    print(now(), 'restarted', cfg['addon'])

    deadline = time.time() + 180
    sensors = cfg.get('version_entities', [])
    while time.time() < deadline and not all((s := ha.state(e)) and commit in s['state'] for e in sensors):
        time.sleep(5)
    time.sleep(20)   # one cycle
    ok = True
    for e in sensors + cfg.get('check_entities', []):
        s = ha.state(e)
        shown = s['state'] if s else 'missing'
        print(f'  {e}: {shown} {s["attributes"].get("summary", "") if s else ""}')
        if e in sensors and commit not in shown: ok = False
    print(now(), 'deployed' if ok else 'VERSION NOT CONFIRMED: check the add-on log')


def ha_files(cfg, write):
    import yaml  # PyYAML: only needed here
    conf = cfg['ha_config_folder']
    rd = lambda p: io.open(p, encoding='utf-8').read()
    indent = lambda text, n: ''.join((' ' * n + l if l.strip() else l) for l in text.splitlines(True))
    entry = lambda aid, f: f"- id: '{aid}'\n" + indent(rd(os.path.join(HA_FILES, f)), 2)

    def replace_entry(text, aid, f):
        lines = text.splitlines(True)
        start = next((i for i, l in enumerate(lines) if l.startswith(f"- id: '{aid}'")), None)
        if start is None:
            return text + ('' if text.endswith('\n') else '\n') + entry(aid, f)
        end = next((i for i in range(start + 1, len(lines)) if lines[i].startswith('- ')), len(lines))
        return ''.join(lines[:start]) + entry(aid, f) + ''.join(lines[end:])

    auto_path, script_path, pkg_path = (os.path.join(conf, 'automations.yaml'), os.path.join(conf, 'scripts.yaml'),
                                        os.path.join(conf, *PACKAGE[0].split('/')))
    auto = rd(auto_path); auto2 = auto
    for aid, f in AUTOMATIONS.items():
        auto2 = replace_entry(auto2, aid, f)
    scr = rd(script_path); lines = scr.splitlines(True)
    s = next(i for i, l in enumerate(lines) if l.startswith(SCRIPT[0] + ':'))
    e = next((i for i in range(s + 1, len(lines)) if re.match(r'^[A-Za-z0-9_]+:', lines[i])), len(lines))
    scr2 = ''.join(lines[:s]) + SCRIPT[0] + ':\n' + indent(rd(os.path.join(HA_FILES, SCRIPT[1])), 2) + ''.join(lines[e:])
    pkg = rd(pkg_path) if os.path.exists(pkg_path) else ''
    pkg2 = rd(os.path.join(HA_FILES, PACKAGE[1]))

    # Validate: still YAML, no duplicate ids, nothing else lost.
    a, a0 = yaml.safe_load(auto2), yaml.safe_load(auto)
    ids = [x['id'] for x in a]
    assert len(ids) == len(set(ids)), 'duplicate automation id'
    assert len(a) == len(a0) + sum(1 for aid in AUTOMATIONS if f"'{aid}'" not in auto), 'unexpected automation count'
    assert set(yaml.safe_load(scr2)) == set(yaml.safe_load(scr)), 'script keys changed'
    yaml.safe_load(pkg2)
    for x in a:
        if x['id'] in AUTOMATIONS: print('  ok', x['id'], x.get('alias', ''))
    changed = [(p, new) for p, old, new in [(auto_path, auto, auto2), (script_path, scr, scr2), (pkg_path, pkg, pkg2)] if old != new]
    print('changed:', ', '.join(os.path.basename(p) for p, _ in changed) or 'nothing')
    if not write or not changed:
        if changed: print('dry run: use --write to apply (backups next to the files)')
        return
    suffix = datetime.datetime.now().strftime('.bak-%Y%m%d-%H%M%S')
    for p, text in changed:
        if os.path.exists(p): shutil.copy2(p, p + suffix)
        io.open(p, 'w', encoding='utf-8', newline='').write(text)
        print('  written', p, '(backup', os.path.basename(p) + suffix + ')')
    ha = Ha(cfg)
    result = json.loads(ha.call('config/core/check_config', {}))
    print('check_config:', result.get('result'), result.get('errors') or '')
    if result.get('result') != 'valid':
        sys.exit('HA config invalid: nothing reloaded; restore the backups')
    for svc in ('automation/reload', 'script/reload', 'template/reload'):
        ha.call('services/' + svc, {})
    print(now(), 'reloaded automations, scripts, templates')


def main():
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = p.add_subparsers(dest='cmd', required=True)
    a = sub.add_parser('apps', help='publish, copy, restart, verify')
    a.add_argument('--dry-run', action='store_true', help='publish only')
    a.add_argument('--force', action='store_true', help='deploy even while the import is high')
    h = sub.add_parser('ha', help="merge PVControl's HA files into the HA config")
    h.add_argument('--write', action='store_true', help='apply (otherwise a dry run)')
    args = p.parse_args()
    cfg = load_config()
    apps(cfg, args.dry_run, args.force) if args.cmd == 'apps' else ha_files(cfg, args.write)


if __name__ == '__main__':
    main()
