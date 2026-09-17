#!/usr/bin/env python3
import runpy
import unittest
from pathlib import Path

summarize = runpy.run_path(str(Path(__file__).with_name('summarize-frame-phases.py')))['summarize']
ROW = ('[GAUNTDL:PROFILE] frame-phases callbacksMs=1.00 cpuMs=8.00 '
       'devicesMs=0.00 renderMs=1.00 fifoDecodeMs=5.00 '
       'type3PushMs=4.00/2 texturedRasterMs=3.00/2')


class PhaseSummaryTests(unittest.TestCase):
    def test_nested_timers_not_added(self):
        report = summarize(ROW + '\n' + ROW)
        self.assertEqual(report['rows'], 2)
        self.assertEqual(report['topLevelTotalMs'], 20)
        self.assertEqual(report['totalsMs']['texturedRasterMs'], 6)
        self.assertEqual(report['topLevelPercent']['cpuMs'], 80)

    def test_incomplete_rejected(self):
        with self.assertRaises(ValueError):
            summarize('[GAUNTDL:PROFILE] frame-phases cpuMs=1.0')

    def test_empty_rejected(self):
        with self.assertRaises(ValueError):
            summarize('No profile')

    def test_last_score(self):
        report = summarize('score first\n' + ROW + '\nscore last\ndisplayRate end')
        self.assertEqual(report['score'], 'score last')
        self.assertEqual(report['displayRate'], 'displayRate end')


if __name__ == '__main__':
    unittest.main()
