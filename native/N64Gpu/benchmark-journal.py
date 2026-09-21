#!/usr/bin/env python3
"""Serial, interleaved managed/native journal timings; correctness first.

Requires a journal and its independent --journal-reference export. Does not
measure CPU emulation, RSP, VI, presentation, or actual game speed.
"""
import json
import os
from pathlib import Path
import statistics
import subprocess
import sys


def main():
    if len(sys.argv) != 6:
        raise SystemExit("Usage: benchmark-journal.py N64PROBE_DLL LIBRARY JOURNAL_DIR REFERENCE_DIR NEW_OUTPUT")
    probe, library, journal, reference, output = map(lambda p: Path(p).resolve(), sys.argv[1:])
    output.mkdir(parents=True, exist_ok=False)
    base = ["taskset", "-c", "6,7", "dotnet", str(probe), "--replay-gpu-journal", str(library), str(journal), str(reference)]
    env = dict(os.environ, PARALLEL_RDP_SMALL_TYPES="1", PARALLEL_RDP_FORCE_SYNC_SHADER="1",
               GRANITE_NUM_WORKER_THREADS="2", GRANITE_VULKAN_NO_VALIDATION="0")
    runs = []
    # Exact whole-memory/reference comparisons stay on for every measured run.
    # Validation warmups also reject an unavailable validation layer.
    for index, mode in enumerate(["strict", "ranges", "strict", "ranges", "ranges", "strict", "strict", "ranges"]):
        validating = index < 2
        env["GRANITE_VULKAN_NO_VALIDATION"] = "0" if validating else "1"
        name = f"{index:02d}-{mode}-{'validation' if validating else 'bench'}"
        command = base + [str(output / name), mode] + ([] if validating else ["--bench"])
        with (output / (name + ".log")).open("w") as log:
            subprocess.run(command, env=env, stdout=log, stderr=subprocess.STDOUT, check=True)
        data = json.loads((output / name / "results.json").read_text())
        if data["frames"] <= 5: raise RuntimeError("Need more than five checkpoints for this timing protocol")
        print(name, "medianMs=", data["afterFirstFiveMedianMs"], "barriers=", data["WriteBarriers"], flush=True)
        if not validating:
            runs.append(dict(name=name, mode=mode, medianMs=data["afterFirstFiveMedianMs"],
                             afterFirstFiveTotalMs=sum(f["elapsedMs"] for f in data["results"][5:]),
                             initializationMs=data["initializationMs"], allFramesMs=data["submitTransferWaitReadbackTotalMs"],
                             writeBarriers=data["WriteBarriers"]))
    medians = {mode: statistics.median(r["medianMs"] for r in runs if r["mode"] == mode) for mode in ["strict", "ranges"]}
    summary = dict(runs=runs, medianMs=medians, reductionPercent=100 * (1 - medians["ranges"] / medians["strict"]),
                   scope="Ordered renderer replay including transfers, waits and full readback; not gameplay FPS or real-time speed",
                   environment={k: env[k] for k in ["PARALLEL_RDP_SMALL_TYPES", "PARALLEL_RDP_FORCE_SYNC_SHADER", "GRANITE_NUM_WORKER_THREADS"]},
                   affinity="6,7", warmup="Two validated complete replays; timing medians exclude first five checkpoints of every run")
    (output / "summary.json").write_text(json.dumps(summary, indent=2) + "\n")
    print(json.dumps(summary, indent=2))


if __name__ == "__main__":
    main()
