# High Seas Havoc Decryption Plan

## Current facts

- The arcade ZIP interleaves `d-25.11a` and `d-26.9a` into a `0x100000` byte 68000 ROM image.
- MAME's base decode produces a valid Mega Drive header and many large exact byte runs against the known Genesis ROMs.
- The tail area `0xe8000-0xfffff` is explained by the simple tail bitswap alone and matches the Genesis ROMs exactly.
- PEEL 4B has been modeled from the dumped fusemap. Its registered counter reproduces MAME's `typedat` table exactly.
- PEEL 5B has been modeled from the dumped fusemap. It explains the kind of six-bit affine/permutation data-line transforms MAME is approximating.

## Known blocker

The startup region is not solved by MAME's current extra pass:

- `017b` wants extra bitswap without final xor: `007c`.
- `2e3f` wants extra bitswap with final xor: `4eb9`.
- `0107` wants extra bitswap without final xor: `0000`.
- `0603` does not become the expected Genesis-like `0700`; MAME-style extra gives `0684` or `0685`.

The first deep PEEL5B search found no single affine PEEL5B mode with one fixed six-bit bus mapping that satisfies the first known startup pairs after the extra bitswap. That means the remaining protection is likely fetch/context gated, not one flat post-transform.

The instruction-path search now finds a plausible partial startup stream:

- `0x0c42`: extra no-xor opcode plus a named `0603 -> 0700` hypothesis gives `ori.w #$0700,SR`.
- `0x0c46` and `0x0c4c`: `2e3f -> 4eb9` and `0107 -> 0000` produce plausible `JSR abs.l` instructions.
- `0x0c52`: raw `2f3c` is likely intentional and decodes as `move.l #imm,-(sp)`.
- The earlier hard stop at `0x0c70` is now better explained by choosing `x0:4eb8`, i.e. `JSR abs.w`, instead of forcing `x1:4eb9` / `JSR abs.l`.
- With `0x0c70 = jsr $00f8.w` and `0x0c74 = x0:01a6` (`bclr d0,-(a6)`), the instruction-path search reaches the block-ending raw `RTS` at `0x0c9a`.
- A PEEL5B search for the Genesis-like long target `$000d0000` at `0x0c70` found no valid fixed six-bit mapping from any `raw/x0/x1` operand combination, which supports the arcade startup being structurally different from the home ROM here.
- Target verification now penalizes `$01xxxx` startup calls. The best path therefore prefers low-bank calls:
  `$0010a2`, `$001082`, `$00107a`, `$00101c`, `$0010f8`, `$0010a8`, `$000e2e`, `$000adc`, `$000aba`, `$000af4`, `$000d34`, and the final push immediate `$000a1c`.
- Weak targets remain and need confirmation: `$00f8.w` points into the vector/header area, while `$001082/$001084/$00101c/$0010a8/$000aba/$000af4` have low local scores and may be encrypted islands, data, or false positives.
- `--write-best-startup` writes a complete candidate image with the current startup patch. Current generated file:
  `/tmp/hshavoc_best_startup_candidate.bin`, CRC `d1e94775`, SHA1 `777a2cd391099647cf3c4215280f574fce147293`.
- Objdump sanity check:
  - Startup `0x0c42-0x0c9a` disassembles cleanly as the candidate path.
  - `$0010a2` looks strong: a clear RAM-clear loop followed by VDP init at `$0010ba`.
  - `$0010f8` is a clean `RTS`, followed by valid jumps to `$0d0000` and `$0d0682`.
  - `$000adc` looks like real code with VDP/MMIO writes.
  - `$001082`, `$00101c`, `$000e2e`, `$000d34`, and the entry alignment around `$000aba/$000af4` still look partially encrypted, data-like, or misaligned.
- Nearby-entry scoring shows likely alignment issues:
  - `$000aba` is probably not the real entry; `$000ab8` has a plausible `movem` prologue and `$000abc` starts a valid MMIO polling sequence.
  - `$000af4` is probably early; `$000af8` scores much better and starts with `33fc ... 00ff`.
  - `$000d34` and `$000d32` are tied by the simple score, but objdump makes `$000d34` look weak after the first instruction.
  - `$000e2e` may be near `$000e32`, which starts with a plausible `43f9`.
- `--write-adjusted-startup` writes a second diagnostic image with nearby-entry target adjustments. Current generated file:
  `/tmp/hshavoc_adjusted_startup_candidate.bin`, CRC `c03098fc`, SHA1 `43a7b02dc88eb80d0d32ef8311a07c5af7ed612f`.
