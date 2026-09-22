#!/usr/bin/env python3
"""Prepare the pinned RDP source with a narrow checkpoint access extension.

The original checkout and submodules stay unchanged. Only two friend
declarations differ; all checkpoint code lives in this repository.
"""
import pathlib
import subprocess
import sys

source = pathlib.Path(sys.argv[1]).resolve()
target = pathlib.Path(sys.argv[2]).resolve()
if source == target or source in target.parents:
    raise SystemExit("Checkpoint source must be outside the upstream checkout")
target.mkdir(parents=True, exist_ok=True)
marker = target / ".euther-checkpoint-source"
identity = str(source) + "\n1cecd042b2619bc505c12bfdc713808386f2b54d\n"
if marker.exists() and marker.read_text() != identity:
    raise SystemExit("Build directory belongs to a different upstream checkout")
if not marker.exists() and any(target.iterdir()):
    raise SystemExit("Checkpoint source directory must initially be empty")
marker.write_text(identity)
entries = subprocess.check_output(["git", "-C", str(source), "ls-tree", "-rz", "HEAD"])
for entry in entries.split(b"\0"):
    if not entry:
        continue
    meta, raw_path = entry.split(b"\t", 1)
    path = pathlib.Path(raw_path.decode())
    src, dst = source / path, target / path
    dst.parent.mkdir(parents=True, exist_ok=True)
    if meta.startswith(b"160000 "):
        if not dst.is_symlink():
            dst.symlink_to(src, target_is_directory=True)
        elif dst.resolve() != src:
            raise SystemExit(f"Unexpected submodule link: {dst}")
        continue
    data = src.read_bytes()
    if str(path) in ("parallel-rdp/rdp_device.hpp", "parallel-rdp/rdp_renderer.hpp"):
        if data.count(b"\nprivate:\n") != 1:
            raise SystemExit(f"Unexpected pinned class layout: {path}")
        data = data.replace(b"\nprivate:\n", b"\nprivate:\n\tfriend class EutherCheckpoint;\n")
    if not dst.exists() or dst.read_bytes() != data:
        dst.write_bytes(data)
        dst.chmod(src.stat().st_mode & 0o777)
