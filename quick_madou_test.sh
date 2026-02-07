#!/bin/bash
cd /home/nichlas/EutherDrive

echo "Quick Madou test with ROL fix..."
echo "================================"

ROM_PATH="/home/nichlas/roms/madou.md"
if [ ! -f "$ROM_PATH" ]; then
    echo "ERROR: ROM not found"
    exit 1
fi

# Create log directory
LOG_DIR="./madou_test_logs_$(date +%H%M%S)"
mkdir -p "$LOG_DIR"

echo "Log directory: $LOG_DIR"
echo "ROM: $ROM_PATH"
echo ""

# Set debug environment variables
export EUTHERDRIVE_TRACE_MD_VDP=1
export EUTHERDRIVE_TRACE_CRAM=1
export EUTHERDRIVE_TRACE_VDP=1
export EUTHERDRIVE_DEBUG_ROTATE=1
export EUTHERDRIVE_TRACE_M68K=1
export EUTHERDRIVE_FRAME_LIMIT=30  # Just 30 frames for quick test

echo "Running emulator for 30 frames..."
echo "Looking for debug messages about ROL.L #8 and D0 fix..."
echo ""

# Run emulator and capture output
cd bin/headless
timeout 10 ./EutherDrive.Headless "$ROM_PATH" 2>&1 | tee "/home/nichlas/EutherDrive/$LOG_DIR/emulator.log"

echo ""
echo "Test complete. Check $LOG_DIR/emulator.log"
echo ""
echo "Searching for key debug messages..."
echo ""

# Check log for our debug messages
cd /home/nichlas/EutherDrive
if [ -f "$LOG_DIR/emulator.log" ]; then
    echo "=== DEBUG MESSAGES FOUND ==="
    grep -i "debug\|rol\|andi\|d0\|013a50\|013a58" "$LOG_DIR/emulator.log" | head -20
    echo ""
    
    echo "=== CHECKING FOR FIX APPLICATION ==="
    if grep -q "DEBUG-FIX.*FIXING D0" "$LOG_DIR/emulator.log"; then
        echo "✓ D0 fix is being applied!"
    else
        echo "✗ D0 fix NOT found in logs"
    fi
    
    if grep -q "ROL-MADOU-CRITICAL" "$LOG_DIR/emulator.log"; then
        echo "✓ ROL debug logging active"
    else
        echo "✗ ROL debug logging NOT found"
    fi
    
    if grep -q "ANDI-MADOU-CRITICAL" "$LOG_DIR/emulator.log"; then
        echo "✓ ANDI debug logging active"
    else
        echo "✗ ANDI debug logging NOT found"
    fi
else
    echo "ERROR: Log file not created"
fi