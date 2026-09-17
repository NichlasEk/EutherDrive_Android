#!/usr/bin/env python3
"""Summarize evented dotnet-trace Speedscope stacks in RunProbeSteps, outside FIFO.

Weights are sampled thread-time, NOT CPU counters. Inlined callees are invisible;
memory API buckets include their children and do not prove a RAM/MMIO split.
"""
import argparse
import collections
import json


def intervals(profile):
    if profile['type'] != 'evented':
        raise ValueError('Expected evented dotnet-trace Speedscope profile')
    scale = {'milliseconds': 1, 'seconds': 1000, 'microseconds': .001, 'nanoseconds': .000001}[profile['unit']]
    stack = []
    previous = profile['startValue']
    for event in profile['events']:
        if event['at'] < previous:
            raise ValueError('Non-monotonic event timestamp')
        if stack and event['at'] > previous:
            yield tuple(stack), (event['at']-previous)*scale
        previous = event['at']
        if event['type'] == 'O':
            stack.append(event['frame'])
        elif event['type'] == 'C' and stack and stack[-1] == event['frame']:
            stack.pop()
        else:
            raise ValueError('Invalid or unbalanced stack event')
    if stack:
        raise ValueError('Unclosed stack')


def summarize(document):
    names = [frame['name'] for frame in document['shared']['frames']]
    totals = collections.Counter()
    leaves = collections.Counter()
    inclusive = collections.Counter()
    buckets = collections.Counter()
    markers = collections.Counter()
    for profile in document['profiles']:
        for indices, weight in intervals(profile):
            stack = [names[index] for index in indices]
            if not any('MipsR5000Core.RunProbeSteps(' in name for name in stack):
                continue
            totals['cpu_loop_sampled_ms'] += weight
            if any('VoodooBringupBackend.DecodeCommandFifoPackets(' in name for name in stack):
                totals['fifo_sampled_ms'] += weight
                continue
            totals['outside_fifo_sampled_ms'] += weight
            if stack[-1] in ('CPU_TIME', 'UNMANAGED_CODE_TIME'):
                markers[stack[-1]] += weight
                stack = stack[:-1]
            leaves[stack[-1]] += weight
            root = next(index for index, name in enumerate(stack) if 'MipsR5000Core.RunProbeSteps(' in name)
            for name in set(stack[root+1:]):
                inclusive[name] += weight
            if any(any(device in name for device in ('VegasVoodooPciDevice.', 'VegasIdePciDevice.',
                    'VegasSioDevice.', 'DcsAudioDevice.', 'IdeDiskDevice.', 'VoodooBringupBackend.', 'VoodooFacade.')) for name in stack):
                bucket = 'visible_device_path'
            elif any('VegasMemoryMap.ReadRuntimeInstruction32(' in name for name in stack):
                bucket = 'visible_instruction_fetch_api'
            elif any('VegasMemoryMap.' in name for name in stack):
                bucket = 'visible_memory_map_path'
            elif any('MipsR5000Core.Execute(' in name or 'MipsR5000Core.ExecuteRuntimeSafeInstruction(' in name for name in stack):
                bucket = 'execute_path_without_visible_memory_or_device'
            else:
                bucket = 'other_cpu_loop_path_including_inlined_work'
            buckets[bucket] += weight
    total = totals['outside_fifo_sampled_ms']
    if not total:
        raise ValueError('No CPU-loop samples outside FIFO; check stack symbols/filter')
    def ranked(counter):
        return [{'name': name, 'sampled_ms': round(value, 3), 'percent': round(value/total*100, 2)}
                for name, value in counter.most_common(30) if value > .000001]
    return {'totals': {key: round(value, 3) for key, value in totals.items()},
            'sample_markers': dict(markers),
            'exclusive_path_buckets': ranked(buckets), 'leaf_frames': ranked(leaves),
            'inclusive_frames_overlap': ranked(inclusive)}


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('trace', help='Speedscope JSON exported by dotnet-trace')
    args = parser.parse_args()
    with open(args.trace, encoding='utf-8') as stream:
        result = summarize(json.load(stream))
    print(json.dumps(result, indent=2))
