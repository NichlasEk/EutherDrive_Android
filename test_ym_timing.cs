using System;
using System.Diagnostics;
using EutherDrive.Core.MdTracerCore;

namespace EutherDrive.Test
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("Testing YM2612 timing logging...");
            
            // Set environment variables for logging
            Environment.SetEnvironmentVariable("EUTHERDRIVE_DIAG_FRAME", "1");
            Environment.SetEnvironmentVariable("EUTHERDRIVE_TRACE_YM_TIMING", "1");
            Environment.SetEnvironmentVariable("EUTHERDRIVE_TRACE_YM_WRITE_TIMING", "1");
            Environment.SetEnvironmentVariable("EUTHERDRIVE_TRACE_AUDIO_BUFFER", "1");
            
            // Initialize the music system
            var music = new md_music();
            music.g_md_ym2612.YM2612_Start();
            
            // Test YM2612_Update
            Console.WriteLine("\nTesting YM2612_Update...");
            var output = music.g_md_ym2612.YM2612_Update();
            Console.WriteLine($"Output: {output.out1}, {output.out2}");
            
            // Test YM2612_UpdateBatch
            Console.WriteLine("\nTesting YM2612_UpdateBatch...");
            short[] buffer = new short[882]; // 10ms at 44.1kHz stereo
            music.g_md_ym2612.YM2612_UpdateBatch(buffer, 10);
            Console.WriteLine($"Buffer filled with {buffer.Length} samples");
            
            // Test a YM write
            Console.WriteLine("\nTesting YM write...");
            music.g_md_ym2612.write8(0xA04000, 0x28, "TEST");
            music.g_md_ym2612.write8(0xA04001, 0x80, "TEST");
            
            Console.WriteLine("\nTest complete!");
        }
    }
}