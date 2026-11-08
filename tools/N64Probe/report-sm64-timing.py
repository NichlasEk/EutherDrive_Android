#!/usr/bin/env python3
"""Report overlapping short-window throughput from opt-in Joybus timing samples.

This measures emulation throughput, not display frame pacing or audible output.
Windows start at real controller reads and end at the first read at least the
requested guest duration later. Existing five-second checkpoint capture remains
inside wall time; no waits are subtracted. Overlapping-window counts are not a
fraction of playback time. The separate withoutCheckpointCapture group excludes
windows spanning checkpoint/image capture; the unfiltered minima are retained.
Passing a replay cannot prove all game situations.
"""
import argparse
import json
import math
from pathlib import Path

CLOCK = 93_750_000


def summarize(samples, start, end, duration, checkpoints=()):
    if not (math.isfinite(duration) and duration > 0 and 0 <= start < end):
        raise ValueError("Invalid timing window")
    for a, b in zip(samples, samples[1:]):
        if b['cycles'] < a['cycles'] or b['wallSeconds'] <= a['wallSeconds'] or b['audioSeconds'] < a['audioSeconds']:
            raise ValueError("Nonmonotonic timing samples")
    rows = []
    j = 0
    for i, a in enumerate(samples):
        if a['cycles'] < start * CLOCK:
            continue
        j = max(j, i + 1)
        while j < len(samples) and samples[j]['cycles'] - a['cycles'] < duration * CLOCK:
            j += 1
        if j == len(samples) or samples[j]['cycles'] > end * CLOCK:
            break
        b = samples[j]
        wall = b['wallSeconds'] - a['wallSeconds']
        guest = (b['cycles'] - a['cycles']) / CLOCK
        row = dict(start=a['cycles'] / CLOCK, end=b['cycles'] / CLOCK,
                   wallSeconds=wall, checkpointCaptures=sum(a['cycles'] <= c < b['cycles'] for c in checkpoints),
                   cycleRealTimePercent=100 * guest / wall,
                   audioRealTimePercent=100 * (b['audioSeconds'] - a['audioSeconds']) / wall)
        if a.get('threadCpuSeconds') is not None and b.get('threadCpuSeconds') is not None:
            row['threadCpuSeconds'] = b['threadCpuSeconds'] - a['threadCpuSeconds']
        rows.append(row)
    if not rows:
        raise ValueError("No complete windows within requested interval")
    result = dict(requestedGuestSeconds=duration, windows=len(rows))
    for key in ('cycleRealTimePercent', 'audioRealTimePercent'):
        ordered = sorted(rows, key=lambda r: r[key])
        result[key] = dict(minimum=ordered[0][key], p05=ordered[int((len(rows)-1)*.05)][key],
                           median=ordered[len(rows)//2][key],
                           windowsBelow100=sum(r[key] < 100 for r in rows), worst=ordered[0])
    clean = [r for r in rows if r['checkpointCaptures'] == 0]
    result['withoutCheckpointCapture'] = dict(windows=len(clean))
    if clean:
        for key in ('cycleRealTimePercent', 'audioRealTimePercent'):
            result['withoutCheckpointCapture'][key] = min(clean, key=lambda r: r[key])
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('timing', type=Path)
    parser.add_argument('--start', type=float, required=True)
    parser.add_argument('--end', type=float, required=True)
    parser.add_argument('--windows', type=float, nargs='+', default=[.25, 1, 5])
    args = parser.parse_args()
    samples = json.loads(args.timing.read_text())
    checkpoint_path = args.timing.with_name('checkpoints.json')
    checkpoints = [p['cycles'] for p in json.loads(checkpoint_path.read_text())] if checkpoint_path.exists() else []
    print(json.dumps(dict(source=str(args.timing.resolve()), start=args.start, end=args.end,
                         windows=[summarize(samples, args.start, args.end, d, checkpoints) for d in args.windows]), indent=2))


if __name__ == '__main__':
    main()
