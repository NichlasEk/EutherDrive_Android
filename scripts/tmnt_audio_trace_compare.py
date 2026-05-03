#!/usr/bin/env python3
import argparse
import os
import re
from collections import Counter, defaultdict


TARGET_FPS = 24_000_000.0 / 4.0 / 384.0 / 264.0
Z80_CLOCK = 3_579_545
YM_CLOCK = 3_579_545
K007232_CLOCK = 3_579_545
UPD_CLOCK = 640_000


LINE_RE = re.compile(r"frame=(\d+) z80cyc=(\d+) pc=0x([0-9A-Fa-f]{4}) (.*)")
BEGIN_RE = re.compile(r"frame=(\d+) begin")
UPD_EXPECTED_RE = re.compile(
    r"sample=0x([0-9A-Fa-f]{2}).*?clocks=(\d+) sec=([0-9.]+) frames=([0-9.]+)"
)


def parse_value(token):
    if token.startswith("0x"):
        return int(token, 16)
    return int(token)


def parse_kv(message):
    result = {}
    for part in message.split():
        if "=" not in part:
            continue
        key, value = part.split("=", 1)
        value = value.rstrip(",")
        if value.startswith("0x"):
            try:
                result[key] = int(value, 16)
            except ValueError:
                result[key] = value
        else:
            try:
                result[key] = int(value)
            except ValueError:
                result[key] = value
    return result


def load_expected(path):
    expected = {}
    if not path or not os.path.exists(path):
        return expected
    with open(path, "r", encoding="utf-8", errors="replace") as fh:
        for line in fh:
            match = UPD_EXPECTED_RE.search(line)
            if match:
                sample = int(match.group(1), 16)
                expected[sample] = {
                    "clocks": int(match.group(2)),
                    "seconds": float(match.group(3)),
                    "frames": float(match.group(4)),
                }
    return expected


def parse_trace(path):
    events = []
    frame_begins = []
    with open(path, "r", encoding="utf-8", errors="replace") as fh:
        for line in fh:
            line = line.strip()
            begin = BEGIN_RE.fullmatch(line)
            if begin:
                frame_begins.append(int(begin.group(1)))
                continue
            match = LINE_RE.fullmatch(line)
            if not match:
                continue
            frame = int(match.group(1))
            z80cyc = int(match.group(2))
            pc = int(match.group(3), 16)
            message = match.group(4)
            events.append((frame, z80cyc, pc, message, parse_kv(message)))
    return frame_begins, events


def frame_time(frame, z80cyc):
    return frame + (z80cyc / (Z80_CLOCK / TARGET_FPS))


def summarize_sound_latches(events):
    commands = []
    for frame, z80cyc, pc, message, kv in events:
        if message.startswith("read soundlatch"):
            commands.append((frame, z80cyc, pc, kv.get("value")))
    counts = Counter(cmd for _, _, _, cmd in commands if isinstance(cmd, int))
    print("Sound latch commands:")
    if not commands:
        print("  none")
        return
    for frame, z80cyc, pc, cmd in commands[:24]:
        rendered = f"0x{cmd:02X}" if isinstance(cmd, int) else str(cmd)
        print(f"  frame={frame:5d} cyc={z80cyc:5d} pc=0x{pc:04X} cmd={rendered}")
    if len(commands) > 24:
        print(f"  ... {len(commands) - 24} more")
    top = ", ".join(f"0x{k:02X}:{v}" for k, v in counts.most_common(12))
    print(f"  top: {top if top else 'none'}")


def summarize_upd(events, expected):
    starts = []
    busy_idle = []
    current_port = None
    for frame, z80cyc, pc, message, kv in events:
        if message.startswith("write upd-port"):
            current_port = kv.get("value")
        elif message.startswith("upd-start-after") and current_port is not None:
            starts.append((current_port, frame, z80cyc, pc))
            current_port = None
        elif message.startswith("read upd-busy") and kv.get("value") == 1:
            busy_idle.append((frame, z80cyc, pc))

    print("\nuPD7759 starts:")
    if not starts:
        print("  none")
        return
    for index, (sample, frame, z80cyc, pc) in enumerate(starts[:32]):
        exp = expected.get(sample)
        extra = ""
        if exp:
            extra = f" expected={exp['frames']:.1f}fr/{exp['seconds']:.3f}s"
            if index + 1 < len(starts):
                next_frame, next_cyc = starts[index + 1][1], starts[index + 1][2]
                actual = frame_time(next_frame, next_cyc) - frame_time(frame, z80cyc)
                extra += f" next-start-delta={actual:.1f}fr"
        print(f"  frame={frame:5d} cyc={z80cyc:5d} pc=0x{pc:04X} sample=0x{sample:02X}{extra}")
    if len(starts) > 32:
        print(f"  ... {len(starts) - 32} more")
    if busy_idle:
        first = busy_idle[0]
        print(f"  first idle read: frame={first[0]} cyc={first[1]} pc=0x{first[2]:04X}")


