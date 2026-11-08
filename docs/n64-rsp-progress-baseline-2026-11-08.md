# RSP progress baseline regression

A CPU scheduling boundary can leave a progress signature that happens to
equal the result of a later tracked-register write. A following ordinary
NOP does not necessarily recompute that signature. An optimization must not
infer a fresh baseline merely because an ordinary instruction executed.

Four differential cases seed current/future signatures and clean/dirty GPR
state. A pending branch forces the initial NOP through the interpreter; the
following ADDIU/NOP pair must execute through compiled blocks in the
candidate. With the deliberately stale matching signature and clean GPR
flag, the stagnant count must be 8, not reset to 1. Complete serialized
Memory/RSP state is compared with an isolated ordinary interpreter.

The cases are included in `--check-rsp-blocks`. To run only these cases:

```sh
N64_PROBE_RSP_BASELINE_ONLY=1 dotnet --fx-version 10.0.14   PROBE/N64Probe.dll --check-rsp-blocks REFERENCE/Ryu64.MIPS.dll
```

The targeted test passes with the corrected deferred-progress candidate
against the accepted reference. The existing 96 programs, 1024 sliced
progress cases, 126 exits, 192 deadlines and 32 code rewrite checks also
pass, as do complete Mega Man/RE2 replay states. Candidate performance did not establish a stable win; production and
launcher behavior remain unchanged. The candidates are archived rather
than enabled. Local evidence: `.build-tmp/n64-mario-2026-11-08/`
`lazy-baseline-check.log`, `lazy-progress-seeded-rsp-check.log` and
`lazy-progress-seeded-cross-game-proof.json`.