- Adjusted target sanity check:
  - `$000ab8` is a strong replacement for `$000aba`: it starts with `48e7 fffe` and runs into coherent MMIO/VDP-looking code.
  - `$000af8` is a strong replacement for `$000af4`: it starts with `33fc 0001 00ff f910` and continues coherently.
  - `$000e32` is only a weak improvement over `$000e2e`: the first instruction looks plausible, but the following stream still looks encrypted/data-like.
  - `$000d32` just returns immediately; this may be intentional, but it does not explain the encrypted/data-like stream at `$000d34`.
- Weak-window variant reporting is now in the lab:
  - `$00101c-$001066` has no independent raw/x0/x1 words that look like strong 68000 opcodes, address-high words, or known startup targets.
  - `$001082/$001084` looks data-like at the entry itself, but `$00109c` starts a plausible raw `41f9 00ff 0000`-style address load before the known-good `$0010a2` code.
  - `$000e32` still gives the best local signal for the `$000e2e` window, with raw `43f9 00ff ...`, but later branch-looking words are not enough to call it solved.
  - `$000d34` starts as raw `41f9` while its following address high word wants `x0:0000`, reinforcing that this area likely needs mixed per-word gating rather than one flat transform.
- A first mixed raw/x0/x1 sequence scorer confirms the same shape:
  - `$00101c`, `$001082`, and `$001084` do not currently decode into any modeled short instruction stream.
  - `$000e32` only gets as far as `raw:43f9 raw:00ff raw:ff87` (`lea $00ffff87,a1`) before stopping.
  - After adding `moveq` recognition, `$000d34` gets one instruction farther: `raw:41f9 x0:0000 raw:0a51` (`lea $00000a51,a0`) followed by `raw:7e0e` (`moveq #$0e,d7`), then stops.
  - One-word `p5?` probes produce many plausible-looking but noisy sequences. Treat these as overfit diagnostics until a common PEEL mode is proven across adjacent words.
- The common target-adjustment probe shows all four adjusted operand fixes can be described as small-delta changes within a six-bit set (`0c7a raw 0e2e->$0e32`, `0c86 raw 0abd->$0ab8`, `0c8c raw/x0/x1->$0af8`, `0c92 raw/x0/x1->$0d32`). This keeps PEEL5B/fetch-context gating plausible, but is not yet a proof of one shared mode.
- The startup context/phase report does not reduce the remaining fixes to a simple `typedat` rule:
  - `$0c7a`: phase `0d`, `typedat=0`, `4b=(0,1,1,0,1)`, needs bits `[2,3,4]`.
  - `$0c86`: phase `03`, `typedat=1`, `4b=(0,0,0,1,1)`, needs bits `[0,2]`.
  - `$0c8c`: phase `06`, `typedat=1`, `4b=(0,0,1,1,0)`, needs bits `[0,1,3,7]`.
  - `$0c92`: phase `09`, `typedat=0`, `4b=(0,1,0,0,1)`, direct `x0`, bits `[1,2]`.
  This supports a multi-stage or PIC-state-dependent model rather than one flat address-nibble formula.
- A first local decode-layer scan is present, but its `p5?` class is intentionally broad and dominates too many chunks. Use it as a warning that unconstrained one-word PEEL probes overfit; the useful signal is still the stricter raw/x0/x1 hits plus adjusted-target bit deltas.
- Exact PEEL5B fitting is now separated from the default report behind `--strict-peel-search`, because one-word and low-delta searches are underconstrained and expensive. The bounded two-word exact probe currently finds at least one shared hardware-shaped candidate:
  - `$0c7a: raw 0e2e -> 0e32` together with `$0c86: x0 0abb -> 0ab8`
  - shared control `(i1,i8,i9,i12,rf13) = (0,1,1,0,0)`
  - shared bit order `(1,2,7,4,0,3)`
  This is stronger than the earlier one-word `p5?` hints, but it still only proves a local two-word compatibility. Three- and four-word common-mode fitting needs a deeper bounded search before being treated as a real algorithm.
- The strict PEEL5B candidate can now be replayed directly in the default report as `p5m`. Replay results:
  - `$0c7a` matches `$0e32` through `p5m:raw`.
  - `$0c86` matches `$0ab8` through `p5m:x0`.
  - `$0c8c` does not reach `$0af8` through this mode.
  - `$0c92` still reaches `$0d32` directly through `x0`, not through this mode.
  - The weak-window sequence scores do not improve: `$101c` remains unknown, `$1082/$1084` remains unknown, `$0e32` remains a short `lea` then stop, and `$0d34` remains `lea` + `moveq` then stop.
  This makes the strict mode useful evidence for local startup operand gating, but not a global code-island decryption rule.
