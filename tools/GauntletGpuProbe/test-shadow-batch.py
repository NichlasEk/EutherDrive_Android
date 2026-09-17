#!/usr/bin/env python3
"""ROM-derived local fixtures only: ABI sequencing and relocated-texture updates."""
import array
import ctypes as c
import itertools
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
lib.gauntlet_shadow_dirty_enqueue.argtypes = [c.c_void_p] * 6 + [c.c_int, c.c_void_p]
lib.gauntlet_shadow_flush.argtypes = [c.c_void_p]
lib.gauntlet_shadow_flush_keep.argtypes = [c.c_void_p]
lib.gauntlet_shadow_read_pixels.argtypes = [c.c_void_p]
lib.gauntlet_shadow_output.argtypes = [c.c_void_p]
lib.gauntlet_shadow_output.restype = c.c_void_p
assert lib.gauntlet_shadow_abi_version() == 4
assert lib.gauntlet_shadow_batch_stats_version() == 1
assert lib.gauntlet_shadow_batch_resident_version() == 1
lib.gauntlet_shadow_draw.argtypes = [c.c_void_p] * 6 + [c.c_int]
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
    if dirty_mode:
        return lib.gauntlet_shadow_dirty_enqueue(ctx, pointer(words,8), pointer(words,8+textures),
            pointer(words,meta), pointer(colors), pointer(depth), reset, pointer(dirty_pages))
    return lib.gauntlet_shadow_enqueue(ctx, pointer(words, 8), pointer(words, 8 + textures),
                                      pointer(words, meta), pointer(colors), pointer(depth), reset)


# Independent per-draw dispatches provide an oracle for slot isolation/order.
# CPU-derived fixture pixels remain the independent final-image oracle.
first[meta+31] = second[meta+31] = 1
os.environ.pop('GAUNTLET_GPU_DROP_TEXTURE_UPDATES', None)
os.environ.pop('EUTHERDRIVE_GAUNTDL_GPU_BBOX', None)  # full-surface reference
ctx = lib.gauntlet_shadow_create(os.fsencode(root / '.build-tmp/gauntlet-gpu-probe/draw.spv'))
assert ctx, lib.gauntlet_shadow_error()
statistics = []
try:
    for index, words in enumerate((first, second)):
        assert lib.gauntlet_shadow_draw(ctx, pointer(words,8), pointer(words,8+textures),
            pointer(words,meta), pointer(colors), pointer(depth), int(index==0)) == 0, lib.gauntlet_shadow_error()
        result = (c.c_uint32*(pixels+16)).from_address(lib.gauntlet_shadow_output(ctx))
        statistics.append(list(result[pixels:pixels+16]))
    assert statistics[0] != statistics[1], 'Fixtures must distinguish statistics slots'
finally:
    lib.gauntlet_shadow_destroy(ctx)

# Audit both sides of dirty-group boundaries, including the final texture page.
# Non-boolean flags must remain dirty; two high bits must not cancel by summing.
os.environ['EUTHERDRIVE_GAUNTDL_GPU_SPARSE_SNAPSHOT'] = '1'
os.environ['EUTHERDRIVE_GAUNTDL_GPU_VERIFY_DIRTY'] = '1'
os.environ.pop('GAUNTLET_GPU_DROP_TEXTURE_UPDATES', None)
dirty_mode = True
for page in (0, 15, 16, 8191):
    changed = array.array('I', first)
    changed[8+page*256+255] ^= 1
    changed[8+textures] ^= 1
    changed[8+textures+511] ^= 1  # NCC is not represented by dirty flags.
    dirty_pages = array.array('I', [0]) * 8192
    dirty_pages[page] = dirty_pages[page ^ 1] = 0x80000000
    ctx = lib.gauntlet_shadow_create(os.fsencode(root / '.build-tmp/gauntlet-gpu-probe/draw.spv'))
    assert ctx, lib.gauntlet_shadow_error()
    try:
        assert enqueue(ctx, first, 1) == 0, lib.gauntlet_shadow_error()
        assert enqueue(ctx, changed, 0) == 0, lib.gauntlet_shadow_error()
        dirty_pages = array.array('I', [0]) * 8192
        assert enqueue(ctx, changed, 0) == 0, lib.gauntlet_shadow_error()
        assert enqueue(ctx, first, 0) == -1
        assert b'Dirty batch tracker missed page' in lib.gauntlet_shadow_error()
    finally:
        lib.gauntlet_shadow_destroy(ctx)
