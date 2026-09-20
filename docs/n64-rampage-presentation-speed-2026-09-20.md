N64 Rampage presentation and performance, 2026-09-20

The reference is commit `09cd1de8`. Work and measurements target the Linux
emulator. The working core extracted from Rampage slot 1 has SHA-256
`83ffa71e4ddf1dcdeb204e24f39fe5ad75cf7705f8247f1424a653533247d636`.
The current slot 1 was checked again and still contains exactly that core.
No user savestate was modified in this pass.

The disappearing scenery was reproduced in the old build. Two presentation
paths exposed unfinished rendering: the RSP dispatcher flushed the visible
snapshot after every cooperative slice, and the first presentation after a
normal VI buffer switch could copy live RDRAM despite an existing completed
snapshot for that buffer. Repeating SetColorImage for the same target also
flushed unnecessarily.

The dispatcher now retains pending drawing across slices and publishes on
FullSync, an actual target change, or successful task completion. Presentation
first looks for a snapshot matching the actual VI address, dimensions and pixel
size, including a top-row offset. This applies to normal and low addresses.
CPU-produced framebuffers still have their existing fallback when there is no
matching RDP snapshot. Memory savestate version 7 appends 21 bytes describing
pending publication, so saving during a slice does not publish or lose the
unfinished drawing. Versions 1 through 6 remain readable and clear the absent
pending-publication state.

`N64_PROBE_CAPTURE_FRAMES=1` now samples presentation every 20 ms, writing changed
PPM images and an index with timestamps, task counts and selection status. From
the same slot 1, the old capture contains obvious sky-only and partly erased
frames; the corrected capture retains the completed scenery. There were 458
changed images in the old capture and 518 in the corrected one. These are
diagnostic captures, not a frame-rate benchmark: image sampling and disk writes
add work, and the fixed number of polls took different wall-clock times.

Native CPU loops now record their complete instruction history once per bounded
region instead of calling the history helper on every backedge. The ring still
contains the same individual instructions, including wrapped writes and partial
prefixes before a failed memory guard. Arithmetic and NOP delay slots no longer
call a runtime operand validator that always returned true for them.

Texture rectangles now advance binary fixed-point coordinates with integer
addition and arithmetic shifts. This preserves negative-coordinate floor,
copy-mode slope division, clipping offsets and flipped axes. Indexed CI4/CI8
rectangles with TLUT precompute their combined palette once per draw when the
draw is large enough to amortize it. The table is rebuilt from current TLUT/TMEM,
primitive/environment colors and mux settings; it is not a cache spanning
commands or savestate loads. Per-pixel alpha, blending and depth processing
remain in the normal path.

A complete Rampage frame was captured as 1,980 DMA chunks / 1,839 commands,
including 141 indexed texture rectangles. Diagnostic command logging was
extended to retain consumed partial-command prefixes, so the tape also preserves
commands split across DMA submissions. The fixed-work RDP replay produced the
same complete serialized Memory state before and after the rectangle changes:
`642B77DCC4D161964CC567FA8FBA1C33424CB4D2186ECE8701DC0FF8B5DD4D0C`.
Median render time was 11.539 ms before and 7.304 ms after, about 37% less time.
Both timing results use the default assembly context, CPU affinity 6,7, 60
warmups and 40 measured runs. The separate reference assembly context is used
only for correctness comparisons.

Before extending CPU windows, four alternating 45-second gameplay runs gave
25.08 / 25.91 graphics tasks per second for the reference and 29.96 / 29.75 for
the corrected renderer and CPU history changes, approximately 17% more
throughput. Rates exclude the first ten seconds. Graphics tasks per second are
not a claim of full-speed gameplay or monitor refresh rate.

A 512-instruction native-window experiment also passed extended architectural
tests and produced the same full CPU/device/RAM state after 50 million Rampage
instructions. It did not provide a clear gameplay improvement and was removed;
the production ceiling remains 128 instructions, shortened at device/VI/COUNT
boundaries. The retained CPU changes passed 1,263 full-state cases, including
1,105 cases actually using compiled code, ring wrap, rejected entries,
self-modifying code and background compilation/reset checks.

Validation includes 4,096 randomized full-rectangle pixel comparisons against
the old renderer, with palette/TMEM changes, all cycle types, clipping, flips,
negative fractional slopes and blending. It also includes 6,847 existing
render/interrupt checks; 1,092 alpha, 960 rectangle-depth and 24 blender cases;
197 streaming-command cases; 81 video checks; 73 VI-buffer checks; 32 RSP slice
boundaries plus 16 unfinished-frame/save-load combinations; and the snapshot
coalescing check. The latter still produces the same RAM and final image while
reducing 240 primitive publications to one FullSync publication.

An additional 106,496 sampler cases match the reference, and all 65,536 RGBA5551
conversions / 1,048,576 filter cases pass. The rectangle differential also passes
4,096 cases with the legacy flip setting.

The final Rampage movement/audio run lasted 101.42 wall-clock seconds, advancing
graphics tasks 5,226 to 8,183 and audio tasks 6,844 to 9,800 with no unknown
instructions or interpreter failures. Reloading its v7 state advanced graphics
8,186 to 8,775 over a further 21.87 seconds while sampling presentation. The
audio capture contains 49.351 seconds of PCM from the longer run; real-time
playback speed is still not achieved. Twenty-second smoke runs of Duke, Mario,
Gauntlet and Perfect Dark all advanced graphics/audio tasks without unknown
instructions or interpreter failures. These smoke runs verify progress, not
complete game compatibility or a performance gain for every title.

The Linux UI Release build completed with zero errors and 383 warnings in the
existing project build. Its MIPS assembly matches the tested final probe:
`19ec29812cee15f357150d7f5d3038bf97fb489f31fa44765d6b4ada049f2ede`.
Restart the application to use the rebuilt core, then load Rampage slot 1.

Evidence is under `.build-tmp/rampage-flicker-speed-2026-09-20/`, including
`reference/`, `presentation-fixed/`, the original CPU profile, `scene-results.json`,
the complete RDP tape, validation logs, `video-before/`, `video-after/`, and
`flicker-before.png` / `flicker-after.png` selected near the same graphics-task
count. These local artifacts include user ROM/state data and are not committed.

Example checks:

```sh
dotnet build tools/N64Probe/N64Probe.csproj -c Release --no-restore
dotnet tools/N64Probe/bin/Release/net8.0/N64Probe.dll --check-cpu-jit
dotnet tools/N64Probe/bin/Release/net8.0/N64Probe.dll --check-rsp-scheduling
dotnet tools/N64Probe/bin/Release/net8.0/N64Probe.dll --check-low-vi
dotnet tools/N64Probe/bin/Release/net8.0/N64Probe.dll --check-rectangles .build-tmp/rampage-flicker-speed-2026-09-20/reference/Ryu64.MIPS.dll
```
