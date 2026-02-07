#!/bin/bash
cd /home/nichlas/EutherDrive

echo "Testing Sonic 2 from clean state..."
echo "==================================="

# Remove any saved states or caches
rm -f "/home/nichlas/roms/sonic2.srm" 2>/dev/null
rm -f "/home/nichlas/roms/sonic2.state" 2>/dev/null

# Build clean
echo "Building..."
dotnet build --configuration Debug > /dev/null 2>&1
if [ $? -ne 0 ]; then
    echo "Build failed!"
    exit 1
fi

echo "Build successful."

# Run for a few frames
echo "Running Sonic 2 for 60 frames..."
echo "--------------------------------"

dotnet run --project EutherDrive.Headless -- "/home/nichlas/roms/sonic2.md" 60 2>&1 | \
  grep -E "(frame=|VDP|VRAM|CRAM|DMA|error|Error|ERROR)" | \
  head -50

echo ""
echo "Check if any PPM files were created in EutherDrive.Headless/"
ls -la EutherDrive.Headless/*.ppm 2>/dev/null | head -5