// Headless test harness for EutherDrive core
// Usage: dotnet run --project EutherDrive.Headless -- /path/to/rom.md [frames]
//        dotnet run --project EutherDrive.Headless -- --test-interlace2
// Default: runs 120 frames

using System;
using System.IO;
using System.Linq;
using EutherDrive.Core;
using EutherDrive.Core.MdTracerCore;

namespace EutherDrive.Headless;

class Program
{
    private const int DefaultFrames = 120;

    static int Main(string[] args)
    {
        // Check for special test modes
        if (args.Length >= 1 && args[0] == "--test-interlace2")
        {
            Console.WriteLine("[HEADLESS] Running interlace mode 2 test...");
            MdVdpInterlaceMode2PatternTest.Run();
            return 0;
        }

        if (args.Length >= 1 && args[0] == "--test-savestate")
        {
            if (args.Length < 2)
            {
                Console.Error.WriteLine("Usage: EutherDrive.Headless --test-savestate <rom_path>");
                return 1;
            }
            return RunSavestateRoundtrip(args[1]);
        }

        if (args.Length < 1)
        {
            Console.Error.WriteLine("Usage: EutherDrive.Headless <rom_path> [frames]");
            Console.Error.WriteLine($"  rom_path: Path to ROM file (.md, .bin, .gen, etc.)");
            Console.Error.WriteLine($"  frames:   Number of frames to run (default: {DefaultFrames})");
            Console.Error.WriteLine("  --test-interlace2: Run interlace mode 2 pattern test");
            return 1;
        }

        string romPath = args[0];
        int framesToRun = args.Length > 1 && int.TryParse(args[1], out int f) ? f : DefaultFrames;

        if (!File.Exists(romPath))
        {
            Console.Error.WriteLine($"Error: ROM file not found: {romPath}");
            return 1;
        }

        Console.WriteLine($"[HEADLESS] Loading ROM: {romPath}");
        Console.WriteLine($"[HEADLESS] Running {framesToRun} frames");

        try
        {
            var adapter = new MdTracerAdapter();
            adapter.LoadRom(romPath);

            Console.WriteLine($"[HEADLESS] ROM loaded, starting emulation...");

            for (int frame = 0; frame < framesToRun; frame++)
            {
                adapter.StepFrame();
                Console.WriteLine($"[HEADLESS] Frame {frame} completed");

                // Early exit if we detect obvious problems
                var z80Pc = adapter.GetZ80Pc();
                if (z80Pc == 0 && frame > 5)
                {
                    Console.WriteLine($"[HEADLESS-WARN] Z80 PC stuck at 0x0000 after frame {frame}");
                }
            }

            Console.WriteLine($"[HEADLESS] Completed {framesToRun} frames");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[HEADLESS-ERROR] {ex.Message}");
            Console.Error.WriteLine(ex.StackTrace);
            return 1;
        }
    }

    private static int RunSavestateRoundtrip(string romPath)
    {
        if (!File.Exists(romPath))
        {
            Console.Error.WriteLine($"Error: ROM file not found: {romPath}");
            return 1;
        }

        Console.WriteLine($"[HEADLESS] Savestate roundtrip test: {romPath}");
        var adapter = new MdTracerAdapter();
        adapter.LoadRom(romPath);

        for (int i = 0; i < 10; i++)
            adapter.StepFrame();

        byte[] snapshot;
        using (var ms = new MemoryStream())
        using (var writer = new BinaryWriter(ms))
        {
            adapter.SaveState(writer);
            writer.Flush();
            snapshot = ms.ToArray();
        }

        for (int i = 0; i < 5; i++)
            adapter.StepFrame();

        using (var ms = new MemoryStream(snapshot))
        using (var reader = new BinaryReader(ms))
        {
            adapter.LoadState(reader);
        }

        byte[] snapshotAfter;
        using (var ms = new MemoryStream())
        using (var writer = new BinaryWriter(ms))
        {
            adapter.SaveState(writer);
            writer.Flush();
            snapshotAfter = ms.ToArray();
        }

        if (!snapshot.SequenceEqual(snapshotAfter))
        {
            Console.Error.WriteLine("[HEADLESS] Savestate roundtrip failed: payload mismatch.");
            return 1;
        }

        Console.WriteLine("[HEADLESS] Savestate roundtrip ok.");
        return 0;
    }
}
