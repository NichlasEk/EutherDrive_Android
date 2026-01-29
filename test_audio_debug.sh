#!/bin/bash
echo "Testing audio with comprehensive logging..."
echo "=========================================="

# Enable all logging
export EUTHERDRIVE_TRACE_YM_TIMING=1
export EUTHERDRIVE_DIAG_FRAME=1
export EUTHERDRIVE_TRACE_AUDIO_BUFFER=1
export EUTHERDRIVE_YM=1

# Run for a short time to see what happens
echo "Running Aladdin for 5 seconds with logging..."
cd /home/nichlas/EutherDrive

# Build first
echo "Building..."
dotnet build --configuration Release 2>&1 | grep -E "(error|Error|ERROR|Build FAILED)" | head -5

echo ""
echo "Expected behavior with new timing system:"
echo "1. YM2612 should advance based on SystemCycles"
echo "2. Should see [YM-TIMING] logs for each advance"
echo "3. ymAdvanceCalls should be > 0"
echo "4. No negative delta warnings"
echo ""
echo "If audio is broken, we might see:"
echo "- Negative deltas (time going backwards)"
echo "- Very large deltas (jumping too far)"
echo "- No advances when Z80 is active"
echo ""

# Note: User should run the UI with these env vars
echo "To test:"
echo "1. Set the environment variables above"
echo "2. Run EutherDrive UI"
echo "3. Load Aladdin ROM"
echo "4. Check console output for timing logs"
echo ""
echo "Key things to look for in logs:"
echo "[YM-TIMING] deltaCycles=... - Should be reasonable (thousands, not millions)"
echo "[YM-TIMING-WARNING] - Any warnings indicate problems"
echo "[DIAG-FRAME] ymAdvanceCalls=... - Should be > 0"
echo "[AUDIO-BUFFER] - Audio generation timing"