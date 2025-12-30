# EutherDrive Memo

## Z80 bus request semantics (A11100)
- 68K code expects bit0=1 to request/grant the Z80 bus and bit0=0 to release.
- ROMs spin on `btst #0,$A11100` until it clears after a request (e.g. Sonic at
  0x0011C6 loops on `BNE`).
- Fix applied: treat `0x0100` writes as "request/grant" and return 0 on reads
  when granted. Old inverted logic caused infinite loop and no VDP init.
- Implemented in:
  - `EutherDrive.Core/MdTracerCore/md_bus.cs` (write/read busreq semantics)
  - `EutherDrive.Core/MegaDriveBus.cs` (read busreq semantics)
- Do NOT invert busreq semantics again; it breaks boot loops and causes games to hang.

## Shinobi III busreq wait loop
- Shinobi III (EU) can hang in a tight loop at 0x06A4AC/0x06A4B4:
  - `33FC 0100 00A1 1100` (move.w #$0100,$A11100)
  - `0839 0000 00A1 1100` (btst #0,$A11100)
  - `66F6` (BNE back to btst)
- If this loop spins, A11100 reads are still returning bit0=1 (bus not granted).
- Verify busreq writes are seen and A11100 reads drop to 0 after the write.
  If not, consider accepting bit0 or bit8 for word writes in busreq handling.

## Z80 window odd-byte reads (A00000..A0FFFF)
- Some games poll odd Z80-window addresses like A01FFD for mailbox/handshake bits.
- 68k byte reads on odd Z80-window addresses should map to the next even byte (addr+1).
- Fix: apply odd-to-next mapping only for reads; keep writes unchanged to avoid corruption.
- Controlled by `EUTHERDRIVE_Z80_ODD_READ_TO_NEXT` (default on) in:
  - `EutherDrive.Core/MdTracerCore/md_bus.cs`
  - `EutherDrive.Core/MegaDriveBus.cs`
