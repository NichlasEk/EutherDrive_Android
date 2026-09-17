#!/usr/bin/env python3
"""Summarize existing Gauntlet phase logs; nested Voodoo timers are not additive."""
import argparse
import json
import re
from pathlib import Path

FIELDS = ('callbacksMs', 'cpuMs', 'devicesMs', 'renderMs',
          'fifoDecodeMs', 'type3PushMs', 'texturedRasterMs')


def summarize(text):
    rows = []
    for line in text.splitlines():
        if '[GAUNTDL:PROFILE] frame-phases ' not in line:
            continue
        values = dict(re.findall(r'\b(\w+Ms)=([0-9]+(?:\.[0-9]+)?)', line))
        if any(field not in values for field in FIELDS):
            raise ValueError('Incomplete frame-phase row')
        rows.append({field: float(values[field]) for field in FIELDS})
    if not rows:
        raise ValueError('No frame-phase rows')
    totals = {field: round(sum(row[field] for row in rows), 3) for field in FIELDS}
    top = sum(totals[field] for field in FIELDS[:4])
    scores = re.findall(r'^score .*$', text, re.MULTILINE)
    rates = re.findall(r'^displayRate .*$', text, re.MULTILINE)
    return {
        'rows': len(rows), 'totalsMs': totals,
        'topLevelTotalMs': round(top, 3),
        'topLevelPercent': {field: round(totals[field] / top * 100, 2) if top else 0
                            for field in FIELDS[:4]},
        'score': scores[-1] if scores else None,
        'displayRate': rates[-1] if rates else None,
        'note': 'Voodoo timers overlap/nest within frame work; do not add them to top-level times. '
                'Source timers are rounded to 0.01 ms per row. Frame logging overhead is outside these intervals.'
    }


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('logs', nargs='+', type=Path)
    args = parser.parse_args()
    for path in args.logs:
        print(json.dumps({'log': str(path), **summarize(path.read_text())}, indent=2))
