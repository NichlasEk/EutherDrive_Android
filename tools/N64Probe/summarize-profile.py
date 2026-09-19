#!/usr/bin/env python3
"""Summarize sampled N64 CPU-thread stacks exported by dotnet-trace to Speedscope.

Weights are sampled thread-time, not hardware CPU counters; inlined work is
charged to its visible caller. Inclusive rows overlap and must not be summed.
"""
import collections
import json
import sys


def summarize(document):
    names = [frame['name'] for frame in document['shared']['frames']]
    leaf = collections.Counter()
    inclusive = collections.Counter()
    buckets = collections.Counter()
    for profile in document['profiles']:
        if profile['type'] != 'evented':
            raise ValueError('Expected evented dotnet-trace Speedscope data')
        scale = {'milliseconds': 1, 'seconds': 1000, 'microseconds': .001,
                 'nanoseconds': .000001}[profile['unit']]
        stack = []
        previous = profile['startValue']
        for event in profile['events']:
            if event['at'] < previous:
                raise ValueError('Non-monotonic profile')
            weight = (event['at'] - previous) * scale
            if stack and weight > 0:
                labels = [names[i] for i in stack if names[i] not in ('CPU_TIME', 'UNMANAGED_CODE_TIME')]
                if any('Ryu64.MIPS.R4300+' in n and 'StartCpuThread' in n for n in labels):
                    leaf[labels[-1]] += weight
                    for label in set(labels):
                        if label.startswith('Ryu64'):
                            inclusive[label] += weight
                    bucket = 'inside_rsp_task' if any('RspInterpreter.ExecuteTask(' in n for n in labels) else 'outside_rsp_task'
                    buckets[bucket] += weight
            previous = event['at']
            if event['type'] == 'O':
                stack.append(event['frame'])
            elif event['type'] == 'C' and stack and stack[-1] == event['frame']:
                stack.pop()
            else:
                raise ValueError('Unbalanced stack events')
        if stack:
            raise ValueError('Unclosed stack')
    total = sum(buckets.values())
    if not total:
        raise ValueError('No N64 CPU-thread samples found')

    def ranked(counter):
        return [{'name': n, 'sampled_ms': round(v, 2), 'percent': round(v / total * 100, 2)}
                for n, v in counter.most_common(25) if v > .000001]

    return {'sampled_thread_ms': round(total, 2), 'buckets': ranked(buckets),
            'leaf_frames': ranked(leaf), 'inclusive_frames_overlap': ranked(inclusive)}


if __name__ == '__main__':
    with open(sys.argv[1], encoding='utf-8') as stream:
        print(json.dumps(summarize(json.load(stream)), indent=2))
