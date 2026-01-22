// Headless test harness for EutherDrive core
// Usage: dotnet run --project EutherDrive.Headless -- /path/to/rom.md [frames]
//        dotnet run --project EutherDrive.Headless -- --test-interlace2
// Default: runs 120 frames

using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using EutherDrive.Core;
using EutherDrive.Core.MdTracerCore;
using EutherDrive.Core.Savestates;

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

        if (args.Length >= 1 && args[0] == "--load-savestate")
        {
            if (args.Length < 3)
            {
                Console.Error.WriteLine("Usage: EutherDrive.Headless --load-savestate <rom_path> <savestate_path> [frames]");
                return 1;
            }
            string romPathArg = args[1];
            string statePathArg = args[2];
            int framesArg = args.Length > 3 && int.TryParse(args[3], out int framesParsed)
                ? framesParsed
                : DefaultFrames;
            return RunFromSavestate(romPathArg, statePathArg, framesArg);
        }

        if (args.Length < 1)
        {
            Console.Error.WriteLine("Usage: EutherDrive.Headless <rom_path> [frames]");
            Console.Error.WriteLine($"  rom_path: Path to ROM file (.md, .bin, .gen, etc.)");
            Console.Error.WriteLine($"  frames:   Number of frames to run (default: {DefaultFrames})");
            Console.Error.WriteLine("  --test-interlace2: Run interlace mode 2 pattern test");
            Console.Error.WriteLine("  --load-savestate: Load savestate and run frames");
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

            // Enable framebuffer analyzer if requested
            if (args.Length > 2 && args[2] == "--analyze-fb")
            {
                adapter.FbAnalyzer.Enabled = true;
                adapter.FbAnalyzer.ConfigureGrid(8, 6);
                adapter.FbAnalyzer.SetSampleRate(1);
                Console.WriteLine("[HEADLESS] Framebuffer analyzer enabled");
            }

            // Check for auto-load savestate flag
            bool loadSlot1OnBoot = Environment.GetEnvironmentVariable("EUTHERDRIVE_LOAD_SLOT1_ON_BOOT") == "1";
            if (loadSlot1OnBoot)
            {
                Console.WriteLine($"[HEADLESS] Auto-loading savestate slot 1...");

                // Debug: show ROM identity
                Console.WriteLine($"[HEADLESS] ROM identity: name={adapter.RomIdentity?.Name}, hash={BitConverter.ToString(adapter.RomIdentity?.Hash ?? [])}");

                // Use SavestateService to load from the savestates directory
                var savestateService = new SavestateService("/home/nichlas/EutherDrive/savestates");

                // Debug: list available savestates
                Console.WriteLine("[HEADLESS] Available savestates:");
                foreach (var file in Directory.GetFiles("/home/nichlas/EutherDrive/savestates", "*.euthstate"))
                {
                    Console.WriteLine($"[HEADLESS]   {Path.GetFileName(file)}");
                }

                try
                {
                    savestateService.Load(adapter, 1);
                    Console.WriteLine($"[HEADLESS] Savestate slot 1 loaded successfully.");
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[HEADLESS-WARN] Failed to load savestate slot 1: {ex.Message}");
                }
            }

            Console.WriteLine($"[HEADLESS] ROM loaded, starting emulation...");

            // Dump frame 0 before running
            Console.WriteLine("[HEADLESS] Framebuffer BEFORE running:");
            adapter.FrameBufferHasContent();
            adapter.DumpFrameBufferToPpm("/home/nichlas/roms/headless_frame0.ppm");

            for (int frame = 0; frame < framesToRun; frame++)
            {
                adapter.StepFrame();

                // Log VDP status and framebuffer
                bool displayOn = adapter.IsVdpDisplayOn();
                bool hasContent = adapter.FrameBufferHasContent();
                Console.WriteLine($"[HEADLESS] Frame {frame}: display={displayOn} fb_has_content={hasContent}");

                // Dump framebuffer at interesting points
                if (frame == 0 || frame == 5 || frame == 10)
                {
                    string ppmPath = $"/home/nichlas/roms/headless_frame{frame}.ppm";
                    adapter.DumpFrameBufferToPpm(ppmPath);
                    Console.WriteLine($"[HEADLESS] Dumped frame {frame} to {ppmPath}");
                }

                // Early exit if we detect obvious problems
                var z80Pc = adapter.GetZ80Pc();
                if (z80Pc == 0 && frame > 5)
                {
                    Console.WriteLine($"[HEADLESS-WARN] Z80 PC stuck at 0x0000 after frame {frame}");
                }
            }

            // Dump frame 0 after running
            Console.WriteLine("[HEADLESS] Framebuffer AFTER running:");
            adapter.FrameBufferHasContent();
            adapter.DumpFrameBufferToPpm("/home/nichlas/roms/headless_output.ppm");

            // Check framebuffer and dump if requested
            Console.WriteLine("[HEADLESS] Checking framebuffer...");
            if (adapter.FrameBufferHasContent())
            {
                string ppmPath = Path.Combine(Path.GetDirectoryName(romPath) ?? ".", "headless_output.ppm");
                adapter.DumpFrameBufferToPpm(ppmPath);
                Console.WriteLine($"[HEADLESS] Framebuffer dumped to {ppmPath}");

                // Also try to convert to PNG if ImageMagick is available
                string pngPath = Path.ChangeExtension(ppmPath, ".png");
                var convertProcess = new System.Diagnostics.Process
                {
                    StartInfo = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "convert",
                        Arguments = $"\"{ppmPath}\" \"{pngPath}\"",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };
                try
                {
                    convertProcess.Start();
                    convertProcess.WaitForExit(5000);
                    if (File.Exists(pngPath))
                    {
                        Console.WriteLine($"[HEADLESS] Converted to PNG: {pngPath}");
                    }
                }
                catch
                {
                    // ImageMagick not available, skip PNG conversion
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

    private static int RunFromSavestate(string romPath, string savestatePath, int framesToRun)
    {
        if (!File.Exists(romPath))
        {
            Console.Error.WriteLine($"Error: ROM file not found: {romPath}");
            return 1;
        }

        if (!File.Exists(savestatePath))
        {
            Console.Error.WriteLine($"Error: Savestate file not found: {savestatePath}");
            return 1;
        }

        Console.WriteLine($"[HEADLESS] Loading ROM: {romPath}");
        Console.WriteLine($"[HEADLESS] Loading savestate: {savestatePath}");
        Console.WriteLine($"[HEADLESS] Running {framesToRun} frames from savestate");

        try
        {
            var adapter = new MdTracerAdapter();
            adapter.LoadRom(romPath);

            int? slotOverride = ParseOptionalIntEnv("EUTHERDRIVE_SAVESTATE_SLOT");
            
            // Use SavestateService like UI does - it expects files in savestates/ directory
            Console.WriteLine($"[HEADLESS] Using SavestateService (UI approach)...");
            var savestateService = new SavestateService("../savestates");
            savestateService.Load(adapter, slotOverride ?? 1);
            Console.WriteLine($"[HEADLESS] Savestate loaded successfully via SavestateService");

            for (int frame = 0; frame < framesToRun; frame++)
            {
                adapter.StepFrame();
                Console.WriteLine($"[HEADLESS] Frame {frame} completed");
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

    private static byte[]? TryLoadSavestatePayload(
        string savestatePath,
        RomIdentity? romIdentity,
        int? slotOverride,
        out string? error)
    {
        error = null;
        if (romIdentity == null)
        {
            error = "ROM identity missing.";
            return null;
        }

        const string fileMagic = "EUTHSTAT";
        const int fileVersion = 1;
        const int slotCountExpected = 3;
        const int slotHashLength = 32;

        using var stream = File.Open(savestatePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);

        string magic = Encoding.ASCII.GetString(reader.ReadBytes(fileMagic.Length));
        if (!string.Equals(magic, fileMagic, StringComparison.Ordinal))
        {
            error = "Savestate magic mismatch.";
            return null;
        }

        int version = reader.ReadInt32();
        if (version != fileVersion)
        {
            error = $"Savestate version mismatch: {version}.";
            return null;
        }

        int slotCount = reader.ReadInt32();
        if (slotCount != slotCountExpected)
        {
            error = $"Savestate slot count mismatch: {slotCount}.";
            return null;
        }

        byte[] fileRomHash = reader.ReadBytes(romIdentity.Hash.Length);
        if (!fileRomHash.SequenceEqual(romIdentity.Hash))
        {
            error = "Savestate ROM hash mismatch.";
            return null;
        }

        int nameLength = reader.ReadInt32();
        if (nameLength > 0)
            reader.ReadBytes(nameLength);

        var slots = new (int Index, bool HasData, int PayloadLength, long PayloadOffset, byte[] Hash)[slotCount];
        for (int i = 0; i < slotCount; i++)
        {
            int slotIndex = reader.ReadInt32();
            bool hasData = reader.ReadByte() != 0;
            reader.ReadInt64(); // savedAt ticks
            reader.ReadInt64(); // frame counter
            int payloadLength = reader.ReadInt32();
            long payloadOffset = reader.ReadInt64();
            byte[] hash = reader.ReadBytes(slotHashLength);
            slots[i] = (slotIndex, hasData, payloadLength, payloadOffset, hash);
        }

        Console.WriteLine("[HEADLESS] Savestate slots:");
        foreach (var slot in slots)
        {
            Console.WriteLine(
                $"[HEADLESS]  slot={slot.Index} hasData={slot.HasData} payloadLen={slot.PayloadLength} offset={slot.PayloadOffset}");
        }

        foreach (var slot in slots)
        {
            if (slotOverride.HasValue && slot.Index != slotOverride.Value)
                continue;
            if (!slot.HasData || slot.PayloadLength <= 0)
                continue;
            if (slot.PayloadOffset < 0 || slot.PayloadOffset + slot.PayloadLength > stream.Length)
                continue;

            stream.Seek(slot.PayloadOffset, SeekOrigin.Begin);
            byte[] payload = reader.ReadBytes(slot.PayloadLength);
            byte[] checksum = SHA256.HashData(payload);
            if (!checksum.SequenceEqual(slot.Hash))
                continue;

            Console.WriteLine($"[HEADLESS] Loaded savestate slot {slot.Index} payload ({payload.Length} bytes)");
            return payload;
        }

        error = "No valid savestate payload found.";
        return null;
    }

    private static int? ParseOptionalIntEnv(string name)
    {
        string? raw = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        if (int.TryParse(raw.Trim(), out int value))
            return value;
        return null;
    }
}
