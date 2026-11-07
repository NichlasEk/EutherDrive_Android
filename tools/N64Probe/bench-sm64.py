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
import time


def pin_candidate_threads(game, output, cpu_core, helper_cores, window_start):
    """Identify the dominant emulation thread before the measured window."""
    while True:
        if game.poll() is not None:
            raise RuntimeError("Game ended before affinity setup")
        try:
            points = json.loads((output / "checkpoints.json").read_text())
            if points[-1]["targetSecond"] >= 20:
                if points[-1]["targetSecond"] >= window_start:
                    raise RuntimeError("Affinity setup must precede the timing window")
                break
        except (FileNotFoundError, json.JSONDecodeError):
            pass
        time.sleep(0.1)

    def cpu_times():
        result = {}
        for task in Path(f"/proc/{game.pid}/task").iterdir():
            try:
                fields = (task / "stat").read_text().rsplit(")", 1)[1].split()
                result[int(task.name)] = int(fields[11]) + int(fields[12])
            except FileNotFoundError:
                pass
        return result

    before = cpu_times()
    time.sleep(1)
    after = cpu_times()
    usage = sorted(((ticks - before[tid], tid) for tid, ticks in after.items() if tid in before), reverse=True)
    if not usage or usage[0][0] < os.sysconf("SC_CLK_TCK") // 2 or (len(usage) > 1 and usage[0][0] < 2 * usage[1][0]):
        raise RuntimeError(f"No unambiguous CPU emulation thread: {usage}")
    cpu_tid = usage[0][1]
    if json.loads((output / "checkpoints.json").read_text())[-1]["targetSecond"] >= window_start:
        raise RuntimeError("CPU identification crossed into the timing window")
    if not ({cpu_core} | helper_cores) <= os.sched_getaffinity(cpu_tid):
        raise RuntimeError("Thread placement must stay inside the process affinity")
    (output / "thread-cpu-sample.json").write_text(json.dumps(usage))
    placement = []
    found = 0
    for task in Path(f"/proc/{game.pid}/task").iterdir():
        try:
            tid = int(task.name)
            name = (task / "comm").read_text().strip()
            cpu = tid == cpu_tid
            os.sched_setaffinity(tid, {cpu_core} if cpu else helper_cores)
            placement.append(dict(tid=tid, name=name, cores=sorted(os.sched_getaffinity(tid))))
            found += int(cpu)
        except (FileNotFoundError, ProcessLookupError):
            pass
    if found != 1:
        raise RuntimeError("Selected emulation thread disappeared")
    (output / "thread-placement.json").write_text(json.dumps(placement, indent=2))


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
    parser.add_argument("--state", type=Path, help="Resume both probes from the same GPU core state; checkpoints use absolute guest seconds")
    parser.add_argument("--candidate-cpu-core", type=int, help="Pin the candidate's dominant emulation thread after startup")
    parser.add_argument("--candidate-helper-cores", help="Comma-separated CPU IDs for the candidate's other threads")
    parser.add_argument("--start", type=int, default=70)
    parser.add_argument("--end", type=int, default=90)
    args = parser.parse_args()
    if (args.candidate_cpu_core is None) != (args.candidate_helper_cores is None):
        parser.error("--candidate-cpu-core and --candidate-helper-cores must be supplied together")
    helper_cores = set()
    if args.candidate_cpu_core is not None:
        try:
            helper_cores = {int(value) for value in args.candidate_helper_cores.split(",")}
        except ValueError:
            parser.error("Helper cores must be comma-separated integers")
        if args.candidate_cpu_core < 0 or min(helper_cores) < 0 or args.candidate_cpu_core in helper_cores or args.start <= 20:
            parser.error("Thread placement needs distinct nonnegative CPUs and a timing window after guest second 20")
    if args.reference_library and not args.library:
        parser.error("--reference-library requires --library for the candidate")
    if args.start < 5 or args.end <= args.start or args.start % 5 or args.end % 5:
        parser.error("start/end must be increasing multiples of 5")
    args.output.mkdir(parents=True, exist_ok=False)
    env = dict(os.environ, PARALLEL_RDP_SMALL_TYPES="1", PARALLEL_RDP_FORCE_SYNC_SHADER="1",
               GRANITE_NUM_WORKER_THREADS="2", GRANITE_VULKAN_NO_VALIDATION="1")
    # Match the desktop launcher even when invoked from a fresh shell. Keep
    # explicit overrides so either GPU scheduling path can still be compared.
    env.setdefault("PARALLEL_RDP_SINGLE_THREADED_COMMAND", "1")
    env.setdefault("EUTHERDRIVE_N64_GPU_OVERLAP", "1")
    env.pop("EUTHERDRIVE_N64_GPU_AUDIT", None)
    env.pop("EUTHERDRIVE_N64_GPU_VALIDATE", None)
    env.pop("EUTHERDRIVE_N64_GPU_LIBRARY", None)
    env.pop("DOTNET_PerfMapEnabled", None)
    env.pop("N64_PROBE_SM64_STATE", None)
    env.pop("N64_PROBE_SM64_SAVE_STATE", None)
    if args.state:
        env["N64_PROBE_SM64_STATE"] = str(args.state.resolve())
    if args.library:
        env["EUTHERDRIVE_N64_GPU_LIBRARY"] = str(args.library.resolve())
    expected = None
    expected_frames = None
    results = []
    manifest = dict(
        runtimes=subprocess.check_output(["dotnet", "--list-runtimes"], text=True).splitlines(),
        cores=args.cores, candidateCores=args.candidate_cores or args.cores, start=args.start, end=args.end,
        romSha256=hashlib.sha256(args.rom.read_bytes()).hexdigest(),
        coreState=dict(path=str(args.state.resolve()), sha256=hashlib.sha256(args.state.read_bytes()).hexdigest()) if args.state else None,
        candidateThreadPlacement=dict(cpu=args.candidate_cpu_core, helpers=sorted(helper_cores)) if helper_cores else None,
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
            game = subprocess.Popen(command, env=run_env, stdout=log, stderr=subprocess.STDOUT)
            try:
                if mode == "candidate" and helper_cores:
                    pin_candidate_threads(game, output, args.candidate_cpu_core, helper_cores, args.start)
                if game.wait() != 0:
                    raise RuntimeError(f"{name}: game failed")
            finally:
                if game.poll() is None:
                    game.terminate()
                    try:
                        game.wait(timeout=10)
                    except subprocess.TimeoutExpired:
                        game.kill()
                        game.wait()
        if (output / "jit-profile.json").exists() or (output / "rsp-profile.txt").exists():
            raise RuntimeError("Instrumented CPU/RSP profile cannot be used as a timing sample")
        points = json.loads((output / "checkpoints.json").read_text())
        state = [{key: point[key] for key in invariant} for point in points]
        state.append({"finalRamSha256": hashlib.sha256((output / "rdram.bin").read_bytes()).hexdigest()})
        if args.state:
            state.append({"finalCpuSha256": hashlib.sha256((output / "cpu-state.bin").read_bytes()).hexdigest()})
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
                   identicalCheckpoints=len(expected) - (2 if args.state else 1), identicalFrames=len(expected_frames))
    (args.output / "summary.json").write_text(json.dumps(summary, indent=2) + "\n")
    print(json.dumps(summary), flush=True)


if __name__ == "__main__":
    main()
