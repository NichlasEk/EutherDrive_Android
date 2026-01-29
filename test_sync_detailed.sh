#!/bin/bash

echo "=== Detailed Sync System Test with Aladdin ==="
echo "Running 50 frames with comprehensive logging..."

# Build the project
echo "Building project..."
dotnet build EutherDrive.Headless 2>&1 | tail -5

# Set up environment variables for detailed logging
export EUTHERDRIVE_LOAD_SAVESTATE=1
export EUTHERDRIVE_SAVESTATE_SLOT=1
export EUTHERDRIVE_HEADLESS_AUDIO=1
export EUTHERDRIVE_FRAMES_TO_RUN=50

# Sync system debugging
export EUTHERDRIVE_TRACE_YM_TIMING=1
export EUTHERDRIVE_TRACE_Z80_TIMING=1
export EUTHERDRIVE_DIAG_FRAME=1

# Additional debugging
export EUTHERDRIVE_TRACE_YM=1
export EUTHERDRIVE_TRACE_AUDSTAT=1

echo ""
echo "Environment variables set:"
echo "EUTHERDRIVE_LOAD_SAVESTATE=1 (slot 1)"
echo "EUTHERDRIVE_HEADLESS_AUDIO=1"
echo "EUTHERDRIVE_FRAMES_TO_RUN=50"
echo "EUTHERDRIVE_TRACE_YM_TIMING=1"
echo "EUTHERDRIVE_TRACE_Z80_TIMING=1"
echo "EUTHERDRIVE_DIAG_FRAME=1"
echo ""

# Run the test
echo "Running headless for 50 frames..."
echo "=== LOG OUTPUT START ==="
timeout 10 dotnet run --project EutherDrive.Headless -- roms/aladdin.bin 2>&1 | \
  grep -E "\[(YM-SYNC|SYNC-COMMON|MASTER-CYCLES|Z80-TIMING|DIAG-FRAME|AUDSTAT)\]" | \
  head -100
echo "=== LOG OUTPUT END ==="

echo ""
echo "=== Analysis ==="
echo "Key things to look for:"
echo "1. YM-SYNC-DEBUG: targetCycle, cyclesToDo, sync.CurrentCycle values"
echo "2. SYNC-COMMON-DEBUG: How sync.CurrentCycle updates"
echo "3. MASTER-CYCLES-DEBUG: How _masterCycles increases"
echo "4. cyclesToDo values: Should be reasonable (not 0, not huge)"
echo "5. Z80-TIMING-SYNC: How often Z80 syncs YM2612"
echo "6. DIAG-FRAME: ymAdvanceCalls per frame"
echo "7. AUDSTAT: YM register write statistics"

echo ""
echo "If cyclesToDo is always 0: Sync system not working"
echo "If cyclesToDo is huge (>10000): _masterCycles advancing too fast"
echo "If cyclesToDo is reasonable (100-1000): Sync working correctly"
echo "If ymAdvanceCalls is 0: YM2612 not advancing at all"