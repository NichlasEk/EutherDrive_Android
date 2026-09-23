# Resident Evil 2: black screen after skipping the movie

Skipping the movie executes a missing CPU instruction, **DMULT**, and the
game's exception handler stops the main game thread. The GPU movie-publication
fix from the previous pass is still working. This second failure happens when
the game prepares the scene after the movie.

## Evidence from slots 2 and 3

Artifacts are local under `.build-tmp/n64-re2-skip-2026-09-23/`.
The untouched copied container has SHA-256
`9123556a8d6f8aa89c8dfed331e1c483f5e32e891c1358ec29bcc6376c5ab6fe`.
The European English/French ROM has SHA-256
`52093e994c89848b17c8e6f26546d66374cf47c9b471ec15da7f322b7ee17ab8`.
No user save or ROM was modified.

Slot 3 already contains one unknown-instruction fault. Its graphics task count
remains at 23,395 during the baseline run, although system/audio work continues.
The saved thread at `0x801033e0` has exception PC `0x800422b0` and cause
`0x10000028`, whose exception code is 10 (Reserved Instruction). The OS faulted
thread pointer at `0x80012954` also points to that thread. Thus loading slot 3
restores the stopped game thread, not a point before the failure.

Loading slot 2 and pressing Start for 0.2 guest seconds after one guest second
reproduces the fault on the frozen preceding core:

```text
Unknown opcode encountered (count=1) pc=0x800422b0 op=0x0189001c
```

The instruction is `DMULT r12,r9`, following two signed-halfword loads and a
word load. The instruction table contained DMULTU but omitted DMULT entirely.
The ordinary unsupported-opcode path therefore raised RI. The new regression
also fails on the frozen old assembly with that same instruction word.

Extracted core-state hashes:

- Slot 2: `67f020192c34e4b735abb0c94a2fb6a0d64692df8adb2be59c90669c2830f5c0`.
- Slot 3: `10fefcb52de357c22f7ad2a07f88c2860c13bae47019da46bbc2f36099bde42c`.

## Fix

Add the regular DMULT decode and interpreter operation. It computes the full
signed 64-by-64-bit product, writing its upper half to HI and lower half to LO.
The existing unsigned multiplication helper supplies the product; subtracting
each negative operand's sign-extension contribution from the upper half gives
the signed result modulo 128 bits. This includes `long.MinValue * -1` without
a host overflow exception.

The opcode costs eight cycles and uses the existing instruction boundary and
delay-slot handling. The one-cycle JIT block classifier continues to reject it.
The operation and cycle count follow the
[NEC VR4300 manual](https://hack64.net/docs/VR43XX.pdf), pages 76 and 421.
There are no ROM-name, address, scene or saved-thread recovery conditions.

## Verification

`N64Probe --check-double-multiply` checks 5,905 signed/unsigned products against
an independent `BigInteger` oracle. It covers signed extremes, carries, all
source-register pairs including aliases and r0, seeded random operands,
MFHI/MFLO visibility, preserved source registers, eight-cycle dispatch and
both kernel address modes. Ten reserved-bit variants remain unsupported.
Taken and untaken branch delay slots have the expected product and nine-cycle
total, and full serialized continuation matches after restore.

Replaying the same Start input from slot 2 now completes 20 guest seconds with
zero unknown instructions. All 20 once-per-second captured images are distinct;
the street scene appears after the movie and continues animating. Graphics
tasks advance from 22,989 to 24,866. These captures demonstrate the transition,
not a real-time performance result or complete game compatibility.

The GPU gameplay-state check then starts from the resulting street-scene save.
After one guest second of warmup, uninterrupted and restored five-second runs
match exactly in CPU/device/RAM/hidden-memory/TMEM state, all five sampled
images, input and all 110,208 audio frames. Vulkan validation and GPU write
auditing are enabled for this check, with no reported validation errors.
`fix-proof.json` records candidate assembly hashes and the comparison result.

The normal and GPU Release desktop builds both pass with zero errors. The new
multiply regression also passes against the actual assemblies from each
desktop output, with hashes recorded in `delivery-proof.json`.

Existing regression checks also pass: 285 multiply-routine batch cases and
2,267 CPU block/JIT cases (2,004 compiled), including state equivalence,
instruction/event boundaries, code invalidation and asynchronous compilation
reset handling. The decoder checks cover one million opcode samples and
4,000,012 next-block cache observations.

## Trying it in the UI

Start the rebuilt GPU desktop with `./scripts/run-n64-gpu-desktop.sh`, load
**slot 2**, then skip the movie. Make a new save after the scene appears.
The existing slot 3 contains the already-stopped thread and cannot resume that
thread merely by implementing the instruction it faulted on earlier.
