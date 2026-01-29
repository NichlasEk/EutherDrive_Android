using System;
using EutherDrive.Core;
using EutherDrive.Core.MdTracerCore;

class TestUiAudioDebug
{
    static void Main()
    {
        Console.WriteLine("[TEST-UI-AUDIO] Starting UI audio debug test...");
        
        // Enable all debugging
        Environment.SetEnvironmentVariable("EUTHERDRIVE_YM", "1");
        Environment.SetEnvironmentVariable("EUTHERDRIVE_DIAG_FRAME", "1");
        Environment.SetEnvironmentVariable("EUTHERDRIVE_TRACE_AUDIO_BUFFER", "1");
        Environment.SetEnvironmentVariable("EUTHERDRIVE_TRACE_YM_TIMING", "1");
        
        string romPath = "/home/nichlas/roms/Aladdin.md";
        
        var adapter = new MdTracerAdapter();
        if (!adapter.LoadRom(romPath))
        {
            Console.WriteLine($"[TEST-UI-AUDIO] Failed to load ROM: {romPath}");
            return;
        }
        
        Console.WriteLine("[TEST-UI-AUDIO] ROM loaded, running 10 frames...");
        
        // Simulate UI calling GetAudioBuffer() each frame
        for (int frame = 0; frame < 10; frame++)
        {
            Console.WriteLine($"\n=== Frame {frame} ===");
            
            // Run one frame
            adapter.StepFrame();
            
            // Get audio (like UI would)
            var audio = adapter.GetAudioBuffer(out int sampleRate, out int channels);
            
            Console.WriteLine($"[TEST-UI-AUDIO] Frame {frame}: audio samples={audio.Length}, rate={sampleRate}, channels={channels}");
            
            // Also check DIAG-FRAME logging
            // (this happens inside MdTracerAdapter based on env var)
        }
        
        Console.WriteLine("\n[TEST-UI-AUDIO] Test complete");
    }
}