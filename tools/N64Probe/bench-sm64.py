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
    parser.add_argument("--reference-library", type=Path,
                        help="Compare two GPU libraries; defaults to --library")
    parser.add_argument("--reference-runtime", help="Explicit installed .NET runtime version (dotnet --fx-version)")
    parser.add_argument("--candidate-runtime", help="Explicit installed .NET runtime version (dotnet --fx-version)")
    parser.add_argument("--cores", default="6,7")
    parser.add_argument("--candidate-cores", help="Candidate CPU affinity; defaults to --cores")
    parser.add_argument("--start", type=int, default=70)
    parser.add_argument("--end", type=int, default=90)
    args = parser.parse_args()
    if args.reference_library and not args.library:
        parser.error("--reference-library requires --library for the candidate")
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
    manifest = dict(
        runtimes=subprocess.check_output(["dotnet", "--list-runtimes"], text=True).splitlines(),
        cores=args.cores, candidateCores=args.candidate_cores or args.cores, start=args.start, end=args.end,
        romSha256=hashlib.sha256(args.rom.read_bytes()).hexdigest(),
        gpuLibraries={mode: dict(path=str(library.resolve()), sha256=hashlib.sha256(library.read_bytes()).hexdigest())
                      for mode, library in (("reference", args.reference_library or args.library),
                                            ("candidate", args.library)) if library},
        environment={key: env.get(key) for key in (
            "PARALLEL_RDP_SMALL_TYPES", "PARALLEL_RDP_FORCE_SYNC_SHADER",
            "PARALLEL_RDP_SINGLE_THREADED_COMMAND", "GRANITE_NUM_WORKER_THREADS",
            "GRANITE_VULKAN_NO_VALIDATION", "EUTHERDRIVE_N64_GPU_OVERLAP",
            "EUTHERDRIVE_N64_CPU_JIT", "EUTHERDRIVE_N64_RSP_BLOCK_JIT", "EUTHERDRIVE_N64_RSP_BLOCK_LOOPS",
            "DOTNET_TieredCompilation", "DOTNET_TieredPGO")},
        variants={mode: dict(probe=str(getattr(args, mode).resolve()),
                            runtime=getattr(args, mode + "_runtime"),
                            files={p.name: hashlib.sha256(p.read_bytes()).hexdigest()
                                   for p in sorted(getattr(args, mode).parent.glob("*"))
                                   if p.is_file() and p.suffix in (".dll", ".json")})
                  for mode in ("reference", "candidate")})
    (args.output / "manifest.json").write_text(json.dumps(manifest, indent=2) + "\n")
    invariant = ("targetSecond", "cycles", "reads", "audioFrames", "audioSeconds", "audioSha256",
                 "inputSha256", "ramSha256", "action", "x", "y", "z", "graphicsTasks", "audioTasks")
    for index, mode in enumerate(("reference", "candidate", "candidate", "reference"), 1):
        name = f"{index}-{mode}"
        output = args.output / name
        print("running", name, flush=True)
        run_env = env.copy()
        if mode == "reference" and args.reference_library:
            run_env["EUTHERDRIVE_N64_GPU_LIBRARY"] = str(args.reference_library.resolve())
        cores = args.candidate_cores if mode == "candidate" and args.candidate_cores else args.cores
        command = ["taskset", "-c", cores, "dotnet"]
        runtime = getattr(args, mode + "_runtime")
        if runtime:
            command.extend(["--fx-version", runtime])
        command.extend([str(getattr(args, mode).resolve()), "--bench-sm64",
                        str(args.rom.resolve()), str(output.resolve()), str(args.end)])
        with (args.output / (name + ".log")).open("w") as log:
            subprocess.run(command,
                           env=run_env, stdout=log, stderr=subprocess.STDOUT, check=True)
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
                      audioSeconds=audio, realTimePercent=100 * audio / elapsed,
                      cycleRealTimePercent=100 * (end["cycles"] - begin["cycles"]) / 93_750_000 / elapsed,
                      intervals=[dict(start=a["targetSecond"], end=b["targetSecond"],
                                      realTimePercent=100 * (b["audioSeconds"] - a["audioSeconds"]) /
                                                      (b["wallSeconds"] - a["wallSeconds"]))
                                 for a, b in zip(points, points[1:])
                                 if args.start <= a["targetSecond"] < b["targetSecond"] <= args.end])
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
