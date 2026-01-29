using System;
using EutherDrive.Core;
using EutherDrive.Core.MdTracerCore;

class TestDac
{
    static void Main()
    {
        Console.WriteLine("[TEST-DAC] Starting Sonic 1 DAC test...");
        
        // Enable all DAC/PSG/YM logging
        Environment.SetEnvironmentVariable("EUTHERDRIVE_TRACE_YM", "1");
        Environment.SetEnvironmentVariable("EUTHERDRIVE_TRACE_PSG", "1");
        Environment.SetEnvironmentVariable("EUTHERDRIVE_TRACE_DAC", "1");
        Environment.SetEnvironmentVariable("EUTHERDRIVE_TRACE_AUDSTAT", "1");
        Environment.SetEnvironmentVariable("EUTHERDRIVE_Z80_YM_WAIT", "20");
        
        string romPath = "/home/nichlas/roms/sonic1.md";
        
        var adapter = new MdTracerAdapter();
        if (!adapter.LoadRom(romPath))
        {
            Console.WriteLine($"[TEST-DAC] Failed to load ROM: {romPath}");
            return;
        }
        
        Console.WriteLine("[TEST-DAC] ROM loaded, running 180 frames...");
        
        // Run for 180 frames (3 seconds at 60Hz)
        for (int frame = 0; frame < 180; frame++)
        {
            adapter.RunFrame();
            
            // Get audio samples (this will trigger audio rendering)
            var samples = adapter.GetPsgFrameSamples();
            if (samples.Length > 0 && frame % 30 == 0) // Every 0.5 seconds
            {
                Console.WriteLine($"[TEST-DAC] Frame {frame}: {samples.Length} audio samples");
            }
        }
        
        Console.WriteLine("[TEST-DAC] Test complete");
    }
}