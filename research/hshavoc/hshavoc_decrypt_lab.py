#!/usr/bin/env python3
"""High Seas Havoc arcade ROM decryption scratch lab.

This script is intentionally read-only for the ROM inputs.  It builds a few
candidate decoded images in memory and prints facts useful for iterating on the
MAME hshavoc.cpp init/decryption logic.
"""

from __future__ import annotations

import argparse
import binascii
import collections
import hashlib
import math
import re
import struct
import zipfile
from pathlib import Path
from itertools import combinations, permutations, product


DEFAULT_DIR = Path("/home/nichlas/roms/MAME/DataEast/hshavoc")
ARCADE_ZIP = "hshavoc.zip"
USA_REF = "High Seas Havoc (U) [!].gen"
EU_REF = "Capt'n Havoc (E) [!].gen"


DATA_BITSWAP = [7, 15, 6, 14, 5, 2, 1, 10, 13, 4, 12, 3, 11, 0, 8, 9]
TAIL_BITSWAP = [7, 15, 6, 14, 5, 2, 1, 0, 13, 4, 12, 3, 11, 10, 9, 8]
EXTRA_BITSWAP = [15, 13, 14, 12, 11, 10, 9, 0, 8, 6, 5, 4, 3, 2, 1, 7]
BASE_DECODE_END = 0xE8000
TYPEDAT = [1, 1, 1, 1, 1, 1, 1, 1, 1, 0, 0, 1, 1, 0, 1, 1]
STRICT_PEEL5B_CONTROL = (0, 1, 1, 0, 0)
STRICT_PEEL5B_BIT_ORDER = (1, 2, 7, 4, 0, 3)
SECOND_PEEL5B_CONTROL = (0, 1, 1, 0, 0)
SECOND_PEEL5B_BIT_ORDER = (4, 0, 3, 1, 7, 8)

BEST_STARTUP_PATCH = {
    0x0C42: 0x007C,
    0x0C44: 0x0700,
    0x0C46: 0x4EB9,
    0x0C48: 0x0000,
    0x0C4A: 0x10A2,
    0x0C4C: 0x4EB9,
    0x0C4E: 0x0000,
    0x0C50: 0x1082,
    0x0C52: 0x2F3C,
    0x0C54: 0x0000,
    0x0C56: 0x1084,
    0x0C58: 0x4EB9,
    0x0C5A: 0x0000,
    0x0C5C: 0x107A,
    0x0C5E: 0x4EB9,
    0x0C60: 0x0000,
    0x0C62: 0x101C,
    0x0C64: 0x4EB9,
    0x0C66: 0x0000,
    0x0C68: 0x10F8,
    0x0C6A: 0x4EB9,
    0x0C6C: 0x0000,
    0x0C6E: 0x10A8,
    0x0C70: 0x4EB8,
    0x0C72: 0x00F8,
    0x0C74: 0x01A6,
    0x0C76: 0x4EB9,
    0x0C78: 0x0000,
    0x0C7A: 0x0E2E,
    0x0C7C: 0x4EB9,
    0x0C7E: 0x0000,
    0x0C80: 0x0ADC,
    0x0C82: 0x4EB9,
    0x0C84: 0x0000,
    0x0C86: 0x0ABA,
    0x0C88: 0x4EB9,
    0x0C8A: 0x0000,
    0x0C8C: 0x0AF4,
    0x0C8E: 0x4EB9,
    0x0C90: 0x0000,
    0x0C92: 0x0D34,
    0x0C94: 0x2F3C,
    0x0C96: 0x0000,
    0x0C98: 0x0A1C,
    0x0C9A: 0x4E75,
}

STARTUP_TARGET_ADJUSTMENTS = [
    (0x0C76, 0x0E2E, [0x0E32]),
    (0x0C82, 0x0ABA, [0x0AB8, 0x0ABC]),
    (0x0C88, 0x0AF4, [0x0AF2, 0x0AF8]),
    (0x0C8E, 0x0D34, [0x0D32]),
]

WEAK_WINDOWS = [
    ("startup call $101c", 0x101C, 0x1068),
    ("startup call $1082/$1084", 0x1082, 0x10A2),
    ("startup call $0e2e/$0e32", 0x0E2E, 0x0E90),
    ("startup call $0d34", 0x0D34, 0x0DB0),
]

KNOWN_STARTUP_WORDS = {
    0x00F8,
    0x0A1C,
    0x0AB8,
    0x0ABC,
    0x0ADC,
    0x0AF2,
    0x0AF8,
    0x0D32,
    0x0D34,
    0x0E2E,
    0x0E32,
    0x101C,
    0x107A,
    0x1082,
    0x1084,
    0x10A2,
    0x10A8,
    0x10F8,
}

PEEL_TARGET_WORDS = {
    0x0000,
    0x000D,
    0x00C0,
    0x00FF,
    0x007C,
    0x0240,
    0x0040,
    0x3039,
    0x303C,
    0x33C0,
    0x33FC,
    0x41F9,
    0x4279,
    0x43F9,
    0x48E7,
    0x4A79,
    0x4CDF,
    0x4E73,
    0x4E75,
    0x4EB8,
    0x4EB9,
    0x4EF9,
    0x6100,
    0x6600,
    0x6700,
} | KNOWN_STARTUP_WORDS

PEEL_PAIR_CACHE: dict[tuple[int, int], bool] = {}
PEEL5B_AFFINE_CONTROLS: list[tuple[int, int, int, int, int]] | None = None
PEEL5B_KNOWN_PAIR_CACHE: dict[tuple[tuple[int, int], ...], list[tuple[tuple[int, ...], tuple[int, ...]]]] = {}


def bitswap(value: int, order: list[int]) -> int:
    out = 0
    for i, bit in enumerate(order):
        out |= ((value >> bit) & 1) << (len(order) - 1 - i)
    return out


def crc_sha1(data: bytes) -> tuple[str, str]:
    return f"{binascii.crc32(data) & 0xffffffff:08x}", hashlib.sha1(data).hexdigest()


def read_arcade_rom(base: Path) -> bytes:
    with zipfile.ZipFile(base / ARCADE_ZIP) as zf:
        even = zf.read("d-25.11a")
        odd = zf.read("d-26.9a")

    rom = bytearray(len(even) + len(odd))
    rom[0::2] = even
    rom[1::2] = odd
    return bytes(rom)


