#!/usr/bin/env python3
"""Audit one completed synchronous resident GPU session, without adding nested timers."""
import argparse
from collections import Counter
import json
import math
from pathlib import Path
import re


def summarize(text, batch_limit=128):
    if batch_limit < 1:
        raise ValueError('batch limit must be positive')

    def single(prefix):
        rows = [line for line in text.splitlines() if line.startswith(prefix)]
        if len(rows) != 1:
            raise ValueError(f'Expected one {prefix} row, got {len(rows)}')
        return dict(re.findall(r'(\w+)=([^\s]+)', rows[0]))

    totals = single('gpuShadowTotals ')
    resident = single('gpuResident ')
    host = single('gpuProfileHost ')
    score = single('score ')
    boundaries = [dict(re.findall(r'(\w+)=([^\s]+)', line))
                  for line in text.splitlines() if line.startswith('gpuShadowBoundary ')]
    if not boundaries or int(resident['pendingPixels']) != 0:
        raise ValueError('Missing boundaries or unfinished resident pixels')
    total = 0
    for index, row in enumerate(boundaries, 1):
        draws = int(row['draws'])
        total += draws
        if (draws < 1 or int(row['segment']) != index or int(row['totalDraws']) != total
                or row.get('mode') != 'replace' or row.get('rasterCounters') != 'PASS'):
            raise ValueError('Inconsistent or unverified replacement segment')
    reads = int(resident['pixelReadbacks'])
    if (reads != len(boundaries) or int(totals['submissions']) != total + reads
            or int(totals['queuedDraws']) != 0):
        raise ValueError('Not a complete synchronous resident session')
    runtime = float(score['runMs'])
    wait = float(host['waitMs'])
    if not math.isfinite(runtime) or not math.isfinite(wait) or runtime <= 0 or not 0 <= wait <= runtime:
        raise ValueError('Invalid run/wait timing')
    reasons = Counter(row['reason'] for row in boundaries)
    unsupported = Counter(line.removeprefix('gpuUnsupportedState ')
                          for line in text.splitlines() if line.startswith('gpuUnsupportedState '))
    sizes = [int(row['draws']) for row in boundaries]
    batches = sum((n + batch_limit - 1) // batch_limit for n in sizes)
    return {
        'draws': total, 'segments': len(sizes), 'boundaryReasons': dict(sorted(reasons.items())),
        'unsupportedStateBreaks': dict(unsupported.most_common()),
        'drawsPerSegment': {'min': min(sizes), 'max': max(sizes), 'mean': total / len(sizes)},
        'submissions': int(totals['submissions']), 'pixelReadbacks': reads,
        'hypotheticalBatchLimit': batch_limit, 'hypotheticalDrawSubmissions': batches,
        'hypotheticalTotalSubmissions': batches + reads,
        'runMs': runtime, 'drawFenceWaitMs': wait,
        'drawFenceWaitPercent': 100 * wait / runtime,
        'zeroDrawWaitOnlyScenarioMs': runtime - wait,
        'note': 'Batch counts preserve current segment boundaries but ignore immediate counter/return dependencies. '
                'They are not an implemented or proven schedule. The zero-wait scenario only removes measured '
                'host draw-fence wait; it is not a prediction or a bound for all batching changes. '
                'Device times overlap host wait; do not add them. Pixel readback waits remain.'
    }


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('logs', nargs='+', type=Path)
    parser.add_argument('--batch-limit', type=int, default=128)
    args = parser.parse_args()
    for path in args.logs:
        print(json.dumps({'log': str(path), **summarize(path.read_text(), args.batch_limit)}, indent=2))
