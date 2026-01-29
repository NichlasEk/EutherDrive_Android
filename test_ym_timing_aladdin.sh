#!/bin/bash

echo "=== Testing YM2612 timing logging with Aladdin ==="
echo ""

# Build the project
echo "Building the project..."
cd /home/nichlas/EutherDrive
if ! dotnet build --configuration Debug 2>&1 | tail -5; then
    echo "Build failed!"
    exit 1
fi

echo ""
echo "Setting up environment variables for YM2612 timing debugging..."

# Enable all our new logging
export EUTHERDRIVE_DIAG_FRAME=1
export EUTHERDRIVE_TRACE_YM_TIMING=1
export EUTHERDRIVE_TRACE_YM_WRITE_TIMING=1
export EUTHERDRIVE_TRACE_AUDIO_BUFFER=1

# Also enable existing YM tracing for context
export EUTHERDRIVE_TRACE_YM=1
export EUTHERDRIVE_TRACE_DAC=1
export EUTHERDRIVE_TRACE_AUDSTAT=1
export EUTHERDRIVE_Z80_SAFE_BOOT=1
export EUTHERDRIVE_YM=1

echo "Environment variables set:"
echo "EUTHERDRIVE_DIAG_FRAME=1"
echo "EUTHERDRIVE_TRACE_YM_TIMING=1"
echo "EUTHERDRIVE_TRACE_YM_WRITE_TIMING=1"
echo "EUTHERDRIVE_TRACE_AUDIO_BUFFER=1"
echo "EUTHERDRIVE_TRACE_YM=1"
echo "EUTHERDRIVE_TRACE_DAC=1"
echo "EUTHERDRIVE_TRACE_AUDSTAT=1"
echo "EUTHERDRIVE_Z80_SAFE_BOOT=1"
echo "EUTHERDRIVE_YM=1"
echo ""

# Check for Aladdin save state
SAVESTATE="savestates/Aladdin_a3779fc7.euthstate"
if [ ! -f "$SAVESTATE" ]; then
    echo "Aladdin save state not found: $SAVESTATE"
    echo "Looking for other Aladdin save states..."
    find savestates/ -name "*Aladdin*" -type f | head -5
    exit 1
fi

echo "Found Aladdin save state: $SAVESTATE"
echo ""

echo "Running headless for 60 frames (1 second) with save state slot 1..."
echo "This will show YM2612 timing patterns and audio buffer status."
echo ""

# Run headless with the save state
dotnet run --project EutherDrive.Headless -- \
    --savestate "$SAVESTATE" \
    --slot 1 \
    --frames 60 2>&1 | \
    grep -A1 -B1 "DIAG-FRAME\|YM-TIMING\|YM-WRITE-TIMING\|AUDIO-BUFFER\|ymAdvanceCalls" | \
    head -200

echo ""
echo "=== Analysis of results ==="
echo "Key things to look for:"
echo "1. ymAdvanceCalls should be > 0 (currently shows 0, which is the bug)"
echo "2. YM-TIMING logs should show regular advance calls"
echo "3. YM-WRITE-TIMING shows when YM registers are written"
echo "4. AUDIO-BUFFER shows audio generation timing"
echo ""
echo "If ymAdvanceCalls is still 0, we need to implement force chip advance."