- A second focused PEEL5B hypothesis is now replayed as `p5h`:
  - shared control `(i1,i8,i9,i12,rf13) = (0,1,1,0,0)`
  - bit order `(4,0,3,1,7,8)`
  - `$0c8c: raw 0a73 -> 0af8`, fixing the remaining strong startup target adjustment.
  - `$0d48: raw 0107 -> 000d`, a possible bank/high-word signal inside the weak `$0d34` continuation.
  - It does not improve weak-window sequence scores, so this should also be treated as local/fetch-context evidence rather than a global decode mode.
- Weak code islands are now profiled separately instead of forcing one shared model:
  - Models tested per island: `raw/x0/x1`, `p5m`, `p5h`, and combined `p5m+p5h`.
  - `$101c`: no model produces independently interesting role hits or a valid sequence.
  - `$1082/$1084`: no model improves the sequence; only the already-known raw signal around `$109c` remains.
  - `$0e32`: `p5m`/`p5h` add role-looking words, but the best sequence remains the raw `lea $00ffff87,a1` followed by stop.
  - `$0d34`: `p5m`/`p5h` add role-looking words, but the best sequence remains raw/x0 `lea $00000a51,a0`, raw `moveq #$0e,d7`, then stop.
  This supports separating startup operand/fetch gating from actual code-island decryption. The weak islands likely need a third, island-specific rule or may include data/table streams rather than linear code.
- An island-specific exact-mode search now anchors on each weak island's first hard stop and requires one exact PEEL5B mode to fit a short instruction-shaped seed:
  - `$101c`: can be forced into `303c 01a6` (`move.w #$01a6,d0`) with a local PEEL mode, but it stops immediately at `$1020`. Treat this as weak/overfit evidence.
  - `$1082/$1084`: no local exact PEEL5B seed improves the sequence.
  - `$0e32`: the hard stop at `$0e38` first looked forceable into a `jsr`, but stricter validation killed it. Odd call targets are now rejected, and the even replacement candidate `jsr $00002e7a` is rejected because `$2e7a` itself scores as `data/unknown`.
  - `$0d34`: no local exact PEEL5B seed improves the stop at `$0d3c`.
  The useful negative result is that the exact island search no longer finds a clean, target-valid continuation for `$1082`, `$0e32`, or `$0d34`; the remaining `$101c` seed is too short to trust.
- Per-island table/math profiling now treats the weak blocks as possible table streams instead of failed linear code:
  - `$101c` and `$0e32` have similar low-entropy/repeated-word structure. They share the same strong XOR fingerprints (`00a4`, `1a98`) and both produce a modeled pointer-like `$00010806` with local `target_score=25`. Treat these as likely shared encrypted table/setup fragments until a code-aware model proves otherwise.
  - `$1082/$1084` is still short and mostly data-like, but it contains the raw MMIO-looking `$00ff0000` longword near `$109e`, matching the already-known plausible raw setup around `$109c-$10a2`.
  - `$0d34` is structurally different: higher entropy, many more pointer-like longword candidates, an `x0/x0` `$00000ad6` candidate with `target_score=12`, an `x1/p5h` `$00010bcc` candidate with `target_score=7`, bank-D candidates, and one VDP-like candidate. This looks more like mixed pointer/data or p5h-gated operand material than a single flat code stream.
  - The pointer counts intentionally overcount because p5 variants can generate several alternatives for one word pair, but the repeated-island fingerprint is useful signal.
- A cross-island table fingerprint scan now looks for windows sharing weak-island repetition/XOR structure:
  - `$101c` is part of a larger local cluster, with strong non-identical matches around `$0ec0`, `$0efa`, `$0f34`, `$0fa8`, `$0fe2`, and `$100c`. The top hit `$0fe2` has `exact=29/38`, `xor_overlap=29/37`, and low entropy `3.23`.
  - The `$0e2e/$0e32` family points back into the `$101c` cluster as well; several matches from `$100e-$102e` score above 220, and `$0ea8` contains the same `$00010806` modeled pointer candidate.
  - `$0d34` does not produce the same kind of independent family. Its best hits are sliding windows around `$0d1e-$0d30`, which means the fingerprint is local structure, not a copy elsewhere.
  - Validating the strongest `$0d34` pointer candidates gives `$00000ad6` as a plausible low-ROM code target (`tst.w $00fff90c`, then branch; score 12) and `$00010bcc` as a weaker high-bank target (`move.w $00ffe090,d0`; score 7). Bank-D variants still score as data/unknown.
