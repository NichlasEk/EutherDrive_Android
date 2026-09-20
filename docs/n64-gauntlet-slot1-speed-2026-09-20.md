# Gauntlet Legends slot 1: TLB translation cache

Baseline: `44aa5f4e`. This is the Nintendo 64 Gauntlet Legends (Europe), not the
arcade Dark Legacy core.

The user's new slot 1 is in the opening in-game narration. Its container was
read and checksum-verified, then extracted to a separate artifact directory:
`.build-tmp/gauntlet-speed-2026-09-20/`.
Original container SHA-256 at extraction:
`a6fabc338fc84e2afe9200394cbb112d04f93287156d7d3366c46aaf70c78bfb`.
The original ROM and savestate were not written by these tools.

## Finding and implementation

Native CPU instruction-pointer sampling attributed 38.36% of samples to
`TLB.TranslateAddress`. Gauntlet executes mapped code around `e0000000`, and
repeatedly scanning all 32 entries costs substantially more than in games using
direct-mapped kernel addresses.

The translator now caches successful translations of 4 KiB subpages in a
256-entry direct-mapped table. Tags contain both the virtual page and current
ASID. Larger page mappings and overlapping entries retain the original scan's
priority because a cache miss still uses that scan. Misses and exceptions are
not cached. Existing translation semantics are preserved, including the current
read/store behavior; this is not a TLB correctness rewrite.

Caches are local to each host thread: UI diagnostics must not race to populate
the CPU's cache. A published version invalidates every thread's derived cache
after indexed/random TLB writes, reset, or state loading. Loading invalidates in
a finally block, including partially failed loads. Translation caches are not
serialized and the savestate format is unchanged.

## Measurements

Timing processes ran serially, pinned to CPU 6, without overlapping builds or
our regression tests. Existing user applications were left running.

Fixed work: 10,000,000 guest instructions from slot 1, three warmups and five
measured samples. Baseline and final thread-local cache both produce full-state
SHA-256 `5A5627022D8DC20EF68FCB211736029BFE84E98DC182C31313464B94A77A9030`.

| | Baseline | Thread-local cache |
| --- | ---: | ---: |
| Median elapsed ms | 1954.444 | 1228.573 |
| Median host instructions | 20,072,587,669 | 9,545,250,273 |
| Median host cycles | 6,004,569,756 | 3,565,479,008 |

This is about 37% less elapsed time for fixed work. Hardware counters were not
multiplexed. An earlier shared-cache prototype was faster but was replaced to
avoid cross-thread cache publication races; its numbers are not the final gain.

Two actual 30-second scene comparisons, discarding the first five seconds:

| Pair | Baseline graphics tasks/s | Updated graphics tasks/s |
| --- | ---: | ---: |
| Updated then baseline | 3.028 | 4.351 |
| Baseline then updated | 2.905 | 5.306 |

Both comparisons improve, but magnitude varies. These are graphics-task rates,
not display FPS or a full-speed claim.

## Correctness checks

- `--check-tlb-cache BASELINE_DLL`: 264,192 differential translations, including
  repeated subpage offsets, cache collisions, ASID changes, global entries,
  invalid mappings, overlapping/larger/malformed masks, read/store misses,
  indexed/random writes, resets, and restoring older TLB snapshots. Serialized
  TLB state and exception outputs match the baseline.
- 128 mapping replacements while a separate reader thread repeatedly translates
  the same virtual address; it sees every new mapping.
- 262,405 opcode-fetch checks and 131,278 word-access checks match the baseline,
  including exceptions and framebuffer epochs.
- Gauntlet continues for 90 seconds from slot 1 with automatic Start/A input;
  graphics count advances from 947 to 1545 without unknown opcodes or RSP failures.
- Duke, Perfect Dark and Mario each continue rendering for 20 seconds from
  established snapshots, with zero unknown CPU opcodes and no RSP failures.
  These overlap the UI build and are correctness-only, not timing evidence.
- Release UI build: 0 errors. Probe and UI core SHA-256 both
  `f51e8e3ba166920b83964717efb5860a7f2326ee857dec91a2969a4f862146f7`.
  The original savestate container checksum is unchanged.

Restart the Release application and load the existing slot 1. No new save or
cold boot is required for this optimization.
