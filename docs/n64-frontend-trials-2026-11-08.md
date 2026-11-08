# N64 frontend profiling and conversion trials, November 8

Reference: accepted operand-validation commit `054a996c`, frozen
`audio-operand-native` probe, Linux .NET 10.0.14. The actual N64Adapter and
timed AudioEngine run with a null sink; these are not physical listening tests.

## Profile

A guest-90–125 replay samples thread CPU over one second, then samples native
instruction pointers for seven seconds using ptrace. The pre-sampling thread
CPU tick deltas are R4300 93, MainThread 43 and AudioEngine 25.
Among main-thread IP samples, PullFrame accounts for 33.72% and
WaitForCpuProgress 5.69%. Sampling includes waiting/native runtime code, so these
percentages are not wall-time cost attribution. The profile is intrusive and
is not used as a performance benchmark. Six guest/RAM/generated-audio
checkpoints match the reference. Mario uses 320x237 RGBA5551 output here.

## Conversion candidates

The LUT candidate uses a 32768-entry native-endian BGRA table (128 KiB);
alpha remains opaque. The SIMD candidate uses portable Vector<ushort>,
exact five-bit scaling `(v * 1053) >> 7`, byte swapping and widening to BGRA.
It retains a scalar fallback and tail, including destination clearing.
Both preserve the original floor(v * 255 / 31), unlike bit replication.

The independent conversion oracle checks all 65536 input words in both
byte orders, unaligned buffers, short/odd buffers, destination canaries and
source preservation: 10694 cases pass for both candidates. SIMD additionally
passes with AVX2 disabled and with all hardware intrinsics disabled.
For 2000 random 320x237 frames, isolated timings were roughly 1124 ms reference,
770–855 ms LUT and 143 ms SIMD (181 ms without AVX2; 1052 ms scalar fallback).
These microbenchmarks do not establish a gameplay benefit.

Each playback variant clones the accepted audio probe and replaces only
EutherDrive.Core.dll. Manifests verify all other DLL/config hashes match.
ABBA order, same state90/input/settings, guest 95–120 measured window:

| Candidate | Reference mean wall | Candidate mean wall | Throughput change | Underruns ref / candidate |
| --- | ---: | ---: | ---: | --- |
| LUT | 26.4430753 s | 27.30623175 s | -3.161% | 222,209 / 328,316 |
| SIMD | 26.9759969 s | 27.275636 s | -1.099% | 303,166 / 353,152 |

Six guest cycles/RAM/generated-audio checkpoints match in every run.
No submission skips or engine drops occurred. Host workloads remain active;
the SIMD adjacent pairs disagree, so its isolated benefit does not establish
a reliable end-to-end gain. Neither conversion candidate is promoted.
Patches are retained under `experiments/n64-frame-{lut,simd}-2026-11-08.patch`.
Local binaries, logs and JSON are under
`.build-tmp/n64-mario-2026-11-08/{profile-audio-current,frame-*,audio-frame-*}`.

## Pacing follow-up

The current wait loop spins for the first millisecond then repeatedly calls
Sleep(0). A separate experiment changes only that call to Sleep(1), retaining
the guest cycle target and wait budget. It can add scheduler latency and must
be evaluated through the actual audio path before acceptance.

The pacing-only ABBA finishes at 26.4185437 s reference versus 26.39208205 s
candidate (+0.100% throughput), with underruns 180/203 reference versus
200/200 candidate. All six guest checkpoints match; no submission skips or
engine drops occur. This is effectively flat and does not establish more
audio headroom. The candidate is not promoted; its patch is retained as
`experiments/n64-frame-wait-2026-11-08.patch`.

Production N64Adapter source and the existing play launcher remain on the
accepted operand-validation version. Only the conversion diagnostic, this
report and experimental patches are committed. The new diagnostic also
passes against the frozen accepted Core assembly. No additional desktop
build or physical listening session is claimed. The 100% real-time target
remains open. Future work should return to measured R4300 block dispatch /
RSP execution cost; do not retry these frontend candidates based only on
their microbenchmarks.