- The `$0ec0-$103e` table-family record model now tests start alignment and record period:
  - The strongest alignment is currently `$0ea0` with a 4-word/8-byte period: score 562, repeat score 152, two pointer-like slot pairs, and 15 repeated pointer-like targets.
  - The 4-word period wins over 3, 5, 6, 8, 9, and 16-word alternatives at the same start. Nearby starts (`$0ea2`, `$0ea8`, `$0eaa`) also keep the 4-word period on top, so the signal is period-stable even if the exact family boundary is still fuzzy.
  - Best-period slots have no direct 68000 opcode roles through raw or p5m; this strongly suggests a compact table/bytecode-like record stream rather than direct executable 68000.
  - Common raw records include `0102 1b3e 01a6 0102`, `1b3e 01a6 0102 0981`, `0101 14c5 01a6 0102`, and `1b3e 01a6 0102 09c1`.
  - Repeated pointer-like pairs still cluster on slot `01/02` through p5m/raw or p5m/x0, but their destinations currently score as data/unknown. Treat them as structural markers, not patch targets yet.
- The 4-word record alphabet view currently sees 52 records and 40 unique symbols in `$0ea0-$103f`:
  - The most common symbol is `0102 1b3e 01a6 0102` with count 3.
  - Repeated symbols confirm the stream is structured but not trivially one repeated filler block.
  - Slot alphabets are small: slot 0 has 10 unique values, slot 1 has 18, slot 2 has 17, and slot 3 has 11. This is consistent with compact fields or state-machine tokens.
- A prioritized reference scan links startup directly into the cluster:
  - `$0c62: x1 -> $101c` has `context_score=73`.
  - `$0c7a: x0 -> $0fa8` has `context_score=46`.
  - `$0c6e` has p5m alternatives into `$1026/$102e/$103a` with `context_score=59`.
  - These are stronger than the many low-score incidental table-looking values elsewhere, and suggest that at least some startup targets are entry points into this record/table family, not ordinary code addresses.
- A focused startup-to-record interpretation now maps those startup operands to concrete 4-word records:
  - `$0c5e: x0/x1 -> $101c` lands at `R04@$1018+04`, record `01a6 0005 1b3e 01a6`.
  - `$0c76: x0/x0 -> $0fa8` lands at `R10@$0fa8+00`, record `1b3e 01a6 0102 09c1`.
  - `$0c6a` has p5m alternatives into `R16@$1020+06`, `R24@$1038+02`, and `R27@$1028+06`.
  - Several entries land at slot offsets `+02/+04/+06`, not only record starts. This suggests the startup targets may be field-entry/state-entry offsets inside a compact interpreter table rather than function starts.
- A halfword-token stream model now flattens `$0ea0-$103f` into 2-byte tokens:
  - The stream has 208 halfword tokens but only 24 unique raw token values, with entropy `3.65`. This is much more table/bytecode-like than random encrypted 68000 code.
  - Frequent transitions are very strong: `01a6 -> 0102` occurs 35 times, `1b3e -> 01a6` 20 times, `14c5 -> 01a6` 15 times, and `0101 -> 14c5` 12 times.
  - Startup entry slots are biased toward late-record entry: `s0:1`, `s1:1`, `s2:1`, `s3:4`. That confirms that mid-record entry is systematic, not a one-off alignment accident.
  - Traces from `$101c`, `$1026`, `$102e`, `$0fa8`, `$0f26`, and `$0f2e` all stay inside the same small token alphabet. This strengthens the interpretation that these are state/table entrypoints, not failed function starts.
  - The most useful next target is now the code that consumes the repeated transitions, especially the `01a6/0102/1b3e/14c5/0101` alphabet and the slot-3 startup entries.
- A first table-consumer probe now searches for modeled `lea`, `movea.l #imm`, or `pea` base loads directly into `$0ea0-$103f`:
  - No direct modeled base-load candidate was found in low ROM. This is useful negative evidence: the table base may be loaded indirectly, synthesized, passed through startup state, or hidden behind another decode layer.
  - Startup entry windows show weak p5h postincrement-looking hits such as `15d9` (`move.b (a1)+,d2`) around `$0f2e/$0f3e/$0f58`, but those hits are inside the token stream itself. Treat them as token-shape hints, not confirmed consumer code.
  - The next consumer search should include indirect pointer loads and routines that receive `$0ea0`-family addresses as arguments rather than only direct absolute base loads.
- An indirect token-reference classifier now separates strong startup operands from loose word hits:
  - The strong references are still only startup operands: `$0c62 -> $101c`, `$0c7a -> $0fa8`, `$0c6e -> $1026/$102e/$103a`, and `$0c7a -> $0f26/$0f2e`.
  - A tempting non-startup `$4ab6 -> $1004` hit was rejected after context checking: `$1004` was raw, while the apparent `jsr abs.w` opcode came only from a p5m transform on the previous word. Mixed-transform opcode/operand pairs should not be trusted as call sites.
  - Remaining non-startup hits (`$0584`, `$0d0e`, `$0e40`, `$0e9a`) are loose words in data/table-like regions, not consumer code.
