#!/bin/bash

# Cold boot - no savestate
# Debug Z80 environment variables
export EUTHERDRIVE_TRACE_Z80_DEBUG_PC=0x00CF
export EUTHERDRIVE_TRACE_Z80_DEBUG_PC_LIMIT=20
export EUTHERDRIVE_TRACE_Z80STEP=1
export EUTHERDRIVE_TRACE_Z80SIG=1
export EUTHERDRIVE_TRACE_Z80SIG_TRANS=1
export EUTHERDRIVE_TRACE_BOOT=1
export EUTHERDRIVE_TRACE_Z80=1
export EUTHERDRIVE_TRACE_Z80_STATS=1
export EUTHERDRIVE_TRACE_Z80_INT=1
export EUTHERDRIVE_Z80_HALT_ON_BUSREQ=0

# Enable Z80 RAM dump when PC leaves boot range
export EUTHERDRIVE_DUMP_Z80_RAM=1

# Disable safe boot (let game upload its own driver)
export EUTHERDRIVE_Z80_SAFE_BOOT=0

# DO NOT load savestate
unset EUTHERDRIVE_LOAD_SLOT1_ON_BOOT

echo "=== Running Strider COLD BOOT (no savestate) ==="
echo "ROM: ~/roms/strider.md"
echo "Frames: 60 (to get past title screen)"
echo "Debug PC: 0x00CF"
echo ""

cd /home/nichlas/EutherDrive
dotnet run --project EutherDrive.Headless -- ~/roms/strider.md 60 2>&1 | tee /tmp/strider_coldboot.log

echo ""
echo "=== Debug output saved to /tmp/strider_coldboot.log ==="
echo "=== Analyzing Z80 state ==="
grep -A5 -B5 "Z80-STATE\|Z80-MEMDUMP\|Z80-STUCK\|Z80-BOOT-CODE\|Z80-0000\|Z80-DEBUG\|Z80-CANRUN\|Z80-RUN\|m68k boot" /tmp/strider_coldboot.log | head -150