def summarize_k007(events):
    regs = [{}, {}]
    starts = []
    writes = Counter()
    for frame, z80cyc, pc, message, kv in events:
        if message.startswith("write k007232"):
            off = kv.get("off")
            value = kv.get("value")
            if isinstance(off, int) and isinstance(value, int):
                writes[off] += 1
                ch = 1 if off >= 6 else 0
                regs[ch][off - (6 if ch else 0)] = value
        elif message.startswith("read k007232"):
            off = kv.get("off")
            if off in (5, 11):
                ch = 1 if off == 11 else 0
                base = regs[ch]
                step = ((base.get(1, 0) & 0x0F) << 8) | base.get(0, 0)
                start = ((base.get(4, 0) & 1) << 16) | (base.get(3, 0) << 8) | base.get(2, 0)
                starts.append((frame, z80cyc, pc, ch, start, step, base.copy()))

    print("\nK007232 starts:")
    if not starts:
        print("  none")
    for frame, z80cyc, pc, ch, start, step, _ in starts[:40]:
        freq = 0.0
        if step < 0x1000:
            freq = K007232_CLOCK / (4.0 * (0x1000 - step))
        print(
            f"  frame={frame:5d} cyc={z80cyc:5d} pc=0x{pc:04X} "
            f"ch={ch} start=0x{start:05X} step=0x{step:03X} nominal={freq:8.1f}Hz"
        )
    if len(starts) > 40:
        print(f"  ... {len(starts) - 40} more")
    if writes:
        top = ", ".join(f"0x{k:02X}:{v}" for k, v in writes.most_common())
        print(f"  writes by offset: {top}")


def summarize_ym(events):
    status_counts = Counter()
    timer_b_edges = []
    last_b = None
    writes = []
    selected = None
    for frame, z80cyc, pc, message, kv in events:
        if message.startswith("read ym2151"):
            status = kv.get("status")
            if isinstance(status, int):
                status_counts[status] += 1
                b = bool(status & 0x02)
                if last_b is not None and b and not last_b:
                    timer_b_edges.append((frame, z80cyc, pc))
                last_b = b
        elif message.startswith("write ym2151"):
            off = kv.get("off")
            value = kv.get("value")
            if off == 0:
                selected = value
            elif off == 1 and selected is not None:
                writes.append((frame, z80cyc, pc, selected, value))

    print("\nYM2151:")
    if status_counts:
        print("  status reads: " + ", ".join(f"0x{k:02X}:{v}" for k, v in status_counts.most_common(12)))
    if timer_b_edges:
        print(f"  timer-B visible rising edges: {len(timer_b_edges)}")
        for frame, z80cyc, pc in timer_b_edges[:12]:
            print(f"    frame={frame:5d} cyc={z80cyc:5d} pc=0x{pc:04X}")
    if writes:
        reg_counts = Counter(reg for _, _, _, reg, _ in writes)
        print("  top register writes: " + ", ".join(f"0x{k:02X}:{v}" for k, v in reg_counts.most_common(16)))
        for frame, z80cyc, pc, reg, value in writes[:20]:
            print(f"    frame={frame:5d} cyc={z80cyc:5d} pc=0x{pc:04X} r0x{reg:02X}=0x{value:02X}")


def summarize_frame_cycles(frame_begins, events):
    by_frame = defaultdict(int)
    for frame, z80cyc, *_ in events:
        by_frame[frame] = max(by_frame[frame], z80cyc)
    if not by_frame:
        return
    values = list(by_frame.values())
    print("\nScheduler:")
    print(f"  MAME Z80 clock: {Z80_CLOCK} Hz")
    print(f"  target fps: {TARGET_FPS:.6f}")
    print(f"  expected Z80 cycles/frame: {Z80_CLOCK / TARGET_FPS:.1f}")
    print(f"  traced frames: {len(values)} min/max z80cyc={min(values)}/{max(values)} avg={sum(values)/len(values):.1f}")


def main():
    parser = argparse.ArgumentParser(description="Compare EutherDrive TMNT audio trace against MAME-derived timing constants.")
    parser.add_argument("trace_dir_or_log")
    args = parser.parse_args()

    trace_path = args.trace_dir_or_log
    expected_path = None
    if os.path.isdir(trace_path):
        expected_path = os.path.join(trace_path, "upd_expected.log")
        trace_path = os.path.join(trace_path, "trace.log")

    if not os.path.exists(trace_path):
        raise SystemExit(f"trace log not found: {trace_path}")

    expected = load_expected(expected_path)
    frame_begins, events = parse_trace(trace_path)
    print(f"trace: {trace_path}")
    print(f"events: {len(events)}")
    summarize_frame_cycles(frame_begins, events)
    summarize_sound_latches(events)
    summarize_upd(events, expected)
    summarize_k007(events)
    summarize_ym(events)


if __name__ == "__main__":
    main()