- A token alphabet run scan expands the probable token/table footprint:
  - Using the repeated `$0ea0` alphabet, the densest merged region is `$0ea0-$1064` with 227 words, 219 token hits, density `0.96`.
  - A separate earlier region `$0e46-$0e80` has 30 words, 28 token hits, density `0.93`.
  - A small later echo at `$11ee-$1214` has 20 words, 17 hits, density `0.85`; it may be related data, a copied subtable, or coincidence and needs validation.
  - `$0e82-$0e9e` now looks like a boundary/metadata bridge between token regions rather than ordinary code.
- A fixed block/trailer model now explains much of `$0ed4-$102e` as six clean `0x3a`-byte blocks:
  - Clean block starts are `$0ed4`, `$0f0e`, `$0f48`, `$0f82`, `$0fbc`, and `$0ff6`. Each has 29 words: 28 mostly-token columns and one parameter word.
  - Most block trailers are `4fbd + param`; B05 is the near variant `4eba + 0074`. This looks like an end/control marker plus argument, not 68000 code.
  - Startup table entries land on fixed block columns: B06/w19 (`$101c`), B06/w24 (`$1026`), B06/w28 (`$102e`), B04/w19 (`$0fa8`), B02/w12 (`$0f26`), and B02/w16 (`$0f2e`). This is stronger evidence for a table interpreter or state-machine entry model.
  - The block-column model shows many stable columns (`w01`, `w04`, `w07`, `w10`, `w13`, `w17`, `w20`, `w23`, `w26`) and low-cardinality variation elsewhere. That strongly argues against random encryption noise.
  - Trailer parameters are not yet decoded, but B04/B05/B06 all have simple relative interpretations that land at `$1030`, the next dense block/prologue region. Treat `$1030` as a likely block target/boundary until disproven.
- A stricter trailer-confidence pass now filters out generic address noise and keeps only high-signal block targets:
  - B04 has `4fbd 00ae`, and raw `from-start` resolves `$0f82 + $00ae = $1030`.
  - B05 has `4eba 0074`, and raw `from-start` resolves `$0fbc + $0074 = $1030`.
  - B06 has `4fbd 013d`, and x0 transforms the parameter to `$003a`, resolving `$0ff6 + $003a = $1030`.
  - A global marker-family scan found the same pattern in the preceding prologue-like block: `$0ed0: 4fbd 0196` resolves from inferred `$0e9a`/P00 context to `$1030`.
  - The only other strong marker-family hit was `$0e38: 4fbd 0006`, whose x0 parameter `$0180` can target B04 (`$0f82`) or B05 (`$0fbc`) depending on whether the relative base is treated as block start or next block. This is a weaker but plausible feeder/control marker.
  - `$1030` is now a four-source convergence target (`P00`, B04, B05, B06), so it should be treated as the current best state-machine join point rather than an incidental table address.
- A focused upstream-marker test now rejects `$0e38` as a normal `0x3a`-byte token block:
  - If `$0e38` is treated as a trailer, the implied block is `$0e02-$0e3a`.
  - That candidate body has only `3/28` token-alphabet hits and `0/9` stable-column hits against the clean B01-B06 family.
  - Exact column matching against clean blocks is effectively absent: best body match is `0/27`, with only trailer-like overlap.
  - The `4fbd 0006` trailer shape is still real, and x0 parameter `$0180` can target B04/B05, but the body does not belong to the main token-block family. Treat it as a separate upstream/control marker candidate, not as a seventh block.
- A pseudo-disassembly view now names the most common halfword tokens and annotates startup entries:
  - Most frequent tokens are `A=01a6` (46), `B=0102` (44), `C=1b3e` (25), `D=14c5` (20), `E=0101` (16), `N5=0005` (11), `G0=0981` (10), `N_A1=00a1` (9), and `G1=09c1` (9).
  - `$0fa8` and `$101c` both enter a similar `C A B G1 E ...` motif, so those two startup refs may be the same semantic entry class in different blocks.
  - `$0f2e` and `$1026` both enter on `D` and continue into `D A ...` motifs, giving a second likely entry class.
  - `$102e` lands on B06's trailer parameter immediately after `JMP`, then flows into `$1030`, so it is probably a tail/join entry rather than a normal body-token entry.
  - `$103a` lands inside B07 on `N5 G0 E D A B ...`, strengthening `$1030-$1068` as a join/finalization block rather than just ordinary continuation data.
