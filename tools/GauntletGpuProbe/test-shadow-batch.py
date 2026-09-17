#!/usr/bin/env python3
"""ROM-derived local fixtures only: ABI sequencing and relocated-texture updates."""
import array
import ctypes as c
import os
import pathlib
import sys

root = pathlib.Path(__file__).resolve().parents[2]
folder = pathlib.Path(sys.argv[1])
lib = c.CDLL(str(root / '.build-tmp/gauntlet-gpu-probe/libgauntlet_shadow.so'))
lib.gauntlet_shadow_create.argtypes = [c.c_char_p]
lib.gauntlet_shadow_create.restype = c.c_void_p
lib.gauntlet_shadow_destroy.argtypes = [c.c_void_p]
lib.gauntlet_shadow_error.restype = c.c_char_p
lib.gauntlet_shadow_enqueue.argtypes = [c.c_void_p] * 6 + [c.c_int]
lib.gauntlet_shadow_flush.argtypes = [c.c_void_p]
lib.gauntlet_shadow_output.argtypes = [c.c_void_p]
lib.gauntlet_shadow_output.restype = c.c_void_p
assert lib.gauntlet_shadow_abi_version() == 4
assert sys.byteorder == 'little'
os.environ['GAUNTLET_GPU_VALIDATION'] = '1'
pixels, textures = 2097152, 2097152
meta = 8 + textures + 512
initial = meta + 256


def load(index):
    words = array.array('I')
    with (folder / f'draw-{index:02}.gdr').open('rb') as f:
        words.frombytes(f.read())
    assert list(words[:6]) == [0x32524447, 2, textures, 512, 256, pixels]
    assert len(words) == initial + 2 * pixels
    return words


first, second = load(0), load(1)
assert first[initial + pixels:] == second[initial:initial + pixels], 'Non-contiguous fixtures'
assert first[7] == second[7]
colors = array.array('H', (v & 65535 for v in first[initial:initial + pixels]))
depth = array.array('H', (v >> 16 for v in first[initial:initial + pixels]))
expected = second[initial + pixels:]

# Rotate bank contents and all LOD bases equally: output must not change.
bank_mask = None
for tmu in range(2):
    p = meta + 128 + 64 * tmu
    if not second[p + 13]:
        continue
    assert bank_mask is None or bank_mask == second[p + 6]
    bank_mask = second[p + 6]
    for lod in range(9):
        second[p + 26 + 3 * lod] = (second[p + 26 + 3 * lod] + 1024) & bank_mask
assert bank_mask in (4194303, 8388607)
bank_words = (bank_mask + 1) // 4
for at in range(8, 8 + textures, bank_words):
    bank = second[at:at + bank_words]
    second[at:at + bank_words] = bank[-256:] + bank[:-256]


def pointer(words, offset=0):
    return words.buffer_info()[0] + offset * words.itemsize


def enqueue(ctx, words, reset):
    return lib.gauntlet_shadow_enqueue(ctx, pointer(words, 8), pointer(words, 8 + textures),
                                      pointer(words, meta), pointer(colors), pointer(depth), reset)


for drop in (False, True):
    if drop:
        os.environ['GAUNTLET_GPU_DROP_TEXTURE_UPDATES'] = '1'
    else:
        os.environ.pop('GAUNTLET_GPU_DROP_TEXTURE_UPDATES', None)
    ctx = lib.gauntlet_shadow_create(os.fsencode(root / '.build-tmp/gauntlet-gpu-probe/draw.spv'))
    assert ctx, lib.gauntlet_shadow_error()
    try:
        assert lib.gauntlet_shadow_flush(ctx) == -1  # empty flush
        assert enqueue(ctx, first, 0) == -1  # missing initial framebuffer
        assert enqueue(ctx, first, 1) == 0, lib.gauntlet_shadow_error()
        assert enqueue(ctx, second, 1) == -1  # do not overwrite pending draws
        assert enqueue(ctx, second, 0) == 0, lib.gauntlet_shadow_error()
        assert lib.gauntlet_shadow_flush(ctx) == 0, lib.gauntlet_shadow_error()
        result = (c.c_uint32 * pixels).from_address(lib.gauntlet_shadow_output(ctx))
        mismatches = sum(result[i] != value for i, value in enumerate(expected))
        assert (mismatches > 0) if drop else (mismatches == 0), mismatches
        print(f'ABI batch relocation dropUpdates={drop} mismatches={mismatches} PASS', flush=True)
        assert lib.gauntlet_shadow_flush(ctx) == -1
    finally:
        lib.gauntlet_shadow_destroy(ctx)
