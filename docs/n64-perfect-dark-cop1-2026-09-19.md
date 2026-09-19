# Perfect Dark intro freeze: missing COP1 usability exception

## Reproduction and cause

The slot-1 run stopped submitting graphics at RSP task 228. At PC 0x70008878,
LBU used t8=0x47008473. The base t3=0x47000000 came from MFC1 f16 at
0x70008610; COP0 Status=0x0400ff01 had CU1 clear. The interpreter nevertheless
allowed COP1 instructions. This bypassed the game's lazy FPU context switching,
letting a thread read another thread's floating-point register contents.

The baseline and idle-optimized cores reach exactly the same failing state,
including register/history dumps. The batch optimization therefore did not
introduce this failure. See [idle-event evidence](n64-perfect-dark-idle-events-2026-09-19.md).

The [NEC VR4300-series manual](https://hack64.net/docs/VR43XX.pdf), section 6.4.12,
printed page 193, requires a non-maskable coprocessor-unusable exception when
CU1 is clear: ExcCode=CpU, CE=1, common exception vector, and EPC/BD identifying
the faulting instruction or its enclosing branch. The handler may restore the
thread's coprocessor state, enable access and retry the instruction.

## Fix

Check CU1 before dispatching COP1 instructions or LWC1/LDC1/SWC1/SDC1. Unwind
through delay-slot handling before entering the CPU exception handler, so a
faulting delay slot cannot finish the enclosing branch. Set Cause.CE=1 and
ExcCode=11, using the existing exception-vector/EPC/EXL handling. No arithmetic
or memory operation is performed before the fault. Enabled COP1 behavior is
unchanged. No game-specific address or ROM check is used.

## Evidence

- 368 exception cases cover 88 table encodings, load/store address-error
  priority, normal and delay-slot execution, EXL already set, pending software
  interrupt preservation and untouched GPR/FPR/control/RAM state.
- Enabled bit transfers and sign extension pass; a two-thread lazy-FPU
  restore/retry check preserves each thread's distinct f16 contents.
- From the same old slot 1, a 60-second run reaches an interactive game menu
  (captured at 45 seconds), continues to 758 graphics tasks and 1,131 audio
  tasks, and never reproduces badv=0x47008473. The baseline plateau was 228
  graphics tasks. Automated Start/A input was enabled for this smoke run.
- This verifies progression into menus, not full mission completion or a
  whole-game FPS gain. The remaining renderer/CPU costs still limit speed.

Local artifacts: `.build-tmp/perfect-dark-events-2026-09-19/`, especially
`cop1-checks.log`, `live-cop1.log`, `live-cop1/frame-0045.ppm`, and
`fault-{reference,candidate}/`. Original user savestate is only read.

```sh
dotnet tools/N64Probe/bin/Release/net8.0/N64Probe.dll --check-cop1-usability
dotnet tools/N64Probe/bin/Release/net8.0/N64Probe.dll --check-idle-events .build-tmp/perfect-dark-vi-2026-09-19/cpu-state.bin
```

Final regression checks: 270 idle-boundary cases (140 accepted batches) and
CPU diagnostics pass. The corrected COP1 replay yields the same state with
batching on/off: `A131DE130D678821723C5462F7E115177D3996BEC02E3106BBF26AB5661B5A82`.
The changed hash versus the preceding checkpoint is expected: COP1 exceptions
now invoke the guest's context handling. Mixed CPU medians were reference
765.421/772.507 ms versus final 776.449/762.727 ms; effectively unchanged
(mean +0.08%). Exact full-state hashes still match for that integer workload.

Linux UI Release was rebuilt successfully; the deployed core matches the
validated final probe DLL: `0cfa894a53f97878bf42f661ed9c38d5a3e027fa920b7ed6d3fc6688f80fea83`.
