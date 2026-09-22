# Mario 64: RAM dispatch and native-copy trials

Continuation from the accepted [HI/LO working-tree checkpoint](n64-mario-hilo-speed-2026-09-22.md),
including its uncommitted changes. Linux only. The reference binaries include
HI/LO support; comparing against `ba8ff476` alone would mix two optimizations.

The retained change is a small RAM-mapping fast path. Eight interleaved Mario
runs measure **3.30% greater throughput**, with all captured states and images
matching. The other four candidates below were removed after measurement.

## CPU alternatives rejected

Each trial used the original-USA Mario cartridge, live Vulkan RDP, identical
controller-read input and the same 70–90 guest-second walking/swimming interval.
Each row is a separate reference/candidate/candidate/reference series. The host
was busier than in the previous checkpoint; absolute rates across sessions are
not comparable. Existing user applications were left running.

| Trial | Reference mean seconds | Candidate mean seconds | Throughput change |
| --- | ---: | ---: | ---: |
| Native and bounded-interpreter branch-likely coverage | 29.9342 | 30.1701 | -0.78% |
| Same coverage with smaller branch checks | 30.3364 | 31.3954 | -3.37% |
| Exact 64-slot lookup for existing loop entries | 29.7391 | 29.8128 | -0.25% |

All trials matched 18 checkpoints, 18 images and final RAM, including audio,
input, positions/actions and task counts. Correctness did not justify keeping
changes without a repeatable speed improvement. All three CPU production
trials and their probe changes have been removed. The previously accepted
HI/LO source and tests were restored byte for byte, without resetting other
working-tree changes.

The branch-likely trials covered BEQL/BNEL/BLTZL/BGEZL/BLEZL/BGTZL, annulled
delay slots, full-width conditions, COUNT/RANDOM, r0 normalization, failed
memory/CU1 guards, live code replacement and cached-code reuse. The final trial
passed 2,565 CPU/JIT cases (2,233 compiled) and 2,201 bounded-interpreter cases,
plus the existing decoder and RANDOM checks. The lookup trial matched the old
recognizer in 2,004,762 comparisons, including every shortcut, single-bit
mutations, NOP's live predecessor, RAM edges and two million random words.
Their source snapshots and patches remain in the local artifact directory.

## Native copy experiment rejected

The accepted HI/LO profile contained 27,502 CPU instruction-pointer samples.
1,461 samples (5.31%) landed in native readback, largely in the scalar RDRAM
word swap and the already-vectorized hidden-memory copy. These are exclusive
CPU sample shares, not predicted wall-time savings.

The trial added an SSE2 path to `swap_words`, reversing four independent 32-bit words
per iteration. Unaligned loads/stores preserve arbitrary byte-patch alignment;
the scalar tail handles the remainder. Builds without SSE2 retain the scalar
implementation. No CPU feature beyond the x86-64 SSE2 baseline is required.

The changed routine handled initial RAM conversion, aligned interiors of CPU
write patches, and full RDRAM readback. Hidden-memory conversion, TMEM, ABI 1,
GPU waits, ownership, command ordering, full-copy size and frame publication
are unchanged. Disassembly confirms 16-byte iterations in native readback.

The C ABI suite passed 68 checks, with 36 additional patch combinations around
16/32/256-byte boundaries in all four byte lanes. The managed ABI's 13 checks
and 93 texture/ordering checks passed. The C ABI suite deliberately submits an
invalid I4 fill to verify error propagation; its validation diagnostic is
expected.

The gameplay comparison uses the same HI/LO probe for both sides and changes
only the shared library. `bench-sm64.py --reference-library OLD --library NEW`
supports this isolated comparison; omitting the new option retains the previous
single-library behavior.

The four-run comparison measured **30.2162 seconds reference versus 30.2209
seconds candidate (-0.016% throughput)**. All 18 checkpoints, images and final
RAM matched. Fewer conversion instructions did not improve the complete game,
so both the native production change and its extra ABI cases were removed.
The existing native library remains unchanged. The benchmark's optional
reference-library argument remains useful for future isolated comparisons.

## RAM mapping fast path

The same CPU profile showed `GetEntry` spending most of its samples clearing a
large stack frame before decoding peripheral aliases. Those aliases were also
tested for every ordinary RAM access. All peripheral aliases start at or above
`0x04000000`; the existing RAM fallback ends at `0x03efffff`.

`GetEntry` now returns the identical RAM descriptor before entering the large
device decoder. That decoder is kept out of line. The descriptor still uses the
live RAM array, the full existing mirrored range, the same name, offsets and
null events. Address translation, byte read/write behavior, GPU ownership,
device side effects and open-bus handling remain in their original paths.

The new `--check-byte-access REFERENCE_DLL` probe extends the existing access
fixture to byte reads/writes, including RAM mirrors, the precise fallback
boundary, peripheral aliases and register read effects. All **131,346 byte
operations** and final serialized state match the accepted core. The same
comparison passes with a watch address of zero. Existing word, opcode-fetch
and framebuffer differential checks also pass.

## Gameplay comparison

Two independent reference/candidate/candidate/reference series use the same
70–90 guest-second movement interval, generating 19.6602157261 seconds of
audio. Cores 6/7, two native workers, synchronous shader compilation and the
unchanged live Vulkan library are shared by both builds. No profiling, builds
or correctness tests run alongside these timing samples.

