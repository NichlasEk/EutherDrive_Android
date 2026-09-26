# Gauntlet Legends (Europe) slot 1: GPU thread registration

The September 24 UI slot 1 state was copied out of the EUTHSTAT container for
read-only Linux benchmarks. The original state is
`/home/nichlas/roms/N64/Gauntlet_Legends__Europe_.z64_344a04c9.euthstate`
(SHA-256 `67bcda75a7b1d7ce8b1121e0fa3981b9f4b2c30cc55ebf6bc3c89af68555c229`).
The extracted core state is
`.build-tmp/n64-gauntlet-legends-2026-09-24/slot1-core.bin`
(SHA-256 `6fb857e9ae0ff0c2e5784c1fdfe897b62110b7f21684c0a328f02bcff4b09ade`).
The ROM hash is `344a04c993618acdbf01f8bc7283874d77ebcc78c534a49f3ac2e38ca61e38a9`.

On the baseline, Granite logged 412,834 `Thread does not exist in thread
manager or is not the main thread` errors during ten guest seconds. The Vulkan
device registers the construction thread as index zero, but the .NET emulator
can later call the native API from another thread. The native ABI already
serializes calls with `api_mutex`; binding the per-instance Granite context now
also registers the entering thread as index zero. This removes the errors
without changing RDP commands or the emulated state.

The Release N64Probe benchmark used the same uninstrumented .NET binary and
GPU flags on both sides, neutral input, CPU affinity 26/27, and the GPU library
copied before the fix versus the rebuilt library. The 5–10 guest-second window
was run reference/candidate/candidate/reference:

| Run | Wall seconds |
| --- | ---: |
| Before 1 | 84.323 |
| After 1 | 77.704 |
| After 2 | 78.418 |
| Before 2 | 83.580 |

Mean wall time fell from 83.951 to 78.061 seconds, or **7.55% greater
throughput**. Both fifth- and tenth-second checkpoints, rendered frames,
audio, input, final RDRAM, and serialized CPU state matched exactly. The
candidate logged zero thread-index errors. Artifacts are under
`.build-tmp/n64-gauntlet-legends-2026-09-24/abba-thread-registration/`.
This is still far below 100% real time: the measured five guest seconds took
about 78 wall seconds. The GPU submit/readback and RSP costs remain large.

The already available early GPU submission was then tested with the corrected
native library, again using a four-run ABBA and the same state/guest window.
Without overlap, the two runs took 77.774 and 78.089 seconds; with the default
256 KiB overlap batch, they took 73.047 and 71.574 seconds. That is **7.77%
greater throughput** on top of the registration fix. All checkpoints, images,
audio, input, final RAM and CPU state matched. Artifacts are under
`.build-tmp/n64-gauntlet-legends-2026-09-24/abba-overlap-after-thread-fix/`.
Across the two separate comparisons, the mean 5–10-second wall time dropped
from 83.951 seconds before either change to 72.310 seconds with both, roughly
16% more throughput. Since those were separate ABBA series, treat that
combined percentage as indicative rather than a single paired measurement.

`scripts/run-n64-gpu-desktop.sh` now enables the previously opt-in overlap
for the Linux GPU desktop. `EUTHERDRIVE_N64_GPU_OVERLAP=0` remains the
switch to turn it off if another scene regresses. This is not a real-time fix;
further work should focus on GPU submit/readback and RSP block costs measured
in `.build-tmp/n64-gauntlet-legends-2026-09-24/profile5.log`.

## Second pass: overlap batch size

Native profiling with overlap on found 72,241 write barriers before RDP
triangle opcode 15 in five guest seconds, accounting for 21.322 seconds of
GPU idle time. Native submit took 26.060 seconds. These barriers may protect
in-flight framebuffer writes or texture reads, so they were left intact.
The raw profile is
`.build-tmp/n64-gauntlet-legends-2026-09-24/native-profile-5.log`.

With the same uninstrumented GPU library on both sides, the 5–10-second ABBA
compared 256 KiB versus 64 KiB for early submission:

| Scene | 256 KiB mean | 64 KiB mean | Throughput change | Exact state |
| --- | ---: | ---: | ---: | --- |
| Gauntlet slot 1 | 69.399 s | 61.116 s | +13.55% | Yes |
| RE2 gameplay slot 1 | 10.147 s | 9.875 s | +2.76% | Yes |
| Mega Man later slot 1 | 8.144 s | 8.042 s | +1.27% | Yes |
| RE2 movie slot 2 | 6.281 s | 6.518 s | -3.64% | Yes |

All four comparisons used two fifth- and tenth-second checkpoints with
matching image, audio, input, final RDRAM and serialized CPU hashes. The
results are under `.build-tmp/n64-gauntlet-legends-2026-09-24/abba-*-64k*/`.
The RE2 movie had `overlapChunks=0` in both modes, so its timing difference
is host noise and provides no evidence about the batch-size choice. The small
RE2 gameplay and Mega Man gains need repetition before generalizing them.
The smaller batch is now selected only for Gauntlet Legends (Europe), using
the ROM's CRC1/CRC2 and country byte. Other ROMs retain 256 KiB. A valid
`EUTHERDRIVE_N64_GPU_OVERLAP_BATCH_KIB` setting overrides either default.
The rebuilt probe confirmed Gauntlet selects 64 KiB and matches the earlier
64 KiB checkpoint without an environment override; RE2's movie selects
256 KiB and matches its reference checkpoint.
