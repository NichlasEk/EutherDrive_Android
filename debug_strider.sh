#!/bin/bash

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
export EUTHERDRIVE_LOAD_SLOT1_ON_BOOT=1
export EUTHERDRIVE_Z80_HALT_ON_BUSREQ=0

# Enable Z80 RAM dump when PC leaves boot range
export EUTHERDRIVE_DUMP_Z80_RAM=1

# Disable safe boot to let Strider run its own sound driver
export EUTHERDRIVE_Z80_SAFE_BOOT=0

echo "=== Running Strider with Z80 debug ==="
echo "ROM: ~/roms/strider.md"
echo "Savestate slot: 1"
echo "Debug PC: 0x00CF"
echo ""

cd /home/nichlas/EutherDrive
dotnet run --project EutherDrive.Headless -- ~/roms/strider.md 10 2>&1 | tee /tmp/strider_debug.log

echo ""
echo "=== Debug output saved to /tmp/strider_debug.log ==="
echo "=== Analyzing Z80 state ==="
grep -A5 -B5 "Z80-STATE\|Z80-MEMDUMP\|Z80-STUCK\|Z80-BOOT-CODE\|Z80-0000\|Z80-DEBUG\|Z80-CANRUN" /tmp/strider_debug.log | head -100