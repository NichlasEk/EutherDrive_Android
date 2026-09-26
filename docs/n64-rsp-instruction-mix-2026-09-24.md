# RSP executed-block instruction mix and VMADH experiment

An `N64RspProfile=true` diagnostic build counted instruction words only after
successfully executed JIT blocks. Counting is absent from normal builds. Both
scenes replayed copied core states for ten guest seconds with the same native
GPU library and neutral input; their image, audio, RAM, and CPU checkpoints
completed. Profile timing is perturbed by instruction recounting and is not a
gameplay-speed result. Raw logs and frozen binaries are under
`.build-tmp/n64-rsp-mix-2026-09-24/`.

| Scene | Executed block instructions | Scalar | VMADH (0f) | VMADN (0e) | SSV (S1) | SDV (S3) |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Mega Man 64, later slot 1 | 202,094,580 | 73,374,121 | 14,197,133 | 11,937,876 | 9,253,390 | 7,641,743 |
| Resident Evil 2, slot 1 | 212,188,273 | 71,644,288 | 16,112,777 | 12,183,572 | 10,478,391 | 8,430,234 |

`VMADH` is the most frequent single vector operation in both scenes. It adds
the signed product at accumulator bit 16. I first removed the redundant low
plane carry calculation inside the generic SIMD body. RSP instruction
differential tests passed 359,848 SIMD cases, and block differential tests
passed 96 programs plus 768 sliced-progress cases. Completed gameplay ABBA
runs had identical checkpoints and frames:

| Candidate | Scene / interval | Throughput change | Interpretation |
| --- | --- | ---: | --- |
| Generic SIMD body branch | Mega Man, 5–10 s | −6.76% | Reference drifted from 11.30 to 9.78 s; unreliable |
| Generic SIMD body branch | RE2, 5–10 s | +1.14% | Both pairs positive, small |
| Generic SIMD body branch | Mega Man, 5–15 s | +6.99% | Both pairs positive, but substantial wall-time variation |
| Generic SIMD body branch | Mega Man repeat, 5–10 s | +0.23% | Opposing pair directions; no repeatable gain |

I then restored the generic SIMD body and placed a dedicated `VMADH` method
behind `EUTHERDRIVE_N64_RSP_FAST_VMADH=1`. The method is selected when a block
compiles, leaving default generated blocks on the original method. The same
binary was benchmarked with the flag off/on:

| Scene / interval | Reference / candidate mean wall time | Throughput change |
| --- | ---: | ---: |
| RE2, 5–10 s | 10.556 / 10.638 s | −0.77% |
| Mega Man, 5–10 s | 10.861 / 11.423 s | −4.92% |

Those runs also had identical checkpoints and frames; the opt-in method passed
the RSP block/slice differential oracle. The flag stays **off by default**.
The dedicated method and raw result sets are retained to support a later
iteration, but no real-time or default-on speed improvement is claimed.

The host had another emulator active during some whole-game measurements, so
GPU/CPU load likely contributed to the large timing variation. A new 8,088
instruction graphics-task capture was created for fixed-work testing, but the
existing isolated task harness could not replay a GPU savestate. A temporary
GPU attachment let it load, yet the serialized CPU/memory state differed at byte 5;
the harness change was removed rather than weakening its exact-state oracle.
Future profiling should isolate `VMADH` and the frequent short vector stores
under steadier host load or with a deterministic GPU-state replay harness.
