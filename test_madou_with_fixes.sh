#!/bin/bash
cd /home/nichlas/EutherDrive

echo "Testing Madou with our ROL.L #8 fix..."
echo "======================================"

# Check for ROM
ROM_PATH="/home/nichlas/roms/madou.md"
if [ ! -f "$ROM_PATH" ]; then
    echo "ERROR: ROM file not found at $ROM_PATH"
    exit 1
fi

echo "ROM found: $ROM_PATH"
echo ""

# Create test directory
TEST_DIR="./test_madou_rol_fix_$(date +%Y%m%d_%H%M%S)"
mkdir -p "$TEST_DIR"

echo "Test directory: $TEST_DIR"
echo ""

# Set environment variables for debugging
export EUTHERDRIVE_TRACE_MD_VDP=1
export EUTHERDRIVE_TRACE_CRAM=1
export EUTHERDRIVE_TRACE_VDP=1
export EUTHERDRIVE_DEBUG_ROTATE=1
export EUTHERDRIVE_TRACE_M68K=1
export EUTHERDRIVE_FRAME_LIMIT=120  # Run for 120 frames

echo "Running headless emulator with debug logging..."
echo "This will capture logs about ROL.L #8, ANDI.W, and D0 values"
echo ""

# Run the headless emulator
cd bin/headless
./EutherDrive.Headless "$ROM_PATH" 2>&1 | tee "$TEST_DIR/emulator.log"

echo ""
echo "Test completed. Check $TEST_DIR/emulator.log for debug output."
echo ""
echo "Looking for these debug messages:"
echo "1. [DEBUG-FIX] FIXING D0 from ... to 0x07000000"
echo "2. [ROL-MADOU-CRITICAL] PC=0x013A50 ROL.L #8,D0: ..."
echo "3. [ANDI-MADOU-CRITICAL] PC=0x013A58 D0 before=... AND with 0x00FF => ..."
echo ""
echo "If we see these, our fix is being applied!"
echo "If graphics are still corrupt, the bug is elsewhere (VDP, palette, etc.)"