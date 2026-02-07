#!/bin/bash
cd /home/nichlas/EutherDrive

echo "Running Madou Monogatari with full VDP logging..."
echo "=================================================="

# Enable all VDP-related logging
export EUTHERDRIVE_TRACE_VDP=1
export EUTHERDRIVE_TRACE_VRAM=1
export EUTHERDRIVE_TRACE_CRAM=1
export EUTHERDRIVE_TRACE_DMA_SRC=1
export EUTHERDRIVE_TRACE_DMA_SRC_LIMIT=100

# Also enable CPU logging to see what's happening
export EUTHERDRIVE_TRACE_ANDI=1
export EUTHERDRIVE_TRACE_ROL=1

echo "Environment variables set:"
echo "  EUTHERDRIVE_TRACE_VDP=1"
echo "  EUTHERDRIVE_TRACE_VRAM=1"
echo "  EUTHERDRIVE_TRACE_CRAM=1"
echo "  EUTHERDRIVE_TRACE_DMA_SRC=1"
echo "  EUTHERDRIVE_TRACE_DMA_SRC_LIMIT=100"
echo "  EUTHERDRIVE_TRACE_ANDI=1"
echo "  EUTHERDRIVE_TRACE_ROL=1"
echo ""

# Run for a limited number of frames to capture initialization
echo "Running headless emulator for 60 frames..."
echo "------------------------------------------"

dotnet run --project EutherDrive.Headless -- "/home/nichlas/roms/madou.md" 60 2>&1 | \
  grep -E "(VRAM|CRAM|VDP|DMA|ANDI|ROL|frame=|addr=)" | \
  head -200

echo ""
echo "Log output saved to madou_vdp_log.txt"
echo "Check for VRAM-ODD-ADDR-WARNING messages"