- A startup-entry motif search now finds repeated entry classes across the token region:
  - `$0fa8` and `$101c` share exact `C A B G1 E` prefix; that motif appears four times (`$0ec0`, `$0fa8`, `$0fe2`, `$101c`).
  - `$0f26` starts `A B G0 F D`; that motif appears three times (`$0f26`, `$0f60`, `$1048`), so it is a real motif class, not a one-off.
  - `$0f2e` and `$1026` both classify as `D A ...`, but `$1026` immediately reaches `D A B JMP $013d`, so it is closer to a tail-control entry than `$0f2e`.
  - `$102e` is now explicitly a tail-parameter/control entry (`$013d C N_A1 N5 C...`), while `$103a` remains the B07 finalization entry.
- A callsite-level token-class model now groups startup operands by semantic class:
  - `$0c5e` has one token-region alternative, `$101c`, and it is clean `entry:CABG1` with motif support 4.
  - `$0c76` is dispatch-like: the same startup callsite has alternatives into `entry:ABG0FD` (`$0f26`, support 3), `entry:DA` (`$0f2e`), and `entry:CABG1` (`$0fa8`, support 4).
  - `$0c6a` is tail/join-control-like: alternatives land at `entry:DA-tail-control` (`$1026`), `entry:tail-param/control` (`$102e`), and `entry:B07-finalize` (`$103a`).
  - This makes the p5m alternatives more plausible as structured dispatch/join alternatives, not merely noisy false positives.
- A boot-flow readiness model now uses the best startup skeleton as a start/render checklist:
  - The structural anchors are solid enough to track: startup skeleton, token block family, `$1030` convergence, and callsite token classes.
  - The best skeleton still contains six weak or ambiguous points before a meaningful render attempt: `$1082/$1084`, `$10a8`/its token-class alternatives, `$00f8.w`, `$0e2e`/its dispatch alternatives, `$0aba/$0af4`, and parts of `$0d34`.
  - For a first MAME-side experiment, the target should not be "full gameplay"; it should be a minimal init patch plus instrumentation that proves expected VDP/register writes and stable control flow.
  - The next proof target is side effects by token class: `CABG1`, `ABG0FD`, `DA`, tail/join, and B07-finalize.
- A startup side-effect model now separates conservative `raw/x0/x1` effects from noisier `p5m/p5h` hypotheses:
  - `$0a1c` is the strongest direct VDP-register-init candidate: it begins with raw writes such as `move.w #$9001,$00c00004`, followed by more VDP control-port writes.
  - `$10a2`, `$107a`, `$10a8`, and the `$1082/$1084` neighborhood all converge on the raw `$10ba/$10c0` pattern: `lea $00c00004,a1` plus MMIO polling around `$00fffe00/$00ffff86`.
  - `$0af4` likely wants the nearby `$0af8` entry: the conservative side-effect scan sees raw MMIO writes and a direct VDP write at `$0b0e` (`move.w d0,$00c00004`).
  - `$0adc/$0aba` also show direct MMIO/VDP-like activity, but they remain alignment-sensitive because `$0ab8/$0abc` previously scored better than `$0aba`.
  - `$101c`, `$1026`, `$102e`, `$103a`, `$0f26`, `$0f2e`, and `$0fa8` remain token/state entries. They are not render-ready side-effect routines until the table/token consumer is identified.
  - `$00f8.w` and `$0d34` currently show no modeled VDP/MMIO side effects in the first `$60` bytes, so they should be instrumented as control-flow/data blockers rather than guessed render init code.
- A MAME render-probe plan is now emitted by the lab:
  - Phase 1 should patch only the best startup skeleton in `init_hshavoc()` and log execution, VDP control-port writes, and MMIO polls.
  - Phase 2 adjustments should stay disabled until the log proves the original target stalls: `$0c7a->$0e32`, `$0c86->$0ab8`, `$0c8c->$0af8`, and `$0c92->$0d32`.
  - First PC log points: `$0c42`, `$0a1c`, `$10ba-$10c0`, `$0af8-$0b14`, token entries `$101c/$0f26/$0f2e/$1026/$102e/$1030/$103a`, and blockers `$00f8/$0d34`.
  - The first useful render signal is not gameplay; it is confirming the modeled VDP writes at `$0a1c` and `$0b0e` plus stable MMIO polling at `$10c0`.
