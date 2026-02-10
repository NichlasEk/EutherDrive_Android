# SMS Z80 LDI PV Fix

## Summary
Daffy Duck in Hollywood and Illusion had corrupted backgrounds because nametable data was only
partially copied into RAM. The Z80 program uses `LDI` followed by `RET PO` to continue a copy
loop until BC reaches zero.

Our Z80 `LDI/LDD` implementation always cleared PV, so `RET PO` always returned immediately,
terminating the copy early. This left large regions of the nametable buffer as 0x00, which
were then written to VRAM.

## Fix
Set PV according to the Z80 specification for `LDI/LDD`: PV = (BC != 0) after the transfer.
This allows the program's `RET PO`/`RET PE` flow to work and the copy completes.

## Files
- `EutherDrive.Core/MdTracerCore/md_z80_operand.cs`
