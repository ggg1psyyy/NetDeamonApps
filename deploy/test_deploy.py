"""Tests of deploy.py's pure parts: python -m unittest discover deploy"""
import datetime, os, sys, unittest

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import deploy


def at(m, s): return datetime.datetime(2026, 10, 29, 17, m, s)


class RestartWindowTests(unittest.TestCase):
    def test_mid_quarter_at_once(self):
        for m, s in [(0, 15), (5, 0), (12, 59), (20, 0), (42, 59)]:
            self.assertEqual(0, deploy.seconds_until_allowed(at(m, s)), (m, s))

    def test_two_minutes_before_a_boundary_waits_until_15_s_after_it(self):
        self.assertEqual(135, deploy.seconds_until_allowed(at(13, 0)))   # 17:13:00 -> 17:15:15
        self.assertEqual(16, deploy.seconds_until_allowed(at(59, 59)))   # 17:59:59 -> 18:00:15

    def test_right_after_a_boundary_waits_for_its_writes(self):
        self.assertEqual(15, deploy.seconds_until_allowed(at(30, 0)))
        self.assertEqual(1, deploy.seconds_until_allowed(at(45, 14)))


class KeepTests(unittest.TestCase):
    def test_live_settings_and_data_are_never_copied(self):
        import fnmatch
        keep = lambda n: any(fnmatch.fnmatch(n, p) for p in deploy.KEEP)
        for n in ['appsettings.json', 'appsettings.Production.json', 'energy_history.db', 'energy_costs.db-wal', 'x.db.bak-1']:
            self.assertTrue(keep(n), n)
        for n in ['NetDeamonApps.dll', 'PVControl.yaml', 'PVControl.deps.json']:
            self.assertFalse(keep(n), n)



class StaleFilesTests(unittest.TestCase):
    def test_what_the_build_no_longer_has_goes_data_stays(self):
        published = ['PVControl.dll', 'PVControl.deps.json', 'PVControl.yaml', 'linq2db.dll']
        deployed = published + ['NetDaemon.AppModel.dll', 'runtimes/linux-arm64/native/libe_sqlite3.so',
                                'energy_costs.db', 'energy_costs.db-wal', 'appsettings.json']
        self.assertEqual(['NetDaemon.AppModel.dll', 'runtimes/linux-arm64/native/libe_sqlite3.so'],
                         deploy.stale_files(published, deployed))

    def test_nothing_stale(self):
        self.assertEqual([], deploy.stale_files(['a.dll'], ['a.dll', 'x.db']))


class InstallTests(unittest.TestCase):
    def test_stop_copy_start_in_order(self):
        calls = []
        deploy.install(lambda: calls.append('stop'), lambda: calls.append('copy'), lambda: calls.append('start'))
        self.assertEqual(['stop', 'copy', 'start'], calls)

    def test_a_failed_copy_still_starts_the_add_on(self):
        calls = []
        def copy(): calls.append('copy'); raise OSError('share gone')
        with self.assertRaises(OSError):
            deploy.install(lambda: calls.append('stop'), copy, lambda: calls.append('start'))
        self.assertEqual(['stop', 'copy', 'start'], calls)

    def test_a_failed_stop_copies_nothing(self):
        calls = []
        def stop(): raise OSError('HA unreachable')
        with self.assertRaises(OSError):
            deploy.install(stop, lambda: calls.append('copy'), lambda: calls.append('start'))
        self.assertEqual([], calls)


if __name__ == '__main__':
    unittest.main()


JOBS_RUNNING = """{"result":"ok","data":{"ignore_conditions":[],"jobs":[
 {"name":"backup_manager_partial_backup","reference":"cf09fd9a","done":false,"child_jobs":[
   {"name":"backup_store_apps","reference":"cf09fd9a","done":false,"child_jobs":[]}]},
 {"name":"backup_manager_partial_backup","reference":"0983fef2","done":true,"child_jobs":[]}]}}"""
JOBS_DONE = """{"result":"ok","data":{"jobs":[{"name":"backup_manager_partial_backup","reference":"cf09fd9a","done":true,"child_jobs":[]},
 {"name":"addon_update","reference":"x","done":false,"child_jobs":[]}]}}"""


class BackupTests(unittest.TestCase):
    """2026-11-10 09:48: the add-on stopped during an add-on update's backup, the Supervisor refused the start (500)."""

    def test_a_running_backup_blocks_also_as_a_child_job(self):
        self.assertEqual(['backup_manager_partial_backup cf09fd9a', 'backup_store_apps cf09fd9a'], deploy.blocking_jobs(JOBS_RUNNING))

    def test_done_backups_and_other_jobs_dont(self):
        self.assertEqual([], deploy.blocking_jobs(JOBS_DONE))
        self.assertEqual([], deploy.blocking_jobs('{"result":"ok","data":{"jobs":[]}}'))

    def test_waits_until_the_backup_is_done(self):
        answers, slept = [JOBS_RUNNING, JOBS_RUNNING, JOBS_DONE], []
        self.assertTrue(deploy.wait_for_jobs(lambda: answers.pop(0), sleep=slept.append, clock=lambda: 0))
        self.assertEqual([deploy.JOB_POLL_S] * 2, slept)

    def test_gives_up_after_the_wait(self):
        t = [0]
        def sleep(s): t[0] += s
        self.assertFalse(deploy.wait_for_jobs(lambda: JOBS_RUNNING, sleep=sleep, clock=lambda: t[0], wait_s=60, poll_s=15))

    def test_jobs_unreadable_doesnt_block(self):
        self.assertTrue(deploy.wait_for_jobs(lambda: None, sleep=lambda s: self.fail('slept')))

    def test_a_refused_start_is_retried_until_it_succeeds(self):
        calls, slept = [], []
        def start():
            calls.append(1)
            if len(calls) < 3: raise RuntimeError('HTTP Error 500')
            return 'ok'
        self.assertEqual('ok', deploy.retrying(start, sleep=slept.append, clock=lambda: 0))
        self.assertEqual(3, len(calls))

    def test_a_start_still_refused_after_the_retry_time_raises(self):
        t = [0]
        def sleep(s): t[0] += s
        def start(): raise RuntimeError('HTTP Error 500')
        with self.assertRaises(RuntimeError):
            deploy.retrying(start, sleep=sleep, clock=lambda: t[0], retry_s=30, poll_s=15)