def words_from(data: bytes) -> list[int]:
    return list(struct.unpack(">" + "H" * (len(data) // 2), data))


def bytes_from(words: list[int]) -> bytes:
    return struct.pack(">" + "H" * len(words), *words)


def read_be_words(data: bytes, offset: int, count: int) -> list[int]:
    end = min(len(data), offset + count * 2)
    if offset < 0 or offset >= len(data) or end <= offset:
        return []
    size = (end - offset) & ~1
    return list(struct.unpack(">" + "H" * (size // 2), data[offset : offset + size]))


def decode_base(raw: bytes) -> list[int]:
    words = words_from(raw)

    for x in range(0xE8000 // 2):
        words[x] = decode_data_word(words[x], TYPEDAT[x & 0xF])

    for x in range(0xE8000 // 2, 0x100000 // 2):
        words[x] = bitswap(words[x], TAIL_BITSWAP)

    for i, xor in enumerate([0x0107, 0x0107, 0x0107, 0x0707]):
        words[i] ^= xor

    return words


def decode_data_word(raw_word: int, typedat: int) -> int:
    word = bitswap(raw_word, DATA_BITSWAP)
    word ^= 0x0501 if typedat else 0x0406

    if word & 0x0400:
        word ^= 0x0200

    if typedat == 0:
        if word & 0x0100:
            word ^= 0x0004
        word = bitswap(word, [15, 14, 13, 12, 11, 9, 10, 8, 7, 6, 5, 4, 3, 2, 1, 0])

    return word


def typedat_from_peel4b() -> list[int]:
    """Recreate MAME's `typedat` table from PEEL 4B output o18.

    With i3 high and i4 low, 4B's registered outputs form a 4-bit counter.
    Inverting o18 over that counter gives the table currently hard-coded in
    hshavoc.cpp.
    """
    values = []
    for state in peel4b_counter_sequence(16):
        _, outputs = peel4b_next_state(i1=0, i2=0, i3=1, i4=0, i5=0, i6=0, i7=0, state=state)
        values.append(1 - outputs[1])
    return values


def apply_mame_extra(words: list[int]) -> list[int]:
    out = words[:]
    for x in range(0x0C42 // 2, 0x0C9A // 2):
        word = out[x] ^ 0x0107
        word = bitswap(word, EXTRA_BITSWAP)
        out[x] = word ^ 0x0001
    return out


def apply_startup_candidate(words: list[int]) -> list[int]:
    """A diagnostic candidate, not a proposed final fix.

    It demonstrates that the first startup word wants the extra transform
    without the final xor, while the 2e3f/0107 call-marker words want the xor.
    """
    out = words[:]
    marker_offsets = [
        0x0C46, 0x0C4C, 0x0C58, 0x0C5E, 0x0C64, 0x0C6A, 0x0C70,
        0x0C76, 0x0C7C, 0x0C82, 0x0C88, 0x0C8E,
    ]

    for addr in (0x0C42, 0x0C44):
        x = addr // 2
        out[x] = bitswap(out[x] ^ 0x0107, EXTRA_BITSWAP)

    for off in marker_offsets:
        for x in range(off // 2, off // 2 + 3):
            source = out[x]
            word = bitswap(source ^ 0x0107, EXTRA_BITSWAP)
            if source in (0x2E3F, 0x0107):
                word ^= 1
            elif word & 1:
                word ^= 1
            out[x] = word

    return out


def apply_startup_opcode_only_candidate(words: list[int]) -> list[int]:
    """Diagnostic startup pass where final bit xor is applied to opwords only.

    MAME's current extra pass turns `2e3f 0107 1025` into
    `4eb9 0001 10a3`, an odd absolute-long JSR target.  Applying the same
    bitswap but reserving the final bit xor for decoded opwords produces
    `4eb9 0000 10a2`, a much more plausible local startup call.
    """
    out = words[:]
    for x in range(0x0C42 // 2, 0x0C9A // 2):
        source = out[x]
        word = bitswap(source ^ 0x0107, EXTRA_BITSWAP)
        if source == 0x2E3F:
            word ^= 1
        out[x] = word
    return out


def apply_best_startup_candidate(words: list[int]) -> list[int]:
    """Apply the current best theoretical startup patch.

    This is a research artifact, not a final protection emulation.  It records
    the highest-scoring linear startup path found by this lab so it can be
    disassembled and tested as a complete candidate image.
    """
    out = words[:]
    for addr, value in BEST_STARTUP_PATCH.items():
        out[addr // 2] = value
    return out


def apply_adjusted_startup_candidate(words: list[int]) -> list[int]:
    out = apply_best_startup_candidate(words)
    # Diagnostic only: patch weak targets to nearby entries that score better.
    out[0x0C7A // 2] = 0x0E32
    out[0x0C86 // 2] = 0x0AB8
    out[0x0C8C // 2] = 0x0AF8
    out[0x0C92 // 2] = 0x0D32
    return out


def peel5b_outputs(
    *,
    i1: int,
    i2: int,
    i3: int,
    i4: int,
    i5: int,
    i6: int,
    i7: int,
    i8: int,
    i9: int,
    i12: int,
    rf13: int,
) -> tuple[int, int, int, int, int, int]:
    """Evaluate the six combinatorial outputs from PEEL18CV8S at 5B.

    The equations are transcribed from `jedutil -view peel18cv8s.5b.bin 18CV8`.
    Output 17 is active low in the fusemap, so this returns the externally
    visible logical level for o17.
    """
    b = bool
    i1, i2, i3, i4, i5, i6, i7, i8, i9, i12, rf13 = map(
        b, (i1, i2, i3, i4, i5, i6, i7, i8, i9, i12, rf13)
    )

    o14 = (
        (not i1 and not i6 and not i7 and i8)
        or (i1 and i6 and i8 and not i9 and not i12 and not rf13)
        or (i1 and not i6 and i8 and not i9 and not i12 and rf13)
        or (not i6 and not i7 and i8 and i9 and not i12)
        or (i6 and i7 and i9)
        or (i1 and i7 and i12)
        or (not i1 and not i7 and i8 and not i9)
        or (i7 and not i8)
    )
    o15 = (
        (i1 and not i7 and i8 and not i9 and not i12 and not rf13)
        or (i6 and not i7 and i9)
        or (i1 and i7 and i8 and not i9 and not i12 and rf13)
        or (not i1 and i6 and i7 and not i9)
        or (not i1 and not i6 and not i7 and i8)
        or (i1 and i6 and i12)
        or (i1 and i6 and i9)
        or (i6 and not i8)
    )
    o16 = (
        (not i4 and i5 and not i9 and not rf13)
        or (i1 and not i4 and not i5 and i8 and not i12 and rf13)
        or (i1 and i4 and not i5 and i8 and not i9 and not i12)
        or (i1 and not i4 and i8 and i9 and not i12)
        or (not i1 and i4 and i8 and i9)
        or (i1 and i5 and i12)
        or (not i1 and not i4 and i8 and not i9)
        or (i5 and not i8)
    )
    no17 = (
        (i4 and not i5 and i8 and not i9 and not i12 and not rf13)
        or (not i1 and not i5 and i8 and not i9)
        or (i1 and i4 and i5 and i8 and not i12)
        or (not i1 and i4 and not i5 and i8)
        or (not i4 and not i5 and not i9 and rf13)
        or (not i4 and i5 and i9)
        or (i1 and not i4 and i12)
        or (not i4 and not i8)
    )
    o18 = (
        (i1 and i3 and not i9 and not rf13)
        or (i1 and not i3 and i8 and not i9 and not i12 and rf13)
        or (not i2 and i8 and i9 and not i12)
        or (not i1 and i2 and i8 and not i9)
        or (not i1 and not i2 and i8 and i9)
        or (i1 and i3 and i12)
        or (i3 and not i8)
    )
    o19 = (
        (i1 and not i2 and i8 and not i9 and not i12 and not rf13)
        or (i1 and not i3 and i8 and i9 and not i12)
        or (not i1 and i3 and i8 and i9)
        or (i1 and i2 and not i9 and rf13)
        or (not i1 and not i3 and i8 and not i9)
        or (i1 and i2 and i12)
        or (i2 and not i8)
    )

    return tuple(int(x) for x in (o14, o15, o16, not no17, o18, o19))


def peel5b_control_summary() -> list[tuple[tuple[int, int, int, int, int], list[str]]]:
    """Return control modes where 5B reduces to input permutation/inversion."""
    input_names = ["i2", "i3", "i4", "i5", "i6", "i7"]
    summary: list[tuple[tuple[int, int, int, int, int], list[str]]] = []

    for control in product((0, 1), repeat=5):
        i1, i8, i9, i12, rf13 = control
        mapping: list[str] = []
        for out_idx in range(6):
            observed = []
            for values in product((0, 1), repeat=6):
                i2, i3, i4, i5, i6, i7 = values
                observed.append(
                    peel5b_outputs(
                        i1=i1,
                        i2=i2,
                        i3=i3,
                        i4=i4,
                        i5=i5,
                        i6=i6,
                        i7=i7,
                        i8=i8,
                        i9=i9,
                        i12=i12,
                        rf13=rf13,
                    )[out_idx]
                )

            found = "logic"
            for in_idx, name in enumerate(input_names):
                source = [values[in_idx] for values in product((0, 1), repeat=6)]
                if observed == source:
                    found = name
                    break
                if observed == [1 - value for value in source]:
                    found = "/" + name
                    break
            mapping.append(found)

        if "logic" not in mapping:
            summary.append((control, mapping))

    return summary


def peel5b_affine_summary() -> list[tuple[tuple[int, int, int, int, int], list[str]]]:
    """Return control modes where all six 5B outputs are affine functions."""
    input_names = ["i2", "i3", "i4", "i5", "i6", "i7"]
    summary: list[tuple[tuple[int, int, int, int, int], list[str]]] = []

    for control in product((0, 1), repeat=5):
        i1, i8, i9, i12, rf13 = control
        forms: list[str] = []
        all_affine = True

        for out_idx in range(6):
            constant = peel5b_outputs(
                i1=i1, i2=0, i3=0, i4=0, i5=0, i6=0, i7=0, i8=i8, i9=i9, i12=i12, rf13=rf13
            )[out_idx]
            coeffs = []
            for in_idx in range(6):
                values = [0] * 6
                values[in_idx] = 1
                coeffs.append(
                    constant
                    ^ peel5b_outputs(
                        i1=i1,
                        i2=values[0],
                        i3=values[1],
                        i4=values[2],
                        i5=values[3],
                        i6=values[4],
                        i7=values[5],
                        i8=i8,
                        i9=i9,
                        i12=i12,
                        rf13=rf13,
                    )[out_idx]
                )

            for values in product((0, 1), repeat=6):
                predicted = constant
                for coeff, value in zip(coeffs, values):
                    predicted ^= coeff & value
                actual = peel5b_outputs(
                    i1=i1,
                    i2=values[0],
                    i3=values[1],
                    i4=values[2],
                    i5=values[3],
                    i6=values[4],
                    i7=values[5],
                    i8=i8,
                    i9=i9,
                    i12=i12,
                    rf13=rf13,
                )[out_idx]
                if predicted != actual:
                    all_affine = False
                    break
            if not all_affine:
                break

            terms = [input_names[idx] for idx, coeff in enumerate(coeffs) if coeff]
            form = "^".join(terms) if terms else "0"
            if constant:
                form = "/" + form
            forms.append(form)

        if all_affine:
            summary.append((control, forms))

    return summary


def peel5b_affine_controls() -> list[tuple[int, int, int, int, int]]:
    global PEEL5B_AFFINE_CONTROLS
    if PEEL5B_AFFINE_CONTROLS is None:
        PEEL5B_AFFINE_CONTROLS = [control for control, _ in peel5b_affine_summary()]
    return PEEL5B_AFFINE_CONTROLS


def peel4b_next_state(
    *, i1: int, i2: int, i3: int, i4: int, i5: int, i6: int, i7: int, state: tuple[int, int, int, int, int]
) -> tuple[tuple[int, int, int, int, int], tuple[int, int, int]]:
    """Evaluate PEEL18CV8S at 4B for one registered update.

    The returned state is (rf12, rf13, rf14, rf15, rf16).  The outputs are
    (o17, o18, o19).  This mirrors `jedutil -view peel18cv8s.4b.bin 18CV8`.
    """
    rf12, rfo13, rfo14, rfo15, rfo16 = map(bool, state)
    i1, i2, i3, i4, i5, i6, i7 = map(bool, (i1, i2, i3, i4, i5, i6, i7))

    if not i3:
        next_state = (0, 0, 0, 0, 0)
    else:
        n12 = i4
        n13 = (
            (not rfo13 and rfo14 and rfo15 and rfo16)
            or (rfo13 and not rfo14)
            or (rfo13 and not rfo15)
            or (rfo13 and not rfo16)
        )
        n14 = (
            (not rfo14 and rfo15 and rfo16)
            or (rfo14 and not rfo15)
            or (rfo14 and not rfo16)
        )
        n15 = (not rfo15 and rfo16) or (rfo15 and not rfo16)
        n16 = not rfo16
        next_state = tuple(int(x) for x in (n12, n13, n14, n15, n16))

    o17 = (i2 and i6) or (i2 and i5)
    o18 = (
        (not i3)
        or (i4 and not rf12 and not rfo13 and not rfo14 and rfo15)
        or (i4 and rf12 and rfo13 and rfo14 and not rfo15 and not rfo16)
        or (not i4 and not rf12 and rfo13 and not rfo15 and rfo16)
        or (i4 and rf12 and not rfo13 and not rfo14 and not rfo15 and not rfo16)
        or (not i4 and rf12 and not rfo13 and rfo15 and not rfo16)
        or (i4 and not rf12 and not rfo13 and rfo15 and rfo16)
        or (not rf12 and rfo13 and not rfo14 and rfo15 and not rfo16)
    )
    no19 = (
        (i1 and i4 and not i5 and not i6 and not i7 and not rf12 and rfo15 and not rfo16)
        or (i1 and not i4 and not i5 and not i6 and not i7 and rf12 and not rfo15 and rfo16)
        or (not i1 and i4 and not i5 and not i6 and not i7 and rf12 and rfo15 and not rfo16)
        or (not i1 and not i5 and not i6 and not i7 and not rf12 and not rfo15 and rfo16)
        or (not i4 and not i5 and not i6 and not i7 and not rf12 and not rfo15 and not rfo16)
        or (not i1 and not i4 and not i5 and not i6 and not i7 and not rfo15 and not rfo16)
        or (not i1 and not i4 and not i5 and not i6 and not i7 and not rf12)
        or (not i3)
    )

    return next_state, tuple(int(x) for x in (o17, o18, not no19))


def peel4b_counter_sequence(length: int = 32) -> list[tuple[int, int, int, int, int]]:
    state = (0, 0, 0, 0, 0)
    sequence = []
    for _ in range(length):
        sequence.append(state)
        state, _ = peel4b_next_state(i1=0, i2=1, i3=1, i4=0, i5=0, i6=0, i7=0, state=state)
    return sequence


def print_runs(label: str, decoded: bytes, ref_name: str, ref: bytes, limit: int) -> None:
    runs: list[tuple[int, int]] = []
    start = 0
    run = 0
    for i, (a, b) in enumerate(zip(decoded, ref)):
        if a == b:
            if run == 0:
                start = i
            run += 1
        else:
            if run >= 16:
                runs.append((start, run))
            run = 0
    if run >= 16:
        runs.append((start, run))

    runs.sort(key=lambda item: item[1], reverse=True)
    print(f"\n== {label} vs {ref_name}: longest exact byte runs")
    for start, length in runs[:limit]:
        print(f"  {start:06x}+{length:05x}")


def cross_reference_runs(
    decoded: bytes,
    ref: bytes,
    *,
    seed_len: int = 32,
    max_seed_refs: int = 24,
) -> list[tuple[int, int, int]]:
    """Find long exact runs between two ROM images at any offset.

    The arcade program is not a flat byte-for-byte copy of the home ROM, so
    same-offset comparisons miss useful anchors.  This indexes even-aligned
    reference seeds, extends each seed forward from the run start, and returns
    `(decoded_offset, reference_offset, length)` tuples.
    """
    if seed_len <= 0 or len(decoded) < seed_len or len(ref) < seed_len:
        return []

    ref_index: dict[bytes, list[int] | None] = {}
    for ref_off in range(0, len(ref) - seed_len + 1, 2):
        seed = ref[ref_off : ref_off + seed_len]
        offsets = ref_index.get(seed)
        if offsets is None and seed in ref_index:
            continue
        if offsets is None:
            ref_index[seed] = [ref_off]
        elif len(offsets) < max_seed_refs:
            offsets.append(ref_off)
        else:
            ref_index[seed] = None

    seen: set[tuple[int, int, int]] = set()
    runs: list[tuple[int, int, int]] = []
    for dec_off in range(0, len(decoded) - seed_len + 1, 2):
        refs = ref_index.get(decoded[dec_off : dec_off + seed_len])
        if not refs:
            continue
        for ref_off in refs:
            if dec_off >= 2 and ref_off >= 2 and decoded[dec_off - 2 : dec_off] == ref[ref_off - 2 : ref_off]:
                continue
            right = seed_len
            while (
                dec_off + right < len(decoded)
                and ref_off + right < len(ref)
                and decoded[dec_off + right] == ref[ref_off + right]
            ):
                right += 1
            key = (dec_off, ref_off, right)
            if key not in seen:
                seen.add(key)
                runs.append(key)

    runs.sort(key=lambda item: (-item[2], item[0], item[1]))
    return runs


def print_cross_reference_runs(label: str, decoded: bytes, ref_name: str, ref: bytes, limit: int) -> None:
    print(f"\n== {label} vs {ref_name}: cross-offset exact anchors")
    if limit <= 0:
        print("  skipped; pass --cross-runs N to print cross-offset anchors")
        return
    runs = cross_reference_runs(decoded, ref)
    if not runs:
        print("  no exact anchors found")
        return
    for dec_off, ref_off, length in runs[:limit]:
        delta = ref_off - dec_off
        print(
            f"  arcade ${dec_off:06x}-${dec_off + length - 1:06x} "
            f"-> ref ${ref_off:06x}-${ref_off + length - 1:06x} "
            f"len=${length:04x} delta={delta:+#x}"
        )


def print_protection_region_reference_anchors(label: str, decoded: bytes, ref_name: str, ref: bytes) -> None:
    print(f"\n== {label} vs {ref_name}: protection-region anchors")
    regions = [
        ("startup skeleton", 0x0C42, 0x0C9C),
        ("weak $0d34 island", 0x0D34, 0x0DB0),
        ("early token run", 0x0E46, 0x0E82),
        ("main token family", 0x0EA0, 0x1066),
        ("$1082 neighborhood", 0x1082, 0x10A2),
    ]
    runs = cross_reference_runs(decoded, ref, seed_len=16, max_seed_refs=64)
    for name, start, end in regions:
        overlaps = [
            (max(dec_off, start), min(dec_off + length, end), dec_off, ref_off, length)
            for dec_off, ref_off, length in runs
            if dec_off < end and dec_off + length > start
        ]
        overlaps = [item for item in overlaps if item[1] - item[0] >= 16]
        overlaps.sort(key=lambda item: (-(item[1] - item[0]), item[2], item[3]))
        print(f"  {name} ${start:04x}-${end - 1:04x}:")
        if not overlaps:
            print("    no 16-byte exact home-ROM anchor")
            continue
        for overlap_start, overlap_end, dec_off, ref_off, length in overlaps[:6]:
            ref_overlap = ref_off + (overlap_start - dec_off)
            print(
                f"    overlap ${overlap_start:04x}-${overlap_end - 1:04x} "
                f"-> ref ${ref_overlap:06x} len=${overlap_end - overlap_start:04x} "
                f"run=${length:04x}"
            )


def print_startup_words(label: str, words: list[int]) -> None:
    print(f"\n== {label}: startup words 0x0c42-0x0c9a")
    for addr in range(0x0C42, 0x0C9C, 6):
        chunk = " ".join(f"{words[(addr + off) // 2]:04x}" for off in range(0, 6, 2))
        print(f"  {addr:04x}: {chunk}")


def print_startup_extra_table(base_words: list[int]) -> None:
    print("\n== startup extra transform diagnostics")
    print("  addr  base  extra(no xor)  extra(mame xor)")
    for addr in range(0x0C42, 0x0C9C, 2):
        word = base_words[addr // 2]
        noxor = bitswap(word ^ 0x0107, EXTRA_BITSWAP)
        mame = noxor ^ 0x0001
        print(f"  {addr:04x}  {word:04x}      {noxor:04x}           {mame:04x}")


def transformed_word_variants(word: int) -> list[tuple[str, int]]:
    noxor = bitswap(word ^ 0x0107, EXTRA_BITSWAP)
    return [("raw", word), ("x0", noxor), ("x1", noxor ^ 1)]


def startup_word_variants(word: int, address: int | None = None) -> list[tuple[str, int]]:
    variants = transformed_word_variants(word)

    # Diagnostic hypothesis: the first SR immediate looks Genesis-like but is
    # not reachable through raw/x0/x1. Keep it named separately so it does not
    # get mistaken for an implemented decryption rule.
    if address == 0x0C44 and word == 0x0603:
        variants.append(("sr?", 0x0700))

    deduped = []
    seen = set()
    for name, value in variants:
        if value not in seen:
            deduped.append((name, value))
            seen.add(value)
    return deduped


def peel_pair_possible(source: int, target: int) -> bool:
    key = (source, target)
    if key not in PEEL_PAIR_CACHE:
        changed = source ^ target
        # Fast probe only.  Full one-pair PEEL5B fitting is too
        # under-constrained for sequence scoring and gets expensive quickly.
        # Small deltas are the useful class seen in the startup target
        # adjustment search, so keep this diagnostic intentionally narrow.
        PEEL_PAIR_CACHE[key] = changed != 0 and changed.bit_count() <= 3
    return PEEL_PAIR_CACHE[key]


def changed_bits_for_pairs(pairs: list[tuple[int, int]]) -> list[int]:
    return sorted({bit for source, target in pairs for bit in range(16) if ((source ^ target) >> bit) & 1})


def peel_search_is_informative(pairs: list[tuple[int, int]]) -> bool:
    """Avoid exhaustive exact searches where too few PEEL bit lines are fixed."""
    return len(changed_bits_for_pairs(pairs)) >= 5


def weak_word_variants(word: int, address: int | None = None, *, include_peel: bool = False) -> list[tuple[str, int]]:
    variants = startup_word_variants(word, address)
    if not include_peel:
        return variants

    seen = {value for _, value in variants}
    peel_hits = []
    for source_name, source in variants:
        for target in sorted(PEEL_TARGET_WORDS):
            if target in seen or target == source:
                continue
            if peel_pair_possible(source, target):
                peel_hits.append((f"p5?{source_name}", target))
                seen.add(target)
                if len(peel_hits) >= 5:
                    return variants + peel_hits
    return variants + peel_hits


def peel5b_mode_word_variants(
    word: int,
    address: int | None,
    *,
    label: str,
    control: tuple[int, int, int, int, int],
    bit_order: tuple[int, ...],
) -> list[tuple[str, int]]:
    variants = startup_word_variants(word, address)
    seen = {value for _, value in variants}
    for source_name, source in list(variants):
        value = apply_peel5b_to_word(source, control, bit_order)
        if value not in seen:
            variants.append((f"{label}:{source_name}", value))
            seen.add(value)
    return variants


def strict_peel5b_word_variants(word: int, address: int | None = None) -> list[tuple[str, int]]:
    return peel5b_mode_word_variants(
        word,
        address,
        label="p5m",
        control=STRICT_PEEL5B_CONTROL,
        bit_order=STRICT_PEEL5B_BIT_ORDER,
    )


def second_peel5b_word_variants(word: int, address: int | None = None) -> list[tuple[str, int]]:
    return peel5b_mode_word_variants(
        word,
        address,
        label="p5h",
        control=SECOND_PEEL5B_CONTROL,
        bit_order=SECOND_PEEL5B_BIT_ORDER,
    )


def combined_peel5b_word_variants(word: int, address: int | None = None) -> list[tuple[str, int]]:
    variants = strict_peel5b_word_variants(word, address)
    seen = {value for _, value in variants}
    for name, value in second_peel5b_word_variants(word, address):
        if value not in seen:
            variants.append((name, value))
            seen.add(value)
    return variants


def apply_peel5b_to_word(word: int, control: tuple[int, int, int, int, int], bit_order: tuple[int, ...]) -> int:
    """Apply one PEEL5B mode to six selected word bits.

    `bit_order` maps o14..o19 and i2..i7 onto physical word bit positions.
    Bits outside the selected six are left untouched.
    """
    i1, i8, i9, i12, rf13 = control
    values = [(word >> bit) & 1 for bit in bit_order]
    outputs = peel5b_outputs(
        i1=i1,
        i2=values[5],
        i3=values[4],
        i4=values[3],
        i5=values[2],
        i6=values[1],
        i7=values[0],
        i8=i8,
        i9=i9,
        i12=i12,
        rf13=rf13,
    )

    out = word
    for bit, value in zip(bit_order, outputs):
        if value:
            out |= 1 << bit
        else:
            out &= ~(1 << bit)
    return out


def search_peel5b_known_pairs(pairs: list[tuple[int, int]], limit: int = 20) -> list[tuple[tuple[int, ...], tuple[int, ...]]]:
    cache_key = tuple(pairs)
    if cache_key in PEEL5B_KNOWN_PAIR_CACHE:
        return PEEL5B_KNOWN_PAIR_CACHE[cache_key][:limit]

    changed = 0
    for source, target in pairs:
        changed |= source ^ target
    required_bits = tuple(bit for bit in range(16) if changed & (1 << bit))
    if len(required_bits) > 6:
        PEEL5B_KNOWN_PAIR_CACHE[cache_key] = []
        return []

    results: list[tuple[tuple[int, ...], tuple[int, ...]]] = []
    affine_controls = peel5b_affine_controls()
    remaining_bits = [bit for bit in range(16) if bit not in required_bits]

    for extras in combinations(remaining_bits, 6 - len(required_bits)):
        for bit_order in permutations(required_bits + extras):
            for control in affine_controls:
                if all(apply_peel5b_to_word(source, control, bit_order) == target for source, target in pairs):
                    results.append((control, bit_order))
                    if len(results) >= limit:
                        PEEL5B_KNOWN_PAIR_CACHE[cache_key] = results
                        return results
    PEEL5B_KNOWN_PAIR_CACHE[cache_key] = results
    return results


def rough_code_start_score(words: list[int], address: int) -> int:
    if address < 0 or address >= len(words) * 2 or address & 1:
        return -100

    first = words[address // 2]
    score = 0
    if address < 0x20000:
        score += 3
    if first in (0x007C, 0x41F9, 0x43F9, 0x303C, 0x4A79, 0x48E7, 0x4EF9, 0x33FC, 0x23FC):
        score += 6
    if (first & 0xF000) in (0x6000, 0x7000):
        score += 3
    if (first & 0xFF00) in (0x4E00, 0x3000, 0x2000, 0x3200, 0x3400, 0x3600, 0x4200, 0x5200, 0x6100):
        score += 2
    if first in (0x4E75, 0x4E73):
        score -= 2
    return score


def print_startup_jsr_candidates(base_words: list[int]) -> None:
    print("\n== startup JSR operand transform candidates")
    print("  x0 means extra bitswap without final xor; x1 means with final xor")
    for addr in range(0x0C42, 0x0C9A, 2):
        if base_words[addr // 2] != 0x2E3F:
            continue

        hi = base_words[(addr // 2) + 1]
        lo = base_words[(addr // 2) + 2]
        candidates = []
        for hi_name, hi_word in transformed_word_variants(hi):
            for lo_name, lo_word in transformed_word_variants(lo):
                target = (hi_word << 16) | lo_word
                score = rough_code_start_score(base_words, target)
                if score > -50:
                    first = base_words[target // 2] if 0 <= target < len(base_words) * 2 and not (target & 1) else None
                    candidates.append((score, hi_name, lo_name, target, first))

        candidates.sort(reverse=True)
        print(f"  {addr:04x}: raw 2e3f {hi:04x} {lo:04x}")
        for score, hi_name, lo_name, target, first in candidates[:6]:
            first_text = "----" if first is None else f"{first:04x}"
            print(f"    score={score:2d} hi={hi_name:<3} lo={lo_name:<3} target={target:06x} first={first_text}")


def score_startup_target(base_words: list[int], target: int) -> int:
    score = rough_code_start_score(base_words, target)
    if target & 1:
        return -80
    if not (0 <= target < 0x20000):
        return -120
    if target >= 0x10000:
        score -= 35

    first_variants = startup_word_variants(base_words[target // 2], target)
    values = {value for _, value in first_variants}
    if values & {0x4E75, 0x007C, 0x41F9, 0x43F9, 0x303C, 0x33FC, 0x23FC, 0x48E7}:
        score += 10
    if any((value & 0xF000) in (0x6000, 0x7000) for value in values):
        score += 4
    return score


def classify_target(base_words: list[int], target: int) -> tuple[int, str]:
    if target & 1:
        return -80, "odd"
    if not (0 <= target < len(base_words) * 2):
        return -120, "outside-rom"

    score = score_startup_target(base_words, target)
    if target >= 0x20000:
        return score, "outside-startup-bank"
    if target >= 0x10000:
        return score, "high-startup-bank"
    if target < 0x200:
        return score - 20, "vector/header-area"
    return score, "low-startup-bank"


def target_preview(base_words: list[int], target: int, words: int = 4) -> str:
    if target & 1 or not (0 <= target <= len(base_words) * 2 - 2):
        return "----"
    values = []
    for idx in range(words):
        addr = target + idx * 2
        if addr >= len(base_words) * 2:
            break
        variants = startup_word_variants(base_words[addr // 2], addr)
        values.append("/".join(f"{name}:{value:04x}" for name, value in variants))
    return " | ".join(values)


def local_entry_score(words: list[int], address: int) -> int:
    if address < 0 or address & 1 or address >= len(words) * 2:
        return -100

    values = []
    for idx in range(4):
        addr = address + idx * 2
        if addr >= len(words) * 2:
            break
        values.append(words[addr // 2])

    if not values:
        return -100

    score = 0
    first = values[0]
    second = values[1] if len(values) > 1 else None
    third = values[2] if len(values) > 2 else None

    if first in (0x48E7, 0x4E75, 0x4E73, 0x4EF9, 0x4EB9, 0x41F9, 0x43F9, 0x33FC, 0x23FC, 0x303C):
        score += 8
    if first in (0x0000, 0xFFFF, 0xFFFE, 0x0107, 0x01A6, 0x01A7, 0x1B3E, 0x4F72):
        score -= 8
    if first in (0x4A79, 0x4279) and second == 0x00FF:
        score += 10
    if first in (0x33FC, 0x23FC) and third == 0x00FF:
        score += 10
    if first in (0x4EB9, 0x4EF9) and second in (0x0000, 0x000D):
        score += 8
    if (first & 0xFF00) in (0x6000, 0x6100, 0x6600, 0x6700):
        score += 5
    if second == 0x00FF or third == 0x00FF:
        score += 2
    return score


def format_variant_word(name: str, value: int) -> str:
    return f"{name}:{value:04x}"


def interesting_word_roles(value: int) -> list[str]:
    roles = []
    exact = {
        0x007C: "ori-to-sr",
        0x4E75: "rts",
        0x4E73: "rte",
        0x4EB8: "jsr-absw",
        0x4EB9: "jsr-absl",
        0x4EF9: "jmp-absl",
        0x41F9: "lea-a0-absl",
        0x43F9: "lea-a1-absl",
        0x48E7: "movem-save",
        0x4CDF: "movem-restore",
        0x33FC: "move-w-imm-abs",
        0x23FC: "move-l-imm-abs",
        0x3039: "move-w-absl-d0",
        0x303C: "move-w-imm-d0",
        0x0240: "andi-w-d0",
        0x0040: "ori-w-d0",
        0x33C0: "move-w-d0-abs",
        0x4279: "clr-w-abs",
        0x4A79: "tst-w-abs",
        0x6100: "bsr-w",
        0x6600: "bne-w",
        0x6700: "beq-w",
        0x00FF: "mmio-hi",
        0x00C0: "vdp-hi",
        0x0000: "zero-hi",
        0x000D: "bank-d-hi",
    }
    if value in exact:
        roles.append(exact[value])
    if (value & 0xF000) in (0x6000, 0x7000):
        roles.append("branch/moveq")
    if (value & 0xF1C0) == 0x51C8:
        roles.append("dbf")
    if (value & 0xFFC0) == 0x4298:
        roles.append("clr-l-postinc")
    if (value & 0xFFC0) == 0x4ED0:
        roles.append("jmp-indirect")
    if (value & 0xFFC0) == 0x4E90:
        roles.append("jsr-indirect")
    if value in KNOWN_STARTUP_WORDS:
        roles.append("startup-target")
    return roles


def print_weak_window_variant_hits(base_words: list[int]) -> None:
    print("\n== weak-window variant hits")
    print("  shows raw/x0/x1 words that independently look like opcodes, address-high words, or branch targets")
    for label, start, end in WEAK_WINDOWS:
        print(f"  {label} ${start:04x}-${end - 2:04x}:")
        hits = []
        for addr in range(start, end, 2):
            variants = []
            for name, value in startup_word_variants(base_words[addr // 2], addr):
                roles = interesting_word_roles(value)
                if roles:
                    variants.append(f"{format_variant_word(name, value)}[{','.join(roles[:2])}]")
            if variants:
                hits.append((addr, variants))

        if not hits:
            print("    no independently interesting variants")
            continue
        for addr, variants in hits[:24]:
            print(f"    ${addr:04x}: {' '.join(variants)}")
        if len(hits) > 24:
            print(f"    ... {len(hits) - 24} more hit lines")


def weak_instruction_candidates(
    base_words: list[int],
    addr: int,
    end: int,
    *,
    include_peel: bool = False,
    variant_provider=None,
) -> list[tuple[int, int, str]]:
    """Return plausible mixed raw/x0/x1 instructions at one address."""
    if addr >= end:
        return []

    def variants(at: int) -> list[tuple[str, int]]:
        if at >= end:
            return []
        if variant_provider is not None:
            return variant_provider(base_words[at // 2], at)
        return weak_word_variants(base_words[at // 2], at, include_peel=include_peel)

    results: list[tuple[int, int, str]] = []
    for op_name, op in variants(addr):
        if op == 0x4E75:
            results.append((14, 2, f"{format_variant_word(op_name, op)} rts"))
        elif op in (0x41F9, 0x43F9, 0x4EB9, 0x4EF9) and addr + 6 <= end:
            mnemonic = {0x41F9: "lea(a0)", 0x43F9: "lea(a1)", 0x4EB9: "jsr", 0x4EF9: "jmp"}[op]
            for hi_name, hi in variants(addr + 2):
                for lo_name, lo in variants(addr + 4):
                    target = (hi << 16) | lo
                    if op in (0x4EB9, 0x4EF9) and target & 1:
                        continue
                    score = 10 if hi in (0x0000, 0x000D, 0x00FF, 0x00C0) else 3
                    if op in (0x4EB9, 0x4EF9) and hi not in (0x0000, 0x000D):
                        score -= 7
                    results.append(
                        (
                            score - (2 if "p5?" in op_name + hi_name + lo_name else 0),
                            6,
                            (
                                f"{format_variant_word(op_name, op)} {format_variant_word(hi_name, hi)} "
                                f"{format_variant_word(lo_name, lo)} {mnemonic} ${target:08x}"
                            ),
                        )
                    )
        elif op in (0x33FC, 0x23FC) and addr + 8 <= end:
            size = "w" if op == 0x33FC else "l"
            for imm_name, imm in variants(addr + 2):
                for hi_name, hi in variants(addr + 4):
                    for lo_name, lo in variants(addr + 6):
                        if hi not in (0x00FF, 0x00C0):
                            continue
                        score = 13 if hi == 0x00FF else 10
                        results.append(
                            (
                                score - (2 if "p5?" in op_name + imm_name + hi_name + lo_name else 0),
                                8,
                                (
                                    f"{format_variant_word(op_name, op)} {format_variant_word(imm_name, imm)} "
                                    f"{format_variant_word(hi_name, hi)} {format_variant_word(lo_name, lo)} "
                                    f"move.{size} #${imm:04x},${(hi << 16) | lo:08x}"
                                ),
                            )
                        )
        elif op in (0x4A79, 0x4279, 0x3039, 0x33C0) and addr + 6 <= end:
            mnemonic = {0x4A79: "tst.w", 0x4279: "clr.w", 0x3039: "move.w abs,d0", 0x33C0: "move.w d0,abs"}[op]
            for hi_name, hi in variants(addr + 2):
                for lo_name, lo in variants(addr + 4):
                    if hi not in (0x00FF, 0x00C0):
                        continue
                    results.append(
                        (
                            11 - (2 if "p5?" in op_name + hi_name + lo_name else 0),
                            6,
                            (
                                f"{format_variant_word(op_name, op)} {format_variant_word(hi_name, hi)} "
                                f"{format_variant_word(lo_name, lo)} {mnemonic} ${(hi << 16) | lo:08x}"
                            ),
                        )
                    )
        elif op in (0x303C, 0x0240, 0x0040) and addr + 4 <= end:
            mnemonic = {0x303C: "move.w #,d0", 0x0240: "andi.w #,d0", 0x0040: "ori.w #,d0"}[op]
            for imm_name, imm in variants(addr + 2):
                penalty = 2 if "p5?" in op_name + imm_name else 0
                results.append((7 - penalty, 4, f"{format_variant_word(op_name, op)} {format_variant_word(imm_name, imm)} {mnemonic} ${imm:04x}"))
        elif op in (0x48E7, 0x4CDF) and addr + 4 <= end:
            mnemonic = "movem-save" if op == 0x48E7 else "movem-restore"
            for mask_name, mask in variants(addr + 2):
                score = 11 if mask in (0xFFFE, 0x7FFF, 0xFFFF) else 6
                penalty = 2 if "p5?" in op_name + mask_name else 0
                results.append((score - penalty, 4, f"{format_variant_word(op_name, op)} {format_variant_word(mask_name, mask)} {mnemonic} ${mask:04x}"))
        elif (op & 0xF000) == 0x7000:
            results.append((5 - (2 if "p5?" in op_name else 0), 2, f"{format_variant_word(op_name, op)} moveq"))
        elif (op & 0xFF00) in (0x6000, 0x6100, 0x6600, 0x6700):
            results.append((5 - (2 if "p5?" in op_name else 0), 2, f"{format_variant_word(op_name, op)} branch"))

    return results


def best_weak_sequence(
    base_words: list[int],
    start: int,
    end: int,
    *,
    include_peel: bool = False,
    variant_provider=None,
) -> tuple[int, list[str]]:
    memo: dict[int, tuple[int, list[str]]] = {}

    def best_from(addr: int) -> tuple[int, list[str]]:
        if addr >= end:
            return 0, []
        if addr in memo:
            return memo[addr]

        best_score = -4
        best_lines = [f"${addr:04x}: data/unknown"]
        for score, size, text in weak_instruction_candidates(
            base_words, addr, end, include_peel=include_peel, variant_provider=variant_provider
        ):
            tail_score, tail = best_from(addr + size)
            combined = score + tail_score
            if combined > best_score:
                best_score = combined
                best_lines = [f"${addr:04x}: {text}"] + tail

        memo[addr] = best_score, best_lines
        return memo[addr]

    return best_from(start)


def print_weak_window_sequence_scores(base_words: list[int]) -> None:
    print("\n== weak-window mixed raw/x0/x1 sequence scores")
    print("  diagnostic only: scores short valid-looking instruction streams inside weak windows")
    modes = [(False, "raw/x0/x1 only"), (True, "raw/x0/x1 plus one-word PEEL probes")]
    for label, start, end in WEAK_WINDOWS:
        limit = min(end, start + 0x30)
        starts = range(start, min(start + 0x10, limit), 2)
        print(f"  {label}:")
        for include_peel, mode_label in modes:
            scored = []
            for candidate_start in starts:
                score, lines = best_weak_sequence(base_words, candidate_start, limit, include_peel=include_peel)
                scored.append((score, candidate_start, lines))
            scored.sort(reverse=True, key=lambda item: item[0])

            print(f"    {mode_label}:")
            for score, candidate_start, lines in scored[:2]:
                print(f"      start ${candidate_start:04x}: score={score}")
                for line in lines[:5]:
                    print(f"        {line}")
                if len(lines) > 5:
                    print("        ...")


def search_startup_instruction_paths(base_words: list[int]) -> list[tuple[int, list[str]]]:
    """Find plausible linear decodes for the protected startup block."""
    end = 0x0C9C
    memo: dict[int, list[tuple[int, list[str]]]] = {}

    def best_from(addr: int) -> list[tuple[int, list[str]]]:
        if addr >= end:
            return [(0, [])]
        if addr in memo:
            return memo[addr]

        word = base_words[addr // 2]
        results: list[tuple[int, list[str]]] = []

        for op_name, op in startup_word_variants(word, addr):
            if op == 0x007C and addr + 4 <= end:
                for imm_name, imm in startup_word_variants(base_words[(addr // 2) + 1], addr + 2):
                    score = 18 if imm == 0x0700 else 8
                    line = f"{addr:04x}: {format_variant_word(op_name, op)} {format_variant_word(imm_name, imm)}  oriw #{imm:04x},SR"
                    for tail_score, tail in best_from(addr + 4):
                        results.append((score + tail_score, [line] + tail))
            elif op == 0x4EB9 and addr + 6 <= end:
                hi_addr = addr + 2
                lo_addr = addr + 4
                for hi_name, hi in startup_word_variants(base_words[hi_addr // 2], hi_addr):
                    for lo_name, lo in startup_word_variants(base_words[lo_addr // 2], lo_addr):
                        target = (hi << 16) | lo
                        target_score, _ = classify_target(base_words, target)
                        if target_score < -20:
                            continue
                        score = 14 + target_score
                        line = (
                            f"{addr:04x}: {format_variant_word(op_name, op)} "
                            f"{format_variant_word(hi_name, hi)} {format_variant_word(lo_name, lo)}  jsr ${target:06x}"
                        )
                        for tail_score, tail in best_from(addr + 6):
                            results.append((score + tail_score, [line] + tail))
            elif op == 0x4EB8 and addr + 4 <= end:
                target_addr = addr + 2
                for target_name, target_word in startup_word_variants(base_words[target_addr // 2], target_addr):
                    target = target_word if target_word < 0x8000 else target_word - 0x10000
                    target_score, _ = classify_target(base_words, target_word & 0xFFFF)
                    score = 8 + target_score
                    line = f"{addr:04x}: {format_variant_word(op_name, op)} {format_variant_word(target_name, target_word)}  jsr ${target & 0xffff:04x}.w"
                    for tail_score, tail in best_from(addr + 4):
                        results.append((score + tail_score, [line] + tail))
            elif op == 0x2F3C and addr + 6 <= end:
                hi_addr = addr + 2
                lo_addr = addr + 4
                for hi_name, hi in startup_word_variants(base_words[hi_addr // 2], hi_addr):
                    for lo_name, lo in startup_word_variants(base_words[lo_addr // 2], lo_addr):
                        imm = (hi << 16) | lo
                        score = 10
                        if imm < 0x20000:
                            score += 5
                        if imm & 1:
                            score -= 4
                        line = (
                            f"{addr:04x}: {format_variant_word(op_name, op)} "
                            f"{format_variant_word(hi_name, hi)} {format_variant_word(lo_name, lo)}  move.l #${imm:06x},-(sp)"
                        )
                        for tail_score, tail in best_from(addr + 6):
                            results.append((score + tail_score, [line] + tail))
            elif op == 0x4E75:
                line = f"{addr:04x}: {format_variant_word(op_name, op)}  rts"
                results.append((20, [line]))
            elif op in (0x01A6, 0x01A7):
                reg = "a6" if op == 0x01A6 else "a7"
                line = f"{addr:04x}: {format_variant_word(op_name, op)}  bclr d0,-({reg})"
                for tail_score, tail in best_from(addr + 2):
                    results.append((3 + tail_score, [line] + tail))
            elif (op & 0xFF00) in (0x6000, 0x6100):
                disp = op & 0xFF
                if disp >= 0x80:
                    disp -= 0x100
                target = addr + 2 + disp
                target_score, _ = classify_target(base_words, target)
                score = 7 + target_score
                line = f"{addr:04x}: {format_variant_word(op_name, op)}  branch ${target:04x}"
                for tail_score, tail in best_from(addr + 2):
                    results.append((score + tail_score, [line] + tail))

        if not results:
            rendered = " ".join(format_variant_word(name, value) for name, value in startup_word_variants(word, addr))
            results.append((-25, [f"{addr:04x}: stop, no modeled instruction from {rendered}"]))

        results.sort(key=lambda item: item[0], reverse=True)
        memo[addr] = results[:8]
        return memo[addr]

    return best_from(0x0C42)[:5]


def print_startup_instruction_paths(base_words: list[int]) -> None:
    print("\n== startup instruction-path search")
    print("  variants are raw/x0/x1 per word; sr? is a named hypothesis for 0603 -> 0700 only")
    paths = search_startup_instruction_paths(base_words)
    if not paths:
        print("  no plausible linear paths found")
        return

    for idx, (score, lines) in enumerate(paths, 1):
        print(f"  path {idx}: score={score}")
        for line in lines[:18]:
            print(f"    {line}")
        if len(lines) > 18:
            print("    ...")


def print_startup_target_verification(base_words: list[int]) -> None:
    targets = [
        0x00F8,
        0x10A2,
        0x1082,
        0x1084,
        0x107A,
        0x101C,
        0x1101C,
        0x10F8,
        0x10A8,
        0x10E2E,
        0x0ADC,
        0x0ABA,
        0x0AF4,
        0x0D34,
        0x0A1C,
    ]
    print("\n== startup target verification")
    for target in targets:
        score, kind = classify_target(base_words, target)
        print(f"  ${target:06x}: score={score:4d} {kind:<20} {target_preview(base_words, target)}")


def print_nearby_entry_candidates(words: list[int]) -> None:
    targets = [0x1082, 0x101C, 0x0E2E, 0x0D34, 0x0ABA, 0x0AF4]
    print("\n== nearby entry candidates for weak targets")
    for target in targets:
        candidates = []
        for addr in range(target - 8, target + 10, 2):
            score = local_entry_score(words, addr)
            if score > 0:
                candidates.append((score, addr, target_preview(words, addr, 3)))
        candidates.sort(reverse=True)
        print(f"  target ${target:04x}:")
        if not candidates:
            print("    no nearby positive-scoring entries")
            continue
        for score, addr, preview in candidates[:5]:
            marker = "*" if addr == target else " "
            print(f"   {marker} ${addr:04x}: score={score:3d} {preview}")


def print_startup_target_adjustment_search(base_words: list[int]) -> None:
    print("\n== startup target adjustment search")
    print("  current target is the best-startup operand; candidates are nearby entries from local scoring")
    for call_addr, current, candidates in STARTUP_TARGET_ADJUSTMENTS:
        lo_addr = call_addr + 4
        encoded = base_words[lo_addr // 2]
        variants = startup_word_variants(encoded, lo_addr)
        print(f"  call ${call_addr:04x}, operand word @${lo_addr:04x}, encoded={encoded:04x}, current=${current:04x}")
        print("    variants:", " ".join(format_variant_word(name, value) for name, value in variants))
        for candidate in candidates:
            delta = current ^ candidate
            changed = [bit for bit in range(16) if (delta >> bit) & 1]
            direct = [name for name, value in variants if value == candidate]
            direct_text = ",".join(direct) if direct else "-"
            print(
                f"    -> ${candidate:04x}: xor_delta=${delta:04x} bits={changed} "
                f"direct={direct_text} one-word-peel=deferred"
            )


def print_common_peel_target_adjustment_search(base_words: list[int], *, strict: bool = False) -> None:
    print("\n== common small-delta probe for startup target adjustments")
    print("  tests whether adjusted operand words fit within one six-bit changed set before doing expensive PEEL fitting")
    chosen = [
        (0x0C7A, 0x0E32),
        (0x0C86, 0x0AB8),
        (0x0C8C, 0x0AF8),
        (0x0C92, 0x0D32),
    ]
    variant_sets = []
    for addr, target in chosen:
        encoded = base_words[addr // 2]
        variants = startup_word_variants(encoded, addr)
        variant_sets.append([(addr, name, value, target) for name, value in variants])

    for count in range(2, 3):
        print(f"  {count}-word combinations:")
        hits = 0
        for selected_indices in combinations(range(len(chosen)), count):
            for selected in product(*(variant_sets[idx] for idx in selected_indices)):
                pairs = [(source, target) for _, _, source, target in selected]
                changed = changed_bits_for_pairs(pairs)
                if len(changed) > 6:
                    continue
                labels = " ".join(f"${addr:04x}:{name}:{source:04x}->{target:04x}" for addr, name, source, target in selected)
                print(f"    small-delta candidate bits={changed} {labels}")
                hits += 1
                if hits >= 8:
                    break
            if hits >= 8:
                break
        if hits == 0:
            print("    no common-mode hits")

    print("  strict PEEL5B common-mode hits:")
    print("    requires one PEEL5B control tuple and one six-bit word mapping to explain all selected words")
    if not strict:
        print("    skipped; pass --strict-peel-search to run bounded exact two-word fitting")
        return

    strict_hits = 0
    for count in range(2, 3):
        best_for_count = []
        for selected_indices in combinations(range(len(chosen)), count):
            for selected in product(*(variant_sets[idx] for idx in selected_indices)):
                pairs = [(source, target) for _, _, source, target in selected if source != target]
                if len(pairs) != len(selected):
                    continue
                changed = changed_bits_for_pairs(pairs)
                if len(changed) > 6 or not peel_search_is_informative(pairs):
                    continue
                results = search_peel5b_known_pairs(pairs, limit=1)
                if not results:
                    continue
                control, bit_order = results[0]
                labels = " ".join(f"${addr:04x}:{name}:{source:04x}->{target:04x}" for addr, name, source, target in selected)
                best_for_count.append((changed, control, bit_order, labels))
                if len(best_for_count) >= 1:
                    break
            if len(best_for_count) >= 1:
                break

        print(f"    {count}-word exact modes:")
        if not best_for_count:
            print("      no exact common PEEL5B mode")
            continue
        for changed, control, bit_order, labels in best_for_count:
            strict_hits += 1
            print(f"      bits={changed} control={control} order={bit_order} {labels}")
    if strict_hits == 0:
        print("    no adjusted target set currently proves a shared exact PEEL5B mode")
    else:
        print("    larger exact common-mode groups are intentionally deferred; three or four words need deep search limits")


def print_strict_peel5b_replay(base_words: list[int]) -> None:
    print("\n== strict PEEL5B candidate replay")
    print(
        "  replays the first exact two-word PEEL5B mode over startup and weak windows; "
        "p5m means fixed-mode output"
    )
    print(f"  control={STRICT_PEEL5B_CONTROL} bit_order={STRICT_PEEL5B_BIT_ORDER}")

    print("  adjusted startup operand coverage:")
    for addr, target in [(0x0C7A, 0x0E32), (0x0C86, 0x0AB8), (0x0C8C, 0x0AF8), (0x0C92, 0x0D32)]:
        variants = strict_peel5b_word_variants(base_words[addr // 2], addr)
        matches = [name for name, value in variants if value == target]
        rendered = " ".join(format_variant_word(name, value) for name, value in variants)
        print(f"    ${addr:04x} target={target:04x} match={','.join(matches) if matches else '-'} {rendered}")

    print("  new independently interesting p5m words:")
    for label, start, end in WEAK_WINDOWS:
        hits = []
        for addr in range(start, end, 2):
            base_values = {value for _, value in startup_word_variants(base_words[addr // 2], addr)}
            for name, value in strict_peel5b_word_variants(base_words[addr // 2], addr):
                if not name.startswith("p5m:") or value in base_values:
                    continue
                roles = interesting_word_roles(value)
                if roles:
                    hits.append(f"${addr:04x}:{name}:{value:04x}[{','.join(roles[:2])}]")
        print(f"    {label}:")
        if not hits:
            print("      no new p5m role hits")
            continue
        for hit in hits[:12]:
            print(f"      {hit}")
        if len(hits) > 12:
            print(f"      ... {len(hits) - 12} more")

    print("  sequence-score replay with fixed p5m mode:")
    for label, start, end in WEAK_WINDOWS:
        limit = min(end, start + 0x30)
        starts = range(start, min(start + 0x10, limit), 2)
        raw_scores = []
        strict_scores = []
        for candidate_start in starts:
            raw_score, raw_lines = best_weak_sequence(base_words, candidate_start, limit)
            strict_score, strict_lines = best_weak_sequence(
                base_words, candidate_start, limit, variant_provider=strict_peel5b_word_variants
            )
            raw_scores.append((raw_score, candidate_start, raw_lines))
            strict_scores.append((strict_score, candidate_start, strict_lines))

        raw_best = max(raw_scores, key=lambda item: item[0])
        strict_best = max(strict_scores, key=lambda item: item[0])
        delta = strict_best[0] - raw_best[0]
        print(f"    {label}: raw_best={raw_best[0]} strict_best={strict_best[0]} delta={delta}")
        for line in strict_best[2][:5]:
            print(f"      {line}")
        if len(strict_best[2]) > 5:
            print("      ...")


def print_second_peel5b_hypothesis_replay(base_words: list[int]) -> None:
    print("\n== second PEEL5B hypothesis replay")
    print(
        "  focused hypothesis for the remaining startup operand; "
        "p5h means fixed second-mode output"
    )
    print(f"  control={SECOND_PEEL5B_CONTROL} bit_order={SECOND_PEEL5B_BIT_ORDER}")

    print("  focused coverage:")
    focused = [
        (0x0C8C, 0x0AF8, "startup operand $0af8"),
        (0x0D48, 0x000D, "possible $0d34 continuation bank high-word"),
        (0x0C7A, 0x0E32, "first-mode startup operand check"),
        (0x0C86, 0x0AB8, "first-mode startup operand check"),
        (0x0C92, 0x0D32, "direct x0 startup operand check"),
    ]
    for addr, target, note in focused:
        variants = second_peel5b_word_variants(base_words[addr // 2], addr)
        matches = [name for name, value in variants if value == target]
        rendered = " ".join(format_variant_word(name, value) for name, value in variants)
        print(f"    ${addr:04x} target={target:04x} match={','.join(matches) if matches else '-'} {note}: {rendered}")

    print("  sequence-score replay with fixed p5h mode:")
    for label, start, end in WEAK_WINDOWS:
        limit = min(end, start + 0x30)
        starts = range(start, min(start + 0x10, limit), 2)
        raw_scores = []
        second_scores = []
        for candidate_start in starts:
            raw_score, raw_lines = best_weak_sequence(base_words, candidate_start, limit)
            second_score, second_lines = best_weak_sequence(
                base_words, candidate_start, limit, variant_provider=second_peel5b_word_variants
            )
            raw_scores.append((raw_score, candidate_start, raw_lines))
            second_scores.append((second_score, candidate_start, second_lines))

        raw_best = max(raw_scores, key=lambda item: item[0])
        second_best = max(second_scores, key=lambda item: item[0])
        delta = second_best[0] - raw_best[0]
        print(f"    {label}: raw_best={raw_best[0]} second_best={second_best[0]} delta={delta}")
        for line in second_best[2][:5]:
            print(f"      {line}")
        if len(second_best[2]) > 5:
            print("      ...")


def print_code_island_model_profiles(base_words: list[int]) -> None:
    print("\n== per-code-island decode model profiles")
    print("  scores each weak island independently against raw/x0/x1, p5m, p5h, and combined p5m+p5h models")
    providers = [
        ("raw/x0/x1", None),
        ("p5m", strict_peel5b_word_variants),
        ("p5h", second_peel5b_word_variants),
        ("p5m+p5h", combined_peel5b_word_variants),
    ]

    for label, start, end in WEAK_WINDOWS:
        limit = min(end, start + 0x40)
        starts = range(start, min(start + 0x14, limit), 2)
        print(f"  {label} ${start:04x}-${end - 2:04x}:")

        model_scores = []
        for model_name, provider in providers:
            scored = []
            for candidate_start in starts:
                score, lines = best_weak_sequence(base_words, candidate_start, limit, variant_provider=provider)
                scored.append((score, candidate_start, lines))
            best = max(scored, key=lambda item: item[0])
            model_scores.append((best[0], model_name, best[1], best[2]))

        model_scores.sort(reverse=True, key=lambda item: item[0])
        raw_score = next(score for score, model_name, _, _ in model_scores if model_name == "raw/x0/x1")
        for score, model_name, candidate_start, lines in model_scores:
            delta = score - raw_score
            print(f"    model={model_name:<8} start=${candidate_start:04x} score={score:3d} delta={delta:3d}")
            for line in lines[:3]:
                print(f"      {line}")
            if len(lines) > 3:
                print("      ...")

        print("    role counts by model:")
        for model_name, provider in providers:
            counts: dict[str, int] = {}
            for addr in range(start, min(end, start + 0x40), 2):
                variants = provider(base_words[addr // 2], addr) if provider is not None else startup_word_variants(base_words[addr // 2], addr)
                for name, value in variants:
                    key = name.split(":", 1)[0]
                    if key not in ("raw", "x0", "x1", "p5m", "p5h"):
                        continue
                    if interesting_word_roles(value):
                        counts[key] = counts.get(key, 0) + 1
            rendered = " ".join(f"{key}={counts.get(key, 0)}" for key in ("raw", "x0", "x1", "p5m", "p5h"))
            print(f"      {model_name:<8} {rendered}")


def fixed_peel5b_variant_provider(label: str, control: tuple[int, ...], bit_order: tuple[int, ...]):
    def provider(word: int, address: int | None = None) -> list[tuple[str, int]]:
        return peel5b_mode_word_variants(word, address, label=label, control=control, bit_order=bit_order)

    return provider


def first_unknown_address(lines: list[str]) -> int | None:
    for line in lines:
        if "data/unknown" not in line:
            continue
        try:
            return int(line.split(":", 1)[0].strip("$"), 16)
        except ValueError:
            return None
    return None


def jsr_jmp_targets_from_lines(lines: list[str]) -> list[int]:
    targets = []
    for line in lines:
        for mnemonic in (" jsr $", " jmp $"):
            marker = line.find(mnemonic)
            if marker < 0:
                continue
            token = line[marker + len(mnemonic) :].split()[0]
            try:
                targets.append(int(token, 16))
            except ValueError:
                pass
    return targets


def target_sequence_preview(base_words: list[int], target: int) -> str:
    if target & 1:
        return "odd-target"
    if not (0 <= target < len(base_words) * 2):
        return "outside-rom"
    limit = min(len(base_words) * 2, target + 0x30)
    score, lines = best_weak_sequence(base_words, target, limit)
    preview = " | ".join(lines[:3])
    return f"score={score} {preview}"


def target_sequence_score(base_words: list[int], target: int) -> int:
    if target & 1 or not (0 <= target < len(base_words) * 2):
        return -100
    limit = min(len(base_words) * 2, target + 0x30)
    score, _ = best_weak_sequence(base_words, target, limit)
    return score


def local_exact_seed_candidates(base_words: list[int], addr: int, end: int) -> list[tuple[str, int, list[tuple[int, int]]]]:
    """Build short instruction-shaped exact PEEL5B seed constraints.

    This intentionally avoids one-word seeds.  A seed must constrain enough
    changed lines across an opcode and at least one operand word to make the
    PEEL5B search meaningfully less underconstrained than the old p5? probe.
    """

    def variants(at: int) -> list[tuple[str, int]]:
        if at >= end:
            return []
        return startup_word_variants(base_words[at // 2], at)

    seeds: list[tuple[str, int, list[tuple[int, int]]]] = []

    def add_seed(description: str, size: int, chosen: list[tuple[str, int, int]]) -> None:
        pairs = [(source, target) for _, source, target in chosen if source != target]
        if not pairs:
            return
        changed = changed_bits_for_pairs(pairs)
        if not (5 <= len(changed) <= 6):
            return
        source_text = " ".join(f"{name}:{source:04x}->{target:04x}" for name, source, target in chosen)
        seeds.append((f"{description} {source_text}", size, pairs))

    if addr + 6 <= end:
        absolute_ops = [(0x41F9, "lea-a0"), (0x43F9, "lea-a1"), (0x4EB9, "jsr"), (0x4EF9, "jmp")]
        for op_target, mnemonic in absolute_ops:
            for op_name, op_source in variants(addr):
                for hi_name, hi_source in variants(addr + 2):
                    for lo_name, lo_target in variants(addr + 4):
                        if op_target in (0x4EB9, 0x4EF9) and lo_target & 1:
                            continue
                        for hi_target in (0x0000, 0x000D, 0x00FF, 0x00C0):
                            add_seed(
                                mnemonic,
                                6,
                                [
                                    (op_name, op_source, op_target),
                                    (hi_name, hi_source, hi_target),
                                    (lo_name, lo_target, lo_target),
                                ],
                            )

        absolute_word_ops = [(0x4A79, "tst.w"), (0x4279, "clr.w"), (0x3039, "move.w-abs-d0"), (0x33C0, "move.w-d0-abs")]
        for op_target, mnemonic in absolute_word_ops:
            for op_name, op_source in variants(addr):
                for hi_name, hi_source in variants(addr + 2):
                    for lo_name, lo_target in variants(addr + 4):
                        for hi_target in (0x00FF, 0x00C0):
                            add_seed(
                                mnemonic,
                                6,
                                [
                                    (op_name, op_source, op_target),
                                    (hi_name, hi_source, hi_target),
                                    (lo_name, lo_target, lo_target),
                                ],
                            )

    if addr + 8 <= end:
        for op_target, mnemonic in ((0x33FC, "move.w-imm-abs"), (0x23FC, "move.l-imm-abs")):
            for op_name, op_source in variants(addr):
                for imm_name, imm_target in variants(addr + 2):
                    for hi_name, hi_source in variants(addr + 4):
                        for lo_name, lo_target in variants(addr + 6):
                            for hi_target in (0x00FF, 0x00C0):
                                add_seed(
                                    mnemonic,
                                    8,
                                    [
                                        (op_name, op_source, op_target),
                                        (imm_name, imm_target, imm_target),
                                        (hi_name, hi_source, hi_target),
                                        (lo_name, lo_target, lo_target),
                                    ],
                                )

    if addr + 4 <= end:
        for op_target, mnemonic in ((0x48E7, "movem-save"), (0x4CDF, "movem-restore")):
            for op_name, op_source in variants(addr):
                for mask_name, mask_source in variants(addr + 2):
                    for mask_target in (0xFFFE, 0x7FFF, 0xFFFF):
                        add_seed(
                            mnemonic,
                            4,
                            [(op_name, op_source, op_target), (mask_name, mask_source, mask_target)],
                        )

        for op_target, mnemonic in ((0x303C, "move.w-imm-d0"), (0x0240, "andi.w-d0"), (0x0040, "ori.w-d0")):
            for op_name, op_source in variants(addr):
                for imm_name, imm_target in variants(addr + 2):
                    add_seed(
                        mnemonic,
                        4,
                        [(op_name, op_source, op_target), (imm_name, imm_target, imm_target)],
                    )

    deduped: list[tuple[str, int, list[tuple[int, int]]]] = []
    seen: set[tuple[tuple[int, int], ...]] = set()
    for description, size, pairs in seeds:
        key = tuple(pairs)
        if key in seen:
            continue
        seen.add(key)
        deduped.append((description, size, pairs))
    return deduped


def print_code_island_exact_mode_search(base_words: list[int]) -> None:
    print("\n== per-code-island exact-mode search at hard stops")
    print("  seeds a local PEEL5B mode from the first stopped word and keeps it only if sequence score improves")

    for label, start, end in WEAK_WINDOWS:
        limit = min(end, start + 0x50)
        starts = range(start, min(start + 0x14, limit), 2)
        raw_scores = [(score, candidate_start, lines) for candidate_start in starts for score, lines in [best_weak_sequence(base_words, candidate_start, limit)]]
        raw_best = max(raw_scores, key=lambda item: item[0])
        hard_stop = first_unknown_address(raw_best[2])
        print(f"  {label}: raw_best start=${raw_best[1]:04x} score={raw_best[0]} hard_stop={f'${hard_stop:04x}' if hard_stop is not None else '-'}")
        if hard_stop is None or hard_stop >= limit:
            print("    no stopped word to anchor")
            continue

        hits = []
        for seed_description, seed_size, pairs in local_exact_seed_candidates(base_words, hard_stop, limit)[:96]:
            modes = search_peel5b_known_pairs(pairs, limit=3)
            for control, bit_order in modes:
                provider = fixed_peel5b_variant_provider("p5x", control, bit_order)
                score, lines = best_weak_sequence(base_words, raw_best[1], limit, variant_provider=provider)
                delta = score - raw_best[0]
                if delta <= 0:
                    continue
                targets = jsr_jmp_targets_from_lines(lines[:5])
                if targets and any(target_sequence_score(base_words, target) < 0 for target in targets):
                    continue
                hits.append((delta, score, seed_size, control, bit_order, seed_description, lines))

        hits.sort(reverse=True, key=lambda item: (item[0], item[1], item[2]))
        if not hits:
            print("    no exact local PEEL5B seed improved the island sequence")
            continue

        for delta, score, seed_size, control, bit_order, seed_description, lines in hits[:4]:
            print(f"    hit delta={delta:3d} score={score:3d} seed_size={seed_size} control={control} bits={bit_order}")
            print(f"      seed {seed_description}")
            for line in lines[:5]:
                print(f"      {line}")
            if len(lines) > 5:
                print("      ...")
            for target in jsr_jmp_targets_from_lines(lines[:5]):
                print(f"      target ${target:08x}: {target_sequence_preview(base_words, target)}")


def shannon_entropy(values: list[int]) -> float:
    if not values:
        return 0.0
    counts: dict[int, int] = {}
    for value in values:
        counts[value] = counts.get(value, 0) + 1
    total = len(values)
    return -sum((count / total) * math.log2(count / total) for count in counts.values())


def table_variant_sets(word: int, address: int | None = None) -> list[tuple[str, int]]:
    variants = combined_peel5b_word_variants(word, address)
    deduped = []
    seen = set()
    for name, value in variants:
        key = name.split(":", 1)[0]
        if key not in ("raw", "x0", "x1", "p5m", "p5h"):
            continue
        if (key, value) in seen:
            continue
        seen.add((key, value))
        deduped.append((key, value))
    return deduped


def table_variant_value(base_words: list[int], addr: int, key: str) -> int | None:
    for name, value in table_variant_sets(base_words[addr // 2], addr):
        if name == key:
            return value
    return None


def table_pointer_kind(target: int) -> str | None:
    if target & 1:
        return None
    if 0 <= target < 0x20000:
        return "rom-low"
    if 0x00FF0000 <= target <= 0x00FFFFFF:
        return "mmio"
    if 0x00C00000 <= target <= 0x00C0001F:
        return "vdp"
    if 0x000D0000 <= target <= 0x000DFFFF:
        return "bank-d"
    return None


def print_code_island_table_math_profiles(base_words: list[int]) -> None:
    print("\n== per-code-island table/math profiles")
    print("  treats weak islands as possible tables: entropy, repeated words, longword pointers, and small deltas")

    for label, start, end in WEAK_WINDOWS:
        scan_end = min(end, start + 0x80)
        words = [base_words[addr // 2] for addr in range(start, scan_end, 2)]
        high_bytes = [word >> 8 for word in words]
        low_bytes = [word & 0xFF for word in words]
        unique_words = len(set(words))
        repeated_words = len(words) - unique_words
        special_counts = {
            "0000": words.count(0x0000),
            "0107": words.count(0x0107),
            "00ff": words.count(0x00FF),
            "4e75": words.count(0x4E75),
        }
        print(f"  {label} ${start:04x}-${scan_end - 2:04x}:")
        print(
            f"    entropy word={shannon_entropy(words):.2f} hi={shannon_entropy(high_bytes):.2f} "
            f"lo={shannon_entropy(low_bytes):.2f} unique={unique_words}/{len(words)} repeats={repeated_words} "
            + " ".join(f"{key}={value}" for key, value in special_counts.items() if value)
        )

        pointer_hits = []
        pointer_counts: dict[str, int] = {}
        transform_pair_counts: dict[str, int] = {}
        for addr in range(start, scan_end - 2, 2):
            hi_variants = table_variant_sets(base_words[addr // 2], addr)
            lo_variants = table_variant_sets(base_words[(addr + 2) // 2], addr + 2)
            for hi_name, hi in hi_variants:
                for lo_name, lo in lo_variants:
                    target = (hi << 16) | lo
                    kind = table_pointer_kind(target)
                    if kind is None:
                        continue
                    pair_key = f"{hi_name}/{lo_name}"
                    pointer_counts[kind] = pointer_counts.get(kind, 0) + 1
                    transform_pair_counts[pair_key] = transform_pair_counts.get(pair_key, 0) + 1
                    if len(pointer_hits) < 10:
                        pointer_hits.append((addr, pair_key, target, kind, target_sequence_score(base_words, target)))

        if pointer_counts:
            rendered_counts = " ".join(f"{key}={pointer_counts[key]}" for key in sorted(pointer_counts))
            rendered_pairs = " ".join(
                f"{key}={count}" for key, count in sorted(transform_pair_counts.items(), key=lambda item: item[1], reverse=True)[:6]
            )
            print(f"    longword-like pairs: {rendered_counts}")
            print(f"    transform pairs: {rendered_pairs}")
            for addr, pair_key, target, kind, target_score in pointer_hits:
                print(f"      ${addr:04x}: {pair_key:<7} -> ${target:08x} {kind:<7} target_score={target_score}")
        else:
            print("    no longword-like pointer pairs through modeled transforms")

        deltas = []
        xor_deltas = []
        for left, right in zip(words, words[1:]):
            deltas.append((right - left) & 0xFFFF)
            xor_deltas.append(left ^ right)
        small_arith = sum(1 for delta in deltas if delta in (0, 1, 2, 0xFFFE, 0xFFFF))
        small_xor = sum(1 for delta in xor_deltas if delta.bit_count() <= 3)
        top_xor: dict[int, int] = {}
        for delta in xor_deltas:
            top_xor[delta] = top_xor.get(delta, 0) + 1
        rendered_xor = " ".join(f"{delta:04x}:{count}" for delta, count in sorted(top_xor.items(), key=lambda item: item[1], reverse=True)[:5])
        print(f"    adjacency: small_arith={small_arith}/{len(deltas)} small_xor={small_xor}/{len(xor_deltas)} top_xor={rendered_xor}")

        phase_counts: dict[int, int] = {}
        for addr in range(start, scan_end, 2):
            roles = []
            for _, value in table_variant_sets(base_words[addr // 2], addr):
                roles.extend(interesting_word_roles(value))
            if roles:
                phase = (addr // 2) & 0xF
                phase_counts[phase] = phase_counts.get(phase, 0) + 1
        if phase_counts:
            rendered_phase = " ".join(f"{phase:02x}:{count}" for phase, count in sorted(phase_counts.items()))
            print(f"    role phases: {rendered_phase}")
        else:
            print("    role phases: none")


def xor_histogram(words: list[int]) -> dict[int, int]:
    hist: dict[int, int] = {}
    for left, right in zip(words, words[1:]):
        delta = left ^ right
        hist[delta] = hist.get(delta, 0) + 1
    return hist


def histogram_overlap(left: dict[int, int], right: dict[int, int]) -> int:
    return sum(min(count, right.get(delta, 0)) for delta, count in left.items())


def table_pointer_targets_in_window(base_words: list[int], start: int, word_count: int) -> list[tuple[int, str, int, str, int]]:
    hits = []
    end = min(len(base_words) * 2, start + word_count * 2)
    for addr in range(start, end - 2, 2):
        hi_variants = table_variant_sets(base_words[addr // 2], addr)
        lo_variants = table_variant_sets(base_words[(addr + 2) // 2], addr + 2)
        for hi_name, hi in hi_variants:
            for lo_name, lo in lo_variants:
                target = (hi << 16) | lo
                kind = table_pointer_kind(target)
                if kind is None:
                    continue
                hits.append((addr, f"{hi_name}/{lo_name}", target, kind, target_sequence_score(base_words, target)))
    return hits


def table_fingerprint_score(anchor_words: list[int], candidate_words: list[int]) -> tuple[int, dict[str, int]]:
    exact = sum(1 for left, right in zip(anchor_words, candidate_words) if left == right)
    anchor_xor = xor_histogram(anchor_words)
    candidate_xor = xor_histogram(candidate_words)
    xor_overlap = histogram_overlap(anchor_xor, candidate_xor)
    anchor_top = {delta for delta, _ in sorted(anchor_xor.items(), key=lambda item: item[1], reverse=True)[:5]}
    top_overlap = sum(min(anchor_xor[delta], candidate_xor.get(delta, 0)) for delta in anchor_top)
    repeats = len(candidate_words) - len(set(candidate_words))
    small_xor = sum(1 for delta in candidate_xor for _ in range(candidate_xor[delta]) if delta.bit_count() <= 3)
    entropy_bonus = max(0, int((6.0 - shannon_entropy(candidate_words)) * 4))
    score = exact * 8 + xor_overlap * 3 + top_overlap * 5 + repeats + small_xor + entropy_bonus
    facts = {
        "exact": exact,
        "xor_overlap": xor_overlap,
        "top_overlap": top_overlap,
        "repeats": repeats,
        "small_xor": small_xor,
    }
    return score, facts


def print_cross_island_table_fingerprint_search(base_words: list[int]) -> None:
    print("\n== cross-island table fingerprint search")
    print("  scans for windows sharing weak-island repetition/XOR structure; this is table-oriented, not linear-code scoring")

    anchors = [
        ("$101c", 0x101C, 38),
        ("$0e32-family", 0x0E2E, 38),
        ("$0d34", 0x0D34, 38),
    ]
    max_start = min(0x12000, len(base_words) * 2)
    stride = 2

    for label, anchor_start, word_count in anchors:
        anchor_words = [base_words[(anchor_start + offset * 2) // 2] for offset in range(word_count)]
        raw_candidates = []
        for start in range(0, max_start - word_count * 2, stride):
            if abs(start - anchor_start) < 4:
                continue
            candidate_words = [base_words[(start + offset * 2) // 2] for offset in range(word_count)]
            score, facts = table_fingerprint_score(anchor_words, candidate_words)
            if score < 65:
                continue
            raw_candidates.append((score, start, facts, shannon_entropy(candidate_words)))

        raw_candidates.sort(reverse=True, key=lambda item: item[0])
        candidates = []
        for score, start, facts, entropy in raw_candidates[:24]:
            pointer_hits = table_pointer_targets_in_window(base_words, start, word_count)
            good_targets = sum(1 for _, _, _, kind, target_score in pointer_hits if kind == "rom-low" and target_score > 0)
            mmio_targets = sum(1 for _, _, _, kind, _ in pointer_hits if kind in ("mmio", "vdp"))
            score += good_targets * 8 + mmio_targets * 3
            candidates.append((score, start, facts, good_targets, mmio_targets, pointer_hits[:3], entropy))

        candidates.sort(reverse=True, key=lambda item: item[0])
        print(f"  anchor {label} ${anchor_start:04x} words={word_count}:")
        if not candidates:
            print("    no non-local fingerprint matches above threshold")
            continue
        for score, start, facts, good_targets, mmio_targets, pointer_hits, entropy in candidates[:10]:
            print(
                f"    ${start:04x}: score={score:3d} exact={facts['exact']:2d} xor={facts['xor_overlap']:2d} "
                f"top={facts['top_overlap']:2d} repeats={facts['repeats']:2d} entropy={entropy:.2f} "
                f"good_ptr={good_targets} mmio_ptr={mmio_targets}"
            )
            for addr, pair_key, target, kind, target_score in pointer_hits:
                print(f"      ${addr:04x}: {pair_key:<7} -> ${target:08x} {kind:<7} target_score={target_score}")


def print_d34_pointer_candidate_validation(base_words: list[int]) -> None:
    print("\n== $0d34 pointer candidate validation")
    print("  checks the strongest table-derived candidates as destinations before using them as decryption anchors")
    candidates = [
        (0x0D36, "x0/x0", 0x00000AD6, "table profile good low-ROM target"),
        (0x0D36, "x1/p5h", 0x00010BCC, "table profile high-bank target"),
        (0x0D36, "p5h/x0", 0x000D0AD6, "bank-D style target"),
        (0x0D36, "p5h/p5h", 0x000D0AD2, "bank-D style target"),
    ]
    for source, mode, target, note in candidates:
        kind = table_pointer_kind(target) or "other"
        score = target_sequence_score(base_words, target)
        preview = target_sequence_preview(base_words, target)
        print(f"  ${source:04x} {mode:<7} -> ${target:08x} {kind:<7} score={score:3d} {note}")
        print(f"    {preview}")


def record_slot_values(base_words: list[int], start: int, end: int, record_words: int, slot: int, variant_key: str) -> list[int]:
    values = []
    addr = start + slot * 2
    while addr < end:
        value = table_variant_value(base_words, addr, variant_key)
        if value is not None:
            values.append(value)
        addr += record_words * 2
    return values


def score_record_period(base_words: list[int], start: int, end: int, record_words: int) -> tuple[int, dict[str, int]]:
    slot_repeat_score = 0
    slot_low_entropy = 0
    role_slots = 0
    pointer_slots = 0
    repeated_pointer_targets = 0

    for slot in range(record_words):
        raw_values = record_slot_values(base_words, start, end, record_words, slot, "raw")
        if len(raw_values) < 3:
            continue
        repeats = len(raw_values) - len(set(raw_values))
        entropy = shannon_entropy(raw_values)
        slot_repeat_score += repeats
        if entropy <= 2.0:
            slot_low_entropy += 1
        if any(interesting_word_roles(value) for value in raw_values):
            role_slots += 1

    for slot in range(record_words - 1):
        target_counts: dict[int, int] = {}
        addr = start + slot * 2
        while addr + 2 < end:
            for hi_name, hi in table_variant_sets(base_words[addr // 2], addr):
                for lo_name, lo in table_variant_sets(base_words[(addr + 2) // 2], addr + 2):
                    target = (hi << 16) | lo
                    kind = table_pointer_kind(target)
                    if kind in ("rom-low", "mmio", "vdp"):
                        target_counts[target] = target_counts.get(target, 0) + 1
            addr += record_words * 2
        if target_counts:
            pointer_slots += 1
            repeated_pointer_targets += sum(count - 1 for count in target_counts.values() if count > 1)

    facts = {
        "repeat": slot_repeat_score,
        "low_entropy_slots": slot_low_entropy,
        "role_slots": role_slots,
        "pointer_slots": pointer_slots,
        "repeated_targets": repeated_pointer_targets,
    }
    score = slot_repeat_score * 3 + slot_low_entropy * 5 + role_slots * 2 + pointer_slots * 8 + repeated_pointer_targets * 6
    return score, facts


def print_table_cluster_record_model(base_words: list[int]) -> None:
    print("\n== $0ec0-$103e table-cluster record model")
    print("  tests candidate record sizes/alignment for the repeated weak-island family before any byte patching")
    cluster_start = 0x0EA0
    cluster_end = 0x1040

    alignment_scores = []
    for start in range(cluster_start, 0x0ED0, 2):
        for record_words in (4, 8, 12, 16):
            score, facts = score_record_period(base_words, start, cluster_end, record_words)
            alignment_scores.append((score, start, record_words, facts))
    alignment_scores.sort(reverse=True, key=lambda item: item[0])

    print("  strongest start/period alignments:")
    for score, start, record_words, facts in alignment_scores[:8]:
        print(
            f"    start=${start:04x} period={record_words:2d}w/{record_words * 2:02x}b score={score:3d} "
            f"repeat={facts['repeat']:3d} pointer_slots={facts['pointer_slots']} repeated_targets={facts['repeated_targets']}"
        )

    best_start = alignment_scores[0][1]
    period_scores = []
    for record_words in range(3, 17):
        score, facts = score_record_period(base_words, best_start, cluster_end, record_words)
        period_scores.append((score, record_words, facts))
    period_scores.sort(reverse=True, key=lambda item: item[0])

    print(f"  candidate record periods at best start ${best_start:04x}:")
    for score, record_words, facts in period_scores[:8]:
        print(
            f"    {record_words:2d} words/{record_words * 2:02x} bytes: score={score:3d} "
            f"repeat={facts['repeat']:2d} low_entropy_slots={facts['low_entropy_slots']:2d} "
            f"role_slots={facts['role_slots']:2d} pointer_slots={facts['pointer_slots']:2d} "
            f"repeated_targets={facts['repeated_targets']:2d}"
        )

    best_record_words = period_scores[0][1]
    print(f"  best period detail: {best_record_words} words/{best_record_words * 2:02x} bytes")
    for slot in range(best_record_words):
        raw_values = record_slot_values(base_words, best_start, cluster_end, best_record_words, slot, "raw")
        x0_values = record_slot_values(base_words, best_start, cluster_end, best_record_words, slot, "x0")
        p5m_values = record_slot_values(base_words, best_start, cluster_end, best_record_words, slot, "p5m")
        if not raw_values:
            continue
        raw_repeats = len(raw_values) - len(set(raw_values))
        role_count = sum(1 for value in raw_values if interesting_word_roles(value))
        p5m_role_count = sum(1 for value in p5m_values if interesting_word_roles(value))
        print(
            f"    slot {slot:02d} +{slot * 2:02x}: raw_entropy={shannon_entropy(raw_values):.2f} "
            f"raw_repeat={raw_repeats:2d} raw_roles={role_count:2d} p5m_roles={p5m_role_count:2d} "
            f"raw0={raw_values[0]:04x} x0_0={x0_values[0] if x0_values else 0:04x} "
            f"p5m0={p5m_values[0] if p5m_values else 0:04x}"
        )

    record_counts: dict[tuple[int, ...], int] = {}
    addr = best_start
    while addr + best_record_words * 2 <= cluster_end:
        record = tuple(base_words[addr // 2 + offset] for offset in range(best_record_words))
        record_counts[record] = record_counts.get(record, 0) + 1
        addr += best_record_words * 2

    print("  most common raw records:")
    for record, count in sorted(record_counts.items(), key=lambda item: item[1], reverse=True)[:10]:
        rendered = " ".join(f"{word:04x}" for word in record)
        print(f"    count={count:2d} {rendered}")

    print("  repeated pointer-like slot pairs for best period:")
    pair_rows = []
    for slot in range(best_record_words - 1):
        counts: dict[tuple[str, int, str], int] = {}
        addr = best_start + slot * 2
        while addr + 2 < cluster_end:
            for hi_name, hi in table_variant_sets(base_words[addr // 2], addr):
                for lo_name, lo in table_variant_sets(base_words[(addr + 2) // 2], addr + 2):
                    target = (hi << 16) | lo
                    kind = table_pointer_kind(target)
                    if kind is None:
                        continue
                    counts[(f"{hi_name}/{lo_name}", target, kind)] = counts.get((f"{hi_name}/{lo_name}", target, kind), 0) + 1
            addr += best_record_words * 2
        repeated = [(count, pair_key, target, kind) for (pair_key, target, kind), count in counts.items() if count > 1]
        repeated.sort(reverse=True)
        for count, pair_key, target, kind in repeated[:3]:
            pair_rows.append((count, slot, pair_key, target, kind, target_sequence_score(base_words, target)))

    if not pair_rows:
        print("    no repeated pointer-like pairs for the best period")
    else:
        pair_rows.sort(reverse=True, key=lambda item: (item[0], item[5]))
        for count, slot, pair_key, target, kind, target_score in pair_rows[:12]:
            print(
                f"    slot {slot:02d}/{slot + 1:02d}: count={count} {pair_key:<7} "
                f"-> ${target:08x} {kind:<7} target_score={target_score}"
            )


def table_record_ids(base_words: list[int], start: int, end: int, record_words: int) -> tuple[dict[tuple[int, ...], str], list[tuple[int, tuple[int, ...], str]]]:
    counts: dict[tuple[int, ...], int] = {}
    records = []
    addr = start
    while addr + record_words * 2 <= end:
        record = tuple(base_words[addr // 2 + offset] for offset in range(record_words))
        counts[record] = counts.get(record, 0) + 1
        records.append((addr, record))
        addr += record_words * 2

    ordered = sorted(counts, key=lambda record: (-counts[record], record))
    names = {record: f"R{idx:02d}" for idx, record in enumerate(ordered)}
    return names, [(addr, record, names[record]) for addr, record in records]


def print_table_cluster_record_alphabet(base_words: list[int]) -> None:
    print("\n== $0ea0 table-cluster record alphabet")
    print("  renders the 4-word table family as repeated symbols and field values")
    start = 0x0EA0
    end = 0x1040
    record_words = 4
    names, records = table_record_ids(base_words, start, end, record_words)

    counts: dict[str, int] = {}
    for _, _, name in records:
        counts[name] = counts.get(name, 0) + 1

    print(f"  records={len(records)} unique={len(names)} start=${start:04x} end=${end:04x} period={record_words * 2:02x} bytes")
    print("  dictionary:")
    for record, name in sorted(names.items(), key=lambda item: int(item[1][1:]))[:24]:
        rendered = " ".join(f"{word:04x}" for word in record)
        print(f"    {name} count={counts[name]:2d} {rendered}")

    print("  symbol stream:")
    for row_start in range(0, len(records), 8):
        chunk = records[row_start:row_start + 8]
        rendered = " ".join(f"{name}@{addr:04x}" for addr, _, name in chunk)
        print(f"    {rendered}")

    print("  field alphabets:")
    for slot in range(record_words):
        values: dict[int, int] = {}
        for _, record, _ in records:
            values[record[slot]] = values.get(record[slot], 0) + 1
        rendered = " ".join(f"{value:04x}:{count}" for value, count in sorted(values.items(), key=lambda item: (-item[1], item[0]))[:12])
        print(f"    slot {slot:02d}: unique={len(values):2d} {rendered}")


def print_table_cluster_reference_scan(base_words: list[int]) -> None:
    print("\n== $0ea0 table-cluster reference scan")
    print("  scans modeled word/longword variants for references into the 4-word record cluster")
    cluster_start = 0x0EA0
    cluster_end = 0x1040
    max_scan = min(0x12000, len(base_words) * 2)

    word_refs = []
    long_refs = []
    for addr in range(0, max_scan, 2):
        for name, value in table_variant_sets(base_words[addr // 2], addr):
            if cluster_start <= value < cluster_end and not (value & 1):
                word_refs.append((addr, name, value, target_sequence_score(base_words, addr - 4 if addr >= 4 else addr)))

        if addr + 2 >= max_scan:
            continue
        for hi_name, hi in table_variant_sets(base_words[addr // 2], addr):
            for lo_name, lo in table_variant_sets(base_words[(addr + 2) // 2], addr + 2):
                target = (hi << 16) | lo
                if cluster_start <= target < cluster_end and not (target & 1):
                    long_refs.append((addr, f"{hi_name}/{lo_name}", target, target_sequence_score(base_words, addr - 4 if addr >= 4 else addr)))

    word_refs.sort(key=lambda item: (item[3], item[0] < 0x2000), reverse=True)
    prioritized_word_refs = [item for item in word_refs if item[3] > 0 or item[0] < 0x1100]
    if prioritized_word_refs:
        print(f"  prioritized 16-bit word-like refs ({len(word_refs)} total):")
        for addr, name, value, context_score in prioritized_word_refs[:24]:
            print(f"    ${addr:04x}: {name:<4} -> ${value:04x} context_score={context_score}")
        if len(prioritized_word_refs) > 24:
            print(f"    ... {len(prioritized_word_refs) - 24} more prioritized")
    else:
        print(f"  no prioritized 16-bit word-like refs into cluster ({len(word_refs)} total raw hits)")

    long_refs.sort(key=lambda item: (item[3], item[0] < 0x2000), reverse=True)
    prioritized_long_refs = [item for item in long_refs if item[3] > 0 or item[0] < 0x1100]
    if prioritized_long_refs:
        print(f"  prioritized 32-bit longword-like refs ({len(long_refs)} total):")
        for addr, pair_key, target, context_score in prioritized_long_refs[:24]:
            print(f"    ${addr:04x}: {pair_key:<7} -> ${target:08x} context_score={context_score}")
        if len(prioritized_long_refs) > 24:
            print(f"    ... {len(prioritized_long_refs) - 24} more prioritized")
    else:
        print(f"  no prioritized 32-bit longword-like refs into cluster ({len(long_refs)} total raw hits)")


def table_record_label_for_address(base_words: list[int], target: int) -> str:
    start = 0x0EA0
    end = 0x1040
    record_words = 4
    if not (start <= target < end):
        return "-"
    names, _ = table_record_ids(base_words, start, end, record_words)
    record_addr = start + ((target - start) // (record_words * 2)) * (record_words * 2)
    slot = (target - record_addr) // 2
    record = tuple(base_words[record_addr // 2 + offset] for offset in range(record_words))
    rendered = " ".join(f"{word:04x}" for word in record)
    return f"{names[record]}@${record_addr:04x}+{slot * 2:02x} [{rendered}]"


def startup_table_call_sites(base_words: list[int]) -> list[tuple[int, str, int, str]]:
    cluster_start = 0x0EA0
    cluster_end = 0x1040

    call_sites = []
    for addr in range(0x0C42, 0x0C9A, 2):
        opcode_variants = startup_word_variants(base_words[addr // 2], addr)
        if not any(value in (0x2E3F, 0x4EB8, 0x4EB9) for _, value in opcode_variants):
            continue

        if addr + 4 >= len(base_words) * 2:
            continue
        hi_addr = addr + 2
        lo_addr = addr + 4
        for hi_name, hi in table_variant_sets(base_words[hi_addr // 2], hi_addr):
            for lo_name, lo in table_variant_sets(base_words[lo_addr // 2], lo_addr):
                target = (hi << 16) | lo
                if cluster_start <= target < cluster_end and not (target & 1):
                    call_sites.append((addr, f"{hi_name}/{lo_name}", target, table_record_label_for_address(base_words, target)))

        # Absolute-word JSR style, useful for the `$00f8.w`-like call shape.
        for lo_name, lo in table_variant_sets(base_words[hi_addr // 2], hi_addr):
            target = lo & 0xFFFF
            if cluster_start <= target < cluster_end and not (target & 1):
                call_sites.append((addr, f"absw:{lo_name}", target, table_record_label_for_address(base_words, target)))

    deduped = []
    seen = set()
    for addr, mode, target, record_label in call_sites:
        key = (addr, mode, target)
        if key in seen:
            continue
        seen.add(key)
        deduped.append((addr, mode, target, record_label))
    return deduped


def print_startup_table_entry_interpretation(base_words: list[int]) -> None:
    print("\n== startup references into $0ea0 record family")
    print("  reinterprets startup JSR operands that land in the 4-word table cluster as record entries")
    call_sites = startup_table_call_sites(base_words)

    if not call_sites:
        print("  no startup operands resolve into the table family")
        return

    for addr, mode, target, record_label in call_sites:
        print(f"  call/operand @${addr:04x}: {mode:<9} -> ${target:04x} {record_label}")


def token_variant_summary(base_words: list[int], addr: int) -> str:
    parts = []
    for key in ("raw", "x0", "p5m", "p5h"):
        value = table_variant_value(base_words, addr, key)
        if value is None:
            continue
        roles = interesting_word_roles(value)
        role_text = f"[{','.join(roles[:2])}]" if roles else ""
        parts.append(f"{key}:{value:04x}{role_text}")
    return " ".join(parts)


def print_table_cluster_token_stream_model(base_words: list[int]) -> None:
    print("\n== $0ea0 halfword-token stream model")
    print("  treats the record family as a 2-byte token stream because startup entries land inside records")
    start = 0x0EA0
    end = 0x1040
    record_words = 4

    tokens = [(addr, base_words[addr // 2]) for addr in range(start, end, 2)]
    counts: dict[int, int] = {}
    slot_counts: dict[int, dict[int, int]] = {}
    transitions: dict[tuple[int, int], int] = {}
    for idx, (addr, word) in enumerate(tokens):
        counts[word] = counts.get(word, 0) + 1
        slot = ((addr - start) // 2) % record_words
        slot_counts.setdefault(word, {})
        slot_counts[word][slot] = slot_counts[word].get(slot, 0) + 1
        if idx + 1 < len(tokens):
            pair = (word, tokens[idx + 1][1])
            transitions[pair] = transitions.get(pair, 0) + 1

    print(f"  tokens={len(tokens)} unique={len(counts)} entropy={shannon_entropy([word for _, word in tokens]):.2f}")
    print("  frequent raw tokens with slot bias:")
    for word, count in sorted(counts.items(), key=lambda item: (-item[1], item[0]))[:18]:
        slots = slot_counts[word]
        slot_text = " ".join(f"s{slot}:{slots.get(slot, 0)}" for slot in range(record_words) if slots.get(slot, 0))
        print(f"    {word:04x}: count={count:2d} {slot_text}")

    print("  repeated adjacent token pairs:")
    repeated_pairs = [(count, left, right) for (left, right), count in transitions.items() if count > 1]
    repeated_pairs.sort(key=lambda item: (-item[0], item[1], item[2]))
    for count, left, right in repeated_pairs[:16]:
        print(f"    {left:04x} -> {right:04x}: count={count}")

    call_sites = startup_table_call_sites(base_words)
    slot_entry_counts: dict[int, int] = {}
    for _, _, target, _ in call_sites:
        slot = ((target - start) // 2) % record_words
        slot_entry_counts[slot] = slot_entry_counts.get(slot, 0) + 1
    if slot_entry_counts:
        rendered = " ".join(f"s{slot}:{slot_entry_counts.get(slot, 0)}" for slot in range(record_words))
        print(f"  startup entry slot distribution: {rendered}")

    if not call_sites:
        print("  no startup token traces")
        return

    print("  startup token traces:")
    seen_targets = set()
    for call_addr, mode, target, record_label in call_sites:
        if target in seen_targets:
            continue
        seen_targets.add(target)
        print(f"    entry @${call_addr:04x} {mode:<9} -> ${target:04x} {record_label}")
        for trace_addr in range(target, min(target + 0x18, end), 2):
            slot = ((trace_addr - start) // 2) % record_words
            print(f"      ${trace_addr:04x} s{slot}: {token_variant_summary(base_words, trace_addr)}")


def print_table_cluster_token_run_scan(base_words: list[int]) -> None:
    print("\n== token alphabet run scan")
    print("  finds contiguous low-ROM regions using the $0ea0-family token alphabet")
    anchor_start = 0x0EA0
    anchor_end = 0x1040
    max_scan = min(0x12000, len(base_words) * 2)

    alphabet_counts: dict[int, int] = {}
    for addr in range(anchor_start, anchor_end, 2):
        word = base_words[addr // 2]
        alphabet_counts[word] = alphabet_counts.get(word, 0) + 1
    alphabet = {word for word, count in alphabet_counts.items() if count >= 2}

    runs = []
    run_start = None
    run_hits = 0
    run_len = 0
    for addr in range(0, max_scan + 2, 2):
        in_range = addr < max_scan
        word = base_words[addr // 2] if in_range else None
        hit = bool(in_range and word in alphabet)
        if hit:
            if run_start is None:
                run_start = addr
                run_hits = 0
                run_len = 0
            run_hits += 1
            run_len += 1
            continue

        if run_start is not None:
            if run_len >= 6 and run_hits / run_len >= 0.75:
                run_end = run_start + run_len * 2
                unique = len({base_words[pos // 2] for pos in range(run_start, run_end, 2)})
                runs.append((run_len, run_hits, run_start, run_end, unique))
            run_start = None
            run_hits = 0
            run_len = 0

    runs.sort(key=lambda item: (-item[0], item[2]))
    if not runs:
        print("  no contiguous token-alphabet runs found")
        return

    print(f"  alphabet_size={len(alphabet)} anchor=${anchor_start:04x}-${anchor_end - 2:04x}")
    for run_len, run_hits, start, end, unique in runs[:16]:
        edge_words = " ".join(f"{base_words[pos // 2]:04x}" for pos in range(start, min(end, start + 0x10), 2))
        print(
            f"    ${start:04x}-${end - 2:04x}: words={run_len:3d} hits={run_hits:3d} "
            f"unique={unique:2d} {edge_words}"
        )

    merged = []
    for run_len, run_hits, start, end, _unique in sorted(runs, key=lambda item: item[2]):
        if not merged or start - merged[-1][1] > 0x0C:
            merged.append([start, end, run_hits, run_len])
            continue
        merged[-1][1] = end
        merged[-1][2] += run_hits
        merged[-1][3] += run_len

    print("  merged regions with <=12-byte gaps:")
    for start, end, hits, words in sorted(merged, key=lambda item: (-(item[1] - item[0]), item[0]))[:10]:
        total_words = (end - start) // 2
        density = hits / total_words if total_words else 0.0
        print(f"    ${start:04x}-${end - 2:04x}: span_words={total_words:3d} token_hits={hits:3d} density={density:.2f}")


def token_block_alphabet(base_words: list[int]) -> set[int]:
    counts: dict[int, int] = {}
    for addr in range(0x0EA0, 0x1040, 2):
        word = base_words[addr // 2]
        counts[word] = counts.get(word, 0) + 1
    return {word for word, count in counts.items() if count >= 2}


def token_block_label_for_address(target: int, block_starts: list[int], block_stride: int, base_index: int = 0) -> str:
    for idx, start in enumerate(block_starts, base_index):
        end = start + block_stride
        if start <= target < end:
            rel = target - start
            word = rel // 2
            return f"B{idx:02d}@${start:04x}+{rel:02x}/w{word:02d}"
    return "-"


def token_block_label_for_named_address(target: int, blocks: list[tuple[str, int]], block_stride: int) -> str:
    for label, start in blocks:
        end = start + block_stride
        if start <= target < end:
            rel = target - start
            word = rel // 2
            return f"{label}@${start:04x}+{rel:02x}/w{word:02d}"
    return "-"


def print_token_block_trailer_model(base_words: list[int]) -> None:
    print("\n== token block/trailer model")
    print("  tests whether dense token runs are fixed-size blocks ending in a marker plus parameter")
    alphabet = token_block_alphabet(base_words)
    block_stride = 0x3A
    block_words = block_stride // 2

    # Main repeated family discovered by the run scan.  `$0ea0` is a shorter
    # prologue block; `$0ed4` begins the clean fixed-stride series.
    starts = [0x0EA0, 0x0ED4, 0x0F0E, 0x0F48, 0x0F82, 0x0FBC, 0x0FF6, 0x1030]
    for idx, start in enumerate(starts):
        end = start + block_stride
        if end > len(base_words) * 2:
            continue
        words = [base_words[addr // 2] for addr in range(start, end, 2)]
        token_hits = sum(1 for word in words[:-1] if word in alphabet)
        trailer_marker = words[-2]
        trailer_param = words[-1]
        first_tokens = " ".join(f"{word:04x}" for word in words[:6])
        print(
            f"  B{idx:02d} ${start:04x}-${end - 2:04x}: "
            f"token_hits={token_hits:2d}/{block_words - 1:2d} "
            f"trailer={trailer_marker:04x} {trailer_param:04x} first={first_tokens}"
        )

    print("  startup entries by block offset:")
    for call_addr, mode, target, record_label in startup_table_call_sites(base_words):
        block_label = token_block_label_for_address(target, starts, block_stride)
        print(f"    @${call_addr:04x} {mode:<9} -> ${target:04x} {block_label:<18} {record_label}")

    print("  bridge/prologue words around $0e80-$0e9e:")
    for addr in range(0x0E80, 0x0EA0, 2):
        word = base_words[addr // 2]
        membership = "tok" if word in alphabet else "---"
        print(f"    ${addr:04x}: {membership} {token_variant_summary(base_words, addr)}")

    echo_start = 0x11EE
    echo_end = 0x1220
    echo_hits = sum(1 for addr in range(echo_start, echo_end, 2) if base_words[addr // 2] in alphabet)
    print(f"  late echo ${echo_start:04x}-${echo_end - 2:04x}: token_hits={echo_hits}/{(echo_end - echo_start) // 2}")
    for addr in range(echo_start, echo_end, 2):
        word = base_words[addr // 2]
        if word in alphabet or word in (0x4EBA, 0x6700):
            membership = "tok" if word in alphabet else "ctl"
            print(f"    ${addr:04x}: {membership} {token_variant_summary(base_words, addr)}")


def print_token_block_column_model(base_words: list[int]) -> None:
    print("\n== fixed token-block column model")
    print("  compares the clean 0x3a-byte blocks column-by-column")
    block_stride = 0x3A
    block_words = block_stride // 2
    starts = [0x0ED4, 0x0F0E, 0x0F48, 0x0F82, 0x0FBC, 0x0FF6]
    labels = [f"B{idx + 1:02d}" for idx in range(len(starts))]

    rows = []
    for start in starts:
        rows.append([base_words[(start + column * 2) // 2] for column in range(block_words)])

    print("  block trailers:")
    for label, start, row in zip(labels, starts, rows):
        print(f"    {label} ${start:04x}: marker={row[-2]:04x} param={row[-1]:04x}")

    print("  columns with variation:")
    for column in range(block_words):
        values = [row[column] for row in rows]
        unique = sorted(set(values))
        if len(unique) == 1:
            continue
        rendered = " ".join(f"{label}:{value:04x}" for label, value in zip(labels, values))
        print(f"    w{column:02d} +{column * 2:02x}: unique={len(unique):2d} {rendered}")

    print("  stable columns:")
    stable = []
    for column in range(block_words):
        values = [row[column] for row in rows]
        if len(set(values)) == 1:
            stable.append((column, values[0]))
    for offset in range(0, len(stable), 8):
        chunk = stable[offset:offset + 8]
        print("    " + " ".join(f"w{column:02d}:{value:04x}" for column, value in chunk))

    print("  startup entries by fixed-block column:")
    for call_addr, mode, target, record_label in startup_table_call_sites(base_words):
        for label, start in zip(labels, starts):
            if start <= target < start + block_stride:
                column = (target - start) // 2
                print(f"    @${call_addr:04x} {mode:<9} -> {label} w{column:02d} +{column * 2:02x} {record_label}")
                break

    echo_start = 0x11EE
    echo_words = [base_words[(echo_start + idx * 2) // 2] for idx in range(16)]
    best = []
    for label, row in zip(labels, rows):
        for offset in range(0, block_words - len(echo_words) + 1):
            exact = sum(1 for idx, word in enumerate(echo_words) if row[offset + idx] == word)
            best.append((exact, label, offset))
    best.sort(reverse=True)
    print("  late echo best fixed-block alignments:")
    for exact, label, offset in best[:8]:
        print(f"    {label} w{offset:02d}: exact={exact}/{len(echo_words)}")


def classify_param_target(base_words: list[int], target: int) -> str:
    kind = table_pointer_kind(target)
    if kind:
        score = target_sequence_score(base_words, target) if 0 <= target < len(base_words) * 2 else -100
        return f"{kind} score={score}"
    if 0x0E46 <= target <= 0x1064 and not (target & 1):
        return "token-region"
    return "-"


def print_token_block_trailer_param_model(base_words: list[int]) -> None:
    print("\n== token block trailer parameter model")
    print("  interprets block trailer parameters as absolute or relative addresses")
    starts = [0x0ED4, 0x0F0E, 0x0F48, 0x0F82, 0x0FBC, 0x0FF6]
    label_blocks = [("P00", 0x0EA0)] + [(f"B{idx:02d}", start) for idx, start in enumerate(starts, 1)] + [("B07", 0x1030)]
    block_stride = 0x3A
    for idx, start in enumerate(starts, 1):
        marker_addr = start + block_stride - 4
        param_addr = start + block_stride - 2
        marker = base_words[marker_addr // 2]
        raw_param = base_words[param_addr // 2]
        print(f"  B{idx:02d} marker@${marker_addr:04x}={marker:04x} param@${param_addr:04x} raw={raw_param:04x}")
        candidates = []
        for mode, param in table_variant_sets(raw_param, param_addr):
            signed = param if param < 0x8000 else param - 0x10000
            interpretations = [
                ("abs", param),
                ("from-start", start + signed),
                ("from-marker", marker_addr + signed),
                ("from-next", start + block_stride + signed),
            ]
            for interp, target in interpretations:
                if target & 1:
                    continue
                quality = classify_param_target(base_words, target)
                if quality == "-":
                    continue
                candidates.append((mode, interp, target, quality))
        if not candidates:
            print("    no address-like interpretations")
            continue
        for mode, interp, target, quality in candidates[:12]:
            label = table_record_label_for_address(base_words, target) if 0x0EA0 <= target < 0x1040 else "-"
            block_label = token_block_label_for_named_address(target, label_blocks, block_stride)
            print(f"    {mode:<4} {interp:<10} -> ${target:04x} {quality:<18} {block_label:<18} {label}")


def trailer_target_score(target: int, block_start_set: set[int], startup_targets: set[int]) -> tuple[int, list[str]]:
    score = 0
    reasons = []
    if target in block_start_set:
        score += 5
        reasons.append("block-start")
    if target == 0x1030:
        score += 4
        reasons.append("1030-converge")
    if target in startup_targets:
        score += 3
        reasons.append("startup-entry")
    if 0x0EA0 <= target <= 0x1064 and not (target & 1):
        score += 2
        reasons.append("token-region")
    if 0x0E46 <= target < 0x0EA0 and not (target & 1):
        score += 1
        reasons.append("early-token-region")
    return score, reasons


def token_block_trailer_candidates(base_words: list[int], start: int, marker_addr: int, param_addr: int) -> list[tuple[int, str, str, int, int, list[str]]]:
    block_stride = 0x3A
    marker = base_words[marker_addr // 2]
    raw_param = base_words[param_addr // 2]
    block_starts = {0x0EA0, 0x0ED4, 0x0F0E, 0x0F48, 0x0F82, 0x0FBC, 0x0FF6, 0x1030}
    startup_targets = {target for _, _, target, _ in startup_table_call_sites(base_words)}
    candidates = []
    for mode, param in table_variant_sets(raw_param, param_addr):
        signed = param if param < 0x8000 else param - 0x10000
        interpretations = [
            ("abs", param),
            ("from-start", start + signed),
            ("from-marker", marker_addr + signed),
            ("from-next", start + block_stride + signed),
        ]
        for interp, target in interpretations:
            if target & 1:
                continue
            score, reasons = trailer_target_score(target, block_starts, startup_targets)
            if marker in (0x4FBD, 0x4EBA, 0x4F72):
                score += 2
                reasons.append(f"marker:{marker:04x}")
            if score:
                candidates.append((score, mode, interp, target, param, reasons))
    candidates.sort(key=lambda item: (-item[0], item[3], item[1], item[2]))
    return candidates


def print_token_block_trailer_confidence_model(base_words: list[int]) -> None:
    print("\n== token block trailer confidence model")
    print("  keeps only high-signal trailer interpretations and suppresses generic address noise")
    starts = [0x0ED4, 0x0F0E, 0x0F48, 0x0F82, 0x0FBC, 0x0FF6]
    label_blocks = [("P00", 0x0EA0)] + [(f"B{idx:02d}", start) for idx, start in enumerate(starts, 1)] + [("B07", 0x1030)]
    block_stride = 0x3A
    hits = []
    for idx, start in enumerate(starts, 1):
        marker_addr = start + block_stride - 4
        param_addr = start + block_stride - 2
        marker = base_words[marker_addr // 2]
        raw_param = base_words[param_addr // 2]
        candidates = token_block_trailer_candidates(base_words, start, marker_addr, param_addr)
        strong = [candidate for candidate in candidates if candidate[0] >= 7]
        if not strong:
            print(f"  B{idx:02d} marker={marker:04x} raw_param={raw_param:04x}: no strong target")
            continue
        for score, mode, interp, target, param, reasons in strong[:4]:
            block_label = token_block_label_for_named_address(target, label_blocks, block_stride)
            record_label = table_record_label_for_address(base_words, target) if 0x0EA0 <= target < 0x1040 else "-"
            print(
                f"  B{idx:02d} marker={marker:04x} raw={raw_param:04x}: "
                f"score={score:2d} {mode:<4} {interp:<10} param={param:04x} "
                f"-> ${target:04x} {block_label:<18} {'/'.join(reasons)} {record_label}"
            )
            hits.append((target, idx, mode, interp))

    convergence: dict[int, list[str]] = {}
    for target, idx, mode, interp in hits:
        convergence.setdefault(target, []).append(f"B{idx:02d}:{mode}:{interp}")
    print("  convergent strong targets:")
    for target, sources in sorted(convergence.items(), key=lambda item: (-len(item[1]), item[0])):
        if len(sources) < 2:
            continue
        block_label = token_block_label_for_named_address(target, label_blocks, block_stride)
        print(f"    ${target:04x} {block_label:<18} <= {', '.join(sources)}")


def print_token_marker_family_scan(base_words: list[int]) -> None:
    print("\n== token marker-family scan")
    print("  searches for 4fbd/4eba/4f72 trailers whose following word can target token blocks")
    block_stride = 0x3A
    max_scan = min(0x14000, len(base_words) * 2 - 2)
    label_blocks = [
        ("E00", 0x0E46),
        ("P00", 0x0EA0),
        ("B01", 0x0ED4),
        ("B02", 0x0F0E),
        ("B03", 0x0F48),
        ("B04", 0x0F82),
        ("B05", 0x0FBC),
        ("B06", 0x0FF6),
        ("B07", 0x1030),
    ]
    hits = []
    for marker_addr in range(0, max_scan, 2):
        marker = base_words[marker_addr // 2]
        if marker not in (0x4FBD, 0x4EBA, 0x4F72):
            continue
        param_addr = marker_addr + 2
        inferred_start = marker_addr - (block_stride - 4)
        if inferred_start < 0:
            continue
        for score, mode, interp, target, param, reasons in token_block_trailer_candidates(
            base_words, inferred_start, marker_addr, param_addr
        ):
            if score < 7:
                continue
            block_label = token_block_label_for_named_address(target, label_blocks, block_stride)
            source_label = token_block_label_for_named_address(marker_addr, label_blocks, block_stride)
            hits.append((score, marker_addr, marker, base_words[param_addr // 2], mode, interp, target, param, source_label, block_label, reasons))

    if not hits:
        print("  no high-signal marker-family hits")
        return

    hits.sort(key=lambda item: (-item[0], item[1], item[6], item[4], item[5]))
    for score, marker_addr, marker, raw_param, mode, interp, target, param, source_label, block_label, reasons in hits[:32]:
        print(
            f"  ${marker_addr:04x} {source_label:<18} marker={marker:04x} raw={raw_param:04x} "
            f"score={score:2d} {mode:<4} {interp:<10} param={param:04x} -> ${target:04x} "
            f"{block_label:<18} {'/'.join(reasons)}"
        )
    if len(hits) > 32:
        print(f"    ... {len(hits) - 32} more high-signal marker hits")


def print_upstream_marker_block_test(base_words: list[int]) -> None:
    print("\n== upstream marker block test")
    print("  tests whether $0e38 can be a real 0x3a-byte trailer rather than a marker-shaped coincidence")
    block_stride = 0x3A
    block_words = block_stride // 2
    alphabet = token_block_alphabet(base_words)
    candidate_start = 0x0E38 - (block_stride - 4)
    marker_addr = candidate_start + block_stride - 4
    param_addr = candidate_start + block_stride - 2
    marker = base_words[marker_addr // 2]
    raw_param = base_words[param_addr // 2]
    row = [base_words[(candidate_start + column * 2) // 2] for column in range(block_words)]
    clean_starts = [0x0ED4, 0x0F0E, 0x0F48, 0x0F82, 0x0FBC, 0x0FF6]
    clean_rows = [
        [base_words[(start + column * 2) // 2] for column in range(block_words)]
        for start in clean_starts
    ]
    stable_columns = []
    for column in range(block_words):
        values = [clean_row[column] for clean_row in clean_rows]
        if len(set(values)) == 1:
            stable_columns.append((column, values[0]))

    token_hits = sum(1 for word in row[:-1] if word in alphabet)
    stable_hits = sum(1 for column, value in stable_columns if row[column] == value)
    stable_rendered = " ".join(
        f"w{column:02d}:{row[column]:04x}{'*' if row[column] == value else ''}"
        for column, value in stable_columns
    )
    print(
        f"  candidate block ${candidate_start:04x}-${candidate_start + block_stride - 2:04x}: "
        f"token_hits={token_hits}/{block_words - 1} stable_hits={stable_hits}/{len(stable_columns)} "
        f"trailer={marker:04x} {raw_param:04x}"
    )
    print(f"  stable-column check: {stable_rendered}")

    best = []
    for label_idx, clean_row in enumerate(clean_rows, 1):
        exact = sum(1 for column, word in enumerate(row) if clean_row[column] == word)
        exact_without_trailer = sum(1 for column, word in enumerate(row[:-2]) if clean_row[column] == word)
        best.append((exact, exact_without_trailer, f"B{label_idx:02d}"))
    best.sort(reverse=True)
    print("  exact column matches against clean blocks:")
    for exact, exact_without_trailer, label in best:
        print(f"    {label}: exact={exact:2d}/{block_words} body_exact={exact_without_trailer:2d}/{block_words - 2}")

    print("  trailer interpretations:")
    label_blocks = [
        ("U00", candidate_start),
        ("B04", 0x0F82),
        ("B05", 0x0FBC),
        ("B07", 0x1030),
    ]
    for score, mode, interp, target, param, reasons in token_block_trailer_candidates(
        base_words, candidate_start, marker_addr, param_addr
    ):
        if score < 5:
            continue
        block_label = token_block_label_for_named_address(target, label_blocks, block_stride)
        print(
            f"    score={score:2d} {mode:<4} {interp:<10} param={param:04x} "
            f"-> ${target:04x} {block_label:<18} {'/'.join(reasons)}"
        )

    print("  candidate block words:")
    for offset in range(0, block_words, 8):
        parts = []
        for column in range(offset, min(block_words, offset + 8)):
            addr = candidate_start + column * 2
            word = row[column]
            mark = "tok" if word in alphabet else "---"
            parts.append(f"${addr:04x}:{mark}:{word:04x}")
        print("    " + " ".join(parts))


TOKEN_NAMES = {
    0x01A6: "A",
    0x0102: "B",
    0x1B3E: "C",
    0x14C5: "D",
    0x0101: "E",
    0x0103: "F",
    0x0981: "G0",
    0x09C1: "G1",
    0x08C6: "G2",
    0x0004: "N4",
    0x0005: "N5",
    0x00A1: "N_A1",
    0x4FBD: "JMP",
    0x4EBA: "BR",
    0x4F72: "CTL",
    0x3B3B: "TERM",
}


def token_short_name(word: int, token_counts: dict[int, int]) -> str:
    if word in TOKEN_NAMES:
        return TOKEN_NAMES[word]
    if token_counts.get(word, 0) >= 2:
        return f"T{word:04x}"
    return f"${word:04x}"


def token_counts_for_region(base_words: list[int], start: int, end: int) -> dict[int, int]:
    counts: dict[int, int] = {}
    for addr in range(start, end, 2):
        word = base_words[addr // 2]
        counts[word] = counts.get(word, 0) + 1
    return counts


def print_token_block_disassembly_model(base_words: list[int]) -> None:
    print("\n== token block pseudo-disassembly")
    print("  renders the main token blocks with stable columns, startup entries, and trailer targets")
    block_stride = 0x3A
    block_words = block_stride // 2
    blocks = [
        ("P00", 0x0EA0),
        ("B01", 0x0ED4),
        ("B02", 0x0F0E),
        ("B03", 0x0F48),
        ("B04", 0x0F82),
        ("B05", 0x0FBC),
        ("B06", 0x0FF6),
        ("B07", 0x1030),
    ]
    clean_starts = [0x0ED4, 0x0F0E, 0x0F48, 0x0F82, 0x0FBC, 0x0FF6]
    clean_rows = [
        [base_words[(start + column * 2) // 2] for column in range(block_words)]
        for start in clean_starts
    ]
    stable_columns = {
        column
        for column in range(block_words)
        if len({row[column] for row in clean_rows}) == 1
    }
    token_counts = token_counts_for_region(base_words, 0x0EA0, 0x1066)
    startup_entries: dict[int, list[str]] = {}
    for call_addr, mode, target, _record_label in startup_table_call_sites(base_words):
        startup_entries.setdefault(target, []).append(f"@{call_addr:04x}:{mode}")

    print("  dictionary:")
    for word, count in sorted(token_counts.items(), key=lambda item: (-item[1], item[0]))[:24]:
        print(f"    {token_short_name(word, token_counts):<6} = {word:04x} count={count}")

    print("  block rows:")
    for label, start in blocks:
        words = [base_words[(start + column * 2) // 2] for column in range(block_words)]
        trailer_marker = words[-2]
        trailer_param = words[-1]
        markers = []
        rendered = []
        for column, word in enumerate(words):
            addr = start + column * 2
            prefix = "=" if column in stable_columns and label.startswith("B") and label != "B07" else " "
            if addr in startup_entries:
                prefix = "@"
                markers.append(f"w{column:02d}:{'/'.join(startup_entries[addr])}")
            rendered.append(f"{prefix}{token_short_name(word, token_counts):<6}")

        print(f"    {label} ${start:04x}: " + " ".join(rendered[:14]))
        print(f"         ${start + 14 * 2:04x}: " + " ".join(rendered[14:]))
        if markers:
            print(f"         entries: {', '.join(markers)}")

        if label == "P00":
            marker_addr = 0x0ED0
            param_addr = 0x0ED2
            candidate_start = marker_addr - (block_stride - 4)
        else:
            marker_addr = start + block_stride - 4
            param_addr = start + block_stride - 2
            candidate_start = start
        trailer_marker = base_words[marker_addr // 2]
        trailer_param = base_words[param_addr // 2]
        strong = token_block_trailer_candidates(base_words, candidate_start, marker_addr, param_addr)
        strong = [candidate for candidate in strong if candidate[0] >= 7]
        if strong:
            best = strong[0]
            score, mode, interp, target, param, reasons = best
            target_label = token_block_label_for_named_address(target, blocks, block_stride)
            print(
                f"         trailer {token_short_name(trailer_marker, token_counts)} {trailer_param:04x}: "
                f"{mode}/{interp} param={param:04x} -> ${target:04x} {target_label} score={score} {'/'.join(reasons)}"
            )
        else:
            print(f"         trailer {token_short_name(trailer_marker, token_counts)} {trailer_param:04x}: no strong block target")

    print("  startup-entry local traces:")
    for target, entries in sorted(startup_entries.items()):
        if not (0x0EA0 <= target <= 0x1064):
            continue
        start = max(0x0EA0, target - 8)
        end = min(0x1066, target + 16)
        parts = []
        for addr in range(start, end, 2):
            word = base_words[addr // 2]
            mark = "@" if addr == target else " "
            parts.append(f"{mark}${addr:04x}:{token_short_name(word, token_counts)}")
        print(f"    ${target:04x} {'/'.join(entries)}: " + " ".join(parts))


def token_named_sequence(base_words: list[int], start: int, words: int, token_counts: dict[int, int]) -> tuple[str, ...]:
    return tuple(
        token_short_name(base_words[(start + offset * 2) // 2], token_counts)
        for offset in range(words)
        if 0 <= start + offset * 2 < len(base_words) * 2
    )


def classify_token_entry_sequence(seq: tuple[str, ...]) -> str:
    if seq[:5] == ("A", "B", "G0", "F", "D"):
        return "entry:ABG0FD"
    if seq[:4] == ("C", "A", "B", "G1"):
        return "entry:CABG1"
    if seq[:3] == ("D", "A", "B") and len(seq) > 3 and seq[3] == "JMP":
        return "entry:DA-tail-control"
    if seq[:2] == ("D", "A"):
        return "entry:DA"
    if seq and (seq[0].startswith("$") or seq[0] in {"JMP", "BR"}):
        return "entry:tail-param/control"
    if seq[:3] == ("N5", "G0", "E"):
        return "entry:B07-finalize"
    return "entry:unknown"


def print_token_entry_motif_model(base_words: list[int]) -> None:
    print("\n== token entry motif model")
    print("  clusters startup entrypoints by short token sequence and searches the token region for matching motifs")
    token_counts = token_counts_for_region(base_words, 0x0EA0, 0x1066)
    token_start = 0x0EA0
    token_end = 0x1066
    startup_entries = sorted(
        (target, call_addr, mode)
        for call_addr, mode, target, _record_label in startup_table_call_sites(base_words)
        if token_start <= target < token_end
    )
    if not startup_entries:
        print("  no startup entries inside token region")
        return

    motif_len = 5
    motif_index: dict[tuple[str, ...], list[int]] = {}
    for addr in range(token_start, token_end - motif_len * 2 + 2, 2):
        seq = token_named_sequence(base_words, addr, motif_len, token_counts)
        motif_index.setdefault(seq, []).append(addr)

    entry_records = []
    for target, call_addr, mode in startup_entries:
        seq5 = token_named_sequence(base_words, target, motif_len, token_counts)
        seq8 = token_named_sequence(base_words, target, 8, token_counts)
        hits = motif_index.get(seq5, [])
        entry_records.append((target, call_addr, mode, seq5, seq8, hits))

    print("  startup entry motifs:")
    for target, call_addr, mode, seq5, seq8, hits in entry_records:
        hit_text = " ".join(f"${addr:04x}" for addr in hits[:10])
        extra = f" +{len(hits) - 10}" if len(hits) > 10 else ""
        print(
            f"    ${target:04x} @{call_addr:04x}:{mode:<9} "
            f"seq5={' '.join(seq5):<24} hits={len(hits):2d} {hit_text}{extra}"
        )
        print(f"      seq8={' '.join(seq8)}")

    print("  pairwise entry sequence similarity:")
    for idx, left in enumerate(entry_records):
        left_target, _left_call, _left_mode, _left_seq5, left_seq8, _left_hits = left
        for right in entry_records[idx + 1:]:
            right_target, _right_call, _right_mode, _right_seq5, right_seq8, _right_hits = right
            exact_prefix = 0
            for a, b in zip(left_seq8, right_seq8):
                if a != b:
                    break
                exact_prefix += 1
            positional = sum(1 for a, b in zip(left_seq8, right_seq8) if a == b)
            multiset = sum(min(left_seq8.count(token), right_seq8.count(token)) for token in set(left_seq8) | set(right_seq8))
            if exact_prefix or positional >= 3 or multiset >= 5:
                print(
                    f"    ${left_target:04x} <-> ${right_target:04x}: "
                    f"prefix={exact_prefix} positional={positional}/8 multiset={multiset}/8"
                )

    print("  repeated non-entry motifs near startup classes:")
    seen = set()
    for _target, _call_addr, _mode, seq5, _seq8, _hits in entry_records:
        if seq5 in seen:
            continue
        seen.add(seq5)
        hits = motif_index.get(seq5, [])
        if len(hits) <= 1:
            continue
        print(f"    motif {' '.join(seq5)}")
        for hit in hits[:12]:
            label = token_block_label_for_named_address(
                hit,
                [("P00", 0x0EA0), ("B01", 0x0ED4), ("B02", 0x0F0E), ("B03", 0x0F48),
                 ("B04", 0x0F82), ("B05", 0x0FBC), ("B06", 0x0FF6), ("B07", 0x1030)],
                0x3A,
            )
            suffix = token_named_sequence(base_words, hit, 8, token_counts)
            print(f"      ${hit:04x} {label:<18} {' '.join(suffix)}")

    print("  column entry classes:")
    for target, call_addr, mode, seq5, _seq8, _hits in entry_records:
        block_label = token_block_label_for_named_address(
            target,
            [("P00", 0x0EA0), ("B01", 0x0ED4), ("B02", 0x0F0E), ("B03", 0x0F48),
             ("B04", 0x0F82), ("B05", 0x0FBC), ("B06", 0x0FF6), ("B07", 0x1030)],
            0x3A,
        )
        cls = classify_token_entry_sequence(seq5)
        print(f"    ${target:04x} {block_label:<18} @{call_addr:04x}:{mode:<9} {cls:<24} {' '.join(seq5)}")


def print_startup_callsite_token_class_model(base_words: list[int]) -> None:
    print("\n== startup callsite token-class model")
    print("  groups all token-region alternatives by startup callsite and semantic entry class")
    token_counts = token_counts_for_region(base_words, 0x0EA0, 0x1066)
    blocks = [
        ("P00", 0x0EA0),
        ("B01", 0x0ED4),
        ("B02", 0x0F0E),
        ("B03", 0x0F48),
        ("B04", 0x0F82),
        ("B05", 0x0FBC),
        ("B06", 0x0FF6),
        ("B07", 0x1030),
    ]
    by_call: dict[int, list[tuple[str, int, str, tuple[str, ...], int]]] = {}
    motif_len = 5
    motif_index: dict[tuple[str, ...], list[int]] = {}
    for addr in range(0x0EA0, 0x1066 - motif_len * 2 + 2, 2):
        motif_index.setdefault(token_named_sequence(base_words, addr, motif_len, token_counts), []).append(addr)

    for call_addr, mode, target, _record_label in startup_table_call_sites(base_words):
        if not (0x0EA0 <= target < 0x1066):
            continue
        seq5 = token_named_sequence(base_words, target, motif_len, token_counts)
        cls = classify_token_entry_sequence(seq5)
        support = len(motif_index.get(seq5, []))
        by_call.setdefault(call_addr, []).append((mode, target, cls, seq5, support))

    if not by_call:
        print("  no startup callsites resolve into token classes")
        return

    for call_addr in sorted(by_call):
        entries = sorted(by_call[call_addr], key=lambda item: (item[2], item[1], item[0]))
        classes = sorted({entry[2] for entry in entries})
        print(f"  @${call_addr:04x}: classes={', '.join(classes)} alternatives={len(entries)}")
        for mode, target, cls, seq5, support in entries:
            block_label = token_block_label_for_named_address(target, blocks, 0x3A)
            print(
                f"    {mode:<9} -> ${target:04x} {block_label:<18} "
                f"{cls:<24} support={support:2d} {' '.join(seq5)}"
            )

    print("  callsite interpretation:")
    for call_addr in sorted(by_call):
        classes = {entry[2] for entry in by_call[call_addr]}
        if "entry:CABG1" in classes and len(classes) == 1:
            interpretation = "single repeated CABG1 entry"
        elif {"entry:ABG0FD", "entry:DA", "entry:CABG1"} & classes and len(classes) > 1:
            interpretation = "dispatch-like ambiguous operand into multiple token classes"
        elif any(cls.startswith("entry:tail") or cls == "entry:DA-tail-control" for cls in classes):
            interpretation = "tail/join control alternatives"
        else:
            interpretation = "unclassified token entry"
        print(f"    @${call_addr:04x}: {interpretation}")


def token_state_blocks() -> list[tuple[str, int]]:
    return [
        ("P00", 0x0EA0),
        ("B01", 0x0ED4),
        ("B02", 0x0F0E),
        ("B03", 0x0F48),
        ("B04", 0x0F82),
        ("B05", 0x0FBC),
        ("B06", 0x0FF6),
        ("B07", 0x1030),
    ]


def token_block_for_target(target: int) -> tuple[str, int, int] | None:
    block_stride = 0x3A
    for label, start in token_state_blocks():
        if start <= target < start + block_stride:
            return label, start, (target - start) // 2
    return None


def print_token_state_machine_graph(base_words: list[int]) -> None:
    print("\n== token state-machine graph")
    print("  turns startup entries and block trailers into a compact control-flow graph")
    block_stride = 0x3A
    block_words = block_stride // 2
    blocks = token_state_blocks()
    token_counts = token_counts_for_region(base_words, 0x0EA0, 0x1066)

    entries_by_block: dict[str, list[tuple[int, int, str, str, tuple[str, ...]]]] = {}
    for call_addr, mode, target, _record_label in startup_table_call_sites(base_words):
        block = token_block_for_target(target)
        if block is None:
            continue
        label, _start, column = block
        seq5 = token_named_sequence(base_words, target, 5, token_counts)
        cls = classify_token_entry_sequence(seq5)
        entries_by_block.setdefault(label, []).append((target, call_addr, mode, cls, seq5))

    print("  nodes and startup entrypoints:")
    for label, start in blocks:
        marker_addr = start + block_stride - 4
        param_addr = start + block_stride - 2
        marker = base_words[marker_addr // 2] if param_addr < len(base_words) * 2 else 0
        param = base_words[param_addr // 2] if param_addr < len(base_words) * 2 else 0
        print(f"    {label} ${start:04x}-${start + block_stride - 2:04x} trailer={marker:04x} {param:04x}")
        for target, call_addr, mode, cls, seq5 in sorted(entries_by_block.get(label, [])):
            column = (target - start) // 2
            to_trailer = max(0, block_words - 2 - column)
            print(
                f"      entry ${target:04x} w{column:02d} @{call_addr:04x}:{mode:<9} "
                f"{cls:<24} to_trailer={to_trailer:2d} {' '.join(seq5)}"
            )

    print("  strong trailer edges:")
    incoming: dict[str, list[str]] = {}
    for idx, (label, start) in enumerate(blocks):
        marker_addr = start + block_stride - 4
        param_addr = start + block_stride - 2
        if param_addr >= len(base_words) * 2:
            continue
        candidates = [
            candidate
            for candidate in token_block_trailer_candidates(base_words, start, marker_addr, param_addr)
            if candidate[0] >= 7
        ]
        if candidates:
            for score, mode, interp, target, param, reasons in candidates[:4]:
                target_block = token_block_for_target(target)
                target_label = target_block[0] if target_block else f"${target:04x}"
                incoming.setdefault(target_label, []).append(label)
                print(
                    f"    {label} --{mode}/{interp} param={param:04x} score={score:02d}--> "
                    f"{target_label} ${target:04x} {'/'.join(reasons)}"
                )
            continue

        if idx + 1 < len(blocks):
            next_label, next_start = blocks[idx + 1]
            incoming.setdefault(next_label, []).append(label)
            print(f"    {label} --fallthrough?--> {next_label} ${next_start:04x}")
        else:
            print(f"    {label} --terminal/unknown--> ?")

    print("  convergence:")
    for label, sources in sorted(incoming.items(), key=lambda item: (-len(set(item[1])), item[0])):
        unique_sources = sorted(set(sources))
        if len(unique_sources) < 2:
            continue
        print(f"    {label} <= {', '.join(unique_sources)}")

    print("  unresolved entry classes:")
    for label, entries in sorted(entries_by_block.items()):
        marker_addr = next(start for block_label, start in blocks if block_label == label) + block_stride - 4
        param_addr = marker_addr + 2
        strong = [
            candidate
            for candidate in token_block_trailer_candidates(base_words, marker_addr - (block_stride - 4), marker_addr, param_addr)
            if candidate[0] >= 7
        ]
        if strong:
            continue
        classes = sorted({cls for _target, _call_addr, _mode, cls, _seq5 in entries})
        print(f"    {label}: entries={len(entries)} classes={', '.join(classes)} no strong trailer edge")


def best_startup_word_at(addr: int) -> int:
    return BEST_STARTUP_PATCH.get(addr, 0)


def classify_boot_target(base_words: list[int], target: int, token_counts: dict[int, int]) -> tuple[str, str]:
    if 0x0EA0 <= target < 0x1066:
        seq5 = token_named_sequence(base_words, target, 5, token_counts)
        cls = classify_token_entry_sequence(seq5)
        return cls, " ".join(seq5)
    kind = table_pointer_kind(target)
    if kind:
        score = target_sequence_score(base_words, target) if 0 <= target < len(base_words) * 2 else -100
        return kind, f"score={score}"
    if 0 <= target < len(base_words) * 2:
        score = target_sequence_score(base_words, target)
        return "code/data-candidate", f"score={score}"
    return "outside-rom", "-"


def print_boot_flow_readiness_model(base_words: list[int]) -> None:
    print("\n== boot flow readiness model")
    print("  summarizes the best startup skeleton, token entry roles, and remaining start/render blockers")
    token_counts = token_counts_for_region(base_words, 0x0EA0, 0x1066)
    token_alt_by_call: dict[int, list[tuple[str, int, str]]] = {}
    for call_addr, mode, target, _record_label in startup_table_call_sites(base_words):
        if not (0x0EA0 <= target < 0x1066):
            continue
        cls, _detail = classify_boot_target(base_words, target, token_counts)
        token_alt_by_call.setdefault(call_addr, []).append((mode, target, cls))

    flow = []
    addr = 0x0C42
    while addr <= 0x0C9A:
        op = best_startup_word_at(addr)
        if op in (0x4EB9, 0x4EF9):
            target = (best_startup_word_at(addr + 2) << 16) | best_startup_word_at(addr + 4)
            flow.append((addr, "jsr.l", target))
            addr += 6
            continue
        if op == 0x4EB8:
            target = best_startup_word_at(addr + 2)
            flow.append((addr, "jsr.w", target))
            addr += 4
            continue
        if op == 0x2F3C:
            value = (best_startup_word_at(addr + 2) << 16) | best_startup_word_at(addr + 4)
            flow.append((addr, "push.l", value))
            addr += 6
            continue
        if op == 0x007C:
            value = best_startup_word_at(addr + 2)
            flow.append((addr, "ori-sr", value))
            addr += 4
            continue
        if op == 0x4E75:
            flow.append((addr, "rts", 0))
            addr += 2
            continue
        flow.append((addr, f"word:{op:04x}", 0))
        addr += 2

    token_calls = 0
    code_calls = 0
    weak_calls = 0
    print("  best-startup skeleton:")
    for addr, kind, value in flow:
        if kind in ("jsr.l", "jsr.w"):
            cls, detail = classify_boot_target(base_words, value, token_counts)
            alt_text = ""
            if addr in token_alt_by_call:
                alts = " ".join(f"{mode}->${target:04x}:{alt_cls}" for mode, target, alt_cls in token_alt_by_call[addr])
                alt_text = f" alternatives=[{alts}]"
            if cls.startswith("entry:"):
                token_calls += 1
            elif "code" in cls or "low" in cls or "vector" in cls:
                code_calls += 1
            if cls in {"code/data-candidate", "data/unknown"} or "score=-" in detail:
                weak_calls += 1
            print(f"    ${addr:04x}: {kind:<5} ${value:08x} -> {cls:<26} {detail}{alt_text}")
        elif kind == "push.l":
            cls, detail = classify_boot_target(base_words, value, token_counts)
            print(f"    ${addr:04x}: {kind:<5} #${value:08x} -> {cls:<26} {detail}")
        elif kind == "ori-sr":
            print(f"    ${addr:04x}: {kind:<5} #${value:04x}")
        elif kind == "rts":
            print(f"    ${addr:04x}: rts")
        else:
            print(f"    ${addr:04x}: {kind}")

    print("  readiness summary:")
    print(f"    token-class calls={token_calls} code/init-like calls={code_calls} weak_or_ambiguous={weak_calls}")
    print("    known-good structural anchors: startup skeleton, token block family, $1030 convergence, callsite classes")
    print("    remaining blockers before a useful render attempt:")
    print("      1. prove whether token entries are consumed by real 68000 code or by address/data tables")
    print("      2. resolve weak init targets around $1082/$1084, $0e32, and $0d34 without overfitting p5? words")
    print("      3. map token classes to side effects: CABG1, ABG0FD, DA, tail/join, B07-finalize")
    print("      4. only then port a minimal MAME init patch and test for VDP/register writes instead of full gameplay")


def effect_target_kind(target: int) -> str | None:
    if 0x00C00000 <= target <= 0x00C0001F:
        return "vdp"
    if 0x00FF0000 <= target <= 0x00FFFFFF:
        return "mmio"
    if 0x000D0000 <= target <= 0x000DFFFF:
        return "bank-d"
    if 0 <= target < 0x20000:
        return "rom-low"
    return None


def startup_flow_targets() -> list[tuple[int, str, int]]:
    targets = []
    addr = 0x0C42
    while addr <= 0x0C9A:
        op = best_startup_word_at(addr)
        if op in (0x4EB9, 0x4EF9):
            targets.append((addr, "call", (best_startup_word_at(addr + 2) << 16) | best_startup_word_at(addr + 4)))
            addr += 6
        elif op == 0x4EB8:
            targets.append((addr, "call.w", best_startup_word_at(addr + 2)))
            addr += 4
        elif op == 0x2F3C:
            targets.append((addr, "push", (best_startup_word_at(addr + 2) << 16) | best_startup_word_at(addr + 4)))
            addr += 6
        elif op == 0x007C:
            addr += 4
        else:
            addr += 2
    return targets


def side_effect_mode_confidence(mode: str) -> tuple[str, int]:
    if "p5" in mode:
        return "p5-hyp", -5
    if "/x1" in mode or mode.startswith("x1/"):
        return "x1-hyp", -2
    return "direct", 0


def side_effect_records_in_window(base_words: list[int], start: int, end: int) -> list[tuple[int, int, str, str, str, str]]:
    """Return modeled VDP/MMIO/RAM-ish instruction hits in a short window.

    This is not a full 68000 disassembler. It intentionally recognizes only
    effects that matter for a first render/init probe: absolute VDP/MMIO
    stores, reads/polls, base loads, and obvious control transfers.
    """
    records = []
    end = min(end, len(base_words) * 2)

    def variants(at: int) -> list[tuple[str, int]]:
        if not (0 <= at < end):
            return []
        return table_variant_sets(base_words[at // 2], at)

    for addr in range(start, end, 2):
        for op_name, op in variants(addr):
            if op in (0x33FC, 0x23FC) and addr + 8 <= end:
                size = "w" if op == 0x33FC else "l"
                for imm_name, imm in variants(addr + 2):
                    for hi_name, hi in variants(addr + 4):
                        for lo_name, lo in variants(addr + 6):
                            target = (hi << 16) | lo
                            kind = effect_target_kind(target)
                            if kind not in {"vdp", "mmio"}:
                                continue
                            mode = f"{op_name}/{imm_name}/{hi_name}/{lo_name}"
                            confidence, penalty = side_effect_mode_confidence(mode)
                            records.append((18 + penalty if kind == "vdp" else 16 + penalty, addr, kind, confidence, mode, f"move.{size} #${imm:04x},${target:08x}"))
            elif op in (0x33C0, 0x4279, 0x4A79, 0x3039) and addr + 6 <= end:
                mnemonic = {0x33C0: "move.w d0", 0x4279: "clr.w", 0x4A79: "tst.w", 0x3039: "move.w abs,d0"}[op]
                for hi_name, hi in variants(addr + 2):
                    for lo_name, lo in variants(addr + 4):
                        target = (hi << 16) | lo
                        kind = effect_target_kind(target)
                        if kind not in {"vdp", "mmio"}:
                            continue
                        mode = f"{op_name}/{hi_name}/{lo_name}"
                        verb = "poll/read" if op in (0x4A79, 0x3039) else "write"
                        confidence, penalty = side_effect_mode_confidence(mode)
                        records.append((14 + penalty if kind == "vdp" else 12 + penalty, addr, kind, confidence, mode, f"{verb}: {mnemonic} ${target:08x}"))
            elif (op & 0xF1FF) == 0x41F9 and addr + 6 <= end:
                reg = (op >> 9) & 7
                for hi_name, hi in variants(addr + 2):
                    for lo_name, lo in variants(addr + 4):
                        target = (hi << 16) | lo
                        kind = effect_target_kind(target)
                        if kind not in {"vdp", "mmio"}:
                            continue
                        mode = f"{op_name}/{hi_name}/{lo_name}"
                        confidence, penalty = side_effect_mode_confidence(mode)
                        records.append((10 + penalty, addr, kind, confidence, mode, f"lea ${target:08x},a{reg}"))
            elif op in (0x4EB9, 0x4EF9) and addr + 6 <= end:
                mnemonic = "jsr" if op == 0x4EB9 else "jmp"
                for hi_name, hi in variants(addr + 2):
                    for lo_name, lo in variants(addr + 4):
                        target = (hi << 16) | lo
                        kind = effect_target_kind(target)
                        if kind != "rom-low":
                            continue
                        mode = f"{op_name}/{hi_name}/{lo_name}"
                        score = target_sequence_score(base_words, target)
                        confidence, penalty = side_effect_mode_confidence(mode)
                        records.append((6 + max(-3, min(score, 8)) + penalty, addr, kind, confidence, mode, f"{mnemonic} ${target:08x} target_score={score}"))
            elif op == 0x4E75:
                confidence, penalty = side_effect_mode_confidence(op_name)
                records.append((3 + penalty, addr, "control", confidence, op_name, "rts"))
            elif (op & 0xF1C0) == 0x51C8:
                confidence, penalty = side_effect_mode_confidence(op_name)
                records.append((3 + penalty, addr, "control", confidence, op_name, "dbf loop"))

    deduped = []
    seen = set()
    for record in sorted(records, key=lambda item: (-item[0], item[1], item[2], item[5])):
        key = (record[1], record[2], record[3], record[5])
        if key in seen:
            continue
        seen.add(key)
        deduped.append(record)
    return deduped


def print_startup_side_effect_model(base_words: list[int]) -> None:
    print("\n== startup side-effect model")
    print("  scans startup targets and token-class alternatives for VDP/MMIO/RAM-init evidence before any render attempt")
    token_counts = token_counts_for_region(base_words, 0x0EA0, 0x1066)
    token_alts_by_call: dict[int, list[tuple[str, int, str]]] = {}
    for call_addr, mode, target, _record_label in startup_table_call_sites(base_words):
        if not (0x0EA0 <= target < 0x1066):
            continue
        seq5 = token_named_sequence(base_words, target, 5, token_counts)
        token_alts_by_call.setdefault(call_addr, []).append((mode, target, classify_token_entry_sequence(seq5)))

    targets: list[tuple[int, str, int, str]] = []
    for call_addr, kind, target in startup_flow_targets():
        targets.append((call_addr, kind, target, "best-startup"))
        for mode, alt_target, cls in token_alts_by_call.get(call_addr, []):
            if alt_target != target:
                targets.append((call_addr, "token-alt", alt_target, f"{mode}:{cls}"))

    for call_addr, kind, target, source in sorted(targets, key=lambda item: (item[0], item[2], item[3])):
        if target & 1 or not (0 <= target < len(base_words) * 2):
            print(f"  @${call_addr:04x} {kind:<9} ${target:08x} {source:<28} outside/odd")
            continue

        cls, detail = classify_boot_target(base_words, target, token_counts)
        if cls.startswith("entry:"):
            seq8 = " ".join(token_named_sequence(base_words, target, 8, token_counts))
            print(f"  @${call_addr:04x} {kind:<9} ${target:08x} {source:<28} {cls:<24} {seq8}")
            print("    effect: token/state entry; direct VDP/MMIO side effects depend on the still-unidentified consumer")
            continue

        window_end = min(len(base_words) * 2, target + 0x60)
        records = side_effect_records_in_window(base_words, target, window_end)
        direct_records = [record for record in records if record[3] == "direct"]
        display_records = direct_records[:8]
        if len(display_records) < 8:
            display_records += [record for record in records if record[3] != "direct"][: 8 - len(display_records)]
        vdp = sum(1 for _, _, effect_kind, confidence, _, _ in records if effect_kind == "vdp" and confidence == "direct")
        mmio = sum(1 for _, _, effect_kind, confidence, _, _ in records if effect_kind == "mmio" and confidence == "direct")
        control = sum(1 for _, _, effect_kind, confidence, _, _ in records if effect_kind == "control" and confidence == "direct")
        hyp = sum(1 for _, _, effect_kind, confidence, _, _ in records if effect_kind in {"vdp", "mmio"} and confidence != "direct")
        print(
            f"  @${call_addr:04x} {kind:<9} ${target:08x} {source:<28} "
            f"{cls:<18} {detail:<12} direct vdp={vdp} mmio={mmio} ctrl={control} p5/x1-hyp={hyp}"
        )
        if not records:
            print("    no modeled side-effect hits in first $60 bytes")
            continue
        for _score, addr, effect_kind, confidence, mode, text in display_records:
            print(f"    ${addr:04x}: {effect_kind:<7} {confidence:<7} {mode:<17} {text}")
        if len(records) > len(display_records):
            print(f"    ... {len(records) - len(display_records)} more modeled hits")

    print("  render-gate interpretation:")
    print("    strong VDP/MMIO targets are candidates for a minimal MAME init probe")
    print("    token-class targets are not render-ready until their consumer routine is identified")
    print("    ambiguous low-score targets should be instrumented for stable control flow, not patched blindly")


def patch_word_line(addr: int, value: int) -> str:
    return f"src[0x{addr:04x} / 2] = 0x{value:04x};"


def print_mame_render_probe_plan(base_words: list[int]) -> None:
    print("\n== MAME render-probe plan")
    print("  concrete instrumentation checklist for the first start/render experiment; does not write ROM data")
    print("  driver facts from hshavoc.cpp: ROM $000000-$1fffff, RAM $200000-$2023ff, init currently stops VDP timers")

    startup_patch = [
        addr for addr in sorted(BEST_STARTUP_PATCH)
        if 0x0C42 <= addr <= 0x0C9A
    ]
    adjusted_operands = {
        0x0C7A: (0x0E32, "$0c76 dispatch weak-code candidate"),
        0x0C86: (0x0AB8, "$0c82 nearby prologue candidate"),
        0x0C8C: (0x0AF8, "$0c88 VDP/MMIO candidate"),
        0x0C92: (0x0D32, "$0c8e immediate-rts candidate"),
    }

    print("  phase 1 patch scope:")
    print("    apply the best-startup words only, then log control flow and VDP/MMIO writes")
    print("    keep adjusted operands disabled for the first run unless the log proves the original target stalls")
    for offset in range(0, len(startup_patch), 6):
        chunk = startup_patch[offset:offset + 6]
        print("    " + " ".join(patch_word_line(addr, BEST_STARTUP_PATCH[addr]) for addr in chunk))

    print("  optional phase 2 operand adjustments:")
    for addr, (value, reason) in adjusted_operands.items():
        original = BEST_STARTUP_PATCH.get(addr, base_words[addr // 2])
        print(f"    {patch_word_line(addr, value)}  // was 0x{original:04x}; {reason}")

    print("  PC break/log points:")
    breakpoints = [
        (0x0C42, "startup entry candidate"),
        (0x0A1C, "direct VDP register init block"),
        (0x10BA, "loads VDP control-port base"),
        (0x10C0, "MMIO poll/read loop"),
        (0x0AF8, "nearby fixed entry for $0c88"),
        (0x0B0E, "direct VDP write from $0af8 path"),
        (0x101C, "CABG1 token/state entry"),
        (0x0F26, "ABG0FD token/state entry"),
        (0x0F2E, "DA token/state entry"),
        (0x1026, "tail-control token/state entry"),
        (0x102E, "tail-parameter token/state entry"),
        (0x1030, "four-source token-block convergence"),
        (0x103A, "B07 finalization token entry"),
        (0x00F8, "weak abs.w call target"),
        (0x0D34, "weak pointer/data blocker"),
    ]
    for addr, note in breakpoints:
        print(f"    pc=${addr:04x}: {note}")

    print("  expected direct effects to confirm:")
    expected_windows = [
        (0x0A1C, 0x0A70, "$0a1c VDP init"),
        (0x10A2, 0x1100, "$10a2/$10a8 VDP/MMIO setup"),
        (0x0AF8, 0x0B20, "$0af8 adjusted startup path"),
    ]
    for start, end, label in expected_windows:
        records = side_effect_records_in_window(base_words, start, end)
        direct = [
            (addr, effect_kind, mode, text)
            for _score, addr, effect_kind, confidence, mode, text in records
            if confidence == "direct" and effect_kind in {"vdp", "mmio"}
        ]
        print(f"    {label}:")
        if not direct:
            print("      no direct modeled VDP/MMIO effects")
            continue
        for addr, effect_kind, mode, text in direct[:8]:
            print(f"      ${addr:04x}: {effect_kind:<4} {mode:<17} {text}")
        if len(direct) > 8:
            print(f"      ... {len(direct) - 8} more direct effects")

    print("  decision gates:")
    print("    if $0a1c writes multiple VDP registers, video-register init is usable enough for a render probe")
    print("    if $10ba/$10c0 executes, log whether MMIO polling returns stable values or loops forever")
    print("    if execution enters token entries, stop treating them as 68000 and find the table consumer")
    print("    if execution reaches $00f8 or $0d34 before VDP writes, prioritize those as control-flow blockers")


def move_from_postincrement_role(word: int) -> str | None:
    size = (word >> 12) & 0xF
    if size not in (1, 2, 3):
        return None
    mode = (word >> 3) & 0x7
    if mode != 3:
        return None
    src_reg = word & 0x7
    dst_reg = (word >> 9) & 0x7
    suffix = {1: "b", 2: "l", 3: "w"}[size]
    return f"move.{suffix} (a{src_reg})+,d{dst_reg}"


def a_register_base_load_role(op: int, target: int) -> str | None:
    if not (0x0EA0 <= target < 0x1040) or target & 1:
        return None
    if (op & 0xF1FF) == 0x41F9:
        return f"lea ${target:04x},a{(op >> 9) & 7}"
    if (op & 0xF1FF) == 0x207C:
        return f"movea.l #${target:04x},a{(op >> 9) & 7}"
    if op == 0x4879:
        return f"pea ${target:04x}"
    return None


def table_consumer_walk_score(base_words: list[int], start: int, end: int) -> tuple[int, list[str]]:
    score = 0
    samples = []
    for addr in range(start, end, 2):
        for name, value in table_variant_sets(base_words[addr // 2], addr):
            role = move_from_postincrement_role(value)
            if role:
                score += 4
                if len(samples) < 6:
                    samples.append(f"${addr:04x}:{name}:{value:04x} {role}")
                continue
            if (value & 0xF1F8) == 0x51C8:
                score += 3
                if len(samples) < 6:
                    samples.append(f"${addr:04x}:{name}:{value:04x} dbf")
            elif value in (0x4E75, 0x4EB9, 0x4E90, 0x4ED0):
                score += 1
                if len(samples) < 6:
                    samples.append(f"${addr:04x}:{name}:{value:04x} control")
    return score, samples


def print_table_cluster_consumer_probe(base_words: list[int]) -> None:
    print("\n== $0ea0 table consumer probe")
    print("  searches low ROM for modeled A-register base loads into the token cluster and nearby postincrement walks")
    cluster_start = 0x0EA0
    cluster_end = 0x1040
    max_scan = min(0x12000, len(base_words) * 2)

    candidates = []
    for addr in range(0, max_scan - 4, 2):
        op_variants = [
            (op_name, op)
            for op_name, op in table_variant_sets(base_words[addr // 2], addr)
            if (op & 0xF1FF) in (0x41F9, 0x207C) or op == 0x4879
        ]
        if not op_variants:
            continue

        hi_variants = [
            (hi_name, hi)
            for hi_name, hi in table_variant_sets(base_words[(addr + 2) // 2], addr + 2)
            if hi == 0
        ]
        if not hi_variants:
            continue

        lo_variants = [
            (lo_name, lo)
            for lo_name, lo in table_variant_sets(base_words[(addr + 4) // 2], addr + 4)
            if cluster_start <= lo < cluster_end and not (lo & 1)
        ]
        if not lo_variants:
            continue

        for op_name, op in op_variants:
            for hi_name, hi in hi_variants:
                for lo_name, lo in lo_variants:
                    target = (hi << 16) | lo
                    role = a_register_base_load_role(op, target)
                    if not role:
                        continue
                    window_start = max(0, addr - 0x20)
                    window_end = min(len(base_words) * 2, addr + 0x60)
                    walk_score, samples = table_consumer_walk_score(base_words, window_start, window_end)
                    candidates.append((walk_score, addr, f"{op_name}/{hi_name}/{lo_name}", target, role, samples))

    candidates.sort(key=lambda item: (-item[0], item[1]))
    if candidates:
        print("  direct base-load candidates:")
        for walk_score, addr, mode, target, role, samples in candidates[:16]:
            print(f"    ${addr:04x}: {mode:<11} {role:<24} walk_score={walk_score}")
            if samples:
                print(f"      {' | '.join(samples)}")
    else:
        print("  no direct modeled lea/movea/pea into the cluster")

    call_sites = startup_table_call_sites(base_words)
    if call_sites:
        print("  walk-score around startup table entries:")
        seen_targets = set()
        for _, _, target, record_label in call_sites:
            if target in seen_targets:
                continue
            seen_targets.add(target)
            walk_score, samples = table_consumer_walk_score(base_words, target, min(target + 0x30, cluster_end))
            print(f"    ${target:04x} {record_label}: walk_score={walk_score}")
            if samples:
                print(f"      {' | '.join(samples)}")


def token_reference_context(base_words: list[int], addr: int, mode: str, target: int, startup_entries: set[int]) -> tuple[int, str]:
    score = 0
    tags = []

    def variant_values(at: int) -> set[int]:
        if not (0 <= at < len(base_words) * 2):
            return set()
        return {value for _, value in table_variant_sets(base_words[at // 2], at)}

    prev4 = variant_values(addr - 4)
    prev2 = variant_values(addr - 2)
    next2 = variant_values(addr + 2)

    if prev4 & {0x4EB9, 0x4EF9, 0x2E3F} and prev2 & {0x0000}:
        score += 20
        tags.append("absl-call/operand")
    if prev4 & {0x2F3C} and prev2 & {0x0000}:
        score += 18
        tags.append("push-long")
    if prev4 & {0x23FC, 0x33FC} and prev2 & {0x0000}:
        score += 14
        tags.append("store-imm")
    prev2_same_mode = table_variant_value(base_words, addr - 2, mode) if 0 <= addr - 2 < len(base_words) * 2 else None
    if prev2_same_mode in (0x4EB8, 0x4EF8, 0x4878):
        score += 16
        tags.append("absw-call/pea-same-mode")
    if prev4 & {0x4879} and prev2 & {0x0000}:
        score += 16
        tags.append("pea-long")
    if next2 & {0x4EB9, 0x4EB8, 0x4E75, 0x2E3F, 0x2F3C}:
        score += 4
        tags.append("near-control")
    if 0x0C42 <= addr <= 0x0C9A:
        score += 8
        tags.append("startup")
    if mode.startswith("p5"):
        score -= 2
    if target in startup_entries:
        score += 5
        tags.append("startup-entry")
    return score, ",".join(tags) if tags else "loose-word"


def print_table_cluster_indirect_reference_flow(base_words: list[int]) -> None:
    print("\n== $0ea0 token indirect reference flow")
    print("  classifies word-sized references into the token cluster as call operands, stack args, or loose table words")
    cluster_start = 0x0EA0
    cluster_end = 0x1040
    max_scan = min(0x12000, len(base_words) * 2)

    refs = []
    startup_entries = {target for _, _, target, _ in startup_table_call_sites(base_words)}
    for addr in range(0, max_scan, 2):
        for mode, value in table_variant_sets(base_words[addr // 2], addr):
            if not (cluster_start <= value < cluster_end and not (value & 1)):
                continue
            score, context = token_reference_context(base_words, addr, mode, value, startup_entries)
            refs.append((score, addr, mode, value, context, table_record_label_for_address(base_words, value)))

    refs.sort(key=lambda item: (-item[0], item[1], item[2], item[3]))
    prioritized = [item for item in refs if item[0] >= 10 or item[1] < 0x1100]
    if not prioritized:
        print(f"  no prioritized token references ({len(refs)} loose raw hits)")
        return

    print(f"  prioritized token references ({len(refs)} total word hits):")
    for score, addr, mode, target, context, label in prioritized[:40]:
        print(f"    ${addr:04x}: {mode:<4} -> ${target:04x} score={score:2d} {context:<24} {label}")
    if len(prioritized) > 40:
        print(f"    ... {len(prioritized) - 40} more prioritized")

    outside_startup = [item for item in prioritized if not (0x0C42 <= item[1] <= 0x0C9A)]
    if outside_startup:
        print("  best non-startup refs:")
        for score, addr, mode, target, context, label in outside_startup[:16]:
            print(f"    ${addr:04x}: {mode:<4} -> ${target:04x} score={score:2d} {context:<24} {label}")
    else:
        print("  no non-startup prioritized refs yet")


def startup_context_row(address: int) -> str:
    word_index = address // 2
    phase = word_index & 0xF
    state = peel4b_counter_sequence(16)[phase]
    return (
        f"idx=${word_index:04x} phase={phase:02x} typedat={TYPEDAT[phase]} "
        f"a_low={address & 0x3f:02x} 4b={state}"
    )


def print_startup_context_phase_report(base_words: list[int]) -> None:
    print("\n== startup context/phase report")
    print("  correlates chosen decode form with address phase, typedat, and PEEL4B counter state")

    mode_counts: dict[tuple[str, int], int] = {}
    for addr in sorted(BEST_STARTUP_PATCH):
        raw = base_words[addr // 2]
        chosen = BEST_STARTUP_PATCH[addr]
        variants = startup_word_variants(raw, addr)
        direct_modes = [name for name, value in variants if value == chosen]
        mode = "/".join(direct_modes) if direct_modes else "forced"
        phase = (addr // 2) & 0xF
        mode_counts[(mode, phase)] = mode_counts.get((mode, phase), 0) + 1

    print("  mode by 16-word phase:")
    for (mode, phase), count in sorted(mode_counts.items(), key=lambda item: (item[0][0], item[0][1])):
        print(f"    mode={mode:<8} phase={phase:02x} count={count}")

    print("  non-direct or adjusted words:")
    interesting = []
    for addr, chosen in sorted(BEST_STARTUP_PATCH.items()):
        raw = base_words[addr // 2]
        variants = startup_word_variants(raw, addr)
        if not any(value == chosen for _, value in variants):
            interesting.append((addr, raw, chosen, "startup-patch"))

    adjusted = {0x0C7A: 0x0E32, 0x0C86: 0x0AB8, 0x0C8C: 0x0AF8, 0x0C92: 0x0D32}
    for addr, chosen in adjusted.items():
        raw = base_words[addr // 2]
        interesting.append((addr, raw, chosen, "adjusted-target"))

    for addr, raw, chosen, reason in interesting:
        variants = startup_word_variants(raw, addr)
        delta = raw ^ chosen
        changed = [bit for bit in range(16) if (delta >> bit) & 1]
        direct = [name for name, value in variants if value == chosen]
        print(
            f"    ${addr:04x} {reason:<15} raw={raw:04x} chosen={chosen:04x} "
            f"direct={','.join(direct) if direct else '-'} bits={changed} {startup_context_row(addr)}"
        )


def print_local_decode_layer_scan(base_words: list[int]) -> None:
    print("\n== local decode-layer scan")
    print("  counts independently interesting words by transform class in each weak window chunk")
    for label, start, end in WEAK_WINDOWS:
        print(f"  {label}:")
        for chunk_start in range(start, end, 0x10):
            chunk_end = min(chunk_start + 0x10, end)
            counts = {"raw": 0, "x0": 0, "x1": 0, "p5?": 0}
            samples = []
            for addr in range(chunk_start, chunk_end, 2):
                raw = base_words[addr // 2]
                variants = weak_word_variants(raw, addr, include_peel=True)
                for name, value in variants:
                    if not interesting_word_roles(value):
                        continue
                    key = "p5?" if name.startswith("p5?") else name
                    if key in counts:
                        counts[key] += 1
                    if len(samples) < 3:
                        samples.append(f"${addr:04x}:{name}:{value:04x}")

            total = sum(counts.values())
            if total == 0:
                continue
            dominant = max(counts, key=lambda key: counts[key])
            sample_text = " ".join(samples)
            print(
                f"    ${chunk_start:04x}-${chunk_end - 2:04x}: "
                f"raw={counts['raw']} x0={counts['x0']} x1={counts['x1']} p5?={counts['p5?']} "
                f"dominant={dominant} {sample_text}"
            )


def print_startup_stop70_operand_search(base_words: list[int]) -> None:
    print("\n== startup 0x0c70 operand focus")
    op_variants = startup_word_variants(base_words[0x0C70 // 2], 0x0C70)
    hi_variants = startup_word_variants(base_words[0x0C72 // 2], 0x0C72)
    lo_variants = startup_word_variants(base_words[0x0C74 // 2], 0x0C74)
    print("  opcode variants:", " ".join(format_variant_word(name, value) for name, value in op_variants))
    print("  operand word 1:", " ".join(format_variant_word(name, value) for name, value in hi_variants))
    print("  operand word 2:", " ".join(format_variant_word(name, value) for name, value in lo_variants))

    print("  abs.l targets from raw/x0/x1:")
    for hi_name, hi in hi_variants:
        for lo_name, lo in lo_variants:
            target = (hi << 16) | lo
            print(f"    {hi_name}/{lo_name}: ${target:08x} score={score_startup_target(base_words, target)}")

    print("  PEEL5B attempt for Genesis-like abs.l target $000d0000:")
    for hi_name, hi in transformed_word_variants(base_words[0x0C72 // 2]):
        for lo_name, lo in transformed_word_variants(base_words[0x0C74 // 2]):
            pairs = [(hi, 0x000D), (lo, 0x0000)]
            changed = sorted({bit for source, target in pairs for bit in range(16) if ((source ^ target) >> bit) & 1})
            hits = search_peel5b_known_pairs(pairs, limit=1) if len(changed) <= 6 else []
            print(
                f"    {hi_name}:{hi:04x}->{0x000d:04x} {lo_name}:{lo:04x}->0000 "
                f"changed={changed} hits={len(hits)}"
            )


def print_startup_reference_comparison(base_words: list[int], reference: bytes, reference_offset: int) -> None:
    ref_words = words_from(reference[reference_offset : reference_offset + (0x0C9A - 0x0C42)])
    print(f"\n== startup comparison against reference window {reference_offset:06x}")
    print("  match is raw/x0/x1 when the reference word equals that candidate")
    for idx, expected in enumerate(ref_words):
        addr = 0x0C42 + idx * 2
        encoded = base_words[addr // 2]
        variants = transformed_word_variants(encoded)
        match = next((name for name, value in variants if value == expected), "")
        rendered = " ".join(f"{name}={value:04x}" for name, value in variants)
        print(f"  {addr:04x}: {rendered} ref={expected:04x} {match}")


def print_peel5b_known_pair_search_case(label: str, pairs: list[tuple[int, int]]) -> None:
    print(f"\n  {label}")
    for source, target in pairs:
        print(f"    {source:04x} -> {target:04x}")

    results = search_peel5b_known_pairs(pairs, limit=8)
    if not results:
        print("    no single affine mode with one six-bit bus mapping")
        return

    print("    controls are i1,i8,i9,i12,rf13; bit_order maps o14..o19/i7..i2 to word bits")
    for control, bit_order in results:
        print(f"    {control}: bits={bit_order}")


def print_peel5b_known_pair_search(deep: bool) -> None:
    """Search whether one PEEL5B mode explains known startup pairs."""
    print("\n== PEEL 5B known-pair search after extra bitswap")
    if not deep:
        print("  skipped; pass --deep-peel-search to run the exhaustive six-bit bus search")
        return

    print("  pairs are x0(source) -> expected for the first startup instruction/call")
    print_peel5b_known_pair_search_case(
        "strict first words, including opcode low-bit correction",
        [(0x007C, 0x007C), (0x0684, 0x0700), (0x4EB8, 0x4EB9), (0x0000, 0x0000)],
    )
    print_peel5b_known_pair_search_case(
        "SR immediate only, leaving opcode final-xor as separate gating",
        [(0x007C, 0x007C), (0x0684, 0x0700), (0x0000, 0x0000)],
    )
    print_peel5b_known_pair_search_case(
        "Genesis-like first call target hypothesis",
        [(0x007C, 0x007C), (0x0684, 0x0700), (0x4EB8, 0x4EB9), (0x0000, 0x0000), (0x10A2, 0x06A8)],
    )
    print_peel5b_known_pair_search_case(
        "late startup JSR operand hypothesis from Genesis window",
        [(0x00F9, 0x000D), (0x01A6, 0x0000), (0x0000, 0x000D), (0x0FA8, 0x0682)],
    )


def print_peel5b_summary() -> None:
    print("\n== PEEL 5B simple modes")
    print("  controls are i1,i8,i9,i12,rf13; outputs are o14..o19")
    for control, mapping in peel5b_control_summary():
        print(f"  {control}: {mapping}")
    print("\n== PEEL 5B affine modes with i8=1")
    print("  controls are i1,i8,i9,i12,rf13; outputs are o14..o19")
    for control, forms in peel5b_affine_summary():
        if control[1] == 1:
            print(f"  {control}: {forms}")


def print_peel4b_summary() -> None:
    print("\n== PEEL 4B registered sequence")
    print("  state is rf12,rf13,rf14,rf15,rf16 with i3 held high and i4 held low")
    for idx, state in enumerate(peel4b_counter_sequence(20)):
        print(f"  {idx:02d}: {state}")
    print(f"  derived typedat = {typedat_from_peel4b()}")
    print(f"  mame typedat    = {TYPEDAT}")


VDP_CTRL_RE = re.compile(
    r"\[VDP-CTRL-PC\] frame=(?P<frame>\d+) pc=0x(?P<pc>[0-9a-fA-F]+).* raw=0x(?P<raw>[0-9a-fA-F]{4})"
)
HSHAVOC_VDPBLK_RE = re.compile(
    r"\[HSHAVOC-VDPBLK-[^\]]+\] (?:pc=0x(?P<pc>[0-9a-fA-F]+) )?frame=(?P<frame>-?\d+) "
    r"block=0x(?P<block>[0-9a-fA-F]+) len=0x(?P<length>[0-9a-fA-F]+) "
    r"sourceWord=0x(?P<source_word>[0-9a-fA-F]+) sourceByte=0x(?P<source>[0-9a-fA-F]+) "
    r"dest=0x(?P<dest>[0-9a-fA-F]+) code=0x(?P<code>[0-9a-fA-F]+)"
)
MD_DMA_SRC_RE = re.compile(
    r"\[DMA-SRC-TRACE-START\] frame=(?P<frame>-?\d+) pc=0x(?P<pc>[0-9a-fA-F]+) "
    r"srcWord=0x(?P<source_word>[0-9a-fA-F]+) srcByte=0x(?P<source>[0-9a-fA-F]+) "
    r"region=(?P<region>[A-Za-z0-9_-]+) len=0x(?P<length>[0-9a-fA-F]+) "
    r"dest=0x(?P<dest>[0-9a-fA-F]+) code=0x(?P<code>[0-9a-fA-F]+)"
)


def decode_vdp_code(control1: int, control2: int) -> int:
    return ((control1 >> 14) & 0x03) | ((control2 >> 2) & 0x0C)


def decode_vdp_dest(control1: int, control2: int) -> int:
    return (control1 & 0x3FFF) | ((control2 & 0x0007) << 14)


def parse_vdp_log_operations(log_path: Path) -> list[dict[str, int | str]]:
    """Parse HSHavoc scanner logs and generic MD VDP control logs.

    The result is metadata-only: source/destination ranges and command sizes,
    not decoded ROM payloads.
    """

    operations: list[dict[str, int | str]] = []
    regs: dict[int, int] = {}
    pending_command: tuple[int, int, int] | None = None

    for line in log_path.read_text(errors="replace").splitlines():
        hsh = HSHAVOC_VDPBLK_RE.search(line)
        if hsh:
            pc_raw = hsh.group("pc")
            operations.append(
                {
                    "kind": "hshavoc-block",
                    "frame": int(hsh.group("frame")),
                    "pc": int(pc_raw, 16) if pc_raw else -1,
                    "length": int(hsh.group("length"), 16),
                    "source": int(hsh.group("source"), 16),
                    "dest": int(hsh.group("dest"), 16),
                    "code": int(hsh.group("code"), 16),
                    "block": int(hsh.group("block"), 16),
                }
            )
            continue

        dma = MD_DMA_SRC_RE.search(line)
        if dma:
            operations.append(
                {
                    "kind": "md-dma-src",
                    "frame": int(dma.group("frame")),
                    "pc": int(dma.group("pc"), 16),
                    "length": int(dma.group("length"), 16),
                    "source": int(dma.group("source"), 16),
                    "source_word": int(dma.group("source_word"), 16),
                    "dest": int(dma.group("dest"), 16),
                    "code": int(dma.group("code"), 16),
                    "region": dma.group("region"),
                }
            )
            continue

        ctrl = VDP_CTRL_RE.search(line)
        if not ctrl:
            continue

        frame = int(ctrl.group("frame"))
        pc = int(ctrl.group("pc"), 16)
        raw = int(ctrl.group("raw"), 16)
        if raw & 0x8000:
            regs[(raw >> 8) & 0x1F] = raw & 0xFF
            pending_command = None
            continue

        if pending_command is None:
            pending_command = (frame, pc, raw)
            continue

        first_frame, first_pc, control1 = pending_command
        pending_command = None
        control2 = raw
        code = decode_vdp_code(control1, control2)
        if (control2 & 0x0080) == 0 or code not in {0x01, 0x03, 0x05}:
            continue

        length = (regs.get(0x13, 0) & 0xFF) | ((regs.get(0x14, 0) & 0xFF) << 8)
        source_word = (
            (regs.get(0x15, 0) & 0xFF)
            | ((regs.get(0x16, 0) & 0xFF) << 8)
            | ((regs.get(0x17, 0) & 0x7F) << 16)
        )
        operations.append(
            {
                "kind": "vdp-ctrl",
                "frame": first_frame,
                "pc": first_pc,
                "length": length,
                "source": source_word << 1,
                "dest": decode_vdp_dest(control1, control2),
                "code": code,
                "control1": control1,
                "control2": control2,
            }
        )

    return operations


def vdp_dest_role(dest: int) -> str:
    if dest < 0xC000:
        return "pattern"
    if dest < 0xE000:
        return "name/sat/hscroll"
    return "name/window/high"


def vdp_source_word_variants(
    raw_words: list[int],
    base_words: list[int],
    source: int,
    length_words: int,
) -> list[tuple[str, list[int]]]:
    start = source // 2
    end = min(start + length_words, len(raw_words), len(base_words))
    base_block = base_words[start:end]
    variants: list[tuple[str, list[int]]] = [("base", base_block)]

    if source < BASE_DECODE_END:
        for phase in range(16):
            phase_block = [
                decode_data_word(raw_words[idx], TYPEDAT[(idx + phase) & 0x0F])
                for idx in range(start, end)
            ]
            variants.append((f"typedat+{phase:02x}", phase_block))

        for phase in range(16):
            phase_block = [
                decode_data_word(raw_words[idx], 1 - TYPEDAT[(idx + phase) & 0x0F])
                for idx in range(start, end)
            ]
            variants.append((f"typedat-inv+{phase:02x}", phase_block))

    transforms = [
        ("x0", lambda word, addr: bitswap(word ^ 0x0107, EXTRA_BITSWAP)),
        ("x1", lambda word, addr: bitswap(word ^ 0x0107, EXTRA_BITSWAP) ^ 0x0001),
        ("p5m", lambda word, addr: apply_peel5b_to_word(word, STRICT_PEEL5B_CONTROL, STRICT_PEEL5B_BIT_ORDER)),
        ("p5h", lambda word, addr: apply_peel5b_to_word(word, SECOND_PEEL5B_CONTROL, SECOND_PEEL5B_BIT_ORDER)),
    ]
    for name, transform in transforms:
        variants.append(
            (
                name,
                [transform(word, source + offset * 2) for offset, word in enumerate(base_block)],
            )
        )

    deduped: list[tuple[str, list[int]]] = []
    seen: set[tuple[int, ...]] = set()
    for name, block in variants:
        key = tuple(block)
        if key in seen:
            continue
        seen.add(key)
        deduped.append((name, block))
    return deduped


def score_name_table_words(words: list[int]) -> tuple[int, list[str]]:
    if not words:
        return -1000, ["empty"]

    tiles = [word & 0x07FF for word in words]
    palettes = [(word >> 13) & 0x03 for word in words]
    priorities = [(word >> 15) & 0x01 for word in words]
    unique_tiles = len(set(tiles))
    unique_words = len(set(words))
    zero_tiles = sum(1 for tile in tiles if tile == 0)
    high_tiles = sum(1 for tile in tiles if tile >= 0x700)
    low_tiles = sum(1 for tile in tiles if tile < 0x400)
    repeated_pairs = sum(1 for a, b in zip(words, words[1:]) if a == b)
    local_steps = sum(1 for a, b in zip(tiles, tiles[1:]) if abs(a - b) <= 2)
    palette_switches = sum(1 for a, b in zip(palettes, palettes[1:]) if a != b)
    priority_switches = sum(1 for a, b in zip(priorities, priorities[1:]) if a != b)

    n = len(words)
    score = 0
    score += min(unique_tiles, n // 2) * 2
    score += low_tiles
    score += local_steps // 2
    score -= high_tiles * 2
    score -= zero_tiles * 3
    score -= repeated_pairs
    score -= palette_switches
    score -= priority_switches * 2
    if unique_words <= max(2, n // 8):
        score -= 20
    if unique_tiles <= max(2, n // 8):
        score -= 20

    notes = [
        f"uniqw={unique_words}",
        f"uniqt={unique_tiles}",
        f"low={low_tiles}/{n}",
        f"high={high_tiles}/{n}",
        f"zero={zero_tiles}",
        f"local={local_steps}",
        f"rep={repeated_pairs}",
        f"palSw={palette_switches}",
    ]
    return score, notes


def score_pattern_words(words: list[int]) -> tuple[int, list[str]]:
    if not words:
        return -1000, ["empty"]

    nibbles = [((word >> shift) & 0x0F) for word in words for shift in (12, 8, 4, 0)]
    zero_nibbles = sum(1 for nibble in nibbles if nibble == 0)
    f_nibbles = sum(1 for nibble in nibbles if nibble == 0x0F)
    colors = len(set(nibbles))
    unique_words = len(set(words))
    repeated_words = sum(1 for a, b in zip(words, words[1:]) if a == b)
    score = colors * 8 + min(unique_words, len(words) // 2) - repeated_words
    if zero_nibbles == len(nibbles) or f_nibbles == len(nibbles):
        score -= 60
    if unique_words <= max(2, len(words) // 16):
        score -= 25
    notes = [
        f"uniqw={unique_words}",
        f"colors={colors}",
        f"zeroNib={zero_nibbles}/{len(nibbles)}",
        f"fNib={f_nibbles}/{len(nibbles)}",
        f"rep={repeated_words}",
    ]
    return score, notes


def score_vdp_source_words(dest: int, words: list[int]) -> tuple[int, list[str]]:
    if dest < 0xC000:
        return score_pattern_words(words)
    return score_name_table_words(words)


def print_vdp_source_transform_score_report(
    log_path: Path,
    raw_words: list[int],
    base_words: list[int],
) -> None:
    print("\n== VDP source transform score report")
    print("  heuristic: ranks alternate local decodes for ROM blocks copied into VDP")
    operations = parse_vdp_log_operations(log_path)
    seen: set[tuple[int, int, int, int]] = set()
    rom_ops: list[dict[str, int | str]] = []
    for op in operations:
        key = (int(op["source"]), int(op["dest"]), int(op["length"]), int(op["code"]))
        if key in seen:
            continue
        seen.add(key)
        source = int(op["source"])
        if 0 <= source < len(raw_words) * 2 and 0 < int(op["length"]) <= 0x4000:
            rom_ops.append(op)

    for op in rom_ops[:48]:
        source = int(op["source"])
        length_words = min(int(op["length"]), (len(raw_words) * 2 - source) // 2)
        dest = int(op["dest"])
        variants = []
        for name, words in vdp_source_word_variants(raw_words, base_words, source, length_words):
            score, notes = score_vdp_source_words(dest, words)
            variants.append((score, name, notes))
        variants.sort(key=lambda item: (-item[0], item[1]))
        base_score = next((score for score, name, _ in variants if name == "base"), None)
        best_score, best_name, best_notes = variants[0]
        delta = best_score - base_score if base_score is not None else 0
        block_text = f" block=${int(op['block']):06x}" if "block" in op else ""
        print(
            f"  src=${source:06x} len=${length_words:04x} dest=${dest:04x}{block_text} "
            f"best={best_name} score={best_score} delta={delta:+d} {' '.join(best_notes)}"
        )
        for score, name, notes in variants[:5]:
            print(f"    {name:<16} score={score:5d} {' '.join(notes)}")


def print_vdp_source_anchor_report(log_path: Path, decoded: bytes, base: Path) -> None:
    print("\n== VDP render-source anchor report")
    print(f"  log={log_path}")
    operations = parse_vdp_log_operations(log_path)
    if not operations:
        print("  no VDP DMA-like operations found")
        return

    refs = [
        (USA_REF, (base / USA_REF).read_bytes()[:0x100000]),
        (EU_REF, (base / EU_REF).read_bytes()[:0x100000]),
    ]
    seen: set[tuple[int, int, int, int]] = set()
    unique: list[dict[str, int | str]] = []
    for op in operations:
        key = (int(op["source"]), int(op["dest"]), int(op["length"]), int(op["code"]))
        if key in seen:
            continue
        seen.add(key)
        unique.append(op)

    rom_ops = [
        op for op in unique
        if 0 <= int(op["source"]) < len(decoded) and 0 < int(op["length"]) <= 0x4000
    ]
    ram_ops = [op for op in unique if 0x00FF0000 <= int(op["source"]) <= 0x00FFFFFF]

    print(f"  operations total={len(operations)} unique={len(unique)} rom={len(rom_ops)} ram={len(ram_ops)}")
    print("  unique ROM-sourced VDP operations:")
    for op in rom_ops[:48]:
        source = int(op["source"])
        length_words = int(op["length"])
        byte_len = min(length_words * 2, len(decoded) - source)
        block = decoded[source : source + byte_len]
        crc = f"{binascii.crc32(block) & 0xffffffff:08x}"
        ref_summaries = []
        for ref_name, ref in refs:
            same_offset = source + byte_len <= len(ref) and ref[source : source + byte_len] == block
            exact_pos = ref.find(block) if 0 < byte_len <= 0x400 else -1
            if same_offset:
                ref_summaries.append(f"{ref_name}:same")
            elif exact_pos >= 0:
                ref_summaries.append(f"{ref_name}:@${exact_pos:06x}")
            else:
                ref_summaries.append(f"{ref_name}:no-anchor")

        pc_text = f"pc=${int(op['pc']):06x}" if int(op["pc"]) >= 0 else "pc=?"
        block_text = f" block=${int(op['block']):06x}" if "block" in op else ""
        print(
            f"    frame={int(op['frame']):5d} {pc_text}{block_text} "
            f"src=${source:06x} len=${length_words:04x} bytes=${byte_len:04x} "
            f"dest=${int(op['dest']):04x} {vdp_dest_role(int(op['dest'])):<16} "
            f"code=${int(op['code']):02x} crc={crc} {'; '.join(ref_summaries)}"
        )
    if len(rom_ops) > 48:
        print(f"    ... {len(rom_ops) - 48} more ROM operations")

    print("  unique RAM-sourced VDP operations:")
    for op in ram_ops[:24]:
        pc_text = f"pc=${int(op['pc']):06x}" if int(op["pc"]) >= 0 else "pc=?"
        block_text = f" block=${int(op['block']):06x}" if "block" in op else ""
        region_text = f" region={op['region']}" if "region" in op else ""
        source_word_text = f" srcWord=${int(op['source_word']):06x}" if "source_word" in op else ""
        print(
            f"    frame={int(op['frame']):5d} {pc_text}{block_text} "
            f"src=${int(op['source']):06x}{source_word_text}{region_text} len=${int(op['length']):04x} "
            f"dest=${int(op['dest']):04x} {vdp_dest_role(int(op['dest'])):<16} code=${int(op['code']):02x}"
        )
    if len(ram_ops) > 24:
        print(f"    ... {len(ram_ops) - 24} more RAM operations")


def tile_has_pixels(vram: bytes, tile_index: int) -> bool:
    start = tile_index * 32
    return 0 <= start <= len(vram) - 32 and any(vram[start : start + 32])


def tile_byte_sum(vram: bytes, tile_index: int) -> int:
    start = tile_index * 32
    if start < 0 or start > len(vram) - 32:
        return 0
    return sum(vram[start : start + 32])


def plane_dimensions(scroll_h: int | None, scroll_v: int | None) -> tuple[int, int]:
    width_by_code = {0: 32, 1: 64, 3: 128}
    height_by_code = {0: 32, 1: 64, 3: 128}
    return width_by_code.get(scroll_h if scroll_h is not None else 1, 64), height_by_code.get(
        scroll_v if scroll_v is not None else 0, 32
    )


def parse_meta_hex(path: Path | None) -> dict[str, int]:
    if path is None:
        return {}
    out: dict[str, int] = {}
    for line in path.read_text(errors="replace").splitlines():
        if "=" not in line:
            continue
        key, value = line.split("=", 1)
        key = key.strip()
        value = value.strip()
        try:
            out[key] = int(value, 0)
        except ValueError:
            continue
    return out


def print_vram_snapshot_report(vram_path: Path, meta_path: Path | None) -> None:
    vram = vram_path.read_bytes()
    if len(vram) != 0x10000:
        print(f"== VRAM snapshot: {vram_path} size=${len(vram):x} (expected $10000)")
        return

    meta = parse_meta_hex(meta_path)
    plane_a = meta.get("vdp_plane_a", 0xC000)
    plane_b = meta.get("vdp_plane_b", 0xE000)
    hscroll = meta.get("vdp_hscroll", 0xD000)
    width, height = plane_dimensions(meta.get("vdp_scroll_h"), meta.get("vdp_scroll_v"))
    cell_count = width * height

    print("== VRAM snapshot")
    print(f"  vram={vram_path}")
    if meta_path:
        print(f"  meta={meta_path}")
    print(
        f"  display={meta.get('vdp_display', -1)} plane_a=${plane_a:04x} "
        f"plane_b=${plane_b:04x} hscroll=${hscroll:04x} cells={width}x{height}"
    )

    for label, base in (("Plane A", plane_a), ("Plane B", plane_b)):
        print_vram_plane_report(label, vram, base, width, cell_count)

    print_vram_pattern_report(vram)


def print_vram_plane_report(label: str, vram: bytes, base: int, width: int, cell_count: int) -> None:
    words = read_be_words(vram, base & 0xFFFF, cell_count)
    if not words:
        print(f"  {label}: base=${base:04x} unavailable")
        return

    tiles = [word & 0x07FF for word in words]
    palettes = [(word >> 13) & 0x03 for word in words]
    priorities = [(word >> 15) & 0x01 for word in words]
    flips = [((word >> 11) & 0x03) for word in words]
    nonblank_refs = sum(1 for tile in tiles if tile_has_pixels(vram, tile))
    invalid_refs = sum(1 for tile in tiles if tile * 32 > len(vram) - 32)
    zero_words = sum(1 for word in words if word == 0)
    repeated_top = collections.Counter(words).most_common(10)
    tile_top = collections.Counter(tiles).most_common(10)
    palette_hist = collections.Counter(palettes)
    priority_hist = collections.Counter(priorities)
    flip_hist = collections.Counter(flips)
    texture_score = sum(1 for tile in tiles if tile_byte_sum(vram, tile) > 0x80)

    print(
        f"  {label}: base=${base:04x} words={len(words)} unique={len(set(words))} "
        f"zero={zero_words} nonblank_tile_refs={nonblank_refs}/{len(words)} "
        f"texture_refs={texture_score}/{len(words)} invalid_refs={invalid_refs}"
    )
    print(
        f"    palettes={dict(sorted(palette_hist.items()))} "
        f"priority={dict(sorted(priority_hist.items()))} flips={dict(sorted(flip_hist.items()))}"
    )
    print("    top_words " + " ".join(f"${word:04x}x{count}" for word, count in repeated_top))
    print("    top_tiles " + " ".join(f"${tile:03x}x{count}" for tile, count in tile_top))
    print_vram_plane_rows(words, width=width)


def print_vram_plane_rows(words: list[int], width: int) -> None:
    rows = min(4, max(1, len(words) // width))
    for row in range(rows):
        cells = words[row * width : row * width + min(width, 16)]
        print(f"    row{row:02d} " + " ".join(f"{word:04x}" for word in cells))


def print_vram_pattern_report(vram: bytes) -> None:
    tiles = len(vram) // 32
    nonblank = [idx for idx in range(tiles) if tile_has_pixels(vram, idx)]
    top = sorted(((tile_byte_sum(vram, idx), idx) for idx in nonblank), reverse=True)[:12]
    if nonblank:
        ranges: list[tuple[int, int]] = []
        start = prev = nonblank[0]
        for idx in nonblank[1:]:
            if idx == prev + 1:
                prev = idx
                continue
            ranges.append((start, prev))
            start = prev = idx
        ranges.append((start, prev))
    else:
        ranges = []

    print(f"  patterns: nonblank_tiles={len(nonblank)}/{tiles}")
    print("    strongest " + " ".join(f"${idx:03x}:{score}" for score, idx in top))
    print("    ranges " + " ".join(f"${a:03x}-${b:03x}" if a != b else f"${a:03x}" for a, b in ranges[:16]))


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--base", type=Path, default=DEFAULT_DIR)
    parser.add_argument("--runs", type=int, default=20)
    parser.add_argument("--deep-peel-search", action="store_true")
    parser.add_argument("--strict-peel-search", action="store_true")
    parser.add_argument("--xref-search", action="store_true")
    parser.add_argument("--cross-runs", type=int, default=0)
    parser.add_argument("--only-cross-ref", action="store_true")
    parser.add_argument("--only-token-graph", action="store_true")
    parser.add_argument("--vdp-log", type=Path)
    parser.add_argument("--only-vdp-log", action="store_true")
    parser.add_argument("--vram-snapshot", type=Path)
    parser.add_argument("--vram-meta", type=Path)
    parser.add_argument("--only-vram-snapshot", action="store_true")
    parser.add_argument("--write-best-startup", type=Path)
    parser.add_argument("--write-adjusted-startup", type=Path)
    args = parser.parse_args()

    raw = read_arcade_rom(args.base)
    base_words = decode_base(raw)
    base_decoded = bytes_from(base_words)
    mame_words = apply_mame_extra(base_words)
    mame_decoded = bytes_from(mame_words)
    candidate_words = apply_startup_candidate(base_words)
    opcode_only_words = apply_startup_opcode_only_candidate(base_words)
    best_startup_words = apply_best_startup_candidate(base_words)
    adjusted_startup_words = apply_adjusted_startup_candidate(base_words)

    if args.write_best_startup:
        candidate = bytes_from(best_startup_words)
        args.write_best_startup.write_bytes(candidate)
        crc, sha1 = crc_sha1(candidate)
        print(
            f"wrote best-startup candidate: {args.write_best_startup} "
            f"size={len(candidate):06x} crc={crc} sha1={sha1}"
        )
    if args.write_adjusted_startup:
        candidate = bytes_from(adjusted_startup_words)
        args.write_adjusted_startup.write_bytes(candidate)
        crc, sha1 = crc_sha1(candidate)
        print(
            f"wrote adjusted-startup candidate: {args.write_adjusted_startup} "
            f"size={len(candidate):06x} crc={crc} sha1={sha1}"
        )

    def print_reference_cross_reports() -> None:
        for ref_name in (USA_REF, EU_REF):
            ref = (args.base / ref_name).read_bytes()[:0x100000]
            print_runs("base/no extra", base_decoded, ref_name, ref, args.runs)
            print_runs("current MAME extra", mame_decoded, ref_name, ref, args.runs)
            print_cross_reference_runs("base/no extra", base_decoded, ref_name, ref, args.cross_runs)
            print_cross_reference_runs("current MAME extra", mame_decoded, ref_name, ref, args.cross_runs)
            print_cross_reference_runs("best startup candidate", bytes_from(best_startup_words), ref_name, ref, args.cross_runs)
            print_cross_reference_runs("adjusted startup candidate", bytes_from(adjusted_startup_words), ref_name, ref, args.cross_runs)
            if args.cross_runs > 0:
                print_protection_region_reference_anchors("base/no extra", base_decoded, ref_name, ref)
                print_protection_region_reference_anchors("best startup candidate", bytes_from(best_startup_words), ref_name, ref)

    print("== inputs")
    for name, data in [
        ("arcade interleaved", raw),
        (USA_REF, (args.base / USA_REF).read_bytes()),
        (EU_REF, (args.base / EU_REF).read_bytes()),
    ]:
        crc, sha1 = crc_sha1(data)
        print(f"  {name}: size={len(data):06x} crc={crc} sha1={sha1}")

    if args.only_cross_ref:
        print_reference_cross_reports()
        return

    if args.only_token_graph:
        print_startup_table_entry_interpretation(base_words)
        print_token_entry_motif_model(base_words)
        print_startup_callsite_token_class_model(base_words)
        print_token_state_machine_graph(base_words)
        return

    if args.vdp_log:
        print_vdp_source_anchor_report(args.vdp_log, base_decoded, args.base)
        print_vdp_source_transform_score_report(args.vdp_log, words_from(raw), base_words)
        if args.only_vdp_log:
            return

    if args.vram_snapshot:
        print_vram_snapshot_report(args.vram_snapshot, args.vram_meta)
        if args.only_vram_snapshot:
            return

    print_startup_words("base/no extra", base_words)
    print_startup_words("current MAME extra", mame_words)
    print_startup_words("diagnostic startup candidate", candidate_words)
    print_startup_words("opcode-only final-xor candidate", opcode_only_words)
    print_startup_extra_table(base_words)
    print_startup_jsr_candidates(base_words)
    print_startup_instruction_paths(base_words)
    print_startup_target_verification(base_words)
    print_nearby_entry_candidates(best_startup_words)
    print_startup_target_adjustment_search(base_words)
    print_common_peel_target_adjustment_search(base_words, strict=args.strict_peel_search)
    print_strict_peel5b_replay(base_words)
    print_second_peel5b_hypothesis_replay(base_words)
    print_code_island_model_profiles(base_words)
    print_code_island_exact_mode_search(base_words)
    print_code_island_table_math_profiles(base_words)
    print_cross_island_table_fingerprint_search(base_words)
    print_d34_pointer_candidate_validation(base_words)
    print_table_cluster_record_model(base_words)
    print_table_cluster_record_alphabet(base_words)
    print_startup_table_entry_interpretation(base_words)
    print_table_cluster_token_stream_model(base_words)
    print_table_cluster_token_run_scan(base_words)
    print_token_block_trailer_model(base_words)
    print_token_block_column_model(base_words)
    print_token_block_trailer_param_model(base_words)
    print_token_block_trailer_confidence_model(base_words)
    print_token_marker_family_scan(base_words)
    print_upstream_marker_block_test(base_words)
    print_token_block_disassembly_model(base_words)
    print_token_entry_motif_model(base_words)
    print_startup_callsite_token_class_model(base_words)
    print_token_state_machine_graph(base_words)
    print_boot_flow_readiness_model(base_words)
    print_startup_side_effect_model(base_words)
    print_mame_render_probe_plan(base_words)
    print_table_cluster_consumer_probe(base_words)
    print_table_cluster_indirect_reference_flow(base_words)
    if args.xref_search:
        print_table_cluster_reference_scan(base_words)
    print_startup_context_phase_report(base_words)
    print_local_decode_layer_scan(base_words)
    print_weak_window_variant_hits(base_words)
    print_weak_window_sequence_scores(base_words)
    print_startup_stop70_operand_search(base_words)
    print_startup_reference_comparison(base_words, (args.base / USA_REF).read_bytes(), 0x0CE8)
    print_peel5b_known_pair_search(args.deep_peel_search)
    print_peel5b_summary()
    print_peel4b_summary()

    print_reference_cross_reports()


if __name__ == "__main__":
    main()