| Series | Run | Reference seconds | RAM fast path seconds |
| --- | --- | ---: | ---: |
| Initial | Reference 1 | 29.9209 | |
| Initial | Candidate 1 | | 28.2489 |
| Initial | Candidate 2 | | 28.4322 |
| Initial | Reference 2 | 30.0806 | |
| Confirmation | Reference 1 | 28.4631 | |
| Confirmation | Candidate 1 | | 27.1909 |
| Confirmation | Candidate 2 | | 28.8967 |
| Confirmation | Reference 2 | 28.0238 | |
| All eight runs | Mean | **29.1221** | **28.1922** |

Pooled throughput increases **3.30%**. The initial series improves 5.86%; the
confirmation improves 0.71%. The variation is material, so the first result
alone is not the claimed gain. Existing desktop applications and services
remain running; do not combine these absolute rates with the earlier HI/LO
session or add improvements arithmetically across sessions.

All **18 checkpoints, 18 frame images and final RAM match across all eight
runs**, including audio/input hashes, CPU cycles, Mario actions/positions and
graphics/audio task counts. This is a headless emulator-core comparison for
one moving Mario scene using live Vulkan RDP. It does not establish desktop
frame rate, full-game performance or 100% real time.

## Final validation

- 131,346 byte operations, 131,278 word-access operations, 262,405 opcode
  fetch operations and 48 framebuffer-write phases match the accepted
  reference. The byte comparison also passes with a trace watch at address
  zero. Comparisons include values, exceptions and serialized final state.
- Controller, audio/resampling, RSP scheduling and RDP streaming checks pass.
- Complete serialized replay states match after 50 million instructions in
  Mario, Duke Nukem, Rampage and Gauntlet, and 50,000,001 in Perfect Dark.
  These are fixed-work correctness comparisons, not speed claims for all
  five games.
- All 36 live GPU checks pass with store auditing and Vulkan validation,
  including independent oracle frames, mirrored RAM reads, byte/word/bulk
  reads, DMA, partial CPU stores and JIT memory guards.
- The core builds for netstandard2.0. Both normal and opt-in GPU Linux
  desktop Release builds finish with zero errors and 504 existing warnings.
  The normal desktop's MIPS assembly matches the tested normal probe byte
  for byte. Both shipping variants contain the RAM fast path and accepted
  HI/LO support, without controller/audio performance callbacks or JIT
  profiling counters.

The normal desktop build is in `EutherDrive.UI/bin/Release/net8.0`; the opt-in
GPU build is in `.build-tmp/n64-live-gpu/desktop` and can be started through
`./scripts/run-n64-gpu-desktop.sh`. Defaults and native library are unchanged.
The existing GPU savestate limitation remains: loading a state falls back to
software rendering. The user application was not restarted.

## Updated profile and continuation

A separate final profiling run matches all 18 gameplay checkpoints, images
and final RAM. It collected 26,654 CPU instruction-pointer samples, of which
24,441 mapped to managed code:

| Method | Exclusive sample share |
| --- | ---: |
| CPU block runner | 14.76% |
| RSP slice execution | 7.86% |
| RSP vector SIMD execution | 7.14% |
| CPU block instruction execution | 4.49% |
| RSP block completion/history | 3.49% |
| Memory-side RSP progress signature | 3.49% |
| CPU memory operand validation | 3.28% |
| Cold memory device decoder | 0.08% |

The large `GetEntry` method and its repeated device-alias checks are no
longer prominent in the profile. RAM lookup can now be inlined into callers,
so absence of a separate sample row does not mean all memory lookup work has
disappeared. These exclusive CPU sample shares are not wall-time savings or
an inclusive call tree. Native GPU bridge code accounts for 6.12% of samples;
its full-copy ownership behavior remains unchanged.

The next larger CPU experiment should investigate hot multiplication
boundaries in the JIT profile. First separate retired instructions from
elapsed guest cycles: the current block runner assumes one cycle per
instruction, while multiplication has a different latency. Preserve COUNT,
RANDOM, device deadlines, delay-slot ordering and intermediate MFC0 values.
Any HI/LO writer must also invalidate or participate in the invariant-loop
proof. Then add only the justified multiplication coverage and require the
same matched gameplay comparison. Branch-likely coverage and the 64-slot loop
lookup already failed to improve this workload; increasing coverage alone
is not enough. Selective GPU readback needs its own ownership design and
independent oracle checks before any synchronization or copy can be removed.

Later continuation: the [multiplication](n64-mario-multiply-speed-2026-09-22.md)
and [operand-guard](n64-mario-operand-guards-speed-2026-09-22.md) CPU candidates
were tested and removed after measurement. The subsequent
[RSP specialization](n64-mario-rsp-specialization-2026-09-22.md) is retained,
with 3.09% more movement throughput in eight matched runs from this RAM/HI-LO
baseline. Its final profile provides the current continuation point.

## Artifacts

`.build-tmp/n64-cpu-dispatch-2026-09-22/` contains frozen HI/LO references,
rejected CPU/native source patches, `ram/` and `ram-normal/` candidate probes,
tests, complete gameplay captures, native library hashes and build logs.
`desktop-proof.json` records the built assemblies and absence of probe hooks;
`replay-results.json` records the five complete-state digests. `ram-abba/`,
`ram-confirm/` and `ram-combined.json` retain all eight performance runs and
their pooled comparison; `summarize-ram.py` rechecks their complete captured
checkpoints, image hashes and final RAM before averaging. `ram-profile/`
contains the new profile, machine-code samples and matching gameplay capture.
`final-proof.txt` records the final artifact/oracle and preservation checks.
The original native library was preserved during testing. ROMs, captures and
runtime binaries are not versioned; user savestate slots were not modified.
