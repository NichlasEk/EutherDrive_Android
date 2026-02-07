#!/bin/bash
cd /home/nichlas/EutherDrive/bin/headless

echo "=== MADOU MAX LOGGING TEST ==="
echo "Running with all debug logging enabled..."
echo ""

# Enable all debug logging
export EUTHERDRIVE_TRACE_VDP=1
export EUTHERDRIVE_TRACE_CRAM=1
export EUTHERDRIVE_TRACE_VRAM=1
export EUTHERDRIVE_TRACE_DMA=1
export EUTHERDRIVE_TRACE_M68K=1
export EUTHERDRIVE_TRACE_PC=1
export EUTHERDRIVE_FRAME_LIMIT=200

echo "Environment variables set:"
env | grep EUTHERDRIVE
echo ""

echo "Running emulator..."
echo "=== OUTPUT START ==="
./EutherDrive.Headless "/home/nichlas/roms/madou.md" 2>&1 | tee /tmp/madou_full.log | head -500
echo "=== OUTPUT END ==="

echo ""
echo "Analyzing log..."
echo ""

echo "=== CRAM WRITES ==="
grep -i "cram" /tmp/madou_full.log | head -20

echo ""
echo "=== DMA ACTIVITY ==="
grep -i "dma" /tmp/madou_full.log | head -20

echo ""
echo "=== VDP WRITES ==="
grep -i "vdp.*write\|vdp.*ctrl" /tmp/madou_full.log | head -20

echo ""
echo "=== MADOU MESSAGES ==="
grep -i "madou\|013A" /tmp/madou_full.log | head -20

echo ""
echo "=== FIRST 100 LINES OF LOG ==="
head -100 /tmp/madou_full.log