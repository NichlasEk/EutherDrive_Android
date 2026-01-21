#!/bin/bash

# Test Sonic 2 with fixed Z80 timing and disabled safe boot
echo "=== Testing Sonic 2 with Z80 timing fixes ==="
echo "Safe boot: DISABLED (Z80SafeBootEnabled=false)"
echo "Z80 starts in reset: ENABLED (Z80ResetAssertOnBoot=true)"
echo ""

# Enable detailed Z80 logging
export EUTHERDRIVE_TRACE_Z80=1
export EUTHERDRIVE_TRACE_Z80SIG=1
export EUTHERDRIVE_TRACE_Z80SIG_TRANS=1
export EUTHERDRIVE_TRACE_Z80_IO=1
export EUTHERDRIVE_TRACE_YM=1
export EUTHERDRIVE_TRACE_Z80_BOOT_IO=1
export EUTHERDRIVE_TRACE_Z80_RAM_WRITE_ADDR=0x0000

# Disable safe boot (already hardcoded to false)
# Z80ResetAssertOnBoot is already hardcoded to true

# Enable YM2612
export EUTHERDRIVE_YM=1

echo "Environment variables:"
echo "EUTHERDRIVE_TRACE_Z80=1"
echo "EUTHERDRIVE_TRACE_Z80SIG=1"
echo "EUTHERDRIVE_TRACE_Z80_IO=1"
echo "EUTHERDRIVE_TRACE_YM=1"
echo "EUTHERDRIVE_YM=1"
echo ""

echo "Running Sonic 2..."
cd /home/nichlas/EutherDrive
dotnet run --project EutherDrive.Headless -- ~/roms/sonic2.md 10 2>&1 | tee /tmp/sonic2_z80_fix.log

echo ""
echo "=== Analysis ==="
echo "Looking for Z80 reset and boot sequence..."
grep -A2 -B2 "Z80RESET\|Z80-RESET-RELEASE\|Z80SAFE\|Z80WIN\|YMTRACE" /tmp/sonic2_z80_fix.log | head -50