#!/usr/bin/env python3
"""Interleave two Release probes on the same captured RSP slice (Linux)."""
import argparse
import json
import os
from pathlib import Path
import re
import statistics
import subprocess


RESULT = re.compile(
    r"rspSliceBench=passed instructions=(\d+) stop=(\S+) "
    r"medianMs=([\d.]+) minMs=([\d.]+) maxMs=([\d.]+) "
    r"oracle=(\S+) rdpCommands=(\d+) sha256=([0-9A-F]+)"
)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--reference", type=Path, required=True)
    parser.add_argument("--candidate", type=Path, required=True)
    parser.add_argument("--capture", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--core", type=int, default=26)
    parser.add_argument("--reference-env", action="append", default=[], metavar="NAME=VALUE")
    parser.add_argument("--candidate-env", action="append", default=[], metavar="NAME=VALUE")
    args = parser.parse_args()
    mode_env = {}
    for mode in ("reference", "candidate"):
        overrides = {}
        for item in getattr(args, mode + "_env"):
            name, separator, value = item.partition("=")
            if not separator or not name or not name.replace("_", "").isalnum():
                parser.error(f"invalid {mode} environment assignment: {item!r}")
            overrides[name] = value
        mode_env[mode] = overrides
    args.output.mkdir(parents=True, exist_ok=False)
    env = os.environ.copy()
    env.pop("EUTHERDRIVE_N64_GPU_LIBRARY", None)
    env.pop("N64_PROBE_RSP_SLICE_LIVE_GPU", None)
    # Tiered recompilation in the middle of a 1,000-slice run produced a
    # spurious 9% gain when the same binary was compared with itself.
    env["DOTNET_TieredCompilation"] = "0"
    expected = None
    results = []
    for index, mode in enumerate(("reference", "candidate", "candidate", "reference"), 1):
        label = f"{index}-{mode}"
        run_env = env.copy()
        run_env.update(mode_env[mode])
        command = ["taskset", "-c", str(args.core), "dotnet",
                   str(getattr(args, mode).resolve()), "--bench-rsp-slice",
                   str(args.capture.resolve())]
        print("running", label, flush=True)
        output = subprocess.run(command, env=run_env, text=True, capture_output=True, check=True)
        (args.output / f"{label}.log").write_text(output.stdout + output.stderr)
        match = RESULT.search(output.stdout)
        if match is None:
            raise RuntimeError(f"{label}: missing RSP slice result")
        instructions, stop, median, minimum, maximum, oracle, commands, digest = match.groups()
        invariant = (instructions, stop, oracle, commands, digest)
        if expected is None:
            expected = invariant
        elif invariant != expected:
            raise RuntimeError(f"{label}: work or state differs: {invariant} != {expected}")
        result = dict(run=label, mode=mode, medianMs=float(median),
                      minMs=float(minimum), maxMs=float(maximum))
        results.append(result)
        print(json.dumps(result), flush=True)
        (args.output / "results.json").write_text(json.dumps(results, indent=2) + "\n")
    means = {mode: statistics.mean(row["medianMs"] for row in results if row["mode"] == mode)
             for mode in ("reference", "candidate")}
    summary = dict(meanMedianMs=means,
                   throughputGainPercent=100 * (means["reference"] / means["candidate"] - 1),
                   instructions=int(expected[0]), stopReason=expected[1],
                   oracle=expected[2], rdpCommands=int(expected[3]), endStateSha256=expected[4])
    (args.output / "summary.json").write_text(json.dumps(summary, indent=2) + "\n")
    print(json.dumps(summary), flush=True)


if __name__ == "__main__":
    main()
