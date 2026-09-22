# Reproducible Mario gameplay and N64 CPU/GPU profiling

Linux continuation from `154e84cf`, using the existing live Vulkan RDP backend.
The normal software renderer remains the default. Full real-time gameplay is
still the target, not a result established by this checkpoint.

## Repeatable gameplay

`N64Probe --bench-sm64 ROM NEW_OUTPUT [endGuestSecond=90]` boots the original
USA cartridge. A diagnostic-only callback applies input on the CPU thread
immediately before controller port 1 returns its Joybus buttons. The sequence
uses guest cycles, including Start/A through the introduction and forward/A
during movement. There is no wall-clock input poller.

The emulator stops on the first controller read at or after the target guest
time, then joins the CPU and synchronizes pending GPU work. Five-second guest
checkpoints record controller-read cycles, input hashes, complete RDRAM hashes,
audio block/rate hashes, generated audio duration, graphics/audio task counts,
Mario's action/position and completed frame images. A final RDRAM dump is also
compared. These are regression comparisons between implementations, not an
independent oracle for the game's entire hardware behavior.

Two initial complete runs matched at all 18 checkpoints, including input,
audio and all RAM bytes. Mario walks at checkpoints 70/75 and swims at 80–90.
The timing interval is **guest seconds 70–90**, excluding boot and dialogue.
This is a headless core benchmark with audio draining and checkpoint frame
capture, not an accelerated Avalonia/OpenGL desktop FPS measurement.

Build with `-p:N64PerformanceProbe=true`; both controller and audio callbacks
compile out of ordinary builds. `-p:N64CpuJitProfile=true` adds separate JIT
admission/usage counters starting at guest second 70. The timing runner rejects
that instrumented build. Native CPU sampling is also separate from timings.

```sh
dotnet build tools/N64Probe/N64Probe.csproj -c Release --no-restore -m:1 \
  -p:N64LiveGpu=true -p:N64PerformanceProbe=true -p:N64CpuJitProfile=false \
  -p:N64RdpJournalCapture=false -o .build-tmp/mario-current

python3 tools/N64Probe/bench-sm64.py \
  --reference /path/to/reference/N64Probe.dll \
  --candidate "$PWD/.build-tmp/mario-current/N64Probe.dll" \
  --rom '/path/to/Super Mario 64 USA.n64' \
  --library "$PWD/.build-tmp/n64-live-gpu/native/libeuther_n64_gpu.so" \
  --output .build-tmp/mario-comparison
```

The runner uses reference/candidate/candidate/reference, cores 6/7, .NET 8,
two native workers, synchronous shader compilation and no Vulkan validation
or store audit. It checks every recorded state and frame before accepting a
comparison. Background desktop/services stay running. Compare paired work;
absolute rates vary with host conditions. The earlier 62% number included
boot, different inputs and a different trajectory, so it is not this baseline.

## Fresh CPU-thread profile

The native sampler captured 27,868 instruction-pointer samples during movement
with the live GPU active; 26,047 mapped to managed methods. Selected exclusive
shares of these CPU-thread samples:

| Method | Share |
| --- | ---: |
| CPU block runner | 12.67% |
| RSP vector SIMD execution | 6.92% |
| RSP slice execution | 5.91% |
| GPU bridge CPU-write staging | 5.55% |
| CPU block instruction execution | 4.92% |
| RSP block completion/history | 4.82% |
| Framebuffer write bookkeeping | 3.38% |
| Memory-side RSP progress signature | 3.10% |
| RSP-side progress signature | 2.34% |

These are CPU instruction locations, not inclusive call trees or proportions
of total wall time. GPU waits and work on other threads cannot be inferred
by summing these rows. The prior software-rendering profile is not reused as
the current GPU profile.

The separate JIT profile sees 18,250 attempted PCs in the movement window.
There are about 9.91 million JIT attempts and 8.22 million compiled guest
instructions. Its counters distinguish short/unsupported blocks, native cache
capacity and busy compiler rejection. The original code permanently refuses
new address entries after its secondary dictionary reaches 8,192 entries,
and never replaces any of its 128 native versions until reset.

## Sparse GPU write staging

The bridge previously scanned all 2,048 RAM pages whenever any CPU writes
needed staging. Two levels of bit masks now enumerate just the dirty pages,
in the same ascending address order. Exact byte masks, same-value writes,
native records, command ordering, readback and synchronization are unchanged.

