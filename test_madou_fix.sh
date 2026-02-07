#!/bin/bash
cd /home/nichlas/EutherDrive

echo "Testing Madou graphics fix..."
echo "============================="

# First, let's check if we can run the headless emulator
if [ ! -f "./bin/EutherDrive.Headless" ] && [ ! -f "./EutherDrive.Headless/bin/Debug/net8.0/EutherDrive.Headless.dll" ]; then
    echo "Building headless emulator..."
    dotnet build EutherDrive.Headless/EutherDrive.Headless.csproj
fi

# Check for ROM file
ROM_PATH="/home/nichlas/roms/madou.md"
if [ ! -f "$ROM_PATH" ]; then
    echo "ERROR: ROM file not found at $ROM_PATH"
    exit 1
fi

echo "ROM found: $ROM_PATH"
echo ""

# Create test directory
TEST_DIR="./test_madou_fixed_$(date +%Y%m%d_%H%M%S)"
mkdir -p "$TEST_DIR"

echo "Test directory: $TEST_DIR"
echo ""

# Run headless emulator with Madou ROM
# We'll run for a few frames to see if graphics are fixed
echo "Running headless emulator with Madou ROM..."
echo "This will take a moment..."

# First, let's check if there's a simple way to run the emulator
# Look for existing test scripts
if [ -f "./test_rendering_components.sh" ]; then
    echo "Using existing test script..."
    # Modify an existing test script for Madou
    cp ./test_rendering_components.sh ./test_madou_temp.sh
    sed -i 's|roms/contra\.gen|roms/madou.md|g' ./test_madou_temp.sh
    sed -i 's|headless_base|'"$TEST_DIR"'|g' ./test_madou_temp.sh
    chmod +x ./test_madou_temp.sh
    
    # Run for fewer frames to be quick
    export EUTHERDRIVE_FRAME_LIMIT=60
    ./test_madou_temp.sh 2>&1 | tee "$TEST_DIR/test.log"
    rm ./test_madou_temp.sh
else
    echo "No existing test script found. Creating simple test..."
    
    # Create a simple C# test program
    cat > "$TEST_DIR/TestMadouSimple.cs" << 'EOF'
using System;
using System.IO;
using EutherDrive.Core;
using EutherDrive.Headless;

namespace TestMadouSimple
{
    class Program
    {
        static void Main(string[] args)
        {
            string romPath = "/home/nichlas/roms/madou.md";
            
            if (!File.Exists(romPath))
            {
                Console.WriteLine($"ROM not found: {romPath}");
                return;
            }
            
            Console.WriteLine($"Testing Madou ROM: {romPath}");
            Console.WriteLine("Running headless emulator for 60 frames...");
            
            try
            {
                var emulator = new HeadlessEmulator();
                emulator.LoadRom(romPath);
                
                // Run for 60 frames
                for (int i = 0; i < 60; i++)
                {
                    emulator.RunFrame();
                    if (i % 10 == 0)
                        Console.WriteLine($"Frame {i} completed");
                }
                
                Console.WriteLine("Test completed successfully!");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
                Console.WriteLine(ex.StackTrace);
            }
        }
    }
}
EOF

    echo "Compiling test program..."
    cd "$TEST_DIR"
    mcs -reference:../EutherDrive.Core/bin/Debug/net8.0/EutherDrive.Core.dll \
        -reference:../EutherDrive.Headless/bin/Debug/net8.0/EutherDrive.Headless.dll \
        -reference:System.Runtime.Serialization.dll \
        TestMadouSimple.cs 2>&1 | tee compile.log
        
    if [ -f "TestMadouSimple.exe" ]; then
        echo "Running test..."
        mono TestMadouSimple.exe 2>&1 | tee run.log
    else
        echo "Compilation failed!"
        cat compile.log
    fi
fi

echo ""
echo "Test completed. Check $TEST_DIR for results."
echo ""
echo "If graphics are fixed, you should see proper palette data being loaded."
echo "The key fix was in ANDI.W instruction preserving high bits of registers."