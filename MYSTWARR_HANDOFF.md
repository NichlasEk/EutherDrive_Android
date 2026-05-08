# Mystic Warriors Handoff

Current date: 2026-05-04.

Scope: Mystic Warriors (`mystwarr.zip`) in `EutherDrive.Core/Arcade/Konami/TmntAdapter.cs`, porting MAME Konami Pre-GX/GX video semantics from local MAME at `/home/nichlas/mame`.

## Current Git State

Mystic Warriors work is committed through:

- `e644e45 Port Mystic Warriors GX video semantics`
- `f30d06f Port Mystic Warriors CCU and sprite semantics`
- `ea40c30 Render Mystic Warriors tiles from decoded graphics`
- `6832e6a Port Mystic Warriors mixer priority gating`

`TmntAdapter.cs` currently has no uncommitted diff after the last rejected experiment was backed out.

Do not revert unrelated dirty files. Current unrelated dirty files seen during handoff:

- `EutherDrive.Core/Arcade/Cps1/Cps1Ym2151.cs`
- `EutherDrive.Core/MdTracerAdapter.cs`
- `EutherDrive.Core/Sega32X/Sega32XScaffoldCore.cs`
- `EutherDrive.Core/Sega32X/Sega32XSh2Cpu.cs`
- `EutherDrive.Core/SegaCd/SegaCdMemory.cs`
- `EutherDrive.Headless/Program.cs`
- `notes/32x_perf_flags_2026-05-02.md`
- `notes/32x_perf_next_dragon_2026-05-01.md`

This handoff file itself is untracked unless you choose to add it.

## Verified Status

Build after the mixer priority commit succeeded:

```sh
dotnet build EutherDrive.Headless/EutherDrive.Headless.csproj -c Release --nologo
```

Result: `0 Error(s)`, many existing warnings.

Slot 1 savestate:

```text
/home/nichlas/roms/MAME/MysticWarriors/mystwarr.zip_98b1a8d5.euthstate
```

Useful run:

```sh
mkdir -p /tmp/mystwarr_mixer_order
env EUTHERDRIVE_HEADLESS_CORE=tmnt \
    EUTHERDRIVE_SAVESTATE_SLOT=1 \
    EUTHERDRIVE_HEADLESS_DUMP_DIR=/tmp/mystwarr_mixer_order \
    dotnet run --no-build --project EutherDrive.Headless/EutherDrive.Headless.csproj -c Release -- \
    --load-savestate /home/nichlas/roms/MAME/MysticWarriors/mystwarr.zip \
    /home/nichlas/roms/MAME/MysticWarriors/mystwarr.zip_98b1a8d5.euthstate 1
```

After `6832e6a`, the user confirmed the slot 1 image is better: clean `Audio House` background, proper layer ordering, no vertical stripe corruption. Still no visible sprites.

Important debug from slot 1:

```text
k055555=inp=1F pri=10/D0/50/90/obj00 pal=04/05/06/07/00
k053245=... regs=02/E9/00/00/20/00/00/00 act=10 live=10/10 vis=0 pix=0
calc=@040/p9 raw=09F y=-154..-115 x=448..527 wh=4x2
```

Sprite-only render still blank:

```sh
env EUTHERDRIVE_HEADLESS_CORE=tmnt \
    EUTHERDRIVE_SAVESTATE_SLOT=1 \
    EUTHERDRIVE_MYSTWARR_RENDER_MASK=s \
    EUTHERDRIVE_HEADLESS_DUMP_DIR=/tmp/mystwarr_sonly \
    dotnet run --no-build --project EutherDrive.Headless/EutherDrive.Headless.csproj -c Release -- \
    --load-savestate /home/nichlas/roms/MAME/MysticWarriors/mystwarr.zip \
    /home/nichlas/roms/MAME/MysticWarriors/mystwarr.zip_98b1a8d5.euthstate 1
```

Observed: `nonzero_pixels=0`, `vis=0`, `pix=0`.

## What Was Fixed

Major useful fix:

- `Decode5BppPixel` now uses decoded tile graphics, not raw ROM bytes. This removed the severe vertical bitplane stripes.

Mixer priority fix:

