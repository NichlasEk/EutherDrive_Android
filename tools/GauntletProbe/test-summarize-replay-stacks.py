#!/usr/bin/env python3
import runpy
import unittest
from pathlib import Path

summarize = runpy.run_path(str(Path(__file__).with_name('summarize-replay-stacks.py')))['summarize']


def fixture():
    return {'shared': {'frames': [{'name': name} for name in
            ['Core!GauntletDarkLegacyMachine.RunFrame()', 'Core!Work()', 'CPU_TIME']]},
            'profiles': [{'type': 'evented', 'unit': 'milliseconds', 'name': 'main',
                          'startValue': 0, 'events': [
                              {'type': 'O', 'at': 0, 'frame': 0},
                              {'type': 'O', 'at': 0, 'frame': 1},
                              {'type': 'O', 'at': 0, 'frame': 2},
                              {'type': 'C', 'at': 3, 'frame': 2},
                              {'type': 'C', 'at': 3, 'frame': 1},
                              {'type': 'C', 'at': 3, 'frame': 0}]}]}


class ReplayStackTests(unittest.TestCase):
    def test_pseudo_frame_removed(self):
        report = summarize(fixture())
        self.assertEqual(report['intervalMs'], 3)
        self.assertEqual(report['methods'][0]['name'], 'Core!Work()')
        self.assertEqual(report['categoryMs']['CPU_TIME'], 3)

    def test_worker_only_excluded(self):
        data = fixture()
        worker = {'type': 'evented', 'unit': 'milliseconds', 'name': 'worker',
                  'startValue': 0, 'events': [{'type': 'O', 'at': 0, 'frame': 1},
                                            {'type': 'C', 'at': 1000, 'frame': 1}]}
        data['profiles'].append(worker)
        self.assertEqual(summarize(data)['intervalMs'], 3)

    def test_unbalanced_rejected(self):
        data = fixture()
        data['profiles'][0]['events'][-1]['frame'] = 1
        with self.assertRaises(ValueError):
            summarize(data)

    def test_no_replay_rejected(self):
        data = fixture()
        data['shared']['frames'][0]['name'] = 'Core!Startup()'
        with self.assertRaises(ValueError):
            summarize(data)


if __name__ == '__main__':
    unittest.main()
