# 32X performance notes - interleave 69 and next target

Date: 2026-05-01

## Current bet: fixed interleave slice 69

Current default:

```bash
EUTHERDRIVE_S32X_M68K_INTERLEAVE_SLICE=69
```

This keeps the same nominal SH2 work per frame. It does not underclock SH2. The cheat is only that the Mega Drive 68K and the 32X SH2 side are synchronized less frequently during normal line execution.

Why it is attractive:

- Big headroom win compared with the old default `16`.
- Doom smoke test kept the same final framebuffer fingerprint in the tested window.
- It attacks scheduler/interleave overhead instead of changing game logic rate.
- It can be overridden per run with `EUTHERDRIVE_S32X_M68K_INTERLEAVE_SLICE=16`.

Known risk:

- Knuckles smoke tests with larger slices showed SH2 PC/fingerprint divergence despite visible output.
- That does not prove broken gameplay, but it means this is not timing-neutral.
- ROM sweep should focus on boot, attract mode, first playable screen, and games with heavy 68K/SH2 comms.

Suggested ROM sweep notes:

- Test default `69`.
- If a title breaks, retry with `16`, `32`, and `64`.
- Log title, region, first bad frame/symptom, and whether lowering the slice fixes it.
- Keep screenshots or savestates for regressions that only appear after boot.

## Fallback plan: adaptive interleave

If fixed `69` is not broad enough, implement adaptive interleave instead of SH2 budget scaling.

Core idea:

- Safe mode: use slice `16` around sensitive 32X communication.
- Turbo mode: use slice `128` or `256` when 68K is not touching 32X-visible state.
- Cooldown: after a 68K access to 32X registers, comm ports, CRAM, framebuffer, or VDP registers, stay in safe mode for a small number of 68K cycles.
- Boot guard: keep safe or semi-safe mode during early boot/handshake frames.

Potential flags:

```bash
EUTHERDRIVE_S32X_ADAPTIVE_INTERLEAVE=1
EUTHERDRIVE_S32X_SAFE_INTERLEAVE_SLICE=16
EUTHERDRIVE_S32X_TURBO_INTERLEAVE_SLICE=256
EUTHERDRIVE_S32X_IO_COOLDOWN_CYCLES=512
EUTHERDRIVE_S32X_BOOT_SAFE_FRAMES=180
```

This should preserve total SH2 work per frame while reducing host overhead during quiet periods.

## Next big dragon: SH2 local execution batches

The next larger target is not another per-op micro-optimization. It is to reduce the number of times the SH2 interpreter returns to the outer scheduler while still preserving cycle accounting.

Name for the idea: SH2 local execution batches.

Instead of:

```text
outer 68K slice -> small 32X slice -> master execute -> slave execute -> repeat
```

try:

```text
outer 68K slice -> give each SH2 a larger local deadline -> execute until deadline or observable event
```

Observable events that must break a batch:

- pending interrupt changes
- DMA progress that affects visible bus state
- 68K-visible comm/register writes
- framebuffer/CRAM/VDP register writes if the 68K could observe them soon
- SH2 sleep/reset
- branch into uncertain or self-modified executable region

Why this is the dragon:

- Current bottleneck is dominated by SH2 instruction overhead plus scheduler/sync churn.
- Fixed interleave improves by reducing outer churn.
- Local SH2 batches attack the inner churn without reducing SH2 work.
- If implemented conservatively, it can be a real speed path rather than a game-speed cheat.

First implementation should be opt-in:

```bash
EUTHERDRIVE_S32X_SH2_LOCAL_BATCH=1
EUTHERDRIVE_S32X_SH2_LOCAL_BATCH_MAX=256
```

Validation targets:

- Doom: fingerprint should remain stable across short headless windows.
- Knuckles: boot and first visible scene should remain stable enough for savestate comparison.
- Mars Sample Program: should not regress boot or black-screen behavior.

Keep `EUTHERDRIVE_S32X_SH2_BUDGET_SCALE` as a diagnostic/turbo cheat only, not a correctness-oriented default.
