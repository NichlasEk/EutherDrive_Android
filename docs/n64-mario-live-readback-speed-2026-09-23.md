# Mario 64: copy back only RDRAM that the GPU could have changed

Linux continuation from the accepted [CPU classification cache](n64-mario-dispatch-speed-2026-09-23.md).
The reference includes the existing uncommitted Castlevania, Mega Man and Mario
changes. Artifacts are in `.build-tmp/n64-mario-sync-2026-09-23/`.

**Retained:** selective live RDRAM readback improves the measured moving-gameplay
throughput by **4.32%** across two independent four-run series. The selected
sequence advances from **72.25% to 75.37%** of audio-derived real time on this
host. All eight runs match the same image, audio, input and game-state samples.
This does not establish full-speed Mario or desktop frame rate.

## Change and memory ownership

Previously every live CPU/GPU synchronization copied and byte-swapped all 8 MiB
of RDRAM, plus 4 MiB of hidden memory and 4 KiB of TMEM. The live CPU already
retains its own stores. RDRAM outside every possible GPU destination therefore
does not need to be copied back.

The native context now accumulates a 256-byte bitset of potentially GPU-written
4 KiB pages. Recognized triangle and rectangle commands mark both current color
and depth destinations. Adjacent marked pages are copied together. Bounds use
the full framebuffer width and 1024 rows, all color sizes, aligned word padding
and RDRAM wrap. They deliberately include depth even when its writes are disabled.
Missing target state and unclassified commands mark all pages.

The bound is inherited from the existing framebuffer-disjointness proof and
depends on the pinned, unscaled paraLLEl-RDP implementation. Its 12-bit quarter-
pixel scissor limits `update_deduced_height()` to 1024 rows; `init_tile()` and
`finish_tile()` bound framebuffer accesses by width and height. I4/I8 destinations
use byte accesses, 16-bit targets use halfwords, and 32-bit targets use words.
This proof must be audited again before changing the backend or resolution.

The optional ABI-1 `ed_n64_gpu_readback_live` export keeps the same timeline wait
and buffer validation. Its first call on a context copies all RAM; later calls
copy only marked pages. The caller must preserve its logical RDRAM and apply
every submitted CPU store to it. The existing live core already does this.
Hidden memory and TMEM remain full copies on every call.

Ordinary `ed_n64_gpu_readback` still copies everything into arbitrary snapshot
buffers. Taking a full snapshot does not consume pending live dirty pages.
The managed adapter falls back to ordinary readback with older ABI-1 libraries.
Derived page metadata is not part of the savestate; a newly restored context
starts with a full copy. No savestate format, write barrier, command order,
emulated clock, interrupt boundary or frame publication rule changes.

The CPU/RSP implementation is unchanged in the retained candidate. The frozen
MIPS assembly is byte-identical to the reference, SHA-256
`b3b543a7448e5ecbf29b54c6a5075fe602b0f504bd9fcca02c39a67ac4cb7fe1`.
The optimization is selected by memory/rendering state, with no ROM-name or
game-address special cases.

## End-to-end measurement

Each sample boots the original-USA Mario ROM and runs through guest second 90
with the same input at each Joybus read. The measured movement window is guest
seconds 70–90 and contains 19.6602157261 seconds of audio. Each series runs
reference, candidate, candidate, reference sequentially on cores 6/7 with two
GPU workers and synchronous shader compilation. Validation, auditing and
profiling are disabled for timing, and no builds or correctness tests overlap it.

Both sides use the same frozen managed probe. The reference native library lacks
the optional export and exercises full readback; the candidate uses live readback.

| Series / interval | Reference mean seconds | Candidate mean seconds | Throughput gain |
| --- | ---: | ---: | ---: |
| First movement series | 26.9896 | 25.9911 | +3.84% |
| Confirmation movement series | 27.4297 | 26.1762 | +4.79% |
| All eight movement runs | **27.2097** | **26.0837** | **+4.32%** |
| Complete run through guest second 90 | 118.8183 | 114.5114 | +3.76% |

Both candidate movement samples beat both references within each series.
Absolute timings vary with host conditions: compare the paired baseline here,
not an older percentage from a different session. These are headless full-core
measurements with live Vulkan RDP, not renderer-only timings or desktop FPS.

`readback-combined.json` verifies all eight runs against one common reference:
18 checkpoints and 18 images per run, including audio/input/RAM hashes, cycles,
controller reads, Mario position/action and graphics/audio task counts. Final
RAM SHA-256 is
`5488b023dbf86cc030b7c18daada7d5fffcbd9d8187b86ab56daf9486cb1bee7`.
Asynchronous JIT diagnostic counters are excluded. This Mario harness does not
export complete final CPU/device state; the cross-game continuations below do.

## Correctness and compatibility

- 77 dedicated readback cases compare all RDRAM, hidden memory and TMEM against
  strict CPU/GPU ordering with full readback. They exercise initial copies,
  CPU-only and same-value writes, all four color sizes and widths up to 1024,
  repeated draws without resetting targets, interleaved full snapshots,
  multiple/aliased/unaligned targets, the last scissor row, actual depth writes,
  all-page coverage, RAM wrap and a fresh restored context. I4 uses valid
  one-cycle rendering rather than hardware-invalid fill mode. Vulkan validation
  reports zero errors.
