#!/bin/bash
cd /home/nichlas/EutherDrive

echo "Testing Madou with detailed logging..."
echo "======================================"

# Set environment variables for logging
export EUTHERDRIVE_TRACE_MADOU_D0_SRC=1
export EUTHERDRIVE_TRACE_MADOU_D0_SRC_LIMIT=100
export EUTHERDRIVE_TRACE_PC=1
export EUTHERDRIVE_TRACE_PCWATCH_START=0x013A40
export EUTHERDRIVE_TRACE_PCWATCH_END=0x013A80

# Create output directory
TEST_DIR="./madou_log_test_$(date +%Y%m%d_%H%M%S)"
mkdir -p "$TEST_DIR"

echo "Test directory: $TEST_DIR"
echo "Logging enabled for:"
echo "  - Madou D0 source tracing"
echo "  - PC range 0x013A40-0x013A80"
echo ""

# Check for ROM
ROM_PATH="/home/nichlas/roms/madou.md"
if [ ! -f "$ROM_PATH" ]; then
    echo "ERROR: ROM not found at $ROM_PATH"
    exit 1
fi

echo "ROM found: $ROM_PATH"
echo ""

# We need to run the emulator. Let's check if we can run headless
if [ -f "./EutherDrive.Headless/bin/Debug/net8.0/EutherDrive.Headless.dll" ]; then
    echo "Running headless emulator with logging..."
    echo "This will generate a lot of output..."
    echo ""
    
    # Create a simple C# program to run the emulator
    cat > "$TEST_DIR/RunMadouLog.cs" << 'EOF'
using System;
using System.IO;
using EutherDrive.Core;
using EutherDrive.Headless;

namespace RunMadouLog
{
    class Program
    {
        static void Main(string[] args)
        {
            string romPath = "/home/nichlas/roms/madou.md";
            
            Console.WriteLine($"Loading ROM: {romPath}");
            
            try
            {
                var emulator = new HeadlessEmulator();
                emulator.LoadRom(romPath);
                
                // Run for a limited number of frames to capture the palette setup
                Console.WriteLine("Running for 120 frames to capture palette setup...");
                for (int i = 0; i < 120; i++)
                {
                    emulator.RunFrame();
                    if (i % 20 == 0)
                        Console.WriteLine($"Frame {i}...");
                }
                
                Console.WriteLine("Test completed.");
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

    echo "Compiling test runner..."
    cd "$TEST_DIR"
    mcs -reference:../EutherDrive.Core/bin/Debug/net8.0/EutherDrive.Core.dll \
        -reference:../EutherDrive.Headless/bin/Debug/net8.0/EutherDrive.Headless.dll \
        -reference:System.Runtime.Serialization.dll \
        RunMadouLog.cs 2>&1 | tee compile.log
        
    if [ -f "RunMadouLog.exe" ]; then
        echo "Running test (output to run.log)..."
        mono RunMadouLog.exe 2>&1 | tee run.log | grep -E "(MADOU|ANDI|ROL|ASL|D0=)" | head -100
        echo ""
        echo "Full output saved to $TEST_DIR/run.log"
        echo "Look for MADOU-* log messages to see what's happening."
    else
        echo "Compilation failed!"
        cat compile.log
    fi
else
    echo "Headless emulator not found. Building..."
    dotnet build EutherDrive.Headless/EutherDrive.Headless.csproj
    echo "Please run this script again after build completes."
fi

echo ""
echo "Test completed. Check $TEST_DIR for logs."