# RSP code mutation regression checks

The block differential suite now adds 32 complete Memory/RSP checkpoints for
repeated blocks, unchanged first words with edited interior instructions,
queued IMEM DMA, and an IMEM rewrite issued through CP0 after three executions
of an already compiled block inside the same slice. It also exercises the
optional transient validation-epoch overflow when a candidate implements it.
The ordinary interpreter remains the independent reference.

Candidate and reference digest:
`06130D35B168B98187B864937E42FB28FB341BA631252489943E73ED6AB1C6C4`.
The existing 96 programs, 1,024 sliced checkpoints, 126 exits, 192 lifecycle
deadlines and 32 direct dispatch checks still pass. Evidence is local under
`.build-tmp/n64-mario-2026-11-08/`, in `slice-rsp-check.log` and
`entry-rsp-check.log`. The new checks do not require a production code cache;
the optional epoch field is absent from the established reference core.

The Mario benchmark manifest also records register-cache, inline-history,
slice-validation, fast-progress and VMADH flags. Earlier November 8 captures
retain their original manifests; `trial-options.json` records their invocation
flags separately, and the register comparison retains its exact local driver.

These are correctness/reproducibility improvements, not a gameplay speed claim.
No ROMs, saved states or captured images are committed.
