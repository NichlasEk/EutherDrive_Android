# N64 direct RAM word access (2026-09-20)

Baseline: `70f1ea1f`, after the event-bounded CPU block work. Artifacts live in
`.build-tmp/duke-word-access-2026-09-20/`. Uses the unchanged Duke slot/snapshots
and ROM recorded in `n64-duke-cpu-blocks-2026-09-20.md`.

## Change

The ordinary untraced KSEG0/KSEG1 RAM path for `ReadUInt32` and `WriteUInt32` is
now a small wrapper. It checks that all four bytes are in RDRAM, accesses the
same big-endian bytes, and retains the existing write-epoch/framebuffer dirty
tracking. It accepts the same unaligned RAM addresses as the old memory API;
CPU instructions still perform their original alignment checks.

Mapped addresses, MMIO, cartridge accesses, RAM-end crossings and every active
trace environment setting use the unchanged slow implementation. A watch
address of zero also selects that path. There is no unsafe pointer access, new
package dependency, device timing change or change to memory contents/layout.

## Evidence

`--check-word-access REFERENCE_MIPS_DLL` loads the old assembly separately and
compares 131,278 operations: patterned memory, both direct aliases, aligned and
unaligned addresses, page and RAM boundaries, cartridge reads and SP semaphore
read side effects. It compares values, exception types/messages and the entire
serialized CPU/device/RAM state, including a registered framebuffer's epochs.

- Value digest: `A7FD6D674872C21405B86674081FB134C03F0EE673FB21ED889AC277C2B8A6C5`
- Full-state digest: `58B94887F8D9FCF925B753F4BAD22DCA0F71466A0BBA34D8E9E63A48B70EC974`

The same 20-million-instruction gameplay replays also match the baseline:

- Duke: `6BF947D1D8777DE7F4BCCD98501A4210207F7F87ADEBADF5340784B577C9FA6F`
- Perfect Dark: `6DF70AF2557F2C750643A18E9D8876C365A3CBF8F3E761BC06A95B657C29CF74`

Release, CPU 6, three warmups and five measured samples per process. Duke process
order was reference/candidate/candidate/reference. The reference process medians
were 1132.846 and 1115.540 ms; candidate medians were 1024.216 and 1086.308 ms.
That is about **6.5% higher throughput** from the mean medians. Host variance is
visible, so do not interpret it as a universal percentage.

Perfect Dark's single candidate/reference comparison was 1417.606/1452.390 ms.
That confirms full-state equivalence but is too small a sample for a robust
performance claim. None of these fixed-work measurements are displayed FPS.

A separate 45-second real CPU-thread comparison, first ten seconds excluded,
measured 18.52 → 19.13 million emulated cycles/s and 5.896 → 6.068 graphics
tasks/s: about 3% more sustained throughput relative to the CPU-block baseline.
This is a separate host run; do not compare its absolute rates with runs made
under different host load.

The watch-address-zero variant also passes the full word-access comparison.
The 73 low-VI cases and 6,845 render/interrupt cases pass. The Linux UI Release
build has zero errors. Its core and the measured candidate both have SHA-256
`2b3368ac75834b54fe00c6308cab261890c422badd0fa37bb08295d1f831e164`.
