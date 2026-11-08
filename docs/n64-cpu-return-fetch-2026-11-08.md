# CPU instruction return-value trial, November 8

The R4300 block loop fetches each next instruction live. The trial replaces
its bool/out opcode protocol with a signed 64-bit return: valid uint bits
are zero-extended; -1 indicates an address outside RDRAM. Bounds, physical
masking, GPU read hazards and device/event limits remain unchanged. Both
old and new entry points share the validated load body. This tests whether
avoiding address-taken opcode locals reduces the host CPU loop overhead.
The earlier local-reference experiment is not included.

## Correctness

- 402,193 instruction-fetch operations against the accepted assembly,
  including mirrored/misaligned/boundary addresses and explicit 0,
  0x7fffffff, 0x80000000, 0xfffffffe and 0xffffffff instruction values.
  Value digest: DCB2503964136282EFAAE1785F297B8C51B63466AE879F1E0EB1C7432929AE8E.
- 1,755 full-state CPU block cases, 2,287 JIT cases (2,004 compiled),
  worker stale-code/reset checks, and 20 fallback/event/history boundaries.
- 60 native GPU integration checks, including two new GPU-owned instruction
  reads through physical and segment-aliased addresses; each must observe
  the rendered word and exactly one read hazard.
- Mega Man 64 and Resident Evil 2 ten-second GPU replays have identical
  complete state, frames, audio and checkpoints.
- netstandard2.0 build succeeds.

Local evidence is under `.build-tmp/n64-mario-2026-11-08/return-fetch*`.
The final fetch checker uses the exact frozen timing candidate MIPS DLL.
GPU journal checks use a separate instrumented build; timings do not.

## Performance and delivery

Mario ABBA matches six checkpoints/frames and complete final state. Mean
wall seconds are 19.87126945 reference and 19.75018635 candidate: +0.613%
throughput. Thread CPU throughput improves 0.629%. This small difference
does not justify production complexity. The experiment and its dedicated
tests are archived in `docs/experiments/n64-cpu-return-fetch-2026-11-08.patch`;
production source and the launcher remain unchanged.
