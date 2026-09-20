# Duke: rectangular muzzle flash and sprites visible through roofs

Baseline: `ae8a3c8e`. User slot 2 reproduces the muzzle flash; slot 3 reproduces
orange gas cylinders visible through the sloping roof. The saves were extracted
read-only into `.build-tmp/duke-flash-2026-09-20/` and
`.build-tmp/duke-occlusion-2026-09-20/`. No game-specific rendering rule is added.

## Causes and corrections

The flash uses I4 intensity textures with combiner `fc309661552eff7f`, yellow
primitive color and red environment color. RGB interpolates between those colors;
alpha is texture alpha times primitive alpha. The intensity decoder previously
forced alpha to 255, turning the dark texture border into opaque red. I4 and I8
now copy intensity to alpha as well as RGB. Palette-enabled decoding still uses
palette alpha. This follows Nintendo's [texture alpha tutorial](https://jrra.zone/n64/doc/tutorial/graphics/5/5_2.htm).

Correct intensity alpha also exposed an existing sky-blending error. Duke's sky
sets blender mux `0x0f0a`: both banks select PIXEL * ZERO + PIXEL * ONE. The old
renderer treated FORCE_BL as ordinary source-over blending regardless of that
mux. Mode decoding now recognizes this exact identity blend and disables the
unnecessary source-over operation. This preserves sky color with partial texture
alpha. The effective blend flag remains in the existing savestate field; no
format change is needed. Other blender equations remain on the existing path.
See Nintendo's [render-mode reference](https://ultra64.ca/files/documentation/online-manuals/man/n64man/gdp/gDPSetRenderMode.html).

The cylinders are texture rectangles, not depth-interpolated triangles. Their
commands set mode `ef80ac3f005049dd` and primitive depths `7f75`, `7f73`, `7f72`.
The rectangle rasterizer previously ignored Z compare/update entirely. It now
uses primitive Z and delta-Z for one/two-cycle rectangles when primitive depth
and compare/update are enabled. Alpha rejection precedes depth writes. Copy
mode and rectangles without primitive-Z selection retain their existing path.
This matches Nintendo's [primitive-depth rectangle example](https://ultra64.ca/files/documentation/online-manuals/man-v5-2/allman52/tutorial/graphics/10/10_2.htm).

## Reproduction and verification

The probe's new `N64_PROBE_FIRE_INPUT=1` holds Z from startup and captures a
frame each second. Slot 2 fired immediately and reproduced the opaque red box.
`N64_PROBE_CAPTURE_RDP=1` captured a complete frame for each scene. Replaying the
tapes with the original core matched the captured images byte-for-byte:
1,152 command lists for slot 2 and 1,155 for slot 3. Replaying those identical
tapes with the fix produces a flame with transparent edges and a roof that hides
the cylinders, while preserving sky color. `capturedFramebufferMatches=False`
for the fixed core is expected because the captured reference contains the bugs.

Artifacts for each scene:
- `capture/rdp-start.bin`, `capture/rdp-tape.bin`: fixed-work input.
- `baseline-replay/rdp-final.ppm`: exact original-core reproduction.
- `final-replay/rdp-final.ppm`, `final.png`: corrected output.
- `final-live/` and `final-live.log`: ordinary CPU/RSP/RDP smoke run.

`--check-sprites` passes 1,092 intensity cases, 960 rectangle-depth cases and
24 blender/savestate cases. These cover every I4/I8 value, both nibbles and row
parities, TLUT alpha, foreground/background sprites, transparent pixels, compare
and update independently, primitive-source selection, copy bypass, flipped
rectangles, depth tolerances, passthrough blending and save/load round trips.
Existing checks pass: 6,845 render/interrupt cases, 24 rectangle-shade cases,
106,496 sampler cases, 156,352 combiner cases, 65,536 RGBA conversions and
1,048,576 filtering cases.

The initial slot-2 container hash was
`032a704a0bfd23f4fb5d940d7a5c00529734dde9f0acf779eeeea8df98ed060f`.
After the user added slot 3, the container hash was
`04501d13f8f2a9e1f9c633b2c890fe04457a4a235fba3c87992cd7884c659ef5`.
The emulator save container was never written by the investigation.

Final ordinary execution completed 20 seconds of firing from slot 2 and
10 seconds from slot 3, both with zero unknown opcodes. Multiple freshly drawn
shots have transparent flash edges; the roof occludes the cylinders. The first
slot-2 output still contains the old rectangular flash from in-flight framebuffer
contents restored with the state. Subsequent outputs (including shots in samples
3, 6 and 11) are corrected; old framebuffer pixels are not rewritten during load.

Release UI build: zero errors, 383 existing warnings. UI and tested probe core
SHA-256 both equal
`bc6187bc3778a01cda377b7c583d5c37df4902a713b7be7e70c38d8ad9b39c8d`.
The current save container still has the slot-3 hash recorded above.
