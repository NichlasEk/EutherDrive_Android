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
