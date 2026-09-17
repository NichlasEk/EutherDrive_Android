import importlib.util
from pathlib import Path
import unittest

spec = importlib.util.spec_from_file_location('gpu_sync', Path(__file__).with_name('summarize-gpu-sync.py'))
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)

LOG = '''gpuShadowBoundary segment=1 draws=129 totalDraws=129 reason=lfb-read mode=replace rasterCounters=PASS
gpuShadowBoundary segment=2 draws=1 totalDraws=130 reason=backend-reset mode=replace rasterCounters=PASS
gpuShadowTotals submissions=132 queuedDraws=0
gpuResident pixelReadbacks=2 pendingPixels=0
gpuProfileHost waitMs=250
score runMs=1000
'''


class Checks(unittest.TestCase):
    def test_schedule_and_wait(self):
        result = module.summarize(LOG)
        self.assertEqual(result['hypotheticalDrawSubmissions'], 3)
        self.assertEqual(result['hypotheticalTotalSubmissions'], 5)
        self.assertEqual(result['zeroDrawWaitOnlyScenarioMs'], 750)
        self.assertEqual(result['drawFenceWaitPercent'], 25)
        self.assertEqual(result['boundaryReasons'], {'lfb-read': 1, 'backend-reset': 1})

    def test_incomplete_or_inconsistent(self):
        for old, new in [('pendingPixels=0', 'pendingPixels=1'), ('totalDraws=130', 'totalDraws=131'),
                         ('submissions=132', 'submissions=131'), ('rasterCounters=PASS', 'rasterCounters=FAIL'),
                         ('queuedDraws=0', 'queuedDraws=1'), ('waitMs=250', 'waitMs=nan')]:
            with self.subTest(old=old), self.assertRaises(ValueError):
                module.summarize(LOG.replace(old, new))

    def test_multiple_sessions_rejected(self):
        with self.assertRaises(ValueError):
            module.summarize(LOG + LOG)

    def test_invalid_limit(self):
        with self.assertRaises(ValueError):
            module.summarize(LOG, 0)

    def test_optional_state_rows(self):
        result = module.summarize('gpuUnsupportedState fbz=000b4779 tm0=80000009\n' + LOG)
        self.assertEqual(result['unsupportedStateBreaks'], {'fbz=000b4779 tm0=80000009': 1})


if __name__ == '__main__':
    unittest.main()
