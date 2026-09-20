N64 CPU polling throughput, 2026-09-20

The reference is `9c6c6396`, including the completed-frame presentation fix.
This pass targets the Linux emulator and uses the same recovered Rampage slot 1
core, SHA-256 `83ffa71e4ddf1dcdeb204e24f39fe5ad75cf7705f8247f1424a653533247d636`.
No user savestate is modified.

A fresh native instruction-pointer profile put 16.28% of samples in CPU block
dispatch, 8.07% in instruction-history recording, 8.01% in JIT lookup, and
10.17% in two small compiled polling blocks. Rampage repeatedly checks two RAM
flags, with either conditional branch able to return to the first check. The
old compiler returned to dispatch between the checks and repeated their work
throughout each quiet interval.

Compilation now proves whether a loop's register inputs are invariant. A
register read before its first write is an input; if any such input is also
written, the optimization is rejected. The accepted subset contains pure
integer operations, validated direct-RAM loads and conditional branches. It
excludes stores, CP0, FPU, trapping operations and MMIO. Each possible taken
prefix is proved separately before several polling blocks can form one region,
still limited to 16 guest instructions of code.

The first iteration executes normally, including code-byte and operand guards.
When a proven prefix branches back to its entry, its remaining complete
iterations have the same register and RAM effects. Their instruction count can
therefore be calculated directly. Timing still advances by precisely that
count, including COUNT/RANDOM. The quiet-window check prevents crossing a device
completion, VI line transition, VI interrupt or COUNT comparison. A failed load
guard leaves the exact executed prefix and PC for ordinary interpretation.
Code bytes are checked on each entry, including changes to a later poll.

Instruction history uses pre-expanded PC/opcode patterns and bounded copies to
the existing ring. It preserves individual entries, wrap position, partial
prefixes, the caller's first entry, and the interpreter's omission of delay
slots. It is not a reduced diagnostic history. The compilation worker operates
only on copied words and immutable derived patterns.

With the repeated polling work removed, a 512-instruction ceiling improved
throughput further than 128. Actual windows are shortened by hardware timing;
uncompiled execution retains its 32-instruction ceiling. A 2,048-instruction
experiment was slower in this scene and was discarded. The initial history and
single-loop changes alone did not show a gameplay gain; the combined polling
region and 512 ceiling are the measured result.

Four alternating 45-second runs, pinned to CPUs 6,7 and excluding their first
ten seconds, produced:

| Build | Run 1 | Run 2 | Mean graphics tasks/s |
| --- | ---: | ---: | ---: |
| Reference | 30.100 | 30.134 | 30.117 |
| Updated CPU | 47.253 | 47.267 | 47.260 |

This is 56.92% more graphics-task throughput. Audio tasks advanced at essentially
the same rate. This counter is not display refresh rate or a claim that the game
has reached full real-time speed. Timed runs do not enable diagnostic traces,
continuous frame capture or another simultaneous benchmark/build.

Fixed-work replays also compare the entire serialized CPU/device/RAM state
against the reference. Rampage, Duke, Mario and Gauntlet each match after exactly
50,000,000 instructions. Perfect Dark matches after 50,000,001: its last branch
and delay slot complete together. The per-game SHA-256 values are preserved in
`replay-results.json`; Rampage is
`E7A9A309A25DEC3B90942C81E9D69063496800BA176884AF577C23F426D62291`.
Instrumented replay timing is not used for the gameplay-speed claim.

The final CPU check passes 1,479 complete-state cases, including 1,317 that
actually execute compiled code. Added cases cover invariant and variant loops,
both exits of chained polls, failed later loads, changes to interior code,
register dependencies in delay slots, all device timers, 511/512 boundaries and
larger requested budgets clamped to 512. The history oracle checks the complete
retained ring and its position, including untouched entries. Background worker
reset/stale-code checks and 1,050,624 RANDOM-counter comparisons also pass.

The movement/audio run lasted 91.55 wall-clock seconds and advanced graphics
tasks 5,227 to 9,296 and audio tasks 6,845 to 10,913, without unknown opcodes or
interpreter failures. Its PCM capture contains 67.900 seconds of nonzero audio,
about 74% of wall-clock time; this heavier moving scene still needs more work to
reach real-time speed. The captured images show the character moving to another
street, with complete buildings, scenery and sprites.

Reloading that run's v7 state advanced graphics 9,300 to 10,154 and audio 10,918
to 11,771 over a further 22.48 seconds. Continuous presentation sampling captured
841 changed images. The brightest and darkest captures both retain the complete
scene; average pixel brightness ranged from 104.82 to 108.13. This diagnostic
range is supporting evidence for this scene, not a general flicker detector.

Twenty-second smoke runs of Duke, Mario, Gauntlet and Perfect Dark all continue
graphics and audio tasks without unknown opcodes or interpreter failures.
These check game progression; the measured performance gain above is Rampage's.

Follow-up sampling puts textured-rectangle drawing at 13.25%, framebuffer pixel
writes at 8.15%, and CPU block dispatch at 12.95%. JIT lookup is down to 2.14% of
samples, and the two polling blocks are no longer among the ten largest entries.
The per-pixel renderer and remaining CPU fallback work are useful next targets;
`final-native/native-summary.json` preserves this profile for continuation.

The Linux UI Release build has zero errors and 383 existing project warnings.
Its MIPS assembly matches the measured probe binary exactly, SHA-256
`90a9410722419a26b3deedc891b1e69032ea922aa581ad985c0579bba3dc8728`.

Artifacts and reproducible scripts are in
`.build-tmp/n64-playable-next-2026-09-20/`. They include the reference and final
binaries, native profile, intermediate candidates, alternating gameplay logs,
and `final-scene-results.json`. These local files include user ROM/state data
and are not committed.

Build and validation entry points:

```sh
dotnet build tools/N64Probe/N64Probe.csproj -c Release --no-restore
dotnet tools/N64Probe/bin/Release/net8.0/N64Probe.dll --check-cpu-jit
dotnet build EutherDrive.UI/EutherDrive.UI.csproj -c Release --no-restore
```

Restart the application to load the rebuilt core, then load Rampage slot 1.
