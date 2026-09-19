"""Compare presented frames against full-rate rendering; never modify states.

Usage: python tools/DariusProbe/check-video-pacing.py PROBE_DLL ROM STATE_DIR
Optional: --frames 360 --slots 1 2 3
Build the Release probe first. Disable adaptive timing to get repeatable skips.
"""
import argparse
import csv
import os
from pathlib import Path
import re
import subprocess
import tempfile


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("probe", type=Path)
    parser.add_argument("rom", type=Path)
    parser.add_argument("state_dir", type=Path)
    parser.add_argument("--frames", type=int, default=360)
    parser.add_argument("--slots", type=int, nargs="+", default=[1, 2, 3])
    args = parser.parse_args()
    if args.frames < 12:
        parser.error("--frames must be at least 12")
    with tempfile.TemporaryDirectory(prefix="darius-pacing-") as temporary:
        for slot in args.slots:
            reference = None
            for divisor in (1, 2, 3):
                directory = Path(temporary) / f"slot{slot}-div{divisor}"
                env = dict(os.environ, EUTHERDRIVE_DARIUSG_ADAPTIVE_RENDER="0",
                           EUTHERDRIVE_DARIUSG_RENDER_DIVISOR=str(divisor),
                           EUTHERDRIVE_DARIUS_PROBE_FIRE="0",
                           EUTHERDRIVE_DARIUS_PROBE_CAPTURE_DIR=str(directory))
                result = subprocess.run(["dotnet", str(args.probe), str(args.rom),
                                         str(args.frames), str(args.state_dir), str(slot)],
                                        env=env, stdout=subprocess.PIPE,
                                        stderr=subprocess.STDOUT, text=True, check=True)
                with (directory / "frames.csv").open() as trace:
                    rows = list(csv.DictReader(trace))
                hashes = dict(re.findall(r"^(audio|state)SHA256=([A-F0-9]+)$", result.stdout, re.M))
                if len(rows) != args.frames or len(hashes) != 2:
                    raise AssertionError("Incomplete probe replay")
                if reference is None:
                    reference = rows, hashes
                    continue
                if hashes != reference[1]:
                    raise AssertionError(f"slot={slot} divisor={divisor}: audio/state changed")
                checked = 0
                for index, row in enumerate(rows):
                    # Allow the initially reconstructed two-stage sprite pipeline to fill.
                    if index < 4 or int(row["emulatedFrame"]) % divisor:
                        continue
                    if row["videoHash"] != reference[0][index]["videoHash"]:
                        raise AssertionError(f"slot={slot} divisor={divisor}: stale picture at frame={row['emulatedFrame']}")
                    if row["reefHash"] != reference[0][index]["reefHash"]:
                        raise AssertionError(f"slot={slot} divisor={divisor}: stale sprite history")
                    checked += 1
                print(f"slot={slot} divisor={divisor} matched={checked} audio/state=identical", flush=True)


if __name__ == "__main__":
    main()
