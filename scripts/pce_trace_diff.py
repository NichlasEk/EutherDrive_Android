#!/usr/bin/env python3
import sys


def parse_line(line: str):
    line = line.strip()
    if not line:
        return None
    out = {}
    for token in line.split():
        if "=" not in token:
            continue
        k, v = token.split("=", 1)
        out[k] = v
    return out


def load(path: str):
    rows = []
    with open(path, "r", encoding="utf-8-sig") as f:
        for line in f:
            parsed = parse_line(line)
            if parsed is not None:
                rows.append(parsed)
    return rows


def main():
    if len(sys.argv) != 3:
        print("Usage: python scripts/pce_trace_diff.py <trace_a.log> <trace_b.log>")
        return 2

    a = load(sys.argv[1])
    b = load(sys.argv[2])
    n = min(len(a), len(b))

    if n == 0:
        print("No comparable rows.")
        return 1

    for i in range(n):
        if a[i] == b[i]:
            continue
        print(f"First divergence at row {i}:")
        keys = sorted(set(a[i].keys()) | set(b[i].keys()))
        for k in keys:
            va = a[i].get(k)
            vb = b[i].get(k)
            if va != vb:
                print(f"  {k}: A={va} B={vb}")
        return 1

    if len(a) != len(b):
        print(f"No value divergence in first {n} rows, but lengths differ: A={len(a)} B={len(b)}")
        return 1

    print(f"Traces match exactly ({n} rows).")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

