# CPU fallback batch boundary checks

CpuBlockChecks adds 20 complete-state comparisons with budgets 33, 64, 127,
128 and 129 and AI interrupt deadlines at 17, 65 and 129 instructions, or no
pending AI deadline. Both instruction-history ring starting positions are
covered in the JIT test mode, with native execution deliberately unavailable.
The actual result is compared with ordinary instruction-by-instruction
execution, including serialized CPU/RAM/device state and exact history.

The published fallback cap is read from the candidate's optional constant;
older cores use the established 32-instruction cap. JIT-disabled execution
retains its separate 32-instruction quiet budget. An existing budget assertion
that explicitly assumed 32 was updated accordingly; the initial failure is
preserved in `fallback-cpu-check-initial.log`, not treated as a passed check.

With the 128-instruction trial core:

- 1,755 ordinary block cases pass, including events, rejection and self-editing.
- 2,287 JIT cases pass; 2,004 execute compiled code, and worker publication,
  stale code rejection and reset during compilation pass.
- Both modes also pass 1,050,624 RANDOM-register transitions, one million
  decoder cases and 4,000,012 next-instruction decode cases.
- Full Mega Man 64 and RE2 GPU replays match the accepted core exactly.
- The netstandard2.0 build passes.

The focused mode `N64_PROBE_CPU_FALLBACK_ONLY=1` runs the new 20 cases after
common decode checks. It was independently run with the accepted old core
(cap 32), the trial core (cap 128), and JIT disabled (cap 32), with exact
history checks enabled in all three. The refactored focused checker uses the
same unchanged MIPS DLL hash as the full-suite candidate.

Example (use the desired built probe and installed runtime):

```sh
N64_PROBE_CPU_FALLBACK_ONLY=1 dotnet --fx-version 10.0.14 path/to/N64Probe.dll --check-cpu-jit
```

Evidence is local under `.build-tmp/n64-mario-2026-11-08/`: `fallback-cpu-check.log`,
`fallback-jit-check.log`, `fallback-targeted-*.log`, `fallback-cross-game-proof.json`
and `fallback-portable.log`. These checks establish correctness, not a speed
claim or a decision to enable the larger batch in production.
