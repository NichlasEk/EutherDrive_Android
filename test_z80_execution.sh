#!/bin/bash

echo "=== Testing Z80 execution and YM2612 timing ==="
echo ""

# Enable detailed logging
export EUTHERDRIVE_TRACE_Z80=1
export EUTHERDRIVE_TRACE_Z80SIG=1
export EUTHERDRIVE_TRACE_YM=1
export EUTHERDRIVE_TRACE_YMBUSY=1
export EUTHERDRIVE_YM=1
export EUTHERDRIVE_TRACE_Z80_IO=1

echo "Running Sonic 2 for 20 frames..."
cd /home/nichlas/EutherDrive
dotnet run --project EutherDrive.Headless -- ~/roms/sonic2.md 20 2>&1 | tee /tmp/z80_exec_test.log

echo ""
echo "=== Analysis ==="
echo "Looking for Z80 execution and YM2612 access..."
grep -A2 -B2 "Z80-EXEC\|Z80BOOT\|YMBUSY\|YMTRACE\|Z80.*active=True" /tmp/z80_exec_test.log | head -100