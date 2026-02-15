# JOX v1 File Format

Minimal ROM-like container for the “Zuul” about-screen runtime.

## Byte Order
Little-endian for all integer fields.

## Header (64 bytes)

Offset | Size | Field | Description
---|---|---|---
0x00 | 4 | Magic | ASCII `"JOX1"`
0x04 | 2 | HeaderSize | Must be `64`
0x06 | 2 | Version | Must be `1`
0x08 | 4 | FileSize | Total file size in bytes
0x0C | 4 | ChunkCount | Number of chunk table entries
0x10 | 4 | Flags | Bit 0 = Loop, Bit 1 = HasAudio
0x14 | 4 | TicksPerSecond | VM tick rate (e.g., 60)
0x18 | 4 | EntryChunkIndex | Initial chunk index (usually 0)
0x1C | 4 | CRC32 | CRC32 of all bytes after header (optional, can be 0)
0x20 | 32 | Title | Null-terminated UTF-8 title

## Chunk Table
Immediately after the header.

Each entry is 16 bytes:

Field | Size | Description
---|---|---
Tag | 4 | ASCII chunk tag (e.g., `VEC2`, `ANIM`)
Offset | 4 | Offset from file start
Length | 4 | Byte length of chunk data
ChunkFlags | 4 | Type-specific flags (unused for v1)

## Chunk Types (v1)

### `VEC2` (geometry)

u32 `lineCount`
then `lineCount` entries of:
- `float x1, y1, x2, y2`

Coordinates are normalized to roughly `-1..+1`.

### `ANIM` (bytecode)

u32 `bytecodeLength`
then `byte[] code`

VM runs at `TicksPerSecond` and executes until `WAIT` or `END`.

#### State
- `pc` program counter
- `tick` (implicit, per tick)
- `f0..f7` float regs
- `i0..i3` int regs
- transform: `yaw`, `pitch`, `scale`, `offsetX`, `offsetY`

#### Opcodes (v1)

Op | Name | Args | Effect
---|---|---|---
0x00 | NOP | – | No-op
0x01 | END | – | Stop (Loop flag → `pc=0`)
0x10 | WAIT | u16 ticks | Pause for N ticks
0x20 | SETF | u8 reg, f32 val | `f[reg]=val`
0x21 | ADDF | u8 reg, f32 val | `f[reg]+=val`
0x30 | LERP | u8 reg, f32 target, u16 ticks | Lerp `f[reg]` to target
0x40 | EMIT | u16 eventId | Emit event (e.g., 1=ROAR)
0x50 | SHAKE | f32 amount, u16 ticks | Camera shake
0x60 | ROTY | u8 reg | `yaw += f[reg]` per tick
0x61 | ROTX | u8 reg | `pitch += f[reg]` per tick
0x70 | SETS | f32 scale | Set scale
0x71 | OFFS | f32 x, f32 y | Set offset
0x80 | DRAWSET | u32 vec2ChunkIndex | Select geometry bank

## Example (conceptual)

```
SETF f0=0.01
WAIT 60
EMIT ROAR
SHAKE 0.8 for 60 ticks
END
```