- K055555 input enables are honored.
- Layer priority sorting was flipped to match MAME-style descending priority object ordering. This made the slot 1 background visibly better.

K055555 values in the state look sane:

- all inputs enabled: `inp=1F`
- layer priorities: A/B/C/D = `10/D0/50/90`
- palette bases: A/B/C/D/OBJ = `04/05/06/07/00`

## Rejected Experiment

I tested moving Mystic `dx=-48, dy=-24` before wrap in `TryComputeSpriteBounds`, because MAME applies GX offsets before wrap when using z-buffered GX draw. It made the first active sprite worse in slot 1:

- before: `y=-154..-115`
- after experiment: `y=-202..-163`

That patch was backed out and not committed. Do not reapply it blindly.

## Key MAME References

Mystic screen update in `/home/nichlas/mame/src/mame/konami/mystwarr_v.cpp`:

```cpp
m_layer_colorbase[i] = m_k055555->K055555_get_palette_index(i) << 4;
m_sprite_colorbase = m_k055555->K055555_get_palette_index(4) << 5;
konamigx_mixer(screen, bitmap, cliprect, nullptr, 0, nullptr, 0, mixerflags, nullptr, 0);
```

Mystic tile callback:

```cpp
const uint8_t mix_code = attr >> 2 & 0b11;
if (mix_code) { priority = 1; m_last_alpha_tile_mix_code = mix_code; }
color = m_layer_colorbase[layer] | (color >> 1 & 0x0f);
```

Mystic sprite callback:

```cpp
priority_mask = color & 0xe0;
const int effect = ((color >> 8) & 0b11) << K055555_MIXSHIFT;
color = m_sprite_colorbase | (color & 0x1f) | effect;
```

K055673 config in `/home/nichlas/mame/src/mame/konami/mystwarr.cpp`:

```cpp
m_k055673->set_config(K055673_LAYOUT_GX, -48, -24);
```

Mystic sprite RAM map in MAME:

```cpp
map(0x400000, 0x40ffff).rw(FUNC(mystwarr_state::k053247_scattered_word_r), FUNC(mystwarr_state::k053247_scattered_word_w)).share("spriteram");
```

The scattered handler maps CPU-visible 0x10000 RAM onto the K055673 internal 0x800-word view only when `(offset & 0x0078) == 0`.

## Next Best Step

Priority mixer was the right step for layers. The next likely issue is still sprite plumbing, not the tile layers.

Concrete next investigation:

1. Add temporary debug in `K053245` to list all active Mystic sprite bounds, not just the first sorted one.
2. Check whether every active object is offscreen or whether the sorted/debug path is misleading.
3. Compare local `TryComputeSpriteBounds` against MAME `k053247_draw_single_sprite_gxcore`, but pay attention to which local path uses GX z-buffer semantics.
4. If bounds are all offscreen, inspect 68000 writes into `0x400000..0x40ffff` and `MystwarrScatterOffset`; a wrong scattered mapping can produce plausible active count but wrong x/y/code words.
5. If some bounds are visible but pixels are zero, focus on `DecodeMystwarrSpritePixel` / GX sprite ROM 5bpp combination.

Likely files/sections:

- `RenderMystwarr`
- `K053245.RenderMystwarrPriority`
- `K053245.RenderSprites`
- `K053245.BuildSortedSpriteList`
- `K053245.TryComputeSpriteBounds`
- `K053245.DecodeMystwarrSpritePixel`
- `K053245.ReadMystwarrCombinedSpriteByte`
- `K053245.WriteMystwarrScatteredWord`
- `K053245.MystwarrScatterOffset`

## Useful Commands

```sh
rg -n "RenderMystwarr|RenderMystwarrPriority|RenderSprites|BuildSortedSpriteList|TryComputeSpriteBounds|DecodeMystwarrSpritePixel|MystwarrScatterOffset" EutherDrive.Core/Arcade/Konami/TmntAdapter.cs
rg -n "k053247_scattered_word|k053247_draw_single_sprite_gxcore|konamigx_mixer|mystwarr_sprite_callback" /home/nichlas/mame/src/mame/konami /home/nichlas/mame/src/devices/video
git status --short
dotnet build EutherDrive.Headless/EutherDrive.Headless.csproj -c Release --nologo
```
