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


if __name__ == '__main__':
    unittest.main()