- Cross-offset reference anchoring is now available through `--cross-runs N --only-cross-ref`.
  - The decoded arcade image has large exact anchors against both Genesis references, so the MAME base decode is definitely recovering substantial real program/data content.
  - Strong US anchors include `$07de16-$092dbd -> $07a88c-$08f833`, `$092dc0-$09f6c5 -> $0a2fc2-$0af8c7`, `$06c1f0-$076449 -> $068bec-$072e45`, and same-offset `$0d0e54-$0d3b07`.
  - Strong EU anchors are similar, including `$0e075e-$0e7fff -> $0e05b6-$0e7e57` and same-offset `$0d0e54-$0d3b07`.
  - The protected boot regions still have no 16-byte exact anchor in either Genesis ROM: startup `$0c42-$0c9b`, weak `$0d34-$0daf`, early token run `$0e46-$0e81`, main token family `$0ea0-$1065`, and `$1082-$10a1`.
  - This strongly argues that the remaining blocker is not a simple misplaced Genesis code window. The startup/token material is arcade-specific, PIC/protection supplied, or behind a separate fetch/data transform.
- A compact token state-machine graph is now emitted by `--only-token-graph`.
  - `$0c5e` is a single repeated `CABG1` entry into B06 at `$101c`.
  - `$0c76` is dispatch-like: alternatives land in `ABG0FD` at `$0f26`, `DA` at `$0f2e`, and `CABG1` at `$0fa8`.
  - `$0c6a` is tail/join-control-like: alternatives land at `$1026`, `$102e`, and B07 finalization `$103a`.
  - B04, B05, and B06 all have strong trailer edges to B07/`$1030`: B04 raw `from-start +$00ae`, B05 raw `from-start +$0074`, and B06 x0 `from-start +$003a`.
  - B02 and B07 currently have no strong trailer edge. B02 is now the best target for finding the missing dispatch/consumer semantics; B07 is likely terminal/finalization or hands off to an external consumer.
- The EutherDrive render bring-up now feeds back into the decryption lab through `--vdp-log`.
  - Slot 3 VDP command-block scanning found 30 unique DMA-like operations: 21 ROM-sourced and 9 RAM-sourced.
  - The visible corrupt layer updates write to VRAM destinations such as `$f000`, `$fd40-$fec0`, `$dce0-$ddc0`, `$da80/$dac0`, and `$c040/$c04a`.
  - The ROM source blocks involved include `$04043a`, `$04139a`, `$04fefa`, `$053f94`, `$054254`, and `$054494`.
  - None of those ROM source blocks have a same-offset match or short exact anchor in either home Genesis reference when viewed through the current MAME base decode.
  - That makes the latest corruption more likely to be wrong remaining data/table decryption or wrong list generation than a pure Mega Drive layer compositor bug. The renderer is showing real motion and input reaction, but it is being fed bad tile/list metadata.
- A first VDP-source transform scorer/probe has now been tested end-to-end:
  - The source scorer prefers `p5h` for `$04043a`, `$04139a`, `$04fefa`, and `$053f94`, and `typedat-inv+08` around `$054494`.
  - EutherDrive can apply that hypothesis behind `EUTHERDRIVE_HSHAVOC_VDP_SOURCE_PROBE=1`.
  - Savestate testing needed `EUTHERDRIVE_HEADLESS_IGNORE_SAVESTATE_ROM_HASH=1`, plus runtime restoration of the selected decoded ROM image into both the 68000 ROM window and cartridge buffer after state load. Without that, the state payload masks ROM-probe changes.
  - With command-block flushing enabled, DMA source traces confirm the probe changes the ROM words read by the VDP, e.g. `$053f94` changes from `0606/0701/...` to `069d/0603/...`.
  - The framebuffer remains visually unchanged except for tiny transient differences. Treat this as a negative visual result: the current `p5h`/typedat-invert source hypothesis is real enough to alter bytes, but it is not the missing visible tilemap/layer fix.

## Next steps

