#!/usr/bin/env python3
"""Parser negative controls. Uses only a local, already captured journal."""
import json
import os
from pathlib import Path
import struct
import subprocess
import sys


def main():
    if len(sys.argv) != 4:
        raise SystemExit("Usage: check-journal.py NATIVE_PROBE JOURNAL NEW_OUTPUT")
    probe, source, output = Path(sys.argv[1]).resolve(), Path(sys.argv[2]), Path(sys.argv[3])
    output.mkdir(parents=True, exist_ok=False)
    original = source.read_bytes()
    header_size = 52 + (12 << 20)
    records = []
    at = header_size
    while at < len(original):
        kind, length, sequence = struct.unpack_from("<BIQ", original, at)
        records.append((kind, at, length))
        at += 13 + length
    if at != len(original):
        raise ValueError("Incomplete source journal")
    results = []
    env = dict(os.environ, GRANITE_NUM_WORKER_THREADS="2")

    def check(name, data, accepted=False):
        path = output / "candidate.bin"
        path.write_bytes(data)
        result = subprocess.run([str(probe), str(path), str(output / "unused"), "--validate-journal"],
                                env=env, capture_output=True, text=True, timeout=30)
        if result.returncode != (0 if accepted else 1):
            raise AssertionError(f"{name}: exit={result.returncode}\n{result.stdout}\n{result.stderr}")
        results.append({"name": name, "exit": result.returncode})

    check("valid", original, True)
    for length in [0, 7, 19, 51, header_size - 1, header_size, len(original) - 1, records[-1][1]]:
        check(f"truncated-{length}", original[:length])
    for name, offset in [("magic", 0), ("flags", 8), ("ram-size", 12), ("hidden-size", 16),
                         ("kind", header_size), ("size", header_size + 1), ("sequence", header_size + 5)]:
        bad = bytearray(original)
        bad[offset] ^= 0xff
        check(name, bad)
    for kind, name, relative in [(1, "patch-address", 0), (2, "command-opcode", 3),
                                 (4, "checkpoint-number", 0), (5, "end-command-count", 4)]:
        record = next((r for r in records if r[0] == kind), None)
        if record is None:
            raise ValueError(f"Source must exercise record kind {kind}")
        bad = bytearray(original)
        if kind == 1:
            struct.pack_into("<I", bad, record[1] + 13, 8 << 20)
        elif kind == 2:
            # A two-word command cannot become a 44-word triangle.
            bad[record[1] + 13 + relative] = 0xcf
        else:
            bad[record[1] + 13 + relative] ^= 0xff
        check(name, bad)
    check("trailing-byte", original + b"\x00")
    (output / "candidate.bin").unlink()
    (output / "results.json").write_text(json.dumps(results, indent=2) + "\n")
    print(f"nativeJournalParserChecks={len(results)} passed")


if __name__ == "__main__":
    main()