print('dirty group boundaries / high-bit flags / NCC / missed-page audit PASS', flush=True)

# A same-texture epoch must actually fuse and preserve overlapping pixel order
# and every statistics slot. The old dispatch path is the differential oracle.
saved_tile = os.environ.get('EUTHERDRIVE_GAUNTDL_GPU_TILE_BATCH')
variant = array.array('I', first)
variant[meta+17] //= 2
variant[meta+18] ^= 0xffff
flipped = array.array('I', variant)
flipped[meta+30] = 2047 if first[meta+30] == 0xffffffff else 0xffffffff
clipped_left, clipped_right = array.array('I', first), array.array('I', variant)
assert first[meta+2] > 32 and first[meta+3] > 16, 'Fixture must span multiple tiles'
clipped_left[meta+2], clipped_left[meta+3] = 17, 9
clipped_right[meta] += first[meta+2]-17
clipped_right[meta+1] += first[meta+3]-9
clipped_right[meta+2], clipped_right[meta+3] = 17, 9
for sequence in ((first, variant, first), (first, variant)*64, (first, variant, flipped, flipped),
                 (clipped_left, clipped_right, clipped_left)):
    tile_outputs = []
    for tile_mode in ('0', '1'):
        os.environ['EUTHERDRIVE_GAUNTDL_GPU_TILE_BATCH'] = tile_mode
        ctx = lib.gauntlet_shadow_create(os.fsencode(root / '.build-tmp/gauntlet-gpu-probe/draw.spv'))
        assert ctx, lib.gauntlet_shadow_error()
        try:
            for index, words in enumerate(sequence):
                assert enqueue(ctx, words, int(index == 0)) == 0, lib.gauntlet_shadow_error()
            assert lib.gauntlet_shadow_flush(ctx) == 0, lib.gauntlet_shadow_error()
            tile_outputs.append(c.string_at(lib.gauntlet_shadow_output(ctx), (pixels+128*16)*4))
        finally:
            lib.gauntlet_shadow_destroy(ctx)
    assert tile_outputs[0] == tile_outputs[1], 'Tile fusion pixel/statistics mismatch'
if saved_tile is None:
    os.environ.pop('EUTHERDRIVE_GAUNTDL_GPU_TILE_BATCH', None)
else:
    os.environ['EUTHERDRIVE_GAUNTDL_GPU_TILE_BATCH'] = saved_tile
print('tile epoch overlapping draws / 128 slots / origin boundary / sparse edge tiles differential PASS', flush=True)