1. Keep the VDP-source anchors (`$04043a`, `$04139a`, `$04fefa`, `$053f94`, `$054254`, `$054494`) as a target set, but do not treat the first `p5h`/typedat-invert probe as solved; it changes DMA data without improving the corrupt framebuffer.
2. Build a fetch-context solver for `0x0c42-0x0c9a` that scores valid 68000 instruction streams instead of comparing only against the home ROM.
3. Extend the candidate set beyond `raw`, `x0`, and `x1` by applying PEEL5B modes before and after the extra bitswap.
4. Keep `$000ab8` and `$000af8` as strong adjusted startup targets unless a stricter hardware-derived rule disproves them.
5. Investigate `$001082`, `$00101c`, `$000e2e/$000e32`, and `$000d34` as likely encrypted islands or data tables.
5. Add a local transform search for each weak target, using surrounding valid code and known MMIO patterns (`00ffxxxx`, `00c00004`, `4e75`, `4eb9`, `33fc`) as constraints.
6. Expand the strict common-mode validator beyond the current bounded two-word startup probe. It must prove the same PEEL control/bit-order across three or more adjacent or fetch-related words before allowing `p5?`-style candidates to score as code.
7. Add a segment/layer scan that summarizes which decode form (`raw`, `x0`, `x1`, strict p5m, p5h, small-delta) dominates each local code/data island, rather than assuming one global mode.
8. Add a stricter continuation test for island exact-mode hits: a hit should survive at least two non-trivial instructions after the seed or reach a validated branch/call target.
9. Investigate whether `$101c`, `$1082`, `$0e32`, and `$0d34` are data/table setup islands rather than linear code. The current linear-code model may be the wrong validator for these blocks.
10. Determine the exact boundary and base alignment of the token/table family. Current evidence says `$0ea0-$1064` is the main dense region, `$0e46-$0e80` is a related earlier region, `$0e82-$0e9e` may be metadata/boundary material, and `$0ed4-$102e` contains six clean `0x3a`-byte subblocks.
11. Build a small alphabet/dictionary view for the 4-word records and compare it against nearby executable routines to infer whether fields are opcodes, offsets, counters, or state-machine tokens.
12. Validate whether `$0ea8`, `$0e54`, `$0f52`, `$0f72`, `$101a`, and `$1034` are repeated record fields that should decode through the same p5m/p5h longword rule.
13. Find the code that consumes the 4-word records/halfword tokens. The direct startup refs prove entry points, but not yet the field semantics. Direct absolute base-load search was negative, so the next pass should model indirect argument/pointer flow.
14. Search specifically for routines that walk repeated token transitions such as `01a6 -> 0102`, `1b3e -> 01a6`, `14c5 -> 01a6`, and `0101 -> 14c5`, including code using `(An)+`, indexed table reads, or A-register bases near `$0ea0`.
14a. Treat mixed-transform opcode/operand call sites as false positives unless the opcode, high word, and low word can be justified by one coherent fetch context.
14b. Decode the `4fbd/4eba + param` trailer semantics. The P00/B04/B05/B06 parameters landing at `$1030` are the strongest current clue for block-to-block control flow.
14c. Use the cross-offset anchors as a negative filter: do not try to force `$0c42-$1065` to match Genesis unless a smaller exact local anchor appears. Focus instead on the PIC/state-machine model for the no-anchor regions.
14d. Treat `$0e38: 4fbd 0006` as a separate upstream/control marker candidate only. It fails the clean block-body test, so do not use it as part of the B01-B06 column model.
14e. Use the pseudo-disassembly motifs to separate startup entry classes: `C A B G1 E`, `A B G0 F D`, `D A ...`, trailer-param tail entries, and B07 finalization entries.
14f. Treat `$0c76` as the first candidate dispatch-like startup callsite and `$0c6a` as the first tail/join-control callsite. Search for the consumer logic that distinguishes those alternatives.
14g. Focus the next solver on B02 and B07:
  - B02 contains both `$0f26` (`ABG0FD`) and `$0f2e` (`DA`) entries but lacks a strong trailer edge.
  - B07 receives three strong edges and `$103a` finalization, so it should reveal whether tokens are executed by a PIC-like state machine, copied to RAM, or consumed by a 68000 routine outside the token block.
14f. Before attempting rendering, keep refining the minimal side-effect checklist: `$0a1c`, `$10a2/$10a8`, and `$0af8` are the first VDP/MMIO candidates; token classes and `$1030` still need a consumer/return model before they can be treated as render-init code.
14g. Build the first MAME-side instrumentation target around VDP control-port writes and MMIO polls rather than full gameplay: log execution of `$0a1c`, `$10ba-$10c0`, `$0af8-$0b14`, and the weak blockers `$00f8/$0d34`. The lab now prints the exact patch words, breakpoints, and expected effects for this probe.
14h. After the first instrumented run, classify the result by gate: VDP register writes seen, MMIO poll stable/looping, token-entry execution seen, or weak blocker reached before any video setup.
15. Use `$00000ad6` as the first concrete `$0d34` pointer anchor; `$00010bcc` is secondary, while bank-D variants should stay rejected until new evidence appears.
16. Build a table-aware scorer for repeated words, longword pointers, MMIO/VDP constants, and target quality. The current linear 68000 scorer is too harsh for likely setup tables.
17. Identify encrypted islands after startup by scanning for low-confidence 68000 code between known-good blocks.
18. Once a stable rule appears, port it back into a clean MAME `init_hshavoc()` patch with comments tying the magic tables to the PEEL dumps.

## Useful commands

```sh
python research/hshavoc/hshavoc_decrypt_lab.py --runs 5
python research/hshavoc/hshavoc_decrypt_lab.py --runs 0 --strict-peel-search
python research/hshavoc/hshavoc_decrypt_lab.py --runs 1 --deep-peel-search
```
