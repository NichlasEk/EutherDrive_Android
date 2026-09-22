#!/usr/bin/env python3
"""Interleave complete-ROM runs with identical controller-read input.

Requires probes built with N64PerformanceProbe=true, without N64CpuJitProfile.
Timings include all emulation work between two actual Joybus checkpoints.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import statistics
import subprocess


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ("reference", "candidate", "rom", "output"):
        parser.add_argument("--" + name, type=Path, required=True)
    parser.add_argument("--library", type=Path)
    parser.add_argument("--cores", default="6,7")
    parser.add_argument("--start", type=int, default=70)
    parser.add_argument("--end", type=int, default=90)
    args = parser.parse_args()
    if args.start < 5 or args.end <= args.start or args.start % 5 or args.end % 5:
        parser.error("start/end must be increasing multiples of 5")
    args.output.mkdir(parents=True, exist_ok=False)
    env = dict(os.environ, PARALLEL_RDP_SMALL_TYPES="1", PARALLEL_RDP_FORCE_SYNC_SHADER="1",
               GRANITE_NUM_WORKER_THREADS="2", GRANITE_VULKAN_NO_VALIDATION="1")
    env.pop("EUTHERDRIVE_N64_GPU_AUDIT", None)
    env.pop("EUTHERDRIVE_N64_GPU_VALIDATE", None)
    env.pop("EUTHERDRIVE_N64_GPU_LIBRARY", None)
    env.pop("DOTNET_PerfMapEnabled", None)
    if args.library:
        env["EUTHERDRIVE_N64_GPU_LIBRARY"] = str(args.library.resolve())
    expected = None
    expected_frames = None
    results = []
    invariant = ("targetSecond", "cycles", "reads", "audioFrames", "audioSeconds", "audioSha256",
                 "inputSha256", "ramSha256", "action", "x", "y", "z", "graphicsTasks", "audioTasks")
    for index, mode in enumerate(("reference", "candidate", "candidate", "reference"), 1):
        name = f"{index}-{mode}"
        output = args.output / name
        print("running", name, flush=True)
        with (args.output / (name + ".log")).open("w") as log:
            subprocess.run(["taskset", "-c", args.cores, "dotnet", str(getattr(args, mode).resolve()),
                            "--bench-sm64", str(args.rom.resolve()), str(output.resolve()), str(args.end)],
                           env=env, stdout=log, stderr=subprocess.STDOUT, check=True)
        if (output / "jit-profile.json").exists():
            raise RuntimeError("Instrumented JIT profile cannot be used as a timing sample")
        points = json.loads((output / "checkpoints.json").read_text())
        state = [{key: point[key] for key in invariant} for point in points]
        state.append({"finalRamSha256": hashlib.sha256((output / "rdram.bin").read_bytes()).hexdigest()})
        frames = {p.name: hashlib.sha256(p.read_bytes()).hexdigest() for p in sorted(output.glob("frame-*.ppm"))}
        if expected is None:
            expected, expected_frames = state, frames
        if expected != state or expected_frames != frames:
            raise RuntimeError(f"{name}: guest/audio/frame state differs; reject the timing comparison")
        begin = next(p for p in points if p["targetSecond"] == args.start)
        end = next(p for p in points if p["targetSecond"] == args.end)
        elapsed = end["wallSeconds"] - begin["wallSeconds"]
        audio = end["audioSeconds"] - begin["audioSeconds"]
        result = dict(run=name, mode=mode, start=args.start, end=args.end, wallSeconds=elapsed,
                      audioSeconds=audio, realTimePercent=100 * audio / elapsed)
        results.append(result)
        print(json.dumps(result), flush=True)
        (args.output / "results.json").write_text(json.dumps(results, indent=2) + "\n")
    means = {mode: statistics.mean(r["wallSeconds"] for r in results if r["mode"] == mode)
             for mode in ("reference", "candidate")}
    summary = dict(meanWallSeconds=means, throughputGainPercent=100 * (means["reference"] / means["candidate"] - 1),
                   identicalCheckpoints=len(expected) - 1, identicalFrames=len(expected_frames))
    (args.output / "summary.json").write_text(json.dumps(summary, indent=2) + "\n")
    print(json.dumps(summary), flush=True)


if __name__ == "__main__":
    main()