# Retain output and texture state across two separately submitted batches.
# The relocated second draw must patch texture on continuation draw zero.
dirty_pages = array.array('I', [1]) * 8192
os.environ.pop('GAUNTLET_GPU_DROP_TEXTURE_UPDATES', None)
os.environ['EUTHERDRIVE_GAUNTDL_GPU_BBOX'] = '1'
os.environ['EUTHERDRIVE_GAUNTDL_GPU_SPARSE_SNAPSHOT'] = '1'
os.environ['EUTHERDRIVE_GAUNTDL_GPU_VERIFY_DIRTY'] = '1'
for dirty_mode in (False, True):
    ctx = lib.gauntlet_shadow_create(os.fsencode(root / '.build-tmp/gauntlet-gpu-probe/draw.spv'))
    assert ctx, lib.gauntlet_shadow_error()
    try:
        assert enqueue(ctx, first, -1) == -1
        assert enqueue(ctx, first, 3) == -1
        assert enqueue(ctx, first, 2) == -1  # no retained batch
        assert enqueue(ctx, first, 1) == 0, lib.gauntlet_shadow_error()
        assert lib.gauntlet_shadow_flush_keep(ctx) == 0, lib.gauntlet_shadow_error()
        assert enqueue(ctx, second, 1) == -1  # cannot discard resident pixels
        assert enqueue(ctx, second, 2) == 0, lib.gauntlet_shadow_error()
        assert lib.gauntlet_shadow_read_pixels(ctx) == -1  # queued draw not executed
        assert lib.gauntlet_shadow_flush(ctx) == 0, lib.gauntlet_shadow_error()
        result = (c.c_uint32*(pixels+128*16)).from_address(lib.gauntlet_shadow_output(ctx))
        assert list(result[:pixels]) == list(expected)
        assert list(result[pixels:pixels+16]) == statistics[1]
        assert not any(result[pixels+16:])
        assert lib.gauntlet_shadow_read_pixels(ctx) == -1  # full flush already read pixels
        # Boundary/reset immediately after a keep flush, with no next draw.
        assert enqueue(ctx, first, 1) == 0, lib.gauntlet_shadow_error()
        assert lib.gauntlet_shadow_flush_keep(ctx) == 0, lib.gauntlet_shadow_error()
        assert lib.gauntlet_shadow_read_pixels(ctx) == 0, lib.gauntlet_shadow_error()
        assert list(result[:pixels]) == list(first[initial+pixels:])
        assert enqueue(ctx, second, 2) == -1  # continuation was invalidated by CPU read
        assert enqueue(ctx, first, 1) == 0, lib.gauntlet_shadow_error()
        assert lib.gauntlet_shadow_flush(ctx) == 0, lib.gauntlet_shadow_error()
        assert list(result[:pixels]) == list(first[initial+pixels:])
        print(f'resident batch dirty={dirty_mode} continuation/relocation/counters/drain/reset PASS', flush=True)
    finally:
        lib.gauntlet_shadow_destroy(ctx)

# Negative control specifically for patches on continuation draw zero:
# without the full dirty audit, a falsely clean mask must corrupt the image.
dirty_mode = True
dirty_pages = array.array('I', [0]) * 8192
os.environ['EUTHERDRIVE_GAUNTDL_GPU_VERIFY_DIRTY'] = '0'
ctx = lib.gauntlet_shadow_create(os.fsencode(root / '.build-tmp/gauntlet-gpu-probe/draw.spv'))
assert ctx, lib.gauntlet_shadow_error()
try:
    assert enqueue(ctx, first, 1) == 0, lib.gauntlet_shadow_error()
    assert lib.gauntlet_shadow_flush_keep(ctx) == 0, lib.gauntlet_shadow_error()
    assert enqueue(ctx, second, 2) == 0, lib.gauntlet_shadow_error()
    assert lib.gauntlet_shadow_flush(ctx) == 0, lib.gauntlet_shadow_error()
    result = (c.c_uint32*pixels).from_address(lib.gauntlet_shadow_output(ctx))
    mismatches = sum(result[i] != value for i, value in enumerate(expected))
    assert mismatches > 0
    print(f'resident continuation missed dirty patch negative control mismatches={mismatches} PASS', flush=True)
finally:
    lib.gauntlet_shadow_destroy(ctx)

