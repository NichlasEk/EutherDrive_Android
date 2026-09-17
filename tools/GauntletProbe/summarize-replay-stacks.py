#!/usr/bin/env python3
"""Attribute evented Speedscope intervals inside RunFrame, excluding worker-only stacks."""
import argparse
import collections
import json
from pathlib import Path


def summarize(data, limit=20):
    names = [frame['name'] for frame in data['shared']['frames']]
    methods = collections.Counter()
    categories = collections.Counter()
    total = 0
    included = []
    for profile in data['profiles']:
        if profile['type'] != 'evented' or profile['unit'] != 'milliseconds':
            raise ValueError('Expected evented millisecond profiles from dotnet-trace')
        stack = []
        last = profile['startValue']
        matched = False
        for event in profile['events']:
            duration = event['at'] - last
            if duration < 0:
                raise ValueError('Unordered events')
            active = [names[index] for index in stack]
            if any('GauntletDarkLegacyMachine.RunFrame(' in name for name in active):
                candidates = [name for name in active if '!' in name]
                if candidates and duration:
                    matched = True
                    total += duration
                    methods[candidates[-1]] += duration
                    category = active[-1] if active[-1] in ('CPU_TIME', 'UNMANAGED_CODE_TIME') else 'unclassified'
                    categories[category] += duration
            last = event['at']
            if event['type'] == 'O':
                stack.append(event['frame'])
            elif event['type'] == 'C':
                if not stack or stack.pop() != event['frame']:
                    raise ValueError('Unbalanced events')
            else:
                raise ValueError('Unknown event type')
        if stack:
            raise ValueError('Unclosed stack')
        if matched:
            included.append(profile['name'])
    if not total:
        raise ValueError('No RunFrame intervals')
    return {
        'profiles': included, 'intervalMs': round(total, 3),
        'categoryMs': {key: round(value, 3) for key, value in categories.items()},
        'methods': [{'name': name, 'attributedMs': round(value, 3),
                     'percent': round(value / total * 100, 2)}
                    for name, value in methods.most_common(limit)],
        'note': 'Nearest symbolized method attribution, not isolated CPU execution time. '
                'Inlining, unmanaged work and waits can be charged to a managed caller. '
                'Worker-only profiles, startup and snapshot save/load are excluded.'
    }


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('trace', type=Path)
    args = parser.parse_args()
    print(json.dumps(summarize(json.loads(args.trace.read_text())), indent=2))
