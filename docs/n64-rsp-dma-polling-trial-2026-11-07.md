# Exact DMA polling shortcut: no established gameplay gain

The [archived patch](experiments/n64-rsp-dma-polling-2026-11-07.patch) recognizes
`MFC0 rt,DMA_BUSY; BNE rt,r0,-2; NOP` while DMA is the only pending lifecycle
event. It batches at most 255 instructions and stops strictly before DMA
completion, which can replace IMEM through a queued transfer. All other
patterns, tracing, zero busy values and near-limit watchdog states fall back.

The experiment preserves exact countdowns, GPRs, branch state, trace PC,
history ring and progress hash. After the first read, the countdown is the
only changing signature input: odd-multiplier FNV folding is bijective in
that input, so subsequent instructions reset stagnation. A conservative
entry guard covers a possible first-read collision. It does not add guest
cycles or change the existing status-delay-slot DMA shortcut.

## Validation

- 756 complete memory/RSP/result checkpoints match ordinary interpretation.
  Cases cover tracked/untracked/zero destination registers, small and large
  budgets, slice resume, countdown boundaries, and queued DMA replacing the
  polling instructions. The shortcut executes 5,400 instructions in the test.
  Digest: `AC183E006B521E3E9CDAE70BA60F35066CC01A548E7D9D01C504555586DAD1EB`.
- RSP scheduling's 32 boundaries, save/load, producer/consumer, interrupt
  ordering and framebuffer publication pass.
- Both MIPS target frameworks build. A Mario pilot and all four ABBA runs
  match 18 checkpoints/images and final RAM, including audio/input and clocks.
  The pilot batches 64,471,980 instructions over 90 guest seconds.

## Rejected timing result

Same .NET 10.0.14, cores 6/7/8/9, two GPU workers and native library. Reference
includes the accepted DMA notification, fetch and RSP signature changes.
Guest seconds 70–90 contain 19.7122401 audio seconds.

| Run | Wall seconds | Audio realtime |
| --- | ---: | ---: |
| Reference A1 | 20.8128813 | 94.71% |
| Poll B1 | 22.9446459 | 85.91% |
| Poll B2 | 19.8507916 | 99.30% |
| Reference A2 | 22.3018836 | 88.39% |

The mean reports +0.75%, but the pairs have opposite signs and the candidate
varies substantially. The 99.3% sample is not evidence of sustained realtime;
its heavier five-second sections are still below realtime. No default is
changed. Production sources are restored to `1838f669`; the patch applies
cleanly and preserves the implementation and additional tests for future work.

Local artifacts: `.build-tmp/n64-mario-2026-11-07/dma-poll/`, `poll-check.log`,
`poll-scheduling.log`, `poll-pilot-proof.json`, `poll-abba/`. No ROM, save or
capture is committed. The independent next experiment isolates CPU-thread
placement using the accepted binary, without this polling shortcut.
