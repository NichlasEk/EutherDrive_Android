#!/usr/bin/env python3
"""ROM-free tests for the scoped Speedscope summarizer."""
import importlib.util
from pathlib import Path
import unittest

spec = importlib.util.spec_from_file_location('cpu_stacks', Path(__file__).with_name('summarize-cpu-stacks.py'))
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


def document(spans, unit='milliseconds'):
    names, events, time = [], [], 0
    for stack, duration in spans:
        indices = []
        for name in stack:
            if name not in names:
                names.append(name)
            indices.append(names.index(name))
        events.extend({'type': 'O', 'frame': index, 'at': time} for index in indices)
        time += duration
        events.extend({'type': 'C', 'frame': index, 'at': time} for index in reversed(indices))
    return {'shared': {'frames': [{'name': name} for name in names]},
            'profiles': [{'type': 'evented', 'unit': unit, 'startValue': 0, 'endValue': time, 'events': events}]}


ROOT = 'Core!MipsR5000Core.RunProbeSteps(int32)'
STEP = 'Core!MipsR5000Core.Step()'
FIFO = 'Core!VoodooBringupBackend.DecodeCommandFifoPackets(string)'


class SummaryTests(unittest.TestCase):
    def test_scope_markers_and_disjoint_buckets(self):
        trace = document([
            ([ROOT, STEP, FIFO, 'CPU_TIME'], 3),
            ([ROOT, STEP, 'Core!VegasMemoryMap.Read32(uint64)', 'CPU_TIME'], 2),
            ([ROOT, STEP, 'Core!VegasMemoryMap.Write32(uint64)', 'Core!VegasVoodooPciDevice.TryWriteMemory32()', 'CPU_TIME'], 1),
            ([ROOT, STEP, 'Core!MipsR5000Core.ExecuteRuntimeSafeInstruction(uint64)', 'CPU_TIME'], 2),
            ([ROOT, STEP, 'UNMANAGED_CODE_TIME'], 2),
            (['OtherThread', FIFO, 'CPU_TIME'], 100),
        ])
        result = module.summarize(trace)
        self.assertEqual(result['totals'], {'cpu_loop_sampled_ms': 10, 'fifo_sampled_ms': 3, 'outside_fifo_sampled_ms': 7})
        self.assertAlmostEqual(sum(row['sampled_ms'] for row in result['exclusive_path_buckets']), 7)
        self.assertNotIn('CPU_TIME', [row['name'] for row in result['leaf_frames']])
        self.assertEqual(result['sample_markers']['UNMANAGED_CODE_TIME'], 2)
        self.assertNotIn(FIFO, [row['name'] for row in result['inclusive_frames_overlap']])

    def test_units_and_recursive_inclusive_count(self):
        result = module.summarize(document([([ROOT, STEP, STEP, 'CPU_TIME'], .002)], 'seconds'))
        self.assertEqual(result['totals']['outside_fifo_sampled_ms'], 2)
        self.assertEqual(result['inclusive_frames_overlap'][0]['sampled_ms'], 2)

    def test_instruction_fetch_precedes_general_memory_bucket(self):
        result = module.summarize(document([
            ([ROOT, STEP, 'Core!VegasMemoryMap.ReadRuntimeInstruction32(uint64)', 'CPU_TIME'], 1)
        ]))
        self.assertEqual(result['exclusive_path_buckets'][0]['name'], 'visible_instruction_fetch_api')

    def test_reject_wrong_scope(self):
        with self.assertRaises(ValueError):
            module.summarize(document([(['OtherThread'], 10)]))

    def test_reject_broken_stack_and_timestamps(self):
        for mutation in ('closing', 'time', 'unclosed', 'format'):
            trace = document([([ROOT, STEP], 1)])
            profile = trace['profiles'][0]
            if mutation == 'closing':
                profile['events'][-2]['frame'] = 0
            elif mutation == 'time':
                profile['events'][-1]['at'] = -1
            elif mutation == 'unclosed':
                profile['events'].pop()
            else:
                profile['type'] = 'sampled'
            with self.subTest(mutation=mutation), self.assertRaises(ValueError):
                module.summarize(trace)


if __name__ == '__main__':
    unittest.main()