dirty_pages = array.array('I', [1]) * 8192
for bbox, drop, dirty_mode in itertools.product((False, True), repeat=3):
    os.environ['EUTHERDRIVE_GAUNTDL_GPU_BBOX'] = '1' if bbox else '0'
    os.environ['EUTHERDRIVE_GAUNTDL_GPU_SPARSE_SNAPSHOT'] = '1' if dirty_mode else '0'
    os.environ['EUTHERDRIVE_GAUNTDL_GPU_VERIFY_DIRTY'] = '1'
    if drop:
        os.environ['GAUNTLET_GPU_DROP_TEXTURE_UPDATES'] = '1'
    else:
        os.environ.pop('GAUNTLET_GPU_DROP_TEXTURE_UPDATES', None)
    ctx = lib.gauntlet_shadow_create(os.fsencode(root / '.build-tmp/gauntlet-gpu-probe/draw.spv'))
    assert ctx, lib.gauntlet_shadow_error()
    try:
        assert lib.gauntlet_shadow_flush(ctx) == -1  # empty flush
        assert enqueue(ctx, first, 0) == -1  # missing initial framebuffer
        first[meta+119] = 16
        assert enqueue(ctx, first, 1) == -1  # caller cannot select an output slot
        first[meta+119] = 0
        assert enqueue(ctx, first, 1) == 0, lib.gauntlet_shadow_error()
        assert enqueue(ctx, second, 1) == -1  # do not overwrite pending draws
        assert enqueue(ctx, second, 0) == 0, lib.gauntlet_shadow_error()
        assert lib.gauntlet_shadow_flush(ctx) == 0, lib.gauntlet_shadow_error()
        result = (c.c_uint32 * pixels).from_address(lib.gauntlet_shadow_output(ctx))
        mismatches = sum(result[i] != value for i, value in enumerate(expected))
        assert (mismatches > 0) if drop else (mismatches == 0), mismatches
        if not drop:
            counters = (c.c_uint32*(128*16)).from_address(lib.gauntlet_shadow_output(ctx)+pixels*4)
            for index, expected_stats in enumerate(statistics):
                assert list(counters[index*16:(index+1)*16]) == expected_stats, index
            assert all(value == 0 for value in counters[32:]), 'Unused slots must be cleared'
            assert list(counters[:16]) != statistics[1], 'Swapped per-draw oracle must fail'
            print('batch per-draw statistics / slot isolation / reserved offset PASS', flush=True)
        print(f'ABI batch relocation bbox={bbox} dirty={dirty_mode} dropUpdates={drop} mismatches={mismatches} PASS', flush=True)
        assert lib.gauntlet_shadow_flush(ctx) == -1
        if not drop:
            # A new, shorter batch must not expose previous slot 1 results.
            assert enqueue(ctx, first, 1) == 0, lib.gauntlet_shadow_error()
            assert lib.gauntlet_shadow_flush(ctx) == 0, lib.gauntlet_shadow_error()
            counters = (c.c_uint32*(128*16)).from_address(lib.gauntlet_shadow_output(ctx)+pixels*4)
            assert list(counters[:16]) == statistics[0]
            assert all(value == 0 for value in counters[16:])
            first[meta+31] = 0  # legacy no-statistics batch remains supported
            assert enqueue(ctx, first, 1) == 0, lib.gauntlet_shadow_error()
            assert lib.gauntlet_shadow_flush(ctx) == 0, lib.gauntlet_shadow_error()
            assert all(value == 0 for value in counters)
            first[meta+31] = 1
            print('batch reset clears stale slots / legacy no-statistics PASS', flush=True)
    finally:
        lib.gauntlet_shadow_destroy(ctx)

# A falsely clean mask must fail the independent full-page audit. A truly
# unchanged second draw may skip every texture page but still reads NCC pages.
dirty_mode = True
os.environ['EUTHERDRIVE_GAUNTDL_GPU_SPARSE_SNAPSHOT'] = '1'
os.environ.pop('GAUNTLET_GPU_DROP_TEXTURE_UPDATES', None)
dirty_pages = array.array('I', [0]) * 8192
ctx = lib.gauntlet_shadow_create(os.fsencode(root / '.build-tmp/gauntlet-gpu-probe/draw.spv'))
assert ctx, lib.gauntlet_shadow_error()
try:
    assert enqueue(ctx, first, 1) == 0, lib.gauntlet_shadow_error()
    assert enqueue(ctx, first, 0) == 0, lib.gauntlet_shadow_error()
    assert enqueue(ctx, second, 0) == -1
    assert b'Dirty batch tracker missed page' in lib.gauntlet_shadow_error()
    print('dirty batch unchanged-page skip / missed-page detection PASS', flush=True)
finally:
    lib.gauntlet_shadow_destroy(ctx)
