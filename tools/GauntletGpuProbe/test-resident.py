#!/usr/bin/env python3
"""Two real GDR2 draws, resident color/depth, and explicit readback boundaries."""
import array
import ctypes as c
import os
import pathlib
import sys

root=pathlib.Path(__file__).resolve().parents[2]
folder=pathlib.Path(sys.argv[1])
assert sys.byteorder=='little'
os.environ['GAUNTLET_GPU_VALIDATION']='1'
lib=c.CDLL(str(root/'.build-tmp/gauntlet-gpu-probe/libgauntlet_shadow.so'))
assert lib.gauntlet_shadow_abi_version()==4
lib.gauntlet_shadow_create.argtypes=[c.c_char_p]
lib.gauntlet_shadow_create.restype=c.c_void_p
lib.gauntlet_shadow_destroy.argtypes=[c.c_void_p]
lib.gauntlet_shadow_error.restype=c.c_char_p
lib.gauntlet_shadow_resident_draw.argtypes=[c.c_void_p]*6+[c.c_int]
lib.gauntlet_shadow_dirty_draw.argtypes=[c.c_void_p]*6+[c.c_int,c.c_void_p]
lib.gauntlet_shadow_read_pixels.argtypes=[c.c_void_p]
lib.gauntlet_shadow_output.argtypes=[c.c_void_p]
lib.gauntlet_shadow_output.restype=c.c_void_p
pixels=2097152
meta=8+pixels+512
initial=meta+256

def load(index):
    words=array.array('I')
    with (folder/f'draw-{index:02}.gdr').open('rb') as f: words.frombytes(f.read())
    assert list(words[:6])==[0x32524447,2,pixels,512,256,pixels]
    assert len(words)==initial+2*pixels
    words[meta+31]=1
    return words

first,second=load(0),load(1)
assert first[initial+pixels:]==second[initial:initial+pixels]
assert first[7]==second[7]
relocate=os.environ.get('GAUNTLET_GPU_TEST_TEXTURE_RELOCATION')=='1'
drop=os.environ.get('GAUNTLET_GPU_DROP_TEXTURE_UPDATES')=='1'
if relocate:
    # Synthetic address/content relocation preserves the CPU pixel oracle.
    # This forces actual changes even when the real draw pair reuses textures.
    bank_mask=None
    for tmu in range(2):
        p=meta+128+64*tmu
        if not second[p+13]: continue
        assert bank_mask is None or bank_mask==second[p+6]
        bank_mask=second[p+6]
        for lod in range(9):
            second[p+26+3*lod]=(second[p+26+3*lod]+1024)&bank_mask
    assert bank_mask in (4194303,8388607)
    bank_words=(bank_mask+1)//4
    for at in range(8,8+pixels,bank_words):
        bank=second[at:at+bank_words]
        second[at:at+bank_words]=bank[-256:]+bank[:-256]
colors=array.array('H',(v&65535 for v in first[initial:initial+pixels]))
depth=array.array('H',(v>>16 for v in first[initial:initial+pixels]))

def pointer(a,offset=0): return a.buffer_info()[0]+offset*a.itemsize

ctx=lib.gauntlet_shadow_create(os.fsencode(root/'.build-tmp/gauntlet-gpu-probe/draw.spv'))
assert ctx,lib.gauntlet_shadow_error()
tracked=os.environ.get('EUTHERDRIVE_GAUNTDL_GPU_DIRTY_TEXTURE')=='1'
dirty=array.array('I',[1])*8192
def draw(words,reset):
    if tracked:
        return lib.gauntlet_shadow_dirty_draw(ctx,pointer(words,8),pointer(words,8+pixels),
            pointer(words,meta),pointer(colors),pointer(depth),reset,pointer(dirty))
    return lib.gauntlet_shadow_resident_draw(ctx,pointer(words,8),pointer(words,8+pixels),
        pointer(words,meta),pointer(colors),pointer(depth),reset)

def compare(words,expect_mismatch=False):
    output=(c.c_uint32*pixels).from_address(lib.gauntlet_shadow_output(ctx))
    count=sum(output[i]!=words[initial+pixels+i] for i in range(pixels))
    assert (count>0) if expect_mismatch else (count==0),count
    print(f'resident compare expectedMismatch={expect_mismatch} mismatches={count}',flush=True)

try:
    assert lib.gauntlet_shadow_read_pixels(ctx)==-1
    assert draw(first,1)==0,lib.gauntlet_shadow_error()
    if tracked and relocate:
        dirty[:]=array.array('I',[0])*8192
        os.environ['EUTHERDRIVE_GAUNTDL_GPU_VERIFY_DIRTY']='1'
        assert draw(second,0)==-1
        assert b'missed page' in lib.gauntlet_shadow_error()
        print('dirty mask omission detected PASS',flush=True)
        # Failed audit aborts before consuming the first changed page.
        dirty[:]=array.array('I',[1])*8192
        os.environ.pop('EUTHERDRIVE_GAUNTDL_GPU_VERIFY_DIRTY',None)
    assert draw(second,0)==0,lib.gauntlet_shadow_error()
    # CPU arrays have never received the first draw's result. GPU must retain it.
    assert draw(first,1)==-1
    assert b'readback before reset' in lib.gauntlet_shadow_error()
    assert lib.gauntlet_shadow_read_pixels(ctx)==0,lib.gauntlet_shadow_error()
    compare(second,relocate and drop)
    assert lib.gauntlet_shadow_read_pixels(ctx)==-1
    assert draw(first,1)==0,lib.gauntlet_shadow_error()
    assert lib.gauntlet_shadow_read_pixels(ctx)==0,lib.gauntlet_shadow_error()
    compare(first)
    print('resident two-draw chain / readback / reset / negative ordering PASS',flush=True)
finally:
    lib.gauntlet_shadow_destroy(ctx)
