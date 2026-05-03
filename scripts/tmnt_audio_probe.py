#!/usr/bin/env python3
import argparse
import math
import os
import struct
import wave


STEMS = [
    ("mix", "mix_s16le.raw"),
    ("ym2151", "ym2151_s16le.raw"),
    ("k007232", "k007232_s16le.raw"),
    ("upd7759", "upd7759_s16le.raw"),
    ("title", "title_s16le.raw"),
]


def read_s16le(path):
    with open(path, "rb") as fh:
        data = fh.read()
    if len(data) & 1:
        data = data[:-1]
    return struct.unpack("<" + "h" * (len(data) // 2), data)


def write_wav(path, samples, sample_rate, channels):
    with wave.open(path, "wb") as wav:
        wav.setnchannels(channels)
        wav.setsampwidth(2)
        wav.setframerate(sample_rate)
        wav.writeframes(struct.pack("<" + "h" * len(samples), *samples))


def stats(samples, channels, sample_rate, silence_threshold):
    if not samples:
        return {
            "frames": 0,
            "seconds": 0.0,
            "peak": 0,
            "rms": 0.0,
            "nonzero": 0,
            "longest_silence_ms": 0.0,
        }

    peak = max(abs(x) for x in samples)
    rms = math.sqrt(sum(x * x for x in samples) / len(samples))
    nonzero = sum(1 for x in samples if x != 0)

    longest = 0
    current = 0
    for frame in range(0, len(samples), channels):
        frame_peak = max(abs(x) for x in samples[frame:frame + channels])
        if frame_peak <= silence_threshold:
            current += 1
            longest = max(longest, current)
        else:
            current = 0

    frames = len(samples) // channels
    return {
        "frames": frames,
        "seconds": frames / sample_rate,
        "peak": peak,
        "rms": rms,
        "nonzero": nonzero,
        "longest_silence_ms": longest * 1000.0 / sample_rate,
    }


def print_interesting_events(path, limit):
    if not os.path.exists(path):
        return

    interesting = ("upd ", "upd start-line", "upd port", "sres", "sound-irq", "main-latch")
    printed = 0
    print("\nEvents:")
    with open(path, "r", encoding="utf-8", errors="replace") as fh:
        for line in fh:
            if any(token in line for token in interesting):
                print(line.rstrip())
                printed += 1
                if printed >= limit:
                    break


def main():
    parser = argparse.ArgumentParser(description="Analyze EutherDrive TMNT audio probe dumps.")
    parser.add_argument("probe_dir")
    parser.add_argument("--sample-rate", type=int, default=44100)
    parser.add_argument("--channels", type=int, default=2)
    parser.add_argument("--silence-threshold", type=int, default=12)
    parser.add_argument("--events", type=int, default=80)
    parser.add_argument("--wav", action="store_true", help="also write WAV files next to the raw stems")
    args = parser.parse_args()

    print(f"TMNT audio probe: {args.probe_dir}")
    print(f"format: s16le {args.sample_rate} Hz {args.channels} channels")
    for name, filename in STEMS:
        path = os.path.join(args.probe_dir, filename)
        if not os.path.exists(path):
            continue

        samples = read_s16le(path)
        s = stats(samples, args.channels, args.sample_rate, args.silence_threshold)
        print(
            f"{name:7} frames={s['frames']:7d} sec={s['seconds']:7.3f} "
            f"peak={s['peak']:5d} rms={s['rms']:8.1f} nonzero={s['nonzero']:8d} "
            f"longest_silence={s['longest_silence_ms']:7.2f}ms"
        )
        if args.wav:
            wav_path = os.path.join(args.probe_dir, f"{name}.wav")
            write_wav(wav_path, samples, args.sample_rate, args.channels)
            print(f"  wrote {wav_path}")

    print_interesting_events(os.path.join(args.probe_dir, "events.log"), args.events)


if __name__ == "__main__":
    main()
