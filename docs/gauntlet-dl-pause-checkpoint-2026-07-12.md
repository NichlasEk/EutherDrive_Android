# Gauntlet Dark Legacy pause checkpoint — 2026-07-12

## Status at pause

The ordinary cold path is reproducible and the guest is alive: it accepts
coin/start input, produces new swaps, and continues into later gameplay. The
graphics are still not recognizable. The visible output remains dense noise
and horizontal bands, so bringup is **not complete** and none of the texture
experiments below should be promoted as a fix.

The corrected ordinary cadence is the historical fast fallback (60000 CPU
steps per frame). The explicit 200000-step runner default in `561f223e` was
disproved and has been removed locally from
`tools/GauntletProbe/run-gauntdl-baseline.sh`.

## Reproducible progression

| Point | Artifact | Result |
| --- | --- | --- |
| Cold f700 | `/tmp/gauntdl-ordinary60-cold-f700.log`, `.ppm`, `.png` | `frameHash=0xf4ccc0af`, PPM SHA-256 `14efebcd674d1daf00fe00a26b19957a9e7e4b849e188fb9bdbe29bb1866c458`, swap 779; noise/bands |
| Continued f1000 | `/tmp/gauntdl-ordinary60-f1000.log`, `.ppm`, `.warm` | Same framebuffer/hash as f700. FIFO read catches write, so this is not a stuck FIFO consumer. Guest has left `Loading Game.` but makes no new swaps in this interval. |
| Coin/start f1100 | `/tmp/gauntdl-ordinary60-input-f1100.log`, `.ppm`, `.png`, `.warm` | `frameHash=0x42925e78`, swap 1307; large new render/texture wave proves input and game progression work |
| Continued f1200 | `/tmp/gauntdl-ordinary60-postinput-f1200.log`, `.ppm`, `.png`, `.warm` | `frameHash=0xb38fc156`, swap 1379; frames continue changing but remain unreadable |

Available restart states are:

- `/tmp/gauntdl-ordinary60-f1000.warm`
- `/tmp/gauntdl-ordinary60-input-f1100.warm`
- `/tmp/gauntdl-ordinary60-postinput-f1200.warm`

The former ordinary f700 state is no longer available. The f520 path was later
overwritten by a stream-27-only run, although that run was byte-identical to
the ordinary f700 framebuffer. Prefer the three later states above or a fresh
ordinary cold run when provenance matters.

## What the latest traces established

At post-input f1200, active repeated Type3 quads come from `pc=0x800c4e5c`,
`cmd=0x0180A8CB`, mode `0x8C24100F`, LOD `0x000020C6`, register base
`0x00001C00`, format 0, 256x256. With the current base shift and sample bias,
the surface resolves to base `0x00E510`, and many triangles sample 75–100%
zero bytes. The serialized late state no longer retains useful writer ownership,
so the next useful trace must connect this active surface to its correct
descriptor/file page earlier in the ordinary cold path.

The `font_story` investigation found a real bad classification: the 256-packet
Type5 walk uses the live font allocation and continues into a GED-overlap
range. However, skipping only that upload from both f520 and frame zero was
byte-identical to the ordinary baseline. It is a duplicate/symptom, not the
visible root cause. Global disk-word replacement and GED zero-fill changed the
picture but incorrectly treated model/float data as texture pixels.

Restricting the source-limit table to stream 27 was inert and reproduced the
ordinary framebuffer exactly. Do not promote it.

The final existing narrow experiment,
`EUTHERDRIVE_GAUNTDL_EXPERIMENT_RUNTIME_WORLD_TEXTURE_UPLOAD_SOURCE=1`, mapped
source `0x802e1918` to `textures.rom` offset `0x0141ff20`. Its cold f700 result
is in `/tmp/gauntdl-ordinary60-worldtex-f700.log`, `.ppm`, and `.png`:
`frameHash=0xc98cd93c`, texture-map nonzero writes 776976. It substantially
changes the colors and data distribution but still shows only noise and
horizontal bands. This proves that `textures.rom` data affects the active
render path, but that fixed global offset/source substitution is not the
correct mapping.

## Negative experiments not to promote

- Explicit 200000-step ordinary default: wrong execution family.
- Texture sample bias 0: `frameHash=0x6624aa3e`, still noise.
- Unshifted base plus bias 0: `frameHash=0x41ec3715`, fewer zero samples but
  still noise.
- Global zero-base disk words: `frameHash=0x077ce75d`, overwrites live objects.
- GED zero-fill and linear variants: more color/structure but invalid content.
- Source-specific `font_story` skip: exact ordinary baseline, no visual effect.
- Broad 256-packet skip: also removes valid sources.
- Stream-27-only limit table: exact ordinary baseline.
- Fixed world-texture source mapping: `frameHash=0xc98cd93c`, altered but still
  unreadable.

## Best next continuation

Resume from the ordinary 60000-step cold path. Do not spend another round on
global base shifts, sample biases, `font_story`, or stream 27. Trace the
descriptor/page selection that feeds the active post-input 256x256 Type3
surface and derive the `textures.rom` file offset per object/page rather than
substituting one fixed source globally. Any candidate must be verified by:

1. a fresh cold run from frame zero;
2. a recognizable screenshot rather than hash change alone;
3. coin/start progression through at least f1200;
4. stable repeated frame behavior;
5. normal Release build, then a narrow commit and push.

## Repository state

Current HEAD is `561f223e Align Gauntlet cold probe timing`. The normal Release
build completed with 0 errors (346 warnings). Two intended local changes are
uncommitted at pause:

- this documentation/progress update plus
  `docs/gauntlet-dl-progress-plan-2026-06-30.md`;
- removal of the disproved explicit 200000-step default from
  `tools/GauntletProbe/run-gauntdl-baseline.sh`.

No emulator code experiment is left enabled or modified. A direct push to the
shared `main` branch was not performed because it requires explicit approval.