- All 666 existing texture/CPU-write ordering cases pass with full readback,
  all 666 pass with live readback, and all 666 also pass through the old-library
  fallback. Each compares complete RAM/hidden/TMEM and expected write barriers.
- The 13 managed ABI/lifetime checks pass, including finalization, concurrent
  disposal and recreation.
- All 58 live GPU checks pass with Vulkan validation and independent CPU-write
  auditing, including Mario oracle frames, GPU-overwritten JIT code, DMA,
  partial/same-value stores and completed-frame publication.
- All six GPU savestate roundtrips pass with exact complete state and resumed
  images, including resident textures, palette loads, noise/dither counters,
  unfinished frames and partial RDP commands.

- Mega Man slot 1 and Castlevania slots 2/3 each continue for 15 guest seconds
  from the existing copied saves. All three sampled checkpoints, their frames,
  final RAM and complete serialized CPU/device/GPU state match the previous
  accepted implementation. `readback-cross-game-proof.json` records the full
  state digests. These are correctness checks, not speed claims for those games.

`validate-readback.log` records the complete passing sequence. ROMs and user
savestate slots were not modified. `preservation-audit.json` confirms that the
pre-existing diffs outside this pass's files remain byte-for-byte intact and
that the rejected RSP source was restored to the starting snapshot.

## Experiments rejected in this pass

| Candidate | Movement throughput result | Decision |
| --- | ---: | --- |
| Reuse RSP code validation within one synchronous slice | First +1.66%, confirmation -0.40% | Restore reference; gain did not repeat |
| Move RSP byte validation from generated blocks to the dispatcher | +0.14% | Restore reference; no useful measured gain |
| Check sparse CPU patch ranges individually against draw targets | -1.92% | Restore reference; Mario barrier count did not fall |

Both RSP candidates passed their differential tests, but correctness alone did
not justify retaining them. The sparse-draw experiment retained all 9400 CPU-write
barriers in the complete Mario capture, while adding bookkeeping. Only live
readback remains in production source. Experimental sources/binaries and timing
series remain under the artifact directory for review; they are not shipping code.

## Profile and next work

The initial separate diagnostic attributed 1.4619 seconds of the movement window
to RAM/hidden/TMEM copying, and 2.1616 seconds to waits before CPU patches. The
final readback timeline wait itself was only 0.0130 seconds. This directed the
retained change toward unnecessary copies rather than that small final wait.
The encompassing submit timer includes its child phases and must not be added
to them. These instrumented values explain cost, not the speedup above.

The final separate diagnostic retains all 18 checkpoints/images and final RAM.
Its movement interval performs the same 586 submissions, 695,976 commands,
1561 CPU-write barriers and 72,748,057 patched bytes. Readback volume falls from
7,375,986,688 to **3,233,144,832 bytes**, a **56.17% reduction**, including the
unchanged full hidden-memory/TMEM copies. Copy time in this diagnostic is
0.6355 seconds. Its 26.9582-second wall time is not a paired timing result; use
the eight uninstrumented runs above for the measured speed improvement.
Details and proof are in `gpu-profile-summary.json` and
`readback-profile-summary.json`.

The next synchronization investigation should examine actual overlapping
CPU-write hazards and the work around them. Sparse patch bounds did not remove
any Mario barriers, so they should not be revived unchanged. CPU/RSP execution
also remains substantial; larger JIT blocks, linked dispatch and vector fusion
need fresh evidence rather than another unmeasured expansion. Reaching 100%
still needs roughly one-third more throughput in this particular sequence.

## Desktop delivery

Both the normal and opt-in GPU Release desktop builds pass with zero errors
and 505 warnings each. `delivery-audit.json` confirms that their MIPS assemblies
match the previous accepted delivery byte for byte, while both core adapters
contain the optional readback path. Performance callbacks, CPU JIT profiling
and RDP journal instrumentation are absent from the shipping assemblies.

The rebuilt shipping native library exports `ed_n64_gpu_readback_live`, contains
no phase profiler, and its `.text`, `.rodata` and `.data.rel.ro` sections match
the measured candidate byte for byte. Its whole-file hash differs because the
experimental translation unit used another source filename: only the build ID
and symbol/string tables differ. The rebuilt library also passes all 77 readback
cases with zero validation errors (`shipping-readback.log`). The shipping hash is
`7b594ea883d196e61c9ea2d457843b6eb71809c1062350375b4e7154d8ec3f07`.

The normal build is in `EutherDrive.UI/bin/Release/net8.0`; the GPU desktop is
in `.build-tmp/n64-live-gpu/desktop`. Start the latter from the repository with:

```sh
./scripts/run-n64-gpu-desktop.sh
```

Live readback is automatic within the GPU core and needs no new setting. Start
from the ROM or a GPU savestate; old software savestates still select software.
No application was launched or restarted during this pass.