| Run | Wall seconds for matched movement interval | Audio/wall |
| --- | ---: | ---: |
| Reference 1 | 27.9931 | 70.23% |
| Sparse pages 1 | 27.4531 | 71.61% |
| Sparse pages 2 | 27.2144 | 72.24% |
| Reference 2 | 28.4855 | 69.02% |

Mean elapsed time falls from 28.2393 to 27.3337 seconds: **3.31% greater
throughput** for the same 19.6602 seconds of generated audio. All 18 checkpoints
and frame images match in all four runs. All 46 live GPU checks pass with
independent CPU-store auditing and Khronos synchronization validation,
including the 20 independent Angrylion checkpoints and two new rounds of
sparse writes to every RAM page, in reverse order, through the last RAM byte.

## CPU cache admission

The accepted change retains the 128-version native limit but allows sufficiently
inactive versions to retire. Address entries share a holder, so retirement
releases its executable delegate even when old address entries remain cached.
Successful execution refreshes its guest-cycle timestamp; the compiler worker
still only publishes code built from copied instruction words. A pending
worker cannot have its holder retired. Newly encountered addresses can use
the bounded direct cache after the secondary dictionary fills. Pending code
is used as soon as its worker publishes it.

The cache-specific checks pass: active versions survive, inactive code makes
room without exceeding the cap, retired executable references are cleared,
late addresses compile and reset removes derived state. The existing 1,993
CPU/JIT differential cases also pass, including stale code, worker/reset
lifetime, complete serialized state, history and event boundaries.

The second independent comparison keeps sparse GPU page staging in both builds:

| Run | Wall seconds for matched movement interval | Audio/wall |
| --- | ---: | ---: |
| Original JIT admission 1 | 27.8143 | 70.68% |
| Adaptive JIT admission 1 | 26.4689 | 74.28% |
| Adaptive JIT admission 2 | 26.6684 | 73.72% |
| Original JIT admission 2 | 27.0867 | 72.58% |

Mean elapsed time falls from 27.4505 to 26.5686 seconds: **3.32% greater
throughput** in this interval. All checkpoints, images and final RAM match.
This is a second paired improvement, not a measured aggregate percentage
against the earlier software-rendering or wall-clock-input runs.

Normal-build regression checks also pass for cache lifetime, controller ports,
audio, RSP scheduling and 197 RDP streaming cases. Complete serialized CPU,
device and RAM state matches the previously accepted reference after
50,000,000 instructions in Mario, Duke, Rampage and Gauntlet, and 50,000,001
in Perfect Dark. These are correctness replays, not performance samples.

The final live 90-guest-second run with independent store auditing and Khronos
synchronization validation matches all 18 gameplay checkpoints and images,
including final RAM and the complete audio hash. It reports 2,618,046 RDP
commands, 2,469 synchronizations, 2,468 completed frames, zero read hazards and
zero validation errors. Audit/validation overhead and concurrent correctness
work exclude that run from performance measurements.

Both ordinary and opt-in GPU Linux desktop Release builds succeed. The normal
desktop's CPU assembly matches the tested normal probe byte for byte, and
contains no controller/audio probe callbacks, JIT profiling members or GPU
memory hooks. Existing savestate formats and user slots are unchanged.

## Next targets

Full RDRAM/hidden/TMEM readback remains unchanged. Selective readback still
needs explicit ownership validation. The CPU profile also identifies costly
RSP block/history completion and progress-signature work, which now deserve
isolated experiments. On the CPU side, inspect rejected hot instruction
sequences around multiply/MFLO and branch-likely boundaries before expanding
JIT coverage; preserve their latency, delay-slot and exception behavior.

The two retained changes improve the measured moving scene, but do not reach
100% real time or establish equivalent speed in other games. Keep measuring
the same controller-read sequence, and profile the updated build before
choosing another broad optimization.

## Artifacts

`.build-tmp/n64-deterministic-2026-09-22/` contains `baseline-profile/profile.json`
with machine-code samples, `jit-profile-run/jit-profile.json`, the matched
baseline runs, `pages-abba/` and `admission-abba/`, build/test logs and frame/RAM
artifacts. ROMs, runtime binaries and these captures are not versioned.
