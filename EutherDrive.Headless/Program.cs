// Headless test harness for EutherDrive core
// Usage: dotnet run --project EutherDrive.Headless -- /path/to/rom.md [frames]
//        dotnet run --project EutherDrive.Headless -- --test-interlace2
//        dotnet run --project EutherDrive.Headless -- --load-savestate /path/to/rom.cue /path/to/state.euthstate [frames]
//        EUTHERDRIVE_LOAD_SLOT1_ON_BOOT=1 dotnet run --project EutherDrive.Headless -- /path/to/rom.cue [frames]
//        EUTHERDRIVE_HEADLESS_CORE=pce EUTHERDRIVE_SAVESTATE_SLOT=1 dotnet run --project EutherDrive.Headless -c Release -- --load-savestate /path/to/rom.cue /path/to/state.euthstate [frames]
//        EUTHERDRIVE_HEADLESS_CORE=psx EUTHERDRIVE_PSX_BIOS=/path/to/scph1001.bin dotnet run --project EutherDrive.Headless -c Release -- /path/to/game.cue [frames]
//        EUTHERDRIVE_HEADLESS_CORE=gba EUTHERDRIVE_GBA_HEADLESS_INPUT_SCRIPT="0-2:start;120-122:a" dotnet run --project EutherDrive.Headless -c Release -- /path/to/game.gba [frames]
// Default: runs 120 frames

using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.IO.Compression;
using EutherDrive.Core;
using EutherDrive.Core.Sega32X;
using EutherDrive.Core.SegaCd;
using EutherDrive.Core.MdTracerCore;
using EutherDrive.Core.Savestates;
using EutherDrive.Core.Arcade;
using EutherDrive.Core.Arcade.Cps1;
using EutherDrive.Core.Arcade.Cps2;
using EutherDrive.Core.Arcade.DataEast.Hshavoc;
using EutherDrive.Core.Arcade.Konami;
using EutherDrive.Core.Arcade.Snk;
using EutherDrive.Core.Arcade.System32;
using EutherDrive.Platforms.DataEast.Deco32;
using EutherDrive.Audio;
using EutherDrive.Core.Cpu.M68000Emu;

namespace EutherDrive.Headless;

// Simple audio sink for headless mode that just consumes audio without playing it
internal sealed class HeadlessAudioSink : IAudioSink
{
    private long _totalSamples;
    private long _lastLogTime;
    private int _sampleRate;
    private int _channels;
    
    public void Start(int sampleRate, int channels)
    {
        _sampleRate = sampleRate;
        _channels = channels;
        Console.WriteLine($"[HEADLESS-AUDIO] Started: sampleRate={sampleRate}, channels={channels}");
    }
    
    public void Submit(ReadOnlySpan<short> interleaved)
    {
        _totalSamples += interleaved.Length;
        
        // Log every second
        long now = Environment.TickCount64;
        if (now - _lastLogTime > 1000)
        {
            _lastLogTime = now;
            Console.WriteLine($"[HEADLESS-AUDIO] Consumed {_totalSamples} samples total ({_totalSamples / _channels} frames)");
        }
    }
    
    public void Stop()
    {
        Console.WriteLine($"[HEADLESS-AUDIO] Stopped");
    }
    
    public void Dispose()
    {
        Console.WriteLine($"[HEADLESS-AUDIO] Final: {_totalSamples} samples consumed ({_totalSamples / _channels} frames)");
    }
}

class Program
{
    private const int DefaultFrames = 120;

    private readonly record struct SnesInputScriptWindow(
        int StartFrame,
        int EndFrame,
        bool Up,
        bool Down,
        bool Left,
        bool Right,
        bool A,
        bool B,
        bool X,
        bool Y,
        bool L,
        bool R,
        bool Start,
        bool Select);

    private static bool ShouldPressStartPulse(int frame, int delay, int pulse, int period, int count)
    {
        if (frame < delay || pulse <= 0 || period <= 0 || count <= 0)
            return false;

        int rel = frame - delay;
        int window = rel / period;
        if (window < 0 || window >= count)
            return false;

        int slot = rel % period;
        return slot < pulse;
    }

    private static List<SnesInputScriptWindow> ParseSnesInputScript(string? raw)
    {
        var result = new List<SnesInputScriptWindow>();
        if (string.IsNullOrWhiteSpace(raw))
            return result;

        foreach (string item in raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int colon = item.IndexOf(':');
            if (colon <= 0 || colon == item.Length - 1)
                continue;

            string rangePart = item[..colon].Trim();
            string buttonsPart = item[(colon + 1)..].Trim();

            int dash = rangePart.IndexOf('-');
            if (dash <= 0 || dash == rangePart.Length - 1)
                continue;

            if (!int.TryParse(rangePart[..dash], NumberStyles.Integer, CultureInfo.InvariantCulture, out int startFrame))
                continue;
            if (!int.TryParse(rangePart[(dash + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out int endFrame))
                continue;
            if (endFrame < startFrame)
                (startFrame, endFrame) = (endFrame, startFrame);

            bool up = false, down = false, left = false, right = false;
            bool a = false, b = false, x = false, y = false;
            bool l = false, r = false, start = false, select = false;

            foreach (string buttonToken in buttonsPart.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                switch (buttonToken.ToLowerInvariant())
                {
                    case "up": up = true; break;
                    case "down": down = true; break;
                    case "left": left = true; break;
                    case "right": right = true; break;
                    case "a": a = true; break;
                    case "b": b = true; break;
                    case "x": x = true; break;
                    case "y": y = true; break;
                    case "l": l = true; break;
                    case "r": r = true; break;
                    case "start": start = true; break;
                    case "select":
                    case "sel": select = true; break;
                }
            }

            result.Add(new SnesInputScriptWindow(
                startFrame, endFrame,
                up, down, left, right,
                a, b, x, y, l, r, start, select));
        }

        return result;
    }

    private static (
        bool Up,
        bool Down,
        bool Left,
        bool Right,
        bool A,
        bool B,
        bool X,
        bool Y,
        bool L,
        bool R,
        bool Start,
        bool Select) ResolveSnesInputForFrame(int frame, IReadOnlyList<SnesInputScriptWindow> script)
    {
        bool up = false, down = false, left = false, right = false;
        bool a = false, b = false, x = false, y = false;
        bool l = false, r = false, start = false, select = false;

        foreach (var window in script)
        {
            if (frame < window.StartFrame || frame > window.EndFrame)
                continue;

            up |= window.Up;
            down |= window.Down;
            left |= window.Left;
            right |= window.Right;
            a |= window.A;
            b |= window.B;
            x |= window.X;
            y |= window.Y;
            l |= window.L;
            r |= window.R;
            start |= window.Start;
            select |= window.Select;
        }

        return (up, down, left, right, a, b, x, y, l, r, start, select);
    }

    private static void LogEnv(string name)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        if (!string.IsNullOrEmpty(value))
            Console.WriteLine($"[HEADLESS] env {name}={value}");
    }


    static int Main(string[] args)
    {
        ConfigureConsoleLogging();
        if (Environment.GetEnvironmentVariable("EUTHERDRIVE_HEADLESS_DEBUG_SCD") == "1")
        {
            EnableScdDebugLogging();
        }

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

        if (args.Length >= 1 && args[0] == "--load-raw-state")
        {
            if (args.Length < 3)
            {
                Console.Error.WriteLine("Usage: EutherDrive.Headless --load-raw-state <rom_path> <raw_state_path> [frames]");
                return 1;
            }
            string romPathArg = args[1];
            string rawStatePathArg = args[2];
            int framesArg = args.Length > 3 && int.TryParse(args[3], out int framesParsed)
                ? framesParsed
                : DefaultFrames;
            return RunFromRawState(romPathArg, rawStatePathArg, framesArg);
        }

        if (args.Length >= 2 && args[0] == "--m68k-tests")
        {
            string path = args[1];
            bool logEach = args.Length > 2 && args[2] == "--log";
            return M68kTestCli.Run(path, logEach);
        }

        if (args.Length < 1)
        {
            Console.Error.WriteLine("Usage: EutherDrive.Headless <rom_path> [frames]");
            Console.Error.WriteLine($"  rom_path: Path to ROM file (.md, .bin, .gen, etc.)");
            Console.Error.WriteLine($"  frames:   Number of frames to run (default: {DefaultFrames})");
            Console.Error.WriteLine("  --test-interlace2: Run interlace mode 2 pattern test");
            Console.Error.WriteLine("  --load-savestate: Load savestate and run frames");
            Console.Error.WriteLine("  --load-raw-state: Load raw core state and run frames");
            Console.Error.WriteLine("  --m68k-tests <path> [--log]: Run 68000 JSON tests (ProcessorTests)");
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
        LogEnv("EUTHERDRIVE_TRACE_Z80_FRAME_CYCLES");
        LogEnv("EUTHERDRIVE_TRACE_Z80_FRAME_CYCLES_EVERY");
        LogEnv("EUTHERDRIVE_TRACE_Z80_AUDIO_RATE");
        LogEnv("EUTHERDRIVE_TRACE_Z80_AUDIO_RATE_EVERY");
        LogEnv("EUTHERDRIVE_TRACE_Z80_AUDIO_RATE_START_FRAME");
        LogEnv("EUTHERDRIVE_CPS2_EEPROM_TRACE");
        LogEnv("EUTHERDRIVE_CPS2_EEPROM_TRACE_LIMIT");
        LogEnv("EUTHERDRIVE_M68K_TRACE_EX");

        string dumpDir = Environment.GetEnvironmentVariable("EUTHERDRIVE_HEADLESS_DUMP_DIR")
            ?? Path.Combine(Directory.GetCurrentDirectory(), "logs");
        Directory.CreateDirectory(dumpDir);

        try
        {
            string? coreOverride = Environment.GetEnvironmentVariable("EUTHERDRIVE_HEADLESS_CORE");
            bool useNes = string.Equals(coreOverride, "nes", StringComparison.OrdinalIgnoreCase)
                || (string.IsNullOrEmpty(coreOverride) && IsNesRomPath(romPath));
            bool useSnes = string.Equals(coreOverride, "snes", StringComparison.OrdinalIgnoreCase)
                || (string.IsNullOrEmpty(coreOverride) && IsSnesRomPath(romPath));
            bool usePsx = string.Equals(coreOverride, "psx", StringComparison.OrdinalIgnoreCase)
                || string.Equals(coreOverride, "ps1", StringComparison.OrdinalIgnoreCase)
                || string.Equals(coreOverride, "playstation", StringComparison.OrdinalIgnoreCase)
                || (string.IsNullOrEmpty(coreOverride) && IsPsxRomPath(romPath));
            bool useGb = string.Equals(coreOverride, "gb", StringComparison.OrdinalIgnoreCase)
                || string.Equals(coreOverride, "gbc", StringComparison.OrdinalIgnoreCase)
                || string.Equals(coreOverride, "gameboy", StringComparison.OrdinalIgnoreCase)
                || (string.IsNullOrEmpty(coreOverride) && IsGbRomPath(romPath));
            bool useGba = string.Equals(coreOverride, "gba", StringComparison.OrdinalIgnoreCase)
                || string.Equals(coreOverride, "agb", StringComparison.OrdinalIgnoreCase)
                || (string.IsNullOrEmpty(coreOverride) && IsGbaRomPath(romPath));
            bool use32X = string.Equals(coreOverride, "32x", StringComparison.OrdinalIgnoreCase)
                || string.Equals(coreOverride, "s32x", StringComparison.OrdinalIgnoreCase)
                || string.Equals(coreOverride, "sega32x", StringComparison.OrdinalIgnoreCase)
                || (string.IsNullOrEmpty(coreOverride) && Is32XRomPath(romPath));
            bool useSmsGg = string.Equals(coreOverride, "smsgg", StringComparison.OrdinalIgnoreCase)
                || string.Equals(coreOverride, "sms", StringComparison.OrdinalIgnoreCase)
                || string.Equals(coreOverride, "gg", StringComparison.OrdinalIgnoreCase)
                || (string.IsNullOrEmpty(coreOverride) && IsMasterSystemRomPath(romPath));
            bool useN64 = string.Equals(coreOverride, "n64", StringComparison.OrdinalIgnoreCase)
                || (string.IsNullOrEmpty(coreOverride) && IsN64RomPath(romPath));
            bool useSegaCd = string.Equals(coreOverride, "segacd", StringComparison.OrdinalIgnoreCase)
                || string.Equals(coreOverride, "scd", StringComparison.OrdinalIgnoreCase)
                || (string.IsNullOrEmpty(coreOverride) && IsSegaCdRomPath(romPath));
            bool usePce = string.Equals(coreOverride, "pce", StringComparison.OrdinalIgnoreCase)
                || string.Equals(coreOverride, "pcecd", StringComparison.OrdinalIgnoreCase)
                || string.Equals(coreOverride, "pcengine", StringComparison.OrdinalIgnoreCase)
                || (string.IsNullOrEmpty(coreOverride) && IsPceRomPath(romPath) && !IsSegaCdRomPath(romPath));
            bool useCps2 = string.Equals(coreOverride, "cps2", StringComparison.OrdinalIgnoreCase)
                || string.Equals(coreOverride, "arcade-cps2", StringComparison.OrdinalIgnoreCase)
                || (string.IsNullOrEmpty(coreOverride) && Cps2DdsomAdapter.IsSupportedArchive(romPath));
            bool useCps1 = string.Equals(coreOverride, "cps1", StringComparison.OrdinalIgnoreCase)
                || string.Equals(coreOverride, "arcade-cps1", StringComparison.OrdinalIgnoreCase)
                || (string.IsNullOrEmpty(coreOverride) && Cps1DinoAdapter.IsSupportedArchive(romPath));
            bool useSystem32 = string.Equals(coreOverride, "system32", StringComparison.OrdinalIgnoreCase)
                || string.Equals(coreOverride, "s32", StringComparison.OrdinalIgnoreCase)
                || string.Equals(coreOverride, "sega-system32", StringComparison.OrdinalIgnoreCase)
                || (string.IsNullOrEmpty(coreOverride) && System32Adapter.IsSupportedArchive(romPath));
            bool useDeco32 = string.Equals(coreOverride, "deco32", StringComparison.OrdinalIgnoreCase)
                || string.Equals(coreOverride, "dataeast-deco32", StringComparison.OrdinalIgnoreCase)
                || string.Equals(coreOverride, "nslasher", StringComparison.OrdinalIgnoreCase)
                || (string.IsNullOrEmpty(coreOverride) && Deco32Adapter.IsSupportedArchive(romPath));
            bool useHshavoc = string.Equals(coreOverride, "hshavoc", StringComparison.OrdinalIgnoreCase)
                || string.Equals(coreOverride, "high-seas-havoc", StringComparison.OrdinalIgnoreCase)
                || string.Equals(coreOverride, "dataeast-hshavoc", StringComparison.OrdinalIgnoreCase)
                || (string.IsNullOrEmpty(coreOverride) && HshavocAdapter.IsSupportedArchive(romPath));
            bool useTmnt = string.Equals(coreOverride, "tmnt", StringComparison.OrdinalIgnoreCase)
                || string.Equals(coreOverride, "tmnt2", StringComparison.OrdinalIgnoreCase)
                || string.Equals(coreOverride, "konami-tmnt", StringComparison.OrdinalIgnoreCase)
                || string.Equals(coreOverride, "konami-tmnt2", StringComparison.OrdinalIgnoreCase)
                || (string.IsNullOrEmpty(coreOverride) && TmntAdapter.IsSupportedArchive(romPath));
            bool useNeoGeo = string.Equals(coreOverride, "neogeo", StringComparison.OrdinalIgnoreCase)
                || string.Equals(coreOverride, "neo-geo", StringComparison.OrdinalIgnoreCase)
                || (string.IsNullOrEmpty(coreOverride) && NeoGeoAdapter.IsSupportedArchive(romPath));
            bool useMcsArcade = string.Equals(coreOverride, "arcade", StringComparison.OrdinalIgnoreCase)
                || string.Equals(coreOverride, "mcs", StringComparison.OrdinalIgnoreCase)
                || string.Equals(coreOverride, "arcade-mcs", StringComparison.OrdinalIgnoreCase)
                || string.Equals(coreOverride, "xsleena", StringComparison.OrdinalIgnoreCase)
                || (string.IsNullOrEmpty(coreOverride) && !useNeoGeo && McsArcadeAdapter.IsLikelyArcadeArchive(romPath));
            if (string.Equals(coreOverride, "md", StringComparison.OrdinalIgnoreCase))
            {
                useNes = false;
                useSnes = false;
                usePsx = false;
                useGb = false;
                useGba = false;
                use32X = false;
                useSmsGg = false;
                useN64 = false;
                useSegaCd = false;
                usePce = false;
                useCps1 = false;
                useCps2 = false;
                useSystem32 = false;
                useDeco32 = false;
                useHshavoc = false;
                useTmnt = false;
                useNeoGeo = false;
                useMcsArcade = false;
            }

            if (useCps1)
            {
                Console.WriteLine("[HEADLESS] Using CPS1 core");
                return RunCps1Headless(romPath, framesToRun, dumpDir, statePayload: null);
            }

            if (useHshavoc)
            {
                Console.WriteLine("[HEADLESS] Using Data East HSHavoc probe core");
                return RunHshavocHeadless(romPath, framesToRun, dumpDir);
            }

            if (useCps2)
            {
                Console.WriteLine("[HEADLESS] Using CPS2 core");
                var cps2 = new Cps2DdsomAdapter();
                cps2.LoadRom(romPath);

                ReadOnlySpan<byte> fbIn = cps2.GetFrameBuffer(out int wIn, out int hIn, out int sIn);
                var statsIn = GetFrameStats(fbIn, wIn, hIn, sIn);
                ulong lastFingerprint = ComputeFrameFingerprint(fbIn, wIn, hIn, sIn);
                int unchangedFrames = 0;
                bool traceCps2Frames = Environment.GetEnvironmentVariable("EUTHERDRIVE_HEADLESS_TRACE_FRAMES") == "1";

                Console.WriteLine($"[HEADLESS] CPS2 fb_has_content={statsIn.HasContent} nonzero_pixels={statsIn.NonZeroPixels} first_nonzero=({statsIn.FirstX},{statsIn.FirstY}) fp=0x{lastFingerprint:X16}");
                DumpBgraToPpm(fbIn, wIn, hIn, sIn, Path.Combine(dumpDir, "headless_frame0.ppm"));

                for (int frame = 0; frame < framesToRun; frame++)
                {
                    cps2.SetInputState(
                        up: false,
                        down: false,
                        left: false,
                        right: false,
                        a: false,
                        b: false,
                        c: false,
                        start: false,
                        x: false,
                        y: false,
                        z: false,
                        mode: false,
                        padType: PadType.SixButton);
                    cps2.RunFrame();

                    ReadOnlySpan<byte> fb = cps2.GetFrameBuffer(out int w, out int h, out int s);
                    var stats = GetFrameStats(fb, w, h, s);
                    ulong fingerprint = ComputeFrameFingerprint(fb, w, h, s);
                    unchangedFrames = fingerprint == lastFingerprint ? unchangedFrames + 1 : 0;
                    lastFingerprint = fingerprint;

                    if (traceCps2Frames || frame == 0 || frame == 5 || frame == 10 || ((frame + 1) % 60) == 0)
                    {
                        Console.WriteLine($"[HEADLESS] Frame {frame}: cps2_fb_has_content={stats.HasContent} nonzero_pixels={stats.NonZeroPixels} first_nonzero=({stats.FirstX},{stats.FirstY}) fp=0x{fingerprint:X16} unchanged={unchangedFrames}");
                    }

                    if (frame == 0 || frame == 5 || frame == 10)
                        DumpBgraToPpm(fb, w, h, s, Path.Combine(dumpDir, $"headless_frame{frame}.ppm"));
                }

                ReadOnlySpan<byte> fbOut = cps2.GetFrameBuffer(out int wOut, out int hOut, out int sOut);
                var statsOut = GetFrameStats(fbOut, wOut, hOut, sOut);
                ulong finalFingerprint = ComputeFrameFingerprint(fbOut, wOut, hOut, sOut);
                Console.WriteLine($"[HEADLESS] CPS2 final fb_has_content={statsOut.HasContent} nonzero_pixels={statsOut.NonZeroPixels} first_nonzero=({statsOut.FirstX},{statsOut.FirstY}) fp=0x{finalFingerprint:X16}");
                DumpBgraToPpm(fbOut, wOut, hOut, sOut, Path.Combine(dumpDir, "headless_output.ppm"));
                Console.WriteLine($"[HEADLESS] Completed {framesToRun} frames");
                return 0;
            }

            if (useTmnt)
            {
                Console.WriteLine("[HEADLESS] Using Konami TMNT core");
                var tmnt = new TmntAdapter();
                tmnt.LoadRom(romPath);
                var tmntInputScript = ParseSnesInputScript(Environment.GetEnvironmentVariable("EUTHERDRIVE_TMNT_HEADLESS_INPUT_SCRIPT"));
                ReadOnlySpan<byte> fbIn = tmnt.GetFrameBuffer(out int wIn, out int hIn, out int sIn);
                var statsIn = GetFrameStats(fbIn, wIn, hIn, sIn);
                Console.WriteLine($"[HEADLESS] TMNT fb_has_content={statsIn.HasContent} nonzero_pixels={statsIn.NonZeroPixels} first_nonzero=({statsIn.FirstX},{statsIn.FirstY})");
                DumpBgraToPpm(fbIn, wIn, hIn, sIn, Path.Combine(dumpDir, "headless_frame0.ppm"));

                long runTicksTotal = 0;
                long runTicksMin = long.MaxValue;
                long runTicksMax = 0;
                for (int frame = 0; frame < framesToRun; frame++)
                {
                    var input = ResolveSnesInputForFrame(frame, tmntInputScript);
                    tmnt.SetInputState(
                        input.Up, input.Down, input.Left, input.Right,
                        input.A, input.B, input.X,
                        input.Start,
                        false, false, false,
                        input.Select,
                        PadType.SixButton);
                    long runStart = Stopwatch.GetTimestamp();
                    tmnt.RunFrame();
                    long runTicks = Stopwatch.GetTimestamp() - runStart;
                    runTicksTotal += runTicks;
                    runTicksMin = Math.Min(runTicksMin, runTicks);
                    runTicksMax = Math.Max(runTicksMax, runTicks);
                    if (frame == 0 || frame == 5 || frame == 10)
                    {
                        ReadOnlySpan<byte> fb = tmnt.GetFrameBuffer(out int w, out int h, out int s);
                        DumpBgraToPpm(fb, w, h, s, Path.Combine(dumpDir, $"headless_frame{frame}.ppm"));
                    }
                }

                ReadOnlySpan<byte> fbOut = tmnt.GetFrameBuffer(out int wOut, out int hOut, out int sOut);
                var statsOut = GetFrameStats(fbOut, wOut, hOut, sOut);
                ulong finalFingerprint = ComputeFrameFingerprint(fbOut, wOut, hOut, sOut);
                Console.WriteLine($"[HEADLESS] TMNT final fb_has_content={statsOut.HasContent} nonzero_pixels={statsOut.NonZeroPixels} first_nonzero=({statsOut.FirstX},{statsOut.FirstY}) fp=0x{finalFingerprint:X16}");
                Console.WriteLine($"[HEADLESS] TMNT debug {tmnt.DebugSummary}");
                DumpBgraToPpm(fbOut, wOut, hOut, sOut, Path.Combine(dumpDir, "headless_output.ppm"));
                PrintHeadlessPerf("TMNT", framesToRun, runTicksTotal, runTicksMin, runTicksMax, 60.0);
                Console.WriteLine($"[HEADLESS] Completed {framesToRun} frames");
                return 0;
            }

            if (useSystem32)
            {
                Console.WriteLine("[HEADLESS] Using Sega System 32 core");
                var system32 = new System32Adapter();
                system32.LoadRom(romPath);

                ReadOnlySpan<byte> fbIn = system32.GetFrameBuffer(out int wIn, out int hIn, out int sIn);
                var statsIn = GetFrameStats(fbIn, wIn, hIn, sIn);
                ulong lastFingerprint = ComputeFrameFingerprint(fbIn, wIn, hIn, sIn);
                int unchangedFrames = 0;
                bool traceFrames = Environment.GetEnvironmentVariable("EUTHERDRIVE_HEADLESS_TRACE_FRAMES") == "1";
                var system32InputScript = ParseSnesInputScript(Environment.GetEnvironmentVariable("EUTHERDRIVE_SYSTEM32_HEADLESS_INPUT_SCRIPT"));

                Console.WriteLine($"[HEADLESS] System32 fb_has_content={statsIn.HasContent} nonzero_pixels={statsIn.NonZeroPixels} first_nonzero=({statsIn.FirstX},{statsIn.FirstY}) fp=0x{lastFingerprint:X16}");
                DumpBgraToPpm(fbIn, wIn, hIn, sIn, Path.Combine(dumpDir, "headless_frame0.ppm"));

                for (int frame = 0; frame < framesToRun; frame++)
                {
                    var input = ResolveSnesInputForFrame(frame, system32InputScript);
                    system32.SetInputState(
                        up: input.Up,
                        down: input.Down,
                        left: input.Left,
                        right: input.Right,
                        a: input.A,
                        b: input.B,
                        c: input.X,
                        start: input.Start,
                        x: input.Y,
                        y: input.L,
                        z: input.R,
                        mode: input.Select,
                        padType: PadType.SixButton);
                    system32.RunFrame();

                    ReadOnlySpan<byte> fb = system32.GetFrameBuffer(out int w, out int h, out int s);
                    var stats = GetFrameStats(fb, w, h, s);
                    ulong fingerprint = ComputeFrameFingerprint(fb, w, h, s);
                    unchangedFrames = fingerprint == lastFingerprint ? unchangedFrames + 1 : 0;
                    lastFingerprint = fingerprint;

                    if (traceFrames || frame == 0 || frame == 5 || frame == 10 || ((frame + 1) % 60) == 0)
                        Console.WriteLine($"[HEADLESS] Frame {frame}: system32_fb_has_content={stats.HasContent} nonzero_pixels={stats.NonZeroPixels} first_nonzero=({stats.FirstX},{stats.FirstY}) fp=0x{fingerprint:X16} unchanged={unchangedFrames}");
                }

                ReadOnlySpan<byte> fbOut = system32.GetFrameBuffer(out int wOut, out int hOut, out int sOut);
                var statsOut = GetFrameStats(fbOut, wOut, hOut, sOut);
                ulong finalFingerprint = ComputeFrameFingerprint(fbOut, wOut, hOut, sOut);
                Console.WriteLine($"[HEADLESS] System32 final fb_has_content={statsOut.HasContent} nonzero_pixels={statsOut.NonZeroPixels} first_nonzero=({statsOut.FirstX},{statsOut.FirstY}) fp=0x{finalFingerprint:X16}");
                DumpBgraToPpm(fbOut, wOut, hOut, sOut, Path.Combine(dumpDir, "headless_output.ppm"));
                Console.WriteLine($"[HEADLESS] Completed {framesToRun} frames");
                return 0;
            }

            if (useDeco32)
            {
                Console.WriteLine("[HEADLESS] Using Data East Deco32 core");
                var deco32 = new Deco32Adapter();
                deco32.LoadRom(romPath);

                ReadOnlySpan<byte> fbIn = deco32.GetFrameBuffer(out int wIn, out int hIn, out int sIn);
                var statsIn = GetFrameStats(fbIn, wIn, hIn, sIn);
                ulong lastFingerprint = ComputeFrameFingerprint(fbIn, wIn, hIn, sIn);
                int unchangedFrames = 0;
                bool traceFrames = Environment.GetEnvironmentVariable("EUTHERDRIVE_HEADLESS_TRACE_FRAMES") == "1";
                var decoInputScript = ParseSnesInputScript(Environment.GetEnvironmentVariable("EUTHERDRIVE_DECO32_HEADLESS_INPUT_SCRIPT"));

                Console.WriteLine($"[HEADLESS] Deco32 fb_has_content={statsIn.HasContent} nonzero_pixels={statsIn.NonZeroPixels} first_nonzero=({statsIn.FirstX},{statsIn.FirstY}) fp=0x{lastFingerprint:X16}");
                DumpBgraToPpm(fbIn, wIn, hIn, sIn, Path.Combine(dumpDir, "headless_frame0.ppm"));

                for (int frame = 0; frame < framesToRun; frame++)
                {
                    var input = ResolveSnesInputForFrame(frame, decoInputScript);
                    deco32.SetInputState(
                        input.Up, input.Down, input.Left, input.Right,
                        input.A, input.B, input.X,
                        input.Start,
                        input.Y, input.L, input.R,
                        input.Select,
                        PadType.SixButton);
                    deco32.RunFrame();

                    ReadOnlySpan<byte> fb = deco32.GetFrameBuffer(out int w, out int h, out int s);
                    var stats = GetFrameStats(fb, w, h, s);
                    ulong fingerprint = ComputeFrameFingerprint(fb, w, h, s);
                    unchangedFrames = fingerprint == lastFingerprint ? unchangedFrames + 1 : 0;
                    lastFingerprint = fingerprint;

                    if (traceFrames || frame == 0 || frame == 5 || frame == 10 || ((frame + 1) % 60) == 0)
                        Console.WriteLine($"[HEADLESS] Frame {frame}: deco32_fb_has_content={stats.HasContent} nonzero_pixels={stats.NonZeroPixels} first_nonzero=({stats.FirstX},{stats.FirstY}) fp=0x{fingerprint:X16} unchanged={unchangedFrames} debug={deco32.DebugSummary}");
                    if (frame == 0 || frame == 5 || frame == 10)
                        DumpBgraToPpm(fb, w, h, s, Path.Combine(dumpDir, $"headless_frame{frame}.ppm"));
                }

                ReadOnlySpan<byte> fbOut = deco32.GetFrameBuffer(out int wOut, out int hOut, out int sOut);
                var statsOut = GetFrameStats(fbOut, wOut, hOut, sOut);
                ulong finalFingerprint = ComputeFrameFingerprint(fbOut, wOut, hOut, sOut);
                Console.WriteLine($"[HEADLESS] Deco32 final fb_has_content={statsOut.HasContent} nonzero_pixels={statsOut.NonZeroPixels} first_nonzero=({statsOut.FirstX},{statsOut.FirstY}) fp=0x{finalFingerprint:X16}");
                Console.WriteLine($"[HEADLESS] Deco32 debug {deco32.DebugSummary}");
                DumpBgraToPpm(fbOut, wOut, hOut, sOut, Path.Combine(dumpDir, "headless_output.ppm"));
                Console.WriteLine($"[HEADLESS] Completed {framesToRun} frames");
                return 0;
            }

            if (useNeoGeo)
            {
                Console.WriteLine("[HEADLESS] Using Neo Geo core");
                using var neoGeo = new NeoGeoAdapter();
                neoGeo.LoadRom(romPath);

                ReadOnlySpan<byte> fbIn = neoGeo.GetFrameBuffer(out int wIn, out int hIn, out int sIn);
                var statsIn = GetFrameStats(fbIn, wIn, hIn, sIn);
                ulong lastFingerprint = ComputeFrameFingerprint(fbIn, wIn, hIn, sIn);
                int unchangedFrames = 0;
                bool traceFrames = Environment.GetEnvironmentVariable("EUTHERDRIVE_HEADLESS_TRACE_FRAMES") == "1";
                var neoGeoInputScript = ParseSnesInputScript(Environment.GetEnvironmentVariable("EUTHERDRIVE_NEOGEO_HEADLESS_INPUT_SCRIPT"));

                Console.WriteLine($"[HEADLESS] NeoGeo fb_has_content={statsIn.HasContent} nonzero_pixels={statsIn.NonZeroPixels} first_nonzero=({statsIn.FirstX},{statsIn.FirstY}) fp=0x{lastFingerprint:X16}");
                DumpBgraToPpm(fbIn, wIn, hIn, sIn, Path.Combine(dumpDir, "headless_frame0.ppm"));

                for (int frame = 0; frame < framesToRun; frame++)
                {
                    var input = ResolveSnesInputForFrame(frame, neoGeoInputScript);
                    neoGeo.SetInputState(
                        input.Up, input.Down, input.Left, input.Right,
                        input.A, input.B, input.X,
                        input.Start,
                        input.Y, input.L, input.R,
                        input.Select,
                        PadType.SixButton);
                    neoGeo.RunFrame();

                    ReadOnlySpan<byte> fb = neoGeo.GetFrameBuffer(out int w, out int h, out int s);
                    var stats = GetFrameStats(fb, w, h, s);
                    ulong fingerprint = ComputeFrameFingerprint(fb, w, h, s);
                    unchangedFrames = fingerprint == lastFingerprint ? unchangedFrames + 1 : 0;
                    lastFingerprint = fingerprint;

                    if (traceFrames || frame == 0 || frame == 5 || frame == 10 || ((frame + 1) % 60) == 0)
                        Console.WriteLine($"[HEADLESS] Frame {frame}: neogeo_fb_has_content={stats.HasContent} nonzero_pixels={stats.NonZeroPixels} first_nonzero=({stats.FirstX},{stats.FirstY}) fp=0x{fingerprint:X16} unchanged={unchangedFrames}");

                    if (frame == 0 || frame == 5 || frame == 10)
                        DumpBgraToPpm(fb, w, h, s, Path.Combine(dumpDir, $"headless_frame{frame}.ppm"));
                }

                ReadOnlySpan<byte> fbOut = neoGeo.GetFrameBuffer(out int wOut, out int hOut, out int sOut);
                var statsOut = GetFrameStats(fbOut, wOut, hOut, sOut);
                ulong finalFingerprint = ComputeFrameFingerprint(fbOut, wOut, hOut, sOut);
                ReadOnlySpan<short> audioOut = neoGeo.GetAudioBuffer(out int audioRate, out int neoGeoAudioChannels);
                int audioNonZero = CountNonZeroAudioSamples(audioOut);
                Console.WriteLine($"[HEADLESS] NeoGeo final fb_has_content={statsOut.HasContent} nonzero_pixels={statsOut.NonZeroPixels} first_nonzero=({statsOut.FirstX},{statsOut.FirstY}) fp=0x{finalFingerprint:X16}");
                Console.WriteLine($"[HEADLESS] NeoGeo audio samples={audioOut.Length} rate={audioRate} channels={neoGeoAudioChannels} nonzero_samples={audioNonZero} max_abs={AudioPeak(audioOut)}");
                DumpBgraToPpm(fbOut, wOut, hOut, sOut, Path.Combine(dumpDir, "headless_output.ppm"));
                Console.WriteLine($"[HEADLESS] Completed {framesToRun} frames");
                return 0;
            }

            if (useMcsArcade)
            {
                Console.WriteLine("[HEADLESS] Using MCS arcade core");
                using var arcade = new McsArcadeAdapter();
                arcade.LoadRom(romPath);

                ReadOnlySpan<byte> fbIn = arcade.GetFrameBuffer(out int wIn, out int hIn, out int sIn);
                var statsIn = GetFrameStats(fbIn, wIn, hIn, sIn);
                ulong lastFingerprint = ComputeFrameFingerprint(fbIn, wIn, hIn, sIn);
                int unchangedFrames = 0;
                bool traceFrames = Environment.GetEnvironmentVariable("EUTHERDRIVE_HEADLESS_TRACE_FRAMES") == "1";
                var mcsInputScript = ParseSnesInputScript(Environment.GetEnvironmentVariable("EUTHERDRIVE_MCS_HEADLESS_INPUT_SCRIPT"));
                int? hshavocMcsSnapshotFrame = ParseOptionalIntEnv("EUTHERDRIVE_HSHAVOC_MCS_SNAPSHOT_FRAME");
                string hshavocMcsSnapshotDir = Environment.GetEnvironmentVariable("EUTHERDRIVE_HSHAVOC_MCS_SNAPSHOT_DIR") ?? dumpDir;

                Console.WriteLine($"[HEADLESS] MCS fb_has_content={statsIn.HasContent} nonzero_pixels={statsIn.NonZeroPixels} first_nonzero=({statsIn.FirstX},{statsIn.FirstY}) fp=0x{lastFingerprint:X16}");
                DumpBgraToPpm(fbIn, wIn, hIn, sIn, Path.Combine(dumpDir, "headless_frame0.ppm"));

                for (int frame = 0; frame < framesToRun; frame++)
                {
                    var input = ResolveSnesInputForFrame(frame, mcsInputScript);
                    arcade.SetInputState(
                        up: input.Up,
                        down: input.Down,
                        left: input.Left,
                        right: input.Right,
                        a: input.A,
                        b: input.B,
                        c: input.X,
                        start: input.Start,
                        x: input.Y,
                        y: input.L,
                        z: input.R,
                        mode: input.Select,
                        padType: PadType.SixButton);
                    arcade.RunFrame();
                    if (hshavocMcsSnapshotFrame == frame)
                    {
                        string prefix = Path.Combine(hshavocMcsSnapshotDir, $"hshavoc_mcs_frame_{frame:D6}");
                        bool dumped = arcade.TryDumpHshavocDebugSnapshot(prefix);
                        Console.WriteLine($"[HEADLESS] MCS hshavoc snapshot frame={frame} dumped={dumped} prefix={prefix}");
                    }

                    ReadOnlySpan<byte> fb = arcade.GetFrameBuffer(out int w, out int h, out int s);
                    var stats = GetFrameStats(fb, w, h, s);
                    ulong fingerprint = ComputeFrameFingerprint(fb, w, h, s);
                    unchangedFrames = fingerprint == lastFingerprint ? unchangedFrames + 1 : 0;
                    lastFingerprint = fingerprint;

                    if (traceFrames || frame == 0 || frame == 5 || frame == 10 || ((frame + 1) % 60) == 0)
                        Console.WriteLine($"[HEADLESS] Frame {frame}: mcs_fb_has_content={stats.HasContent} nonzero_pixels={stats.NonZeroPixels} first_nonzero=({stats.FirstX},{stats.FirstY}) fp=0x{fingerprint:X16} unchanged={unchangedFrames}");

                    if (frame == 0 || frame == 5 || frame == 10)
                        DumpBgraToPpm(fb, w, h, s, Path.Combine(dumpDir, $"headless_frame{frame}.ppm"));
                }

                ReadOnlySpan<byte> fbOut = arcade.GetFrameBuffer(out int wOut, out int hOut, out int sOut);
                var statsOut = GetFrameStats(fbOut, wOut, hOut, sOut);
                ulong finalFingerprint = ComputeFrameFingerprint(fbOut, wOut, hOut, sOut);
                ReadOnlySpan<short> audioOut = arcade.GetAudioBuffer(out int audioRate, out int mcsAudioChannels);
                int audioNonZero = CountNonZeroAudioSamples(audioOut);
                Console.WriteLine($"[HEADLESS] MCS final fb_has_content={statsOut.HasContent} nonzero_pixels={statsOut.NonZeroPixels} first_nonzero=({statsOut.FirstX},{statsOut.FirstY}) fp=0x{finalFingerprint:X16}");
                Console.WriteLine($"[HEADLESS] MCS audio samples={audioOut.Length} rate={audioRate} channels={mcsAudioChannels} nonzero_samples={audioNonZero} max_abs={AudioPeak(audioOut)}");
                DumpBgraToPpm(fbOut, wOut, hOut, sOut, Path.Combine(dumpDir, "headless_output.ppm"));
                Console.WriteLine($"[HEADLESS] Completed {framesToRun} frames");
                return 0;
            }

            if (useNes)
            {
                Console.WriteLine("[HEADLESS] Using NES core");
                var nes = new NesAdapter();
                nes.LoadRom(romPath);
                bool autoStart = Environment.GetEnvironmentVariable("EUTHERDRIVE_NES_HEADLESS_AUTO_START") == "1";
                int autoStartDelayFrames = ParseOptionalIntEnv("EUTHERDRIVE_NES_HEADLESS_AUTO_START_DELAY_FRAMES") ?? 90;
                int autoStartPulseFrames = ParseOptionalIntEnv("EUTHERDRIVE_NES_HEADLESS_AUTO_START_PULSE_FRAMES") ?? 2;
                int autoStartPeriodFrames = ParseOptionalIntEnv("EUTHERDRIVE_NES_HEADLESS_AUTO_START_PERIOD_FRAMES") ?? 90;
                int autoStartPulseCount = ParseOptionalIntEnv("EUTHERDRIVE_NES_HEADLESS_AUTO_START_PULSE_COUNT") ?? 4;
                bool autoStartLog = Environment.GetEnvironmentVariable("EUTHERDRIVE_NES_HEADLESS_AUTO_START_LOG") == "1";
                bool lastStartPressed = false;

                Console.WriteLine("[HEADLESS] Framebuffer BEFORE running:");
                ReadOnlySpan<byte> fbIn = nes.GetFrameBuffer(out int wIn, out int hIn, out int sIn);
                var statsIn = GetFrameStats(fbIn, wIn, hIn, sIn);
                Console.WriteLine($"[HEADLESS] NES fb_has_content={statsIn.HasContent} nonzero_pixels={statsIn.NonZeroPixels} first_nonzero=({statsIn.FirstX},{statsIn.FirstY})");
                DumpBgraToPpm(fbIn, wIn, hIn, sIn, Path.Combine(dumpDir, "headless_frame0.ppm"));

                for (int frame = 0; frame < framesToRun; frame++)
                {
                    bool startPressed = autoStart &&
                        ShouldPressStartPulse(frame, autoStartDelayFrames, autoStartPulseFrames, autoStartPeriodFrames, autoStartPulseCount);
                    nes.SetInputState(
                        up: false,
                        down: false,
                        left: false,
                        right: false,
                        a: false,
                        b: false,
                        c: false,
                        start: startPressed,
                        x: false,
                        y: false,
                        z: false,
                        mode: false,
                        padType: PadType.SixButton);
                    if (autoStartLog && startPressed != lastStartPressed)
                        Console.WriteLine($"[HEADLESS] NES auto-start start={(startPressed ? 1 : 0)} frame={frame}");
                    lastStartPressed = startPressed;

                    nes.RunFrame();
                    ReadOnlySpan<byte> fb = nes.GetFrameBuffer(out int w, out int h, out int s);
                    var stats = GetFrameStats(fb, w, h, s);
                    Console.WriteLine($"[HEADLESS] Frame {frame}: nes_fb_has_content={stats.HasContent} nonzero_pixels={stats.NonZeroPixels} first_nonzero=({stats.FirstX},{stats.FirstY})");
                    if (frame == 0 || frame == 5 || frame == 10)
                    {
                        string ppmPath = Path.Combine(dumpDir, $"headless_frame{frame}.ppm");
                        DumpBgraToPpm(fb, w, h, s, ppmPath);
                        Console.WriteLine($"[HEADLESS] Dumped frame {frame} to {ppmPath}");
                    }
                }

                Console.WriteLine("[HEADLESS] Framebuffer AFTER running:");
                ReadOnlySpan<byte> fbOut = nes.GetFrameBuffer(out int wOut, out int hOut, out int sOut);
                var statsOut = GetFrameStats(fbOut, wOut, hOut, sOut);
                Console.WriteLine($"[HEADLESS] NES fb_has_content={statsOut.HasContent} nonzero_pixels={statsOut.NonZeroPixels} first_nonzero=({statsOut.FirstX},{statsOut.FirstY})");
                DumpBgraToPpm(fbOut, wOut, hOut, sOut, Path.Combine(dumpDir, "headless_output.ppm"));
                if (Environment.GetEnvironmentVariable("EUTHERDRIVE_HEADLESS_NES_SNAPSHOT") == "1")
                {
                    string snapPrefix = nes.CaptureDebugSnapshot(dumpDir);
                    Console.WriteLine($"[HEADLESS] NES snapshot captured: {snapPrefix}");
                }
                Console.WriteLine($"[HEADLESS] Completed {framesToRun} frames");
                return 0;
            }

            if (useSnes)
            {
                Console.WriteLine("[HEADLESS] Using SNES core");
                var snes = new SnesAdapter();
                snes.LoadRom(romPath);

                HeadlessAudioSink? snesAudioSink = null;
                bool enableSnesAudio = Environment.GetEnvironmentVariable("EUTHERDRIVE_HEADLESS_AUDIO") == "1";
                if (enableSnesAudio)
                {
                    snesAudioSink = new HeadlessAudioSink();
                }

                bool autoStart = Environment.GetEnvironmentVariable("EUTHERDRIVE_SNES_HEADLESS_AUTO_START") == "1";
                int autoStartDelayFrames = ParseOptionalIntEnv("EUTHERDRIVE_SNES_HEADLESS_AUTO_START_DELAY_FRAMES") ?? 0;
                int autoStartPulseFrames = ParseOptionalIntEnv("EUTHERDRIVE_SNES_HEADLESS_AUTO_START_PULSE_FRAMES") ?? 1;
                int autoStartPeriodFrames = ParseOptionalIntEnv("EUTHERDRIVE_SNES_HEADLESS_AUTO_START_PERIOD_FRAMES") ?? 60;
                int autoStartPulseCount = ParseOptionalIntEnv("EUTHERDRIVE_SNES_HEADLESS_AUTO_START_PULSE_COUNT") ?? 1;
                bool autoStartLog = Environment.GetEnvironmentVariable("EUTHERDRIVE_SNES_HEADLESS_AUTO_START_LOG") == "1";
                bool lastStartPressed = false;
                var snesInputScript = ParseSnesInputScript(Environment.GetEnvironmentVariable("EUTHERDRIVE_SNES_HEADLESS_INPUT_SCRIPT"));
                int[] snesPeekAddrs = ParseOptionalHexAddrEnv("EUTHERDRIVE_TRACE_SNES_PEEK_ADDRS");
                int? sa1SnapshotFrameSavestate = ParseOptionalIntEnv("EUTHERDRIVE_SNES_HEADLESS_SA1_SNAPSHOT_FRAME");
                HashSet<int> snesDumpFrames = ParseFrameSetEnv("EUTHERDRIVE_HEADLESS_DUMP_FRAMES");
                int? snesDumpFrameSingle = ParseOptionalIntEnv("EUTHERDRIVE_HEADLESS_DUMP_FRAME");
                if (snesDumpFrameSingle.HasValue && snesDumpFrameSingle.Value >= 0)
                    snesDumpFrames.Add(snesDumpFrameSingle.Value);
                HashSet<int> snesRawDumpFrames = ParseFrameSetEnv("EUTHERDRIVE_HEADLESS_SNES_RAW_DUMP_FRAMES");
                int? snesRawDumpFrameSingle = ParseOptionalIntEnv("EUTHERDRIVE_HEADLESS_SNES_RAW_DUMP_FRAME");
                if (snesRawDumpFrameSingle.HasValue && snesRawDumpFrameSingle.Value >= 0)
                    snesRawDumpFrames.Add(snesRawDumpFrameSingle.Value);

                bool traceSnesFrames = Environment.GetEnvironmentVariable("EUTHERDRIVE_HEADLESS_TRACE_FRAMES") == "1";
                bool traceSnesPpuSnapshot = Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_SNES_PPU_SNAPSHOT") == "1";
                bool traceSpcWindow = Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_SNES_SPC_WINDOW") == "1";
                bool traceSnesCheckpoints = Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_SNES_CHECKPOINTS") == "1";
                bool traceSnesFrameEnd = Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_SNES_FRAME_END") == "1";
                bool sa1SnapshotOnExit = Environment.GetEnvironmentVariable("EUTHERDRIVE_SNES_HEADLESS_SA1_SNAPSHOT_ON_EXIT") == "1";
                bool traceSnesPerf = Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_SNES_PERF") == "1";
                bool traceSnesPerfEveryFrame = Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_SNES_PERF_EVERY_FRAME") == "1";
                int traceSnesCheckpointEvery = Math.Max(1, ParseOptionalIntEnv("EUTHERDRIVE_TRACE_SNES_CHECKPOINT_EVERY") ?? 1);
                int traceSnesCheckpointStart = ParseOptionalIntEnv("EUTHERDRIVE_TRACE_SNES_CHECKPOINT_START_FRAME") ?? 0;
                int traceSnesCheckpointEnd = ParseOptionalIntEnv("EUTHERDRIVE_TRACE_SNES_CHECKPOINT_END_FRAME") ?? int.MaxValue;
                int traceSnesPerfStart = ParseOptionalIntEnv("EUTHERDRIVE_TRACE_SNES_PERF_FRAME_START") ?? 0;
                int traceSnesPerfEnd = ParseOptionalIntEnv("EUTHERDRIVE_TRACE_SNES_PERF_FRAME_END") ?? int.MaxValue;
                StreamWriter? snesTraceWriter = null;
                if (traceSnesFrames || traceSnesCheckpoints)
                {
                    string tracePath = Path.Combine(dumpDir, "headless_snes_trace.log");
                    snesTraceWriter = new StreamWriter(tracePath, append: false, Encoding.UTF8)
                    {
                        AutoFlush = true
                    };
                }
                void Trace(string message)
                {
                    Console.WriteLine(message);
                    snesTraceWriter?.WriteLine(message);
                }
                void TraceFrameEnd(string message)
                {
                    Console.WriteLine(message);
                    snesTraceWriter?.WriteLine(message);
                }
                void TracePeek(string label)
                {
                    if (snesPeekAddrs.Length > 0)
                        Trace(DumpSnesPeek(snes, label, snesPeekAddrs));
                }
                void TraceCheckpoint(int frame)
                {
                    if (!traceSnesCheckpoints)
                        return;
                    if (frame < traceSnesCheckpointStart || frame > traceSnesCheckpointEnd)
                        return;
                    if (((frame - traceSnesCheckpointStart) % traceSnesCheckpointEvery) != 0)
                        return;
                    Trace($"[SNES-CHECKPOINT] frame={frame} {snes.GetDivergenceCheckpoint()}");
                }
                bool dumpSnesPpuRaw = Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_SNES_PPU_RAW") == "1";

                Console.WriteLine("[HEADLESS] Framebuffer BEFORE running:");
                DumpSnesFrame(snes, Path.Combine(dumpDir, "headless_frame0.ppm"), traceSnesFrames);
                if (dumpSnesPpuRaw)
                    DumpSnesPpuRaw(snes, Path.Combine(dumpDir, "snes_ppu_before"));
                TracePeek("before");

                bool prevHasContent = false;
                for (int frame = 0; frame < framesToRun; frame++)
                {
                    bool startPressed = autoStart &&
                        ShouldPressStartPulse(frame, autoStartDelayFrames, autoStartPulseFrames, autoStartPeriodFrames, autoStartPulseCount);
                    var scriptInput = ResolveSnesInputForFrame(frame, snesInputScript);
                    snes.SetInputState(
                        up: scriptInput.Up,
                        down: scriptInput.Down,
                        left: scriptInput.Left,
                        right: scriptInput.Right,
                        a: scriptInput.A,
                        b: scriptInput.B,
                        x: scriptInput.X,
                        y: scriptInput.Y,
                        z: scriptInput.L,
                        c: scriptInput.R,
                        start: startPressed || scriptInput.Start,
                        mode: scriptInput.Select,
                        padType: PadType.SixButton);
                    if (autoStartLog && startPressed != lastStartPressed)
                        Console.WriteLine($"[HEADLESS] SNES auto-start start={(startPressed ? 1 : 0)} frame={frame}");
                    lastStartPressed = startPressed;

                    snes.RunFrame();
                    TraceCheckpoint(frame);
                    if (ShouldTraceSnesPerfFrame(frame, traceSnesPerf, traceSnesPerfEveryFrame, traceSnesPerfStart, traceSnesPerfEnd) &&
                        snes.TryGetFramePerfSummary(out string perfSummary) &&
                        !string.IsNullOrWhiteSpace(perfSummary))
                    {
                        Trace($"[HEADLESS][SNES-PERF] frame={frame} {perfSummary.Replace(Environment.NewLine, " | ")}");
                    }

                    if (traceSnesFrames)
                    {
                        var state = snes.GetPpuState();
                        Trace($"[HEADLESS] Frame {frame}: ppu forcedBlank={state.ForcedBlank} bright={state.Brightness} mode={state.Mode} tm=0x{state.MainScreenMask:X2} ts=0x{state.SubScreenMask:X2} overscan={state.OverscanEnabled} frameOverscan={state.FrameOverscan} pseudoHires={state.PseudoHires} interlace={state.Interlace} objInterlace={state.ObjInterlace} vblank={state.InVblank} hblank={state.InHblank} nmi={state.InNmi} xy=({state.XPos},{state.YPos})");
                        ReadOnlySpan<byte> fb = snes.GetFrameBuffer(out int width, out int height, out int stride);
                        var stats = GetFrameStats(fb, width, height, stride);
                        Trace($"[HEADLESS] Frame {frame}: snes_fb_has_content={stats.HasContent} nonzero_pixels={stats.NonZeroPixels} first_nonzero=({stats.FirstX},{stats.FirstY})");
                        if (prevHasContent && !stats.HasContent)
                        {
                            Trace($"[HEADLESS] Frame {frame}: transition to BLACK (mode={state.Mode} tm=0x{state.MainScreenMask:X2} ts=0x{state.SubScreenMask:X2} forcedBlank={state.ForcedBlank} bright={state.Brightness})");
                        }
                        if (!prevHasContent && stats.HasContent)
                        {
                            Trace($"[HEADLESS] Frame {frame}: transition to CONTENT (mode={state.Mode} tm=0x{state.MainScreenMask:X2} ts=0x{state.SubScreenMask:X2} forcedBlank={state.ForcedBlank} bright={state.Brightness})");
                            if (traceSnesPpuSnapshot)
                            {
                                string? snapshot = snes.GetPpuDebugSnapshot();
                                if (!string.IsNullOrEmpty(snapshot))
                                    Trace($"[HEADLESS] Frame {frame}: ppu-snapshot{Environment.NewLine}{snapshot}");
                            }
                            TracePeek($"frame {frame} content");
                        }
                        prevHasContent = stats.HasContent;
                    }

                    if (snesAudioSink != null)
                    {
                        var audio = snes.GetAudioBuffer(out int rate, out int channels);
                        if (frame == 0)
                            snesAudioSink.Start(rate, channels);
                        if (!audio.IsEmpty)
                            snesAudioSink.Submit(audio);
                    }

                    if (frame == 0 || frame == 5 || frame == 10 || snesDumpFrames.Contains(frame))
                    {
                        DumpSnesFrame(snes, Path.Combine(dumpDir, $"headless_frame{frame}.ppm"), traceSnesFrames);
                    }
                    if (snesRawDumpFrames.Contains(frame))
                    {
                        DumpSnesPpuRaw(snes, Path.Combine(dumpDir, $"snes_ppu_frame{frame}"));
                    }
                    if (snes.System.CPU is KSNES.CPU.CPU cpu)
                    {
                        if (sa1SnapshotFrameSavestate == frame && snes.System.ROM.Sa1 is KSNES.Specialchips.SA1.Sa1 snapshotSa1)
                        {
                            string snapshotPath = Path.Combine(dumpDir, $"sa1_snapshot_frame{frame}.txt");
                            string snapshot = snapshotSa1.GetKirbyDebugSnapshot();
                            Console.WriteLine($"[HEADLESS] SA1 snapshot frame={frame}");
                            Console.WriteLine(snapshot);
                            File.WriteAllText(snapshotPath, snapshot);
                        }
                        string sa1Pc = snes.System.ROM.Sa1 is KSNES.Specialchips.SA1.Sa1 sa1 && sa1.GetCpu() is KSNES.CPU.CPU sa1Cpu ? $" SA1 PC=0x{sa1Cpu.ProgramCounter24:X6}" : "";
                        ushort? spcPcValue = snes.System.APU?.Spc?.ProgramCounter;
                        string spcPc = spcPcValue.HasValue ? $" SPC PC=0x{spcPcValue.Value:X4}" : "";
                        if (traceSnesFrames || traceSnesFrameEnd || traceSpcWindow)
                            TraceFrameEnd($"[HEADLESS] Frame {frame} ending SNES PC=0x{cpu.ProgramCounter24:X6}{sa1Pc}{spcPc}");
                        if (traceSpcWindow && spcPcValue.HasValue)
                        {
                            TraceFrameEnd($"[HEADLESS] Frame {frame} SPC window {DumpSpcWindow(snes.System.APU, spcPcValue.Value)}");
                        }
                    }
                }

                Console.WriteLine("[HEADLESS] Framebuffer AFTER running:");
                DumpSnesFrame(snes, Path.Combine(dumpDir, "headless_output.ppm"), traceSnesFrames);
                if (dumpSnesPpuRaw)
                    DumpSnesPpuRaw(snes, Path.Combine(dumpDir, "snes_ppu_after"));
                if (sa1SnapshotOnExit && snes.System.ROM.Sa1 is KSNES.Specialchips.SA1.Sa1 finalSa1)
                {
                    string snapshotPath = Path.Combine(dumpDir, "sa1_snapshot_final.txt");
                    File.WriteAllText(snapshotPath, finalSa1.GetKirbyDebugSnapshot());
                    Console.WriteLine($"[HEADLESS] SA1 final snapshot: {snapshotPath}");
                }
                if (snesPeekAddrs.Length > 0)
                    Console.WriteLine(DumpSnesPeek(snes, "after", snesPeekAddrs));
                snesAudioSink?.Dispose();
                snesTraceWriter?.Dispose();
                Console.WriteLine($"[HEADLESS] Completed {framesToRun} frames");
                return 0;
            }

            if (useGb)
            {
                Console.WriteLine("[HEADLESS] Using GB/GBC core");
                var gb = new GbAdapter();
                gb.LoadRom(romPath);
                Console.WriteLine($"[HEADLESS] {gb.RomSummary}");

                HeadlessAudioSink? gbAudioSink = null;
                bool enableGbAudio = Environment.GetEnvironmentVariable("EUTHERDRIVE_HEADLESS_AUDIO") == "1";
                if (enableGbAudio)
                    gbAudioSink = new HeadlessAudioSink();

                ReadOnlySpan<byte> gbFbIn = gb.GetFrameBuffer(out int gbWIn, out int gbHIn, out int gbSIn);
                DumpBgraToPpm(gbFbIn, gbWIn, gbHIn, gbSIn, Path.Combine(dumpDir, "headless_frame0.ppm"));

                for (int frame = 0; frame < framesToRun; frame++)
                {
                    gb.RunFrame();
                    var samples = gb.GetAudioBuffer(out int sampleRate, out int channels);
                    if (!samples.IsEmpty)
                    {
                        gbAudioSink ??= new HeadlessAudioSink();
                        gbAudioSink.Start(sampleRate, channels);
                        gbAudioSink.Submit(samples);
                    }
                }

                ReadOnlySpan<byte> gbFbOut = gb.GetFrameBuffer(out int gbWOut, out int gbHOut, out int gbSOut);
                DumpBgraToPpm(gbFbOut, gbWOut, gbHOut, gbSOut, Path.Combine(dumpDir, "headless_output.ppm"));
                gbAudioSink?.Dispose();
                Console.WriteLine($"[HEADLESS] Completed {framesToRun} frames");
                return 0;
            }

            if (useGba)
            {
                Console.WriteLine("[HEADLESS] Using GBA core");
                var gba = new GbaAdapter();
                gba.LoadRom(romPath);
                string gbaTracePath = Path.Combine(dumpDir, "headless_gba_trace.log");
                using var gbaTraceWriter = new StreamWriter(gbaTracePath, append: false, Encoding.UTF8) { AutoFlush = true };
                void TraceGba(string message)
                {
                    Console.WriteLine(message);
                    gbaTraceWriter.WriteLine(message);
                }

                HeadlessAudioSink? gbaAudioSink = null;
                bool enableGbaAudio = Environment.GetEnvironmentVariable("EUTHERDRIVE_HEADLESS_AUDIO") == "1";
                if (enableGbaAudio)
                    gbaAudioSink = new HeadlessAudioSink();

                bool autoStart = Environment.GetEnvironmentVariable("EUTHERDRIVE_GBA_HEADLESS_AUTO_START") == "1";
                int autoStartDelayFrames = ParseOptionalIntEnv("EUTHERDRIVE_GBA_HEADLESS_AUTO_START_DELAY_FRAMES") ?? 0;
                int autoStartPulseFrames = ParseOptionalIntEnv("EUTHERDRIVE_GBA_HEADLESS_AUTO_START_PULSE_FRAMES") ?? 2;
                int autoStartPeriodFrames = ParseOptionalIntEnv("EUTHERDRIVE_GBA_HEADLESS_AUTO_START_PERIOD_FRAMES") ?? 60;
                int autoStartPulseCount = ParseOptionalIntEnv("EUTHERDRIVE_GBA_HEADLESS_AUTO_START_PULSE_COUNT") ?? 1;
                bool autoStartLog = Environment.GetEnvironmentVariable("EUTHERDRIVE_GBA_HEADLESS_AUTO_START_LOG") == "1";
                bool traceGbaFrames = Environment.GetEnvironmentVariable("EUTHERDRIVE_HEADLESS_TRACE_FRAMES") == "1";
                bool lastStartPressed = false;
                var gbaInputScript = ParseSnesInputScript(Environment.GetEnvironmentVariable("EUTHERDRIVE_GBA_HEADLESS_INPUT_SCRIPT"));

                TraceGba($"[HEADLESS] {gba.RomSummary}");
                TraceGba("[HEADLESS] Framebuffer BEFORE running:");
                ReadOnlySpan<byte> fbIn = gba.GetFrameBuffer(out int wIn, out int hIn, out int sIn);
                var statsIn = GetFrameStats(fbIn, wIn, hIn, sIn);
                ulong lastFingerprint = ComputeFrameFingerprint(fbIn, wIn, hIn, sIn);
                int unchangedFrames = 0;
                TraceGba($"[HEADLESS] GBA fb_has_content={statsIn.HasContent} nonzero_pixels={statsIn.NonZeroPixels} first_nonzero=({statsIn.FirstX},{statsIn.FirstY}) frameCounter={gba.FrameCounter ?? -1} keyinput=0x{gba.DebugKeyInput ?? 0xFFFF:X4} fp=0x{lastFingerprint:X16}");
                DumpBgraToPpm(fbIn, wIn, hIn, sIn, Path.Combine(dumpDir, "headless_frame0.ppm"));

                for (int frame = 0; frame < framesToRun; frame++)
                {
                    bool startPressed = autoStart &&
                        ShouldPressStartPulse(frame, autoStartDelayFrames, autoStartPulseFrames, autoStartPeriodFrames, autoStartPulseCount);
                    var scriptInput = ResolveSnesInputForFrame(frame, gbaInputScript);
                    gba.SetInputState(
                        up: scriptInput.Up,
                        down: scriptInput.Down,
                        left: scriptInput.Left,
                        right: scriptInput.Right,
                        a: scriptInput.A,
                        b: scriptInput.B,
                        c: scriptInput.R,
                        start: startPressed || scriptInput.Start,
                        x: false,
                        y: false,
                        z: scriptInput.L,
                        mode: scriptInput.Select,
                        padType: PadType.SixButton);
                    if (autoStartLog && startPressed != lastStartPressed)
                        TraceGba($"[HEADLESS] GBA auto-start start={(startPressed ? 1 : 0)} frame={frame}");
                    lastStartPressed = startPressed;

                    gba.RunFrame();

                    if (gbaAudioSink != null)
                    {
                        var audio = gba.GetAudioBuffer(out int rate, out int channels);
                        if (frame == 0)
                            gbaAudioSink.Start(rate, channels);
                        if (!audio.IsEmpty)
                            gbaAudioSink.Submit(audio);
                    }

                    ReadOnlySpan<byte> fb = gba.GetFrameBuffer(out int w, out int h, out int s);
                    var stats = GetFrameStats(fb, w, h, s);
                    ulong fingerprint = ComputeFrameFingerprint(fb, w, h, s);
                    unchangedFrames = fingerprint == lastFingerprint ? (unchangedFrames + 1) : 0;
                    lastFingerprint = fingerprint;

                    if (traceGbaFrames || frame == 0 || frame == 5 || frame == 10 || ((frame + 1) % 60) == 0)
                    {
                        TraceGba($"[HEADLESS] Frame {frame}: gba_fb_has_content={stats.HasContent} nonzero_pixels={stats.NonZeroPixels} first_nonzero=({stats.FirstX},{stats.FirstY}) frameCounter={gba.FrameCounter ?? -1} keyinput=0x{gba.DebugKeyInput ?? 0xFFFF:X4} fp=0x{fingerprint:X16} unchanged={unchangedFrames}");
                    }

                    if (frame == 0 || frame == 5 || frame == 10)
                        DumpBgraToPpm(fb, w, h, s, Path.Combine(dumpDir, $"headless_frame{frame}.ppm"));
                }

                TraceGba("[HEADLESS] Framebuffer AFTER running:");
                ReadOnlySpan<byte> fbOut = gba.GetFrameBuffer(out int wOut, out int hOut, out int sOut);
                var statsOut = GetFrameStats(fbOut, wOut, hOut, sOut);
                ulong finalFingerprint = ComputeFrameFingerprint(fbOut, wOut, hOut, sOut);
                TraceGba($"[HEADLESS] GBA fb_has_content={statsOut.HasContent} nonzero_pixels={statsOut.NonZeroPixels} first_nonzero=({statsOut.FirstX},{statsOut.FirstY}) frameCounter={gba.FrameCounter ?? -1} keyinput=0x{gba.DebugKeyInput ?? 0xFFFF:X4} fp=0x{finalFingerprint:X16}");
                DumpBgraToPpm(fbOut, wOut, hOut, sOut, Path.Combine(dumpDir, "headless_output.ppm"));
                gbaAudioSink?.Dispose();
                TraceGba($"[HEADLESS] Completed {framesToRun} frames");
                return 0;
            }

            if (use32X)
            {
                bool use32XScaffold = Environment.GetEnvironmentVariable("EUTHERDRIVE_HEADLESS_32X_SCAFFOLD") == "1";
                if (!use32XScaffold)
                {
                    Console.WriteLine("[HEADLESS] Using Sega 32X core via MD host bridge");
                    var md = new MdTracerAdapter();
                    md.LoadRom(romPath);

                    ReadOnlySpan<byte> hostFbIn = md.GetFrameBuffer(out int hostWIn, out int hostHIn, out int hostSIn);
                    var hostStatsIn = GetFrameStats(hostFbIn, hostWIn, hostHIn, hostSIn);
                    ulong hostLastFingerprint = ComputeFrameFingerprint(hostFbIn, hostWIn, hostHIn, hostSIn);
                    int hostUnchangedFrames = 0;
                    bool hostTrace32XFrames = Environment.GetEnvironmentVariable("EUTHERDRIVE_HEADLESS_TRACE_FRAMES") == "1";
                    bool hostTrace32XWords = Environment.GetEnvironmentVariable("EUTHERDRIVE_HEADLESS_TRACE_32X_WORDS") == "1";
                    bool hostDump32XLayer = Environment.GetEnvironmentVariable("EUTHERDRIVE_HEADLESS_DUMP_32X_LAYER") == "1";
                    bool hostDump32XOtherLayer = Environment.GetEnvironmentVariable("EUTHERDRIVE_HEADLESS_DUMP_32X_OTHER_LAYER") == "1";
                    bool hostDump32XRaw = Environment.GetEnvironmentVariable("EUTHERDRIVE_HEADLESS_DUMP_32X_RAW") == "1";

                    Console.WriteLine(
                        $"[HEADLESS] 32X-host fb_has_content={hostStatsIn.HasContent} nonzero_pixels={hostStatsIn.NonZeroPixels} " +
                        $"first_nonzero=({hostStatsIn.FirstX},{hostStatsIn.FirstY}) frameCounter={md.FrameCounter ?? -1} " +
                        $"m68k=0x{md.GetM68kPc():X6} " +
                        $"mpc=0x{md.Debug32XMasterProgramCounter ?? 0:X8} spc=0x{md.Debug32XSlaveProgramCounter ?? 0:X8} fp=0x{hostLastFingerprint:X16}");
                    if (md.Debug32XRenderedPixelStats is { } hostPixelStatsIn)
                    {
                        Console.WriteLine(
                            $"[HEADLESS] 32X-host pixels transparent={hostPixelStatsIn.TransparentPixels} " +
                            $"low={hostPixelStatsIn.LowPriorityPixels} high={hostPixelStatsIn.HighPriorityPixels}");
                    }
                    if (hostTrace32XWords)
                    {
                        Console.WriteLine(
                            $"[HEADLESS] 32X-host words m={md.Debug32XMasterWords ?? string.Empty} s={md.Debug32XSlaveWords ?? string.Empty} comm={md.Debug32XCommPorts ?? string.Empty}");
                    }
                    DumpBgraToPpm(hostFbIn, hostWIn, hostHIn, hostSIn, Path.Combine(dumpDir, "headless_frame0.ppm"));
                    if (hostDump32XLayer)
                        md.Dump32XLayerToPpm(Path.Combine(dumpDir, "headless_frame0_32x.ppm"));
                    if (hostDump32XOtherLayer)
                        md.Dump32XOtherLayerToPpm(Path.Combine(dumpDir, "headless_frame0_32x_other.ppm"));
                    if (hostDump32XRaw)
                        md.Dump32XRawVdpState(Path.Combine(dumpDir, "headless_frame0_32x_raw"));

                    for (int frame = 0; frame < framesToRun; frame++)
                    {
                        md.RunFrame();
                        ReadOnlySpan<byte> hostFb = md.GetFrameBuffer(out int hostW, out int hostH, out int hostS);
                        var hostStats = GetFrameStats(hostFb, hostW, hostH, hostS);
                        ulong hostFingerprint = ComputeFrameFingerprint(hostFb, hostW, hostH, hostS);
                        hostUnchangedFrames = hostFingerprint == hostLastFingerprint ? (hostUnchangedFrames + 1) : 0;
                        hostLastFingerprint = hostFingerprint;

                        if (hostTrace32XFrames || frame == 0 || frame == 1 || frame == 2 || frame == 5 || frame == 10 || ((frame + 1) % 60) == 0)
                        {
                            Console.WriteLine(
                                $"[HEADLESS] Frame {frame}: 32x_host_fb_has_content={hostStats.HasContent} nonzero_pixels={hostStats.NonZeroPixels} " +
                                $"first_nonzero=({hostStats.FirstX},{hostStats.FirstY}) frameCounter={md.FrameCounter ?? -1} " +
                                $"m68k=0x{md.GetM68kPc():X6} " +
                                $"mpc=0x{md.Debug32XMasterProgramCounter ?? 0:X8} spc=0x{md.Debug32XSlaveProgramCounter ?? 0:X8} " +
                                $"fp=0x{hostFingerprint:X16} unchanged={hostUnchangedFrames}");
                            if (md.Debug32XRenderedPixelStats is { } hostPixelStats)
                            {
                                Console.WriteLine(
                                    $"[HEADLESS] Frame {frame}: 32x_host_pixels transparent={hostPixelStats.TransparentPixels} " +
                                    $"low={hostPixelStats.LowPriorityPixels} high={hostPixelStats.HighPriorityPixels}");
                            }
                            if (hostTrace32XWords)
                            {
                                Console.WriteLine(
                                    $"[HEADLESS] Frame {frame}: 32x_host_words m={md.Debug32XMasterWords ?? string.Empty} s={md.Debug32XSlaveWords ?? string.Empty} comm={md.Debug32XCommPorts ?? string.Empty}");
                            }
                        }

                        if (frame == 0 || frame == 1 || frame == 2 || frame == 5 || frame == 10)
                        {
                            DumpBgraToPpm(hostFb, hostW, hostH, hostS, Path.Combine(dumpDir, $"headless_frame{frame}.ppm"));
                            if (hostDump32XLayer)
                                md.Dump32XLayerToPpm(Path.Combine(dumpDir, $"headless_frame{frame}_32x.ppm"));
                            if (hostDump32XOtherLayer)
                                md.Dump32XOtherLayerToPpm(Path.Combine(dumpDir, $"headless_frame{frame}_32x_other.ppm"));
                            if (hostDump32XRaw)
                                md.Dump32XRawVdpState(Path.Combine(dumpDir, $"headless_frame{frame}_32x_raw"));
                        }
                    }

                    ReadOnlySpan<byte> hostFbOut = md.GetFrameBuffer(out int hostWOut, out int hostHOut, out int hostSOut);
                    var hostStatsOut = GetFrameStats(hostFbOut, hostWOut, hostHOut, hostSOut);
                    ulong hostFinalFingerprint = ComputeFrameFingerprint(hostFbOut, hostWOut, hostHOut, hostSOut);
                    Console.WriteLine(
                        $"[HEADLESS] 32X-host final fb_has_content={hostStatsOut.HasContent} nonzero_pixels={hostStatsOut.NonZeroPixels} " +
                        $"first_nonzero=({hostStatsOut.FirstX},{hostStatsOut.FirstY}) frameCounter={md.FrameCounter ?? -1} " +
                        $"m68k=0x{md.GetM68kPc():X6} " +
                        $"mpc=0x{md.Debug32XMasterProgramCounter ?? 0:X8} spc=0x{md.Debug32XSlaveProgramCounter ?? 0:X8} fp=0x{hostFinalFingerprint:X16}");
                    if (md.Debug32XRenderedPixelStats is { } hostPixelStatsOut)
                    {
                        Console.WriteLine(
                            $"[HEADLESS] 32X-host final pixels transparent={hostPixelStatsOut.TransparentPixels} " +
                            $"low={hostPixelStatsOut.LowPriorityPixels} high={hostPixelStatsOut.HighPriorityPixels}");
                    }
                    if (hostTrace32XWords)
                    {
                        Console.WriteLine(
                            $"[HEADLESS] 32X-host final words m={md.Debug32XMasterWords ?? string.Empty} s={md.Debug32XSlaveWords ?? string.Empty} comm={md.Debug32XCommPorts ?? string.Empty}");
                    }
                    DumpBgraToPpm(hostFbOut, hostWOut, hostHOut, hostSOut, Path.Combine(dumpDir, "headless_output.ppm"));
                    if (hostDump32XLayer)
                        md.Dump32XLayerToPpm(Path.Combine(dumpDir, "headless_output_32x.ppm"));
                    if (hostDump32XOtherLayer)
                        md.Dump32XOtherLayerToPpm(Path.Combine(dumpDir, "headless_output_32x_other.ppm"));
                    if (hostDump32XRaw)
                        md.Dump32XRawVdpState(Path.Combine(dumpDir, "headless_output_32x_raw"));
                    Console.WriteLine($"[HEADLESS] Completed {framesToRun} frames");
                    return 0;
                }

                Console.WriteLine("[HEADLESS] Using Sega 32X scaffold core");
                var s32x = new Sega32XAdapter();
                s32x.LoadRom(romPath);

                Console.WriteLine($"[HEADLESS] {s32x.RomSummary}");
                ReadOnlySpan<byte> fbIn = s32x.GetFrameBuffer(out int wIn, out int hIn, out int sIn);
                var statsIn = GetFrameStats(fbIn, wIn, hIn, sIn);
                ulong lastFingerprint = ComputeFrameFingerprint(fbIn, wIn, hIn, sIn);
                int unchangedFrames = 0;
                bool trace32XFrames = Environment.GetEnvironmentVariable("EUTHERDRIVE_HEADLESS_TRACE_FRAMES") == "1";

                Console.WriteLine(
                    $"[HEADLESS] 32X fb_has_content={statsIn.HasContent} nonzero_pixels={statsIn.NonZeroPixels} " +
                    $"first_nonzero=({statsIn.FirstX},{statsIn.FirstY}) frameCounter={s32x.FrameCounter ?? -1} " +
                    $"mpc=0x{s32x.DebugMasterProgramCounter ?? 0:X8} spc=0x{s32x.DebugSlaveProgramCounter ?? 0:X8} fp=0x{lastFingerprint:X16}");
                DumpBgraToPpm(fbIn, wIn, hIn, sIn, Path.Combine(dumpDir, "headless_frame0.ppm"));

                for (int frame = 0; frame < framesToRun; frame++)
                {
                    s32x.RunFrame();
                    ReadOnlySpan<byte> fb = s32x.GetFrameBuffer(out int w, out int h, out int s);
                    var stats = GetFrameStats(fb, w, h, s);
                    ulong fingerprint = ComputeFrameFingerprint(fb, w, h, s);
                    unchangedFrames = fingerprint == lastFingerprint ? (unchangedFrames + 1) : 0;
                    lastFingerprint = fingerprint;

                    if (trace32XFrames || frame == 0 || frame == 1 || frame == 2 || frame == 5 || frame == 10 || ((frame + 1) % 60) == 0)
                    {
                        Console.WriteLine(
                            $"[HEADLESS] Frame {frame}: 32x_fb_has_content={stats.HasContent} nonzero_pixels={stats.NonZeroPixels} " +
                            $"first_nonzero=({stats.FirstX},{stats.FirstY}) frameCounter={s32x.FrameCounter ?? -1} " +
                            $"mpc=0x{s32x.DebugMasterProgramCounter ?? 0:X8} spc=0x{s32x.DebugSlaveProgramCounter ?? 0:X8} " +
                            $"fp=0x{fingerprint:X16} unchanged={unchangedFrames}");
                    }

                    if (frame == 0 || frame == 1 || frame == 2 || frame == 5 || frame == 10)
                        DumpBgraToPpm(fb, w, h, s, Path.Combine(dumpDir, $"headless_frame{frame}.ppm"));
                }

                ReadOnlySpan<byte> fbOut = s32x.GetFrameBuffer(out int wOut, out int hOut, out int sOut);
                var statsOut = GetFrameStats(fbOut, wOut, hOut, sOut);
                ulong finalFingerprint = ComputeFrameFingerprint(fbOut, wOut, hOut, sOut);
                Console.WriteLine(
                    $"[HEADLESS] 32X final fb_has_content={statsOut.HasContent} nonzero_pixels={statsOut.NonZeroPixels} " +
                    $"first_nonzero=({statsOut.FirstX},{statsOut.FirstY}) frameCounter={s32x.FrameCounter ?? -1} " +
                    $"mpc=0x{s32x.DebugMasterProgramCounter ?? 0:X8} spc=0x{s32x.DebugSlaveProgramCounter ?? 0:X8} fp=0x{finalFingerprint:X16}");
                DumpBgraToPpm(fbOut, wOut, hOut, sOut, Path.Combine(dumpDir, "headless_output.ppm"));
                Console.WriteLine($"[HEADLESS] Completed {framesToRun} frames");
                return 0;
            }

            if (useSmsGg)
            {
                Console.WriteLine("[HEADLESS] Using SMS/GG core");
                var smsgg = new SmsGgAdapter();
                smsgg.LoadRom(romPath);

                HeadlessAudioSink? smsggAudioSink = null;
                bool enableSmsGgAudio = Environment.GetEnvironmentVariable("EUTHERDRIVE_HEADLESS_AUDIO") == "1";
                if (enableSmsGgAudio)
                    smsggAudioSink = new HeadlessAudioSink();

                bool autoStart = Environment.GetEnvironmentVariable("EUTHERDRIVE_SMSGG_HEADLESS_AUTO_START") == "1";
                int autoStartDelayFrames = ParseOptionalIntEnv("EUTHERDRIVE_SMSGG_HEADLESS_AUTO_START_DELAY_FRAMES") ?? 0;
                int autoStartPulseFrames = ParseOptionalIntEnv("EUTHERDRIVE_SMSGG_HEADLESS_AUTO_START_PULSE_FRAMES") ?? 2;
                int autoStartPeriodFrames = ParseOptionalIntEnv("EUTHERDRIVE_SMSGG_HEADLESS_AUTO_START_PERIOD_FRAMES") ?? 60;
                int autoStartPulseCount = ParseOptionalIntEnv("EUTHERDRIVE_SMSGG_HEADLESS_AUTO_START_PULSE_COUNT") ?? 1;
                bool autoStartLog = Environment.GetEnvironmentVariable("EUTHERDRIVE_SMSGG_HEADLESS_AUTO_START_LOG") == "1";
                bool traceSmsGgFrames = Environment.GetEnvironmentVariable("EUTHERDRIVE_HEADLESS_TRACE_FRAMES") == "1";
                bool lastStartPressed = false;

                Console.WriteLine($"[HEADLESS] {smsgg.RomSummary}");
                ReadOnlySpan<byte> fbIn = smsgg.GetFrameBuffer(out int wIn, out int hIn, out int sIn);
                var statsIn = GetFrameStats(fbIn, wIn, hIn, sIn);
                ulong lastFingerprint = ComputeFrameFingerprint(fbIn, wIn, hIn, sIn);
                int unchangedFrames = 0;
                Console.WriteLine($"[HEADLESS] SMSGG fb_has_content={statsIn.HasContent} nonzero_pixels={statsIn.NonZeroPixels} first_nonzero=({statsIn.FirstX},{statsIn.FirstY}) fp=0x{lastFingerprint:X16}");
                DumpBgraToPpm(fbIn, wIn, hIn, sIn, Path.Combine(dumpDir, "headless_frame0.ppm"));

                for (int frame = 0; frame < framesToRun; frame++)
                {
                    bool startPressed = autoStart &&
                        ShouldPressStartPulse(frame, autoStartDelayFrames, autoStartPulseFrames, autoStartPeriodFrames, autoStartPulseCount);
                    smsgg.SetInputState(
                        up: false,
                        down: false,
                        left: false,
                        right: false,
                        a: false,
                        b: false,
                        c: false,
                        start: startPressed,
                        x: false,
                        y: false,
                        z: false,
                        mode: false,
                        padType: PadType.SixButton);
                    if (autoStartLog && startPressed != lastStartPressed)
                        Console.WriteLine($"[HEADLESS] SMSGG auto-start start={(startPressed ? 1 : 0)} frame={frame}");
                    lastStartPressed = startPressed;

                    smsgg.RunFrame();

                    if (smsggAudioSink != null)
                    {
                        var audio = smsgg.GetAudioBuffer(out int rate, out int channels);
                        if (frame == 0)
                            smsggAudioSink.Start(rate, channels);
                        if (!audio.IsEmpty)
                            smsggAudioSink.Submit(audio);
                    }

                    ReadOnlySpan<byte> fb = smsgg.GetFrameBuffer(out int w, out int h, out int s);
                    var stats = GetFrameStats(fb, w, h, s);
                    ulong fingerprint = ComputeFrameFingerprint(fb, w, h, s);
                    unchangedFrames = fingerprint == lastFingerprint ? (unchangedFrames + 1) : 0;
                    lastFingerprint = fingerprint;

                    if (traceSmsGgFrames || frame == 0 || frame == 1 || frame == 2 || frame == 5 || frame == 10 || ((frame + 1) % 60) == 0)
                    {
                        Console.WriteLine($"[HEADLESS] Frame {frame}: smsgg_fb_has_content={stats.HasContent} nonzero_pixels={stats.NonZeroPixels} first_nonzero=({stats.FirstX},{stats.FirstY}) fp=0x{fingerprint:X16} unchanged={unchangedFrames}");
                    }

                    if (frame == 0 || frame == 1 || frame == 2 || frame == 5 || frame == 10)
                        DumpBgraToPpm(fb, w, h, s, Path.Combine(dumpDir, $"headless_frame{frame}.ppm"));
                }

                ReadOnlySpan<byte> fbOut = smsgg.GetFrameBuffer(out int wOut, out int hOut, out int sOut);
                var statsOut = GetFrameStats(fbOut, wOut, hOut, sOut);
                ulong finalFingerprint = ComputeFrameFingerprint(fbOut, wOut, hOut, sOut);
                Console.WriteLine($"[HEADLESS] SMSGG final fb_has_content={statsOut.HasContent} nonzero_pixels={statsOut.NonZeroPixels} first_nonzero=({statsOut.FirstX},{statsOut.FirstY}) fp=0x{finalFingerprint:X16}");
                DumpBgraToPpm(fbOut, wOut, hOut, sOut, Path.Combine(dumpDir, "headless_output.ppm"));
                smsggAudioSink?.Dispose();
                Console.WriteLine($"[HEADLESS] Completed {framesToRun} frames");
                return 0;
            }

            if (useN64)
            {
                Console.WriteLine("[HEADLESS] Using N64 core");
                SetEnvDefault("EUTHERDRIVE_N64_SKIP_AUDIO", "1");
                var n64 = new N64Adapter();
                n64.LoadRom(romPath);

                HeadlessAudioSink? n64AudioSink = null;
                bool enableN64Audio = Environment.GetEnvironmentVariable("EUTHERDRIVE_HEADLESS_AUDIO") == "1";
                if (enableN64Audio)
                    n64AudioSink = new HeadlessAudioSink();

                Console.WriteLine("[HEADLESS] Framebuffer BEFORE running:");
                ReadOnlySpan<byte> fb0 = n64.GetFrameBuffer(out int w0, out int h0, out int s0);
                var stats0 = GetFrameStats(fb0, w0, h0, s0);
                Console.WriteLine($"[HEADLESS] N64 fb_has_content={stats0.HasContent} nonzero_pixels={stats0.NonZeroPixels} first_nonzero=({stats0.FirstX},{stats0.FirstY})");
                DumpBgraToPpm(fb0, w0, h0, s0, Path.Combine(dumpDir, "headless_frame0.ppm"));

                bool stopOnFramebuffer = IsEnvEnabled("EUTHERDRIVE_N64_HEADLESS_STOP_ON_FRAMEBUFFER");
                int stopMinFrame = ParseOptionalIntEnv("EUTHERDRIVE_N64_HEADLESS_STOP_MIN_FRAME") ?? 0;
                int stopStableFrames = Math.Max(1, ParseOptionalIntEnv("EUTHERDRIVE_N64_HEADLESS_STOP_STABLE_FRAMES") ?? 1);
                int framebufferStableCount = 0;
                int completedFrames = 0;

                for (int frame = 0; frame < framesToRun; frame++)
                {
                    n64.RunFrame();
                    completedFrames = frame + 1;

                    if (n64AudioSink != null)
                    {
                        var audio = n64.GetAudioBuffer(out int rate, out int channels);
                        if (frame == 0)
                            n64AudioSink.Start(rate, channels);
                        if (!audio.IsEmpty)
                            n64AudioSink.Submit(audio);
                    }

                    if (frame == 0 || frame == 5 || frame == 10)
                    {
                        ReadOnlySpan<byte> fb = n64.GetFrameBuffer(out int w, out int h, out int s);
                        var stats = GetFrameStats(fb, w, h, s);
                        Console.WriteLine($"[HEADLESS] Frame {frame}: fb_has_content={stats.HasContent} nonzero_pixels={stats.NonZeroPixels} first_nonzero=({stats.FirstX},{stats.FirstY})");
                        string ppmPath = Path.Combine(dumpDir, $"headless_frame{frame}.ppm");
                        DumpBgraToPpm(fb, w, h, s, ppmPath);
                        Console.WriteLine($"[HEADLESS] Dumped frame {frame} to {ppmPath}");
                    }

                    if (stopOnFramebuffer && frame >= stopMinFrame)
                    {
                        ReadOnlySpan<byte> fb = n64.GetFrameBuffer(out int w, out int h, out int s);
                        var stats = GetFrameStats(fb, w, h, s);
                        framebufferStableCount = stats.HasContent ? framebufferStableCount + 1 : 0;
                        if (framebufferStableCount >= stopStableFrames)
                        {
                            Console.WriteLine(
                                $"[HEADLESS] N64 early stop at frame {frame}: fb_has_content={stats.HasContent} " +
                                $"nonzero_pixels={stats.NonZeroPixels} first_nonzero=({stats.FirstX},{stats.FirstY}) stable={framebufferStableCount}");
                            break;
                        }
                    }
                }

                Console.WriteLine("[HEADLESS] Framebuffer AFTER running:");
                ReadOnlySpan<byte> fbOut = n64.GetFrameBuffer(out int wOut, out int hOut, out int sOut);
                var statsOut = GetFrameStats(fbOut, wOut, hOut, sOut);
                Console.WriteLine($"[HEADLESS] N64 fb_has_content={statsOut.HasContent} nonzero_pixels={statsOut.NonZeroPixels} first_nonzero=({statsOut.FirstX},{statsOut.FirstY})");
                DumpBgraToPpm(fbOut, wOut, hOut, sOut, Path.Combine(dumpDir, "headless_output.ppm"));
                n64AudioSink?.Dispose();
                // Stop R4300 thread before exit to avoid background runaway logs after frame loop.
                n64.Reset();
                Console.WriteLine($"[HEADLESS] Completed {completedFrames} frames");
                return 0;
            }

            if (usePsx)
            {
                Console.WriteLine("[HEADLESS] Using PSX core");
                ConfigurePsxAdapterFromEnv();

                var psx = new PsxAdapter();
                psx.LoadRom(romPath);

                HeadlessAudioSink? psxAudioSink = null;
                bool enablePsxAudio = Environment.GetEnvironmentVariable("EUTHERDRIVE_HEADLESS_AUDIO") == "1";
                if (enablePsxAudio)
                    psxAudioSink = new HeadlessAudioSink();

                bool autoStart = Environment.GetEnvironmentVariable("EUTHERDRIVE_PSX_HEADLESS_AUTO_START") == "1";
                int autoStartDelayFrames = ParseOptionalIntEnv("EUTHERDRIVE_PSX_HEADLESS_AUTO_START_DELAY_FRAMES") ?? 120;
                int autoStartPulseFrames = ParseOptionalIntEnv("EUTHERDRIVE_PSX_HEADLESS_AUTO_START_PULSE_FRAMES") ?? 2;
                int autoStartPeriodFrames = ParseOptionalIntEnv("EUTHERDRIVE_PSX_HEADLESS_AUTO_START_PERIOD_FRAMES") ?? 60;
                int autoStartPulseCount = ParseOptionalIntEnv("EUTHERDRIVE_PSX_HEADLESS_AUTO_START_PULSE_COUNT") ?? 4;
                bool autoStartLog = Environment.GetEnvironmentVariable("EUTHERDRIVE_PSX_HEADLESS_AUTO_START_LOG") == "1";
                bool tracePsxFrames = Environment.GetEnvironmentVariable("EUTHERDRIVE_HEADLESS_TRACE_FRAMES") == "1";
                bool tracePsxStart = Environment.GetEnvironmentVariable("EUTHERDRIVE_PSX_TRACE_START") == "1";
                string? tracePsxStartFile = Environment.GetEnvironmentVariable("EUTHERDRIVE_PSX_START_TRACE_FILE");
                string? tracePsxCodeFile = Environment.GetEnvironmentVariable("EUTHERDRIVE_PSX_CODE_TRACE_FILE");
                int[] tracePsxCodeAddresses = ParseOptionalHexAddrEnv("EUTHERDRIVE_PSX_CODE_TRACE_ADDR");
                uint? tracePsxCodeAddress = tracePsxCodeAddresses.Length > 0 ? (uint)tracePsxCodeAddresses[0] : null;
                int tracePsxFrameStart = ParseOptionalIntEnv("EUTHERDRIVE_PSX_TRACE_FRAME_START") ?? 0;
                int tracePsxFrameEnd = ParseOptionalIntEnv("EUTHERDRIVE_PSX_TRACE_FRAME_END") ?? int.MaxValue;
                bool tracePsxEveryFrame = IsEnvEnabled("EUTHERDRIVE_PSX_TRACE_EVERY_FRAME");
                bool holdUp = IsEnvEnabled("EUTHERDRIVE_PSX_HEADLESS_HOLD_UP");
                bool holdDown = IsEnvEnabled("EUTHERDRIVE_PSX_HEADLESS_HOLD_DOWN");
                bool holdLeft = IsEnvEnabled("EUTHERDRIVE_PSX_HEADLESS_HOLD_LEFT");
                bool holdRight = IsEnvEnabled("EUTHERDRIVE_PSX_HEADLESS_HOLD_RIGHT");
                bool holdA = IsEnvEnabled("EUTHERDRIVE_PSX_HEADLESS_HOLD_A");
                bool holdB = IsEnvEnabled("EUTHERDRIVE_PSX_HEADLESS_HOLD_B");
                bool holdC = IsEnvEnabled("EUTHERDRIVE_PSX_HEADLESS_HOLD_C");
                bool holdStart = IsEnvEnabled("EUTHERDRIVE_PSX_HEADLESS_HOLD_START");
                bool holdX = IsEnvEnabled("EUTHERDRIVE_PSX_HEADLESS_HOLD_X");
                bool holdY = IsEnvEnabled("EUTHERDRIVE_PSX_HEADLESS_HOLD_Y");
                bool holdZ = IsEnvEnabled("EUTHERDRIVE_PSX_HEADLESS_HOLD_Z");
                bool holdMode = IsEnvEnabled("EUTHERDRIVE_PSX_HEADLESS_HOLD_MODE");
                bool lastStartPressed = false;

                Console.WriteLine("[HEADLESS] Framebuffer BEFORE running:");
                ReadOnlySpan<byte> fb0 = psx.GetFrameBuffer(out int w0, out int h0, out int s0);
                var stats0 = GetFrameStats(fb0, w0, h0, s0);
                Console.WriteLine($"[HEADLESS] PSX fb_has_content={stats0.HasContent} nonzero_pixels={stats0.NonZeroPixels} first_nonzero=({stats0.FirstX},{stats0.FirstY})");
                DumpBgraToPpm(fb0, w0, h0, s0, Path.Combine(dumpDir, "headless_frame0.ppm"));

                for (int frame = 0; frame < framesToRun; frame++)
                {
                    bool startPressed = holdStart || (autoStart &&
                        ShouldPressStartPulse(frame, autoStartDelayFrames, autoStartPulseFrames, autoStartPeriodFrames, autoStartPulseCount));
                    psx.SetInputState(
                        up: holdUp,
                        down: holdDown,
                        left: holdLeft,
                        right: holdRight,
                        a: holdA,
                        b: holdB,
                        c: holdC,
                        start: startPressed,
                        x: holdX,
                        y: holdY,
                        z: holdZ,
                        mode: holdMode,
                        padType: PadType.SixButton);

                    if (autoStartLog && startPressed != lastStartPressed)
                        Console.WriteLine($"[HEADLESS] PSX auto-start start={(startPressed ? 1 : 0)} frame={frame}");
                    lastStartPressed = startPressed;

                    psx.RunFrame();

                    if (psxAudioSink != null)
                    {
                        var audio = psx.GetAudioBuffer(out int rate, out int channels);
                        if (frame == 0)
                            psxAudioSink.Start(rate, channels);
                        if (!audio.IsEmpty)
                            psxAudioSink.Submit(audio);
                    }

                    if (tracePsxFrames || frame == 0 || frame == 5 || frame == 10)
                    {
                        ReadOnlySpan<byte> fb = psx.GetFrameBuffer(out int w, out int h, out int s);
                        var stats = GetFrameStats(fb, w, h, s);
                        Console.WriteLine($"[HEADLESS] Frame {frame}: psx_fb_has_content={stats.HasContent} nonzero_pixels={stats.NonZeroPixels} first_nonzero=({stats.FirstX},{stats.FirstY})");
                        if (frame == 0 || frame == 5 || frame == 10)
                        {
                            string ppmPath = Path.Combine(dumpDir, $"headless_frame{frame}.ppm");
                            DumpBgraToPpm(fb, w, h, s, ppmPath);
                            Console.WriteLine($"[HEADLESS] Dumped frame {frame} to {ppmPath}");
                        }
                    }

                    if (ShouldTracePsxFrame(frame, tracePsxStart, tracePsxEveryFrame, tracePsxFrameStart, tracePsxFrameEnd))
                    {
                        if (psx.TryGetDebugState(out string debugState))
                        {
                            string line = $"[HEADLESS][PSX-START] frame={frame} {debugState}";
                            Console.WriteLine(line);
                            if (!string.IsNullOrWhiteSpace(tracePsxStartFile))
                            {
                                Directory.CreateDirectory(Path.GetDirectoryName(tracePsxStartFile) ?? ".");
                                File.AppendAllText(tracePsxStartFile, line + Environment.NewLine);
                            }
                        }
                        if (psx.TryGetFramePerfSummary(out string perfSummary) && !string.IsNullOrWhiteSpace(perfSummary))
                        {
                            string line = $"[HEADLESS][PSX-PERF] frame={frame} {perfSummary.Replace(Environment.NewLine, " | ")}";
                            Console.WriteLine(line);
                            if (!string.IsNullOrWhiteSpace(tracePsxStartFile))
                            {
                                Directory.CreateDirectory(Path.GetDirectoryName(tracePsxStartFile) ?? ".");
                                File.AppendAllText(tracePsxStartFile, line + Environment.NewLine);
                            }
                        }
                        if (!string.IsNullOrWhiteSpace(tracePsxCodeFile) && psx.TryGetDebugCodeWindow(out string codeWindow, address: tracePsxCodeAddress))
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(tracePsxCodeFile) ?? ".");
                            File.AppendAllText(tracePsxCodeFile, $"[HEADLESS][PSX-SAVESTATE-CODE] frame={frame}{Environment.NewLine}{codeWindow}");
                        }
                    }
                }

                Console.WriteLine("[HEADLESS] Framebuffer AFTER running:");
                ReadOnlySpan<byte> fbOut = psx.GetFrameBuffer(out int wOut, out int hOut, out int sOut);
                var statsOut = GetFrameStats(fbOut, wOut, hOut, sOut);
                Console.WriteLine($"[HEADLESS] PSX fb_has_content={statsOut.HasContent} nonzero_pixels={statsOut.NonZeroPixels} first_nonzero=({statsOut.FirstX},{statsOut.FirstY})");
                DumpBgraToPpm(fbOut, wOut, hOut, sOut, Path.Combine(dumpDir, "headless_output.ppm"));
                psxAudioSink?.Dispose();
                Console.WriteLine($"[HEADLESS] Completed {framesToRun} frames");
                return 0;
            }

            if (useSegaCd)
            {
                Console.WriteLine("[HEADLESS] Using Sega CD core");
                var scd = new SegaCdAdapter();
                scd.LoadRom(romPath);
                bool autoStart = IsEnvEnabled("EUTHERDRIVE_SCD_HEADLESS_AUTO_START");
                int autoStartDelayFrames = ParseOptionalIntEnv("EUTHERDRIVE_SCD_HEADLESS_AUTO_START_DELAY_FRAMES") ?? 120;
                int autoStartPulseFrames = ParseOptionalIntEnv("EUTHERDRIVE_SCD_HEADLESS_AUTO_START_PULSE_FRAMES") ?? 2;
                int autoStartPeriodFrames = ParseOptionalIntEnv("EUTHERDRIVE_SCD_HEADLESS_AUTO_START_PERIOD_FRAMES") ?? 90;
                int autoStartPulseCount = ParseOptionalIntEnv("EUTHERDRIVE_SCD_HEADLESS_AUTO_START_PULSE_COUNT") ?? 4;
                bool autoStartLog = IsEnvEnabled("EUTHERDRIVE_SCD_HEADLESS_AUTO_START_LOG");
                bool holdUp = IsEnvEnabled("EUTHERDRIVE_SCD_HEADLESS_HOLD_UP") || IsEnvEnabled("EUTHERDRIVE_MD_HEADLESS_HOLD_UP");
                bool holdDown = IsEnvEnabled("EUTHERDRIVE_SCD_HEADLESS_HOLD_DOWN") || IsEnvEnabled("EUTHERDRIVE_MD_HEADLESS_HOLD_DOWN");
                bool holdLeft = IsEnvEnabled("EUTHERDRIVE_SCD_HEADLESS_HOLD_LEFT") || IsEnvEnabled("EUTHERDRIVE_MD_HEADLESS_HOLD_LEFT");
                bool holdRight = IsEnvEnabled("EUTHERDRIVE_SCD_HEADLESS_HOLD_RIGHT") || IsEnvEnabled("EUTHERDRIVE_MD_HEADLESS_HOLD_RIGHT");
                bool holdA = IsEnvEnabled("EUTHERDRIVE_SCD_HEADLESS_HOLD_A") || IsEnvEnabled("EUTHERDRIVE_MD_HEADLESS_HOLD_A");
                bool holdB = IsEnvEnabled("EUTHERDRIVE_SCD_HEADLESS_HOLD_B") || IsEnvEnabled("EUTHERDRIVE_MD_HEADLESS_HOLD_B");
                bool holdC = IsEnvEnabled("EUTHERDRIVE_SCD_HEADLESS_HOLD_C") || IsEnvEnabled("EUTHERDRIVE_MD_HEADLESS_HOLD_C");
                bool holdStart = IsEnvEnabled("EUTHERDRIVE_SCD_HEADLESS_HOLD_START") || IsEnvEnabled("EUTHERDRIVE_MD_HEADLESS_HOLD_START");
                bool holdX = IsEnvEnabled("EUTHERDRIVE_SCD_HEADLESS_HOLD_X") || IsEnvEnabled("EUTHERDRIVE_MD_HEADLESS_HOLD_X");
                bool holdY = IsEnvEnabled("EUTHERDRIVE_SCD_HEADLESS_HOLD_Y") || IsEnvEnabled("EUTHERDRIVE_MD_HEADLESS_HOLD_Y");
                bool holdZ = IsEnvEnabled("EUTHERDRIVE_SCD_HEADLESS_HOLD_Z") || IsEnvEnabled("EUTHERDRIVE_MD_HEADLESS_HOLD_Z");
                bool holdMode = IsEnvEnabled("EUTHERDRIVE_SCD_HEADLESS_HOLD_MODE") || IsEnvEnabled("EUTHERDRIVE_MD_HEADLESS_HOLD_MODE");
                bool inputEnabled =
                    autoStart || holdUp || holdDown || holdLeft || holdRight ||
                    holdA || holdB || holdC || holdStart || holdX || holdY || holdZ || holdMode;
                bool lastStartPressed = false;
                bool autoStartCompleted = false;

                if (inputEnabled)
                {
                    Console.WriteLine(
                        $"[HEADLESS-SCD-INPUT] autoStart={autoStart} hold up={holdUp} down={holdDown} left={holdLeft} right={holdRight} " +
                        $"a={holdA} b={holdB} c={holdC} start={holdStart} x={holdX} y={holdY} z={holdZ} mode={holdMode}");
                }

                Console.WriteLine("[HEADLESS] Framebuffer BEFORE running:");
                ReadOnlySpan<byte> fb0 = scd.GetFrameBuffer(out int w0, out int h0, out int s0);
                var stats0 = GetFrameStats(fb0, w0, h0, s0);
                Console.WriteLine($"[HEADLESS] SegaCD fb_has_content={stats0.HasContent} nonzero_pixels={stats0.NonZeroPixels} first_nonzero=({stats0.FirstX},{stats0.FirstY})");
                DumpBgraToPpm(fb0, w0, h0, s0, Path.Combine(dumpDir, "headless_frame0.ppm"));

                var scdDumpFrames = new HashSet<int>();
                string? scdDumpFramesRaw = Environment.GetEnvironmentVariable("EUTHERDRIVE_HEADLESS_DUMP_FRAMES");
                if (!string.IsNullOrWhiteSpace(scdDumpFramesRaw))
                {
                    foreach (string part in scdDumpFramesRaw.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (int.TryParse(part.Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int frameIndex))
                            scdDumpFrames.Add(frameIndex);
                    }
                }
                int? scdDumpFrameSingle = ParseOptionalIntEnv("EUTHERDRIVE_HEADLESS_DUMP_FRAME");
                if (scdDumpFrameSingle.HasValue)
                    scdDumpFrames.Add(scdDumpFrameSingle.Value);

                for (int frame = 0; frame < framesToRun; frame++)
                {
                    bool startPressed = holdStart || (autoStart && !autoStartCompleted &&
                        ShouldPressStartPulse(frame, autoStartDelayFrames, autoStartPulseFrames, autoStartPeriodFrames, autoStartPulseCount));
                    scd.SetInputState(
                        up: holdUp,
                        down: holdDown,
                        left: holdLeft,
                        right: holdRight,
                        a: holdA,
                        b: holdB,
                        c: holdC,
                        start: startPressed,
                        x: holdX,
                        y: holdY,
                        z: holdZ,
                        mode: holdMode,
                        padType: PadType.SixButton);
                    if (autoStartLog && startPressed != lastStartPressed)
                        Console.WriteLine($"[HEADLESS] Sega CD auto-start start={(startPressed ? 1 : 0)} frame={frame}");
                    lastStartPressed = startPressed;

                    scd.RunFrame();

                    if (autoStart && !autoStartCompleted && frame >= autoStartDelayFrames)
                    {
                        uint mainPc = scd.MainCpuPc;
                        if (mainPc >= SegaCdMemory.BiosLen)
                        {
                            autoStartCompleted = true;
                            if (autoStartLog)
                                Console.WriteLine($"[HEADLESS] Sega CD auto-start stop frame={frame} pc=0x{mainPc:X6}");
                        }
                    }

                    if (frame == 0 || frame == 5 || frame == 10)
                    {
                        ReadOnlySpan<byte> fb = scd.GetFrameBuffer(out int w, out int h, out int s);
                        var stats = GetFrameStats(fb, w, h, s);
                        Console.WriteLine($"[HEADLESS] Frame {frame}: fb_has_content={stats.HasContent} nonzero_pixels={stats.NonZeroPixels} first_nonzero=({stats.FirstX},{stats.FirstY})");
                        string ppmPath = Path.Combine(dumpDir, $"headless_frame{frame}.ppm");
                        DumpBgraToPpm(fb, w, h, s, ppmPath);
                        Console.WriteLine($"[HEADLESS] Dumped frame {frame} to {ppmPath}");
                    }
                    else if (scdDumpFrames.Contains(frame))
                    {
                        ReadOnlySpan<byte> fb = scd.GetFrameBuffer(out int w, out int h, out int s);
                        string ppmPath = Path.Combine(dumpDir, $"headless_frame{frame}.ppm");
                        DumpBgraToPpm(fb, w, h, s, ppmPath);
                        Console.WriteLine($"[HEADLESS] Dumped frame {frame} to {ppmPath}");
                    }
                }

                Console.WriteLine("[HEADLESS] Framebuffer AFTER running:");
                ReadOnlySpan<byte> fbOut = scd.GetFrameBuffer(out int wOut, out int hOut, out int sOut);
                var statsOut = GetFrameStats(fbOut, wOut, hOut, sOut);
                Console.WriteLine($"[HEADLESS] SegaCD fb_has_content={statsOut.HasContent} nonzero_pixels={statsOut.NonZeroPixels} first_nonzero=({statsOut.FirstX},{statsOut.FirstY})");
                DumpBgraToPpm(fbOut, wOut, hOut, sOut, Path.Combine(dumpDir, "headless_output.ppm"));
                if (Environment.GetEnvironmentVariable("EUTHERDRIVE_HEADLESS_DUMP_PRG") == "1")
                {
                    string prgPath = Path.Combine(dumpDir, "headless_prg_ram.bin");
                    scd.DumpPrgRam(prgPath);
                    Console.WriteLine($"[HEADLESS] Dumped PRG RAM to {prgPath}");
                }
                if (Environment.GetEnvironmentVariable("EUTHERDRIVE_HEADLESS_DUMP_MAIN_RAM") == "1")
                {
                    string mainRamPath = Path.Combine(dumpDir, "headless_main_ram_ff0000.bin");
                    scd.DumpMainRam(mainRamPath);
                    Console.WriteLine($"[HEADLESS] Dumped main RAM to {mainRamPath}");
                }
                if (Environment.GetEnvironmentVariable("EUTHERDRIVE_HEADLESS_DUMP_CDC") == "1")
                {
                    string cdcPath = Path.Combine(dumpDir, "headless_cdc_ram.bin");
                    scd.DumpCdcRam(cdcPath);
                    Console.WriteLine($"[HEADLESS] Dumped CDC RAM to {cdcPath}");
                }
                if (Environment.GetEnvironmentVariable("EUTHERDRIVE_HEADLESS_DUMP_VDP_REGS") == "1")
                {
                    string vdpPath = Path.Combine(dumpDir, "headless_vdp_regs.txt");
                    scd.DumpVdpRegisters(vdpPath);
                    Console.WriteLine($"[HEADLESS] Dumped VDP registers to {vdpPath}");
                }
                if (Environment.GetEnvironmentVariable("EUTHERDRIVE_HEADLESS_DUMP_SCD_REGS") == "1")
                {
                    string scdPath = Path.Combine(dumpDir, "headless_scd_regs.txt");
                    scd.DumpScdRegisters(scdPath);
                    Console.WriteLine($"[HEADLESS] Dumped Sega CD registers to {scdPath}");
                }
                Console.WriteLine($"[HEADLESS] Completed {framesToRun} frames");
                return 0;
            }

            if (usePce)
            {
                Console.WriteLine("[HEADLESS] Using PCE CD core");
                var pce = new PceCdAdapter();
                pce.LoadRom(romPath);

                bool autoRun = Environment.GetEnvironmentVariable("EUTHERDRIVE_PCE_HEADLESS_AUTO_RUN") == "1";
                int autoRunDelayFrames = ParseOptionalIntEnv("EUTHERDRIVE_PCE_HEADLESS_AUTO_RUN_DELAY_FRAMES") ?? 90;
                int autoRunPulseFrames = ParseOptionalIntEnv("EUTHERDRIVE_PCE_HEADLESS_AUTO_RUN_PULSE_FRAMES") ?? 3;
                int autoRunPeriodFrames = ParseOptionalIntEnv("EUTHERDRIVE_PCE_HEADLESS_AUTO_RUN_PERIOD_FRAMES") ?? 90;
                int autoRunPulseCount = ParseOptionalIntEnv("EUTHERDRIVE_PCE_HEADLESS_AUTO_RUN_PULSE_COUNT") ?? 8;
                bool autoRunLog = Environment.GetEnvironmentVariable("EUTHERDRIVE_PCE_HEADLESS_AUTO_RUN_LOG") == "1";
                string? pceTracePath = Environment.GetEnvironmentVariable("EUTHERDRIVE_PCE_TRACE_FILE");
                StreamWriter? pceTraceWriter = null;
                if (!string.IsNullOrWhiteSpace(pceTracePath))
                {
                    string fullPath = Path.IsPathRooted(pceTracePath)
                        ? pceTracePath
                        : Path.Combine(dumpDir, pceTracePath);
                    Directory.CreateDirectory(Path.GetDirectoryName(fullPath) ?? dumpDir);
                    pceTraceWriter = new StreamWriter(fullPath, append: false, Encoding.UTF8);
                    Console.WriteLine($"[HEADLESS] PCE trace file: {fullPath}");
                }

                var pceDumpFrames = new HashSet<int>();
                string? pceDumpFramesRaw = Environment.GetEnvironmentVariable("EUTHERDRIVE_HEADLESS_DUMP_FRAMES");
                if (!string.IsNullOrWhiteSpace(pceDumpFramesRaw))
                {
                    foreach (string part in pceDumpFramesRaw.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (int.TryParse(part.Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int frameIndex))
                            pceDumpFrames.Add(frameIndex);
                    }
                }
                int? pceDumpFrameSingle = ParseOptionalIntEnv("EUTHERDRIVE_HEADLESS_DUMP_FRAME");
                if (pceDumpFrameSingle.HasValue)
                    pceDumpFrames.Add(pceDumpFrameSingle.Value);
                bool pceSnapshotOnDump = Environment.GetEnvironmentVariable("EUTHERDRIVE_PCE_SNAPSHOT_ON_DUMP") == "1";

                if (autoRun)
                {
                    Console.WriteLine(
                        $"[HEADLESS] PCE auto-run enabled delay={autoRunDelayFrames} pulse={autoRunPulseFrames} period={autoRunPeriodFrames} count={autoRunPulseCount}");
                }

                static bool ShouldPressStart(int frame, int delay, int pulse, int period, int count)
                {
                    if (frame < delay || pulse <= 0 || period <= 0 || count <= 0)
                        return false;
                    int rel = frame - delay;
                    int window = rel / period;
                    if (window < 0 || window >= count)
                        return false;
                    int slot = rel % period;
                    return slot < pulse;
                }

                Console.WriteLine("[HEADLESS] Framebuffer BEFORE running:");
                ReadOnlySpan<byte> fb0 = pce.GetFrameBuffer(out int w0, out int h0, out int s0);
                var stats0 = GetFrameStats(fb0, w0, h0, s0);
                Console.WriteLine($"[HEADLESS] PCE fb_has_content={stats0.HasContent} nonzero_pixels={stats0.NonZeroPixels} first_nonzero=({stats0.FirstX},{stats0.FirstY})");
                DumpBgraToPpm(fb0, w0, h0, s0, Path.Combine(dumpDir, "headless_frame0.ppm"));

                bool lastStartPressed = false;
                for (int frame = 0; frame < framesToRun; frame++)
                {
                    bool startPressed = autoRun &&
                        ShouldPressStart(frame, autoRunDelayFrames, autoRunPulseFrames, autoRunPeriodFrames, autoRunPulseCount);
                    pce.SetInputState(
                        up: false,
                        down: false,
                        left: false,
                        right: false,
                        a: false,
                        b: false,
                        c: false,
                        start: startPressed,
                        x: false,
                        y: false,
                        z: false,
                        mode: false,
                        padType: PadType.SixButton);
                    if (autoRunLog && startPressed != lastStartPressed)
                        Console.WriteLine($"[HEADLESS] PCE auto-run start={(startPressed ? 1 : 0)} frame={frame}");
                    lastStartPressed = startPressed;

                    pce.RunFrame();
                    if (pceTraceWriter != null)
                        pceTraceWriter.WriteLine(pce.BuildDeterminismTraceLine(frame));

                    if (frame == 0 || frame == 5 || frame == 10 || pceDumpFrames.Contains(frame))
                    {
                        ReadOnlySpan<byte> fb = pce.GetFrameBuffer(out int w, out int h, out int s);
                        var stats = GetFrameStats(fb, w, h, s);
                        Console.WriteLine($"[HEADLESS] Frame {frame}: fb_has_content={stats.HasContent} nonzero_pixels={stats.NonZeroPixels} first_nonzero=({stats.FirstX},{stats.FirstY})");
                        string ppmPath = Path.Combine(dumpDir, $"headless_frame{frame}.ppm");
                        DumpBgraToPpm(fb, w, h, s, ppmPath);
                        Console.WriteLine($"[HEADLESS] Dumped frame {frame} to {ppmPath}");
                        if (pceSnapshotOnDump)
                        {
                            string snapPrefix = pce.CaptureDebugSnapshot(dumpDir);
                            Console.WriteLine($"[HEADLESS] PCE snapshot captured: {snapPrefix}");
                        }
                    }
                }

                Console.WriteLine("[HEADLESS] Framebuffer AFTER running:");
                ReadOnlySpan<byte> fbOut = pce.GetFrameBuffer(out int wOut, out int hOut, out int sOut);
                var statsOut = GetFrameStats(fbOut, wOut, hOut, sOut);
                Console.WriteLine($"[HEADLESS] PCE fb_has_content={statsOut.HasContent} nonzero_pixels={statsOut.NonZeroPixels} first_nonzero=({statsOut.FirstX},{statsOut.FirstY})");
                DumpBgraToPpm(fbOut, wOut, hOut, sOut, Path.Combine(dumpDir, "headless_output.ppm"));
                pceTraceWriter?.Flush();
                pceTraceWriter?.Dispose();
                Console.WriteLine($"[HEADLESS] Completed {framesToRun} frames");
                return 0;
            }

            var adapter = new MdTracerAdapter();
            adapter.LoadRom(romPath);
            object coreAudioLock = new();
            const int audioSampleRate = 44100;
            const int audioChannels = 2;
            const int audioBufferChunkFrames = 256;
            int audioTargetFrames = GetHeadlessAudioTargetFrames(audioSampleRate);
            bool audioThrottle = Environment.GetEnvironmentVariable("EUTHERDRIVE_HEADLESS_AUDIO_THROTTLE") != "0";
            long audioLastSystemCycles = 0;
            double audioFrameAccumulator = 0;
            double audioCyclesScale = 1.0;
            double systemCyclesScale = 1.0;
            bool traceAudioCycles = Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_AUDIO_CYCLES") == "1";
            long audioCycleLogLastTicks = 0;
            {
                string? raw = Environment.GetEnvironmentVariable("EUTHERDRIVE_AUDIO_CYCLES_SCALE");
                if (!string.IsNullOrWhiteSpace(raw)
                    && double.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double value)
                    && value > 0)
                {
                    audioCyclesScale = value;
                }
            }
            {
                string? raw = Environment.GetEnvironmentVariable("EUTHERDRIVE_SYSTEM_CYCLES_SCALE");
                if (!string.IsNullOrWhiteSpace(raw)
                    && double.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double value)
                    && value > 0)
                {
                    systemCyclesScale = value;
                }
            }
            
            // Initialize audio engine for headless mode (enables YM2612 timing)
            AudioEngine? audioEngine = null;
            bool enableAudio = Environment.GetEnvironmentVariable("EUTHERDRIVE_HEADLESS_AUDIO") == "1";
            if (enableAudio)
            {
                Console.WriteLine("[HEADLESS] Audio engine enabled (EUTHERDRIVE_HEADLESS_AUDIO=1)");
                var audioSink = new HeadlessAudioSink();
                
                // Read buffer frames from environment variable, default to 8192
                int bufferFrames = 8192;
                string? bufferFramesRaw = Environment.GetEnvironmentVariable("EUTHERDRIVE_AUDIO_BUFFER_FRAMES");
                if (!string.IsNullOrWhiteSpace(bufferFramesRaw)
                    && int.TryParse(bufferFramesRaw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int bufferValue)
                    && bufferValue > 0)
                {
                    bufferFrames = bufferValue;
                }
                
                // Read batch frames from environment variable, default to 1024
                int batchFrames = 1024;
                string? batchFramesRaw = Environment.GetEnvironmentVariable("EUTHERDRIVE_AUDIO_BATCH_FRAMES");
                if (!string.IsNullOrWhiteSpace(batchFramesRaw)
                    && int.TryParse(batchFramesRaw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int batchValue)
                    && batchValue > 0)
                {
                    batchFrames = batchValue;
                }
                
                Console.WriteLine($"[HEADLESS] Audio buffer: {bufferFrames} frames, batch: {batchFrames} frames");
                audioEngine = new AudioEngine(audioSink, 44100, 2, framesPerBatch: batchFrames, bufferFrames: bufferFrames);
                audioEngine.Start();
            }
            else
            {
                Console.WriteLine("[HEADLESS] Audio engine disabled (set EUTHERDRIVE_HEADLESS_AUDIO=1 to enable)");
            }

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

                string savestateRoot = GetSavestateRoot();
                var savestateService = new SavestateService(savestateRoot);

                // Debug: list available savestates
                Console.WriteLine($"[HEADLESS] Available savestates in: {savestateRoot}");
                if (Directory.Exists(savestateRoot))
                {
                    foreach (var file in Directory.GetFiles(savestateRoot, "*.euthstate"))
                    {
                        Console.WriteLine($"[HEADLESS]   {Path.GetFileName(file)}");
                    }
                }
                else
                {
                    Console.WriteLine("[HEADLESS]   (savestate directory missing)");
                }

                try
                {
                    savestateService.Load(adapter, 1);
                    Console.WriteLine($"[HEADLESS] Savestate slot 1 loaded successfully.");

                    // After load, reset audio timing so SystemCycles deltas start clean.
                    audioLastSystemCycles = 0;
                    audioFrameAccumulator = 0;

                    // Ensure YM stays enabled if requested.
                    if (Environment.GetEnvironmentVariable("EUTHERDRIVE_YM") == "1")
                        adapter.SetYmEnabled(true);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[HEADLESS-WARN] Failed to load savestate slot 1: {ex.Message}");
                }
            }

             Console.WriteLine($"[HEADLESS] ROM loaded, starting emulation...");

             // Warm-up: run some frames after savestate load to let VDP stabilize
             if (loadSlot1OnBoot)
             {
                 Console.WriteLine($"[HEADLESS] Running 60 warm-up frames after savestate load...");
                 for (int i = 0; i < 60; i++)
                 {
                     adapter.StepFrame();
                 }
                 Console.WriteLine($"[HEADLESS] Warm-up complete");
             }

            // Dump frame 0 before running (after warm-up)
             Console.WriteLine("[HEADLESS] Framebuffer BEFORE running:");
             adapter.FrameBufferHasContent();
             adapter.DumpFrameBufferToPpm(Path.Combine(dumpDir, "headless_frame0.ppm"));

              var dumpFrames = new HashSet<int>();
              string? dumpFramesRaw = Environment.GetEnvironmentVariable("EUTHERDRIVE_HEADLESS_DUMP_FRAMES");
              if (!string.IsNullOrWhiteSpace(dumpFramesRaw))
              {
                  foreach (string part in dumpFramesRaw.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
                  {
                      if (int.TryParse(part.Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int frameIndex))
                          dumpFrames.Add(frameIndex);
                  }
              }
              int? dumpFrameSingle = ParseOptionalIntEnv("EUTHERDRIVE_HEADLESS_DUMP_FRAME");
              if (dumpFrameSingle.HasValue)
                  dumpFrames.Add(dumpFrameSingle.Value);

              for (int frame = 0; frame < framesToRun; frame++)
              {
                  lock (coreAudioLock)
                  {
                      adapter.StepFrame();
                  }

                  if (audioEngine != null)
                  {
                      long currentCycles = adapter.GetSystemCycles();
                      if (audioLastSystemCycles == 0)
                      {
                          audioLastSystemCycles = currentCycles;
                      }
                      else
                      {
                          long deltaCycles = currentCycles - audioLastSystemCycles;
                          if (deltaCycles > 0)
                          {
                              audioLastSystemCycles = currentCycles;
                              double m68kClockHz = adapter.GetM68kClockHz();
                              if (m68kClockHz <= 0)
                                  break;
                              audioFrameAccumulator += (deltaCycles * systemCyclesScale) * audioCyclesScale * (audioSampleRate / m68kClockHz);

                              if (traceAudioCycles && System.Diagnostics.Stopwatch.GetTimestamp() - audioCycleLogLastTicks > System.Diagnostics.Stopwatch.Frequency)
                              {
                                  audioCycleLogLastTicks = System.Diagnostics.Stopwatch.GetTimestamp();
                                  double expectedPerFrame = m68kClockHz / adapter.GetTargetFps();
                                  Console.WriteLine($"[AUDIO-CYCLES] deltaCycles={deltaCycles} expectedPerFrame={expectedPerFrame:F1} ratio={(deltaCycles / expectedPerFrame):F3}");
                              }
                              int frames = (int)audioFrameAccumulator;
                              if (frames > 0)
                              {
                                  audioFrameAccumulator -= frames;
                                  int loops = 0;
                                  while (frames > 0 && loops < 32)
                                  {
                                      int chunk = frames < audioBufferChunkFrames ? frames : audioBufferChunkFrames;
                                      var audio = adapter.GetAudioBufferForFrames(chunk, out int sampleRate, out int channels);
                                      if (!audio.IsEmpty && sampleRate == audioSampleRate && channels == audioChannels)
                                      {
                                          audioEngine.Submit(audio);
                                          frames -= chunk;
                                      }
                                      else
                                      {
                                          break;
                                      }
                                      loops++;
                                  }
                              }
                          }
                      }
                  }

                  if (audioEngine != null && audioThrottle)
                  {
                      int waitLoops = 0;
                      while (audioEngine.BufferedFrames > audioTargetFrames && waitLoops < 200)
                      {
                          Thread.Sleep(1);
                          waitLoops++;
                      }
                  }

                  // Log VDP status and framebuffer
                  bool displayOn = adapter.IsVdpDisplayOn();
                  bool hasContent = adapter.FrameBufferHasContent();
                  Console.WriteLine($"[HEADLESS] Frame {frame}: display={displayOn} fb_has_content={hasContent}");

                // Dump framebuffer at interesting points
                if (frame == 0 || frame == 5 || frame == 10 || dumpFrames.Contains(frame))
                {
                    string ppmPath = Path.Combine(dumpDir, $"headless_frame{frame}.ppm");
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
            adapter.DumpFrameBufferToPpm(Path.Combine(dumpDir, "headless_output.ppm"));

            if (Environment.GetEnvironmentVariable("EUTHERDRIVE_HEADLESS_MD_SNAPSHOT") == "1")
            {
                string snapPrefix = adapter.CaptureDebugSnapshot(dumpDir);
                Console.WriteLine($"[HEADLESS] MD snapshot captured: {snapPrefix}");
            }

            // Check framebuffer and dump if requested
            Console.WriteLine("[HEADLESS] Checking framebuffer...");
            if (adapter.FrameBufferHasContent())
            {
                string ppmPath = Path.Combine(dumpDir, "headless_output.ppm");
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

    private static int RunHshavocHeadless(string romPath, int framesToRun, string dumpDir)
    {
        using var hshavoc = new HshavocAdapter();
        hshavoc.LoadRom(romPath);

        string tracePath = Path.Combine(dumpDir, "hshavoc_boot_trace.log");
        using var trace = new StreamWriter(tracePath, append: false, Encoding.UTF8);
        trace.WriteLine("# EutherDrive HSHavoc headless boot trace");
        trace.WriteLine($"rom={romPath}");
        trace.WriteLine($"phase2={(Environment.GetEnvironmentVariable("EUTHERDRIVE_HSHAVOC_PHASE2") == "1" ? 1 : 0)}");
        trace.WriteLine($"decode_profile={Environment.GetEnvironmentVariable("EUTHERDRIVE_HSHAVOC_DECODE_PROFILE") ?? "<default>"}");
        trace.WriteLine("frame,pc,z80,sr,d0,d1,d2,a0,a1,a2,a7,vdp_display,op0,op1,op2,fb_content,nonzero_pixels,first_x,first_y,fingerprint");
        DumpHshavocCodeIslands(hshavoc, Path.Combine(dumpDir, "hshavoc_code_islands.txt"));

        ReadOnlySpan<byte> fbIn = hshavoc.GetFrameBuffer(out int wIn, out int hIn, out int sIn);
        var statsIn = GetFrameStats(fbIn, wIn, hIn, sIn);
        ulong lastFingerprint = ComputeFrameFingerprint(fbIn, wIn, hIn, sIn);
        int unchangedFrames = 0;
        bool traceFrames = Environment.GetEnvironmentVariable("EUTHERDRIVE_HEADLESS_TRACE_FRAMES") == "1";
        HashSet<int> dumpFrames = ParseFrameSetEnv("EUTHERDRIVE_HEADLESS_DUMP_FRAMES");
        int? dumpFrameSingle = ParseOptionalIntEnv("EUTHERDRIVE_HEADLESS_DUMP_FRAME");
        if (dumpFrameSingle.HasValue)
            dumpFrames.Add(dumpFrameSingle.Value);
        bool snapshotOnDumpFrame = Environment.GetEnvironmentVariable("EUTHERDRIVE_HEADLESS_HSHAVOC_SNAPSHOT_ON_DUMP") == "1";
        var hshavocInputScript = ParseSnesInputScript(Environment.GetEnvironmentVariable("EUTHERDRIVE_HSHAVOC_HEADLESS_INPUT_SCRIPT"));

        WriteHshavocTraceLine(trace, -1, hshavoc, statsIn, lastFingerprint);
        Console.WriteLine(
            $"[HEADLESS] HSHavoc load pc=0x{hshavoc.GetM68kPc():X6} z80=0x{hshavoc.GetZ80Pc():X4} " +
            $"sr=0x{hshavoc.GetM68kStatusRegister():X4} regs={FormatHshavocRegisters(hshavoc)} " +
            $"vdp={hshavoc.GetVdpDisplayStatus()} op={FormatHshavocWords(hshavoc)} fb_has_content={statsIn.HasContent} nonzero_pixels={statsIn.NonZeroPixels} " +
            $"first_nonzero=({statsIn.FirstX},{statsIn.FirstY}) fp=0x{lastFingerprint:X16}");
        DumpBgraToPpm(fbIn, wIn, hIn, sIn, Path.Combine(dumpDir, "headless_frame0.ppm"));

        for (int frame = 0; frame < framesToRun; frame++)
        {
            var input = ResolveSnesInputForFrame(frame, hshavocInputScript);
            hshavoc.SetInputState(
                up: input.Up,
                down: input.Down,
                left: input.Left,
                right: input.Right,
                a: input.A,
                b: input.B,
                c: input.X,
                start: input.Start,
                x: input.Y,
                y: input.L,
                z: input.R,
                mode: input.Select,
                padType: PadType.SixButton);
            hshavoc.RunFrame();

            ReadOnlySpan<byte> fb = hshavoc.GetFrameBuffer(out int w, out int h, out int s);
            var stats = GetFrameStats(fb, w, h, s);
            ulong fingerprint = ComputeFrameFingerprint(fb, w, h, s);
            unchangedFrames = fingerprint == lastFingerprint ? (unchangedFrames + 1) : 0;
            lastFingerprint = fingerprint;
            WriteHshavocTraceLine(trace, frame, hshavoc, stats, fingerprint);

            if (traceFrames || frame == 0 || frame == 1 || frame == 2 || frame == 5 || frame == 10 || ((frame + 1) % 60) == 0)
            {
                Console.WriteLine(
                    $"[HEADLESS] Frame {frame}: hshavoc_fb_has_content={stats.HasContent} nonzero_pixels={stats.NonZeroPixels} " +
                    $"first_nonzero=({stats.FirstX},{stats.FirstY}) pc=0x{hshavoc.GetM68kPc():X6} z80=0x{hshavoc.GetZ80Pc():X4} " +
                    $"sr=0x{hshavoc.GetM68kStatusRegister():X4} regs={FormatHshavocRegisters(hshavoc)} " +
                    $"vdp={hshavoc.GetVdpDisplayStatus()} op={FormatHshavocWords(hshavoc)} fp=0x{fingerprint:X16} unchanged={unchangedFrames}");
            }

            if (frame == 0 || frame == 1 || frame == 2 || frame == 5 || frame == 10)
                DumpBgraToPpm(fb, w, h, s, Path.Combine(dumpDir, $"headless_frame{frame}.ppm"));

            if (dumpFrames.Contains(frame))
            {
                string ppmPath = Path.Combine(dumpDir, $"headless_frame{frame}.ppm");
                DumpBgraToPpm(fb, w, h, s, ppmPath);
                Console.WriteLine($"[HEADLESS] HSHavoc dumped frame {frame} to {ppmPath}");
                if (snapshotOnDumpFrame)
                {
                    string snapPrefix = hshavoc.CaptureDebugSnapshot(dumpDir);
                    Console.WriteLine($"[HEADLESS] HSHavoc snapshot frame {frame}: {snapPrefix}");
                }
            }
        }

        ReadOnlySpan<byte> fbOut = hshavoc.GetFrameBuffer(out int wOut, out int hOut, out int sOut);
        var statsOut = GetFrameStats(fbOut, wOut, hOut, sOut);
        ulong finalFingerprint = ComputeFrameFingerprint(fbOut, wOut, hOut, sOut);
        DumpBgraToPpm(fbOut, wOut, hOut, sOut, Path.Combine(dumpDir, "headless_output.ppm"));

        if (Environment.GetEnvironmentVariable("EUTHERDRIVE_HEADLESS_HSHAVOC_SNAPSHOT") == "1")
        {
            string snapPrefix = hshavoc.CaptureDebugSnapshot(dumpDir);
            Console.WriteLine($"[HEADLESS] HSHavoc snapshot captured: {snapPrefix}");
        }

        Console.WriteLine(
            $"[HEADLESS] HSHavoc final fb_has_content={statsOut.HasContent} nonzero_pixels={statsOut.NonZeroPixels} " +
            $"first_nonzero=({statsOut.FirstX},{statsOut.FirstY}) pc=0x{hshavoc.GetM68kPc():X6} z80=0x{hshavoc.GetZ80Pc():X4} " +
            $"sr=0x{hshavoc.GetM68kStatusRegister():X4} regs={FormatHshavocRegisters(hshavoc)} " +
            $"vdp={hshavoc.GetVdpDisplayStatus()} op={FormatHshavocWords(hshavoc)} fp=0x{finalFingerprint:X16}");
        Console.WriteLine($"[HEADLESS] HSHavoc boot trace: {tracePath}");
        Console.WriteLine($"[HEADLESS] Completed {framesToRun} frames");
        return 0;
    }

    private static void WriteHshavocTraceLine(StreamWriter trace, int frame, HshavocAdapter hshavoc, FrameStats stats, ulong fingerprint)
    {
        uint pc = hshavoc.GetM68kPc() & 0x00FF_FFFE;
        trace.WriteLine(
            string.Create(
                CultureInfo.InvariantCulture,
                $"{frame},{pc:X6},{hshavoc.GetZ80Pc():X4},{hshavoc.GetM68kStatusRegister():X4},{hshavoc.GetM68kDataRegister(0):X8},{hshavoc.GetM68kDataRegister(1):X8},{hshavoc.GetM68kDataRegister(2):X8},{hshavoc.GetM68kAddressRegister(0):X8},{hshavoc.GetM68kAddressRegister(1):X8},{hshavoc.GetM68kAddressRegister(2):X8},{hshavoc.GetM68kAddressRegister(7):X8},{hshavoc.GetVdpDisplayStatus()},{hshavoc.ReadM68kWord(pc):X4},{hshavoc.ReadM68kWord(pc + 2):X4},{hshavoc.ReadM68kWord(pc + 4):X4},{(stats.HasContent ? 1 : 0)},{stats.NonZeroPixels},{stats.FirstX},{stats.FirstY},{fingerprint:X16}"));
    }

    private static string FormatHshavocWords(HshavocAdapter hshavoc)
    {
        uint pc = hshavoc.GetM68kPc() & 0x00FF_FFFE;
        return $"0x{hshavoc.ReadM68kWord(pc):X4},0x{hshavoc.ReadM68kWord(pc + 2):X4},0x{hshavoc.ReadM68kWord(pc + 4):X4}";
    }

    private static string FormatHshavocRegisters(HshavocAdapter hshavoc)
    {
        return string.Create(
            CultureInfo.InvariantCulture,
            $"D0=0x{hshavoc.GetM68kDataRegister(0):X8} D1=0x{hshavoc.GetM68kDataRegister(1):X8} D2=0x{hshavoc.GetM68kDataRegister(2):X8} A0=0x{hshavoc.GetM68kAddressRegister(0):X8} A1=0x{hshavoc.GetM68kAddressRegister(1):X8} A2=0x{hshavoc.GetM68kAddressRegister(2):X8} A7=0x{hshavoc.GetM68kAddressRegister(7):X8}");
    }

    private static void DumpHshavocCodeIslands(HshavocAdapter hshavoc, string path)
    {
        using var writer = new StreamWriter(path, append: false, Encoding.UTF8);
        DumpHshavocWords(writer, hshavoc, 0x000C40, 0x70);
        DumpHshavocWords(writer, hshavoc, 0x001000, 0x140);
        DumpHshavocWords(writer, hshavoc, 0x000A00, 0x140);
        DumpHshavocWords(writer, hshavoc, 0x001E00, 0x360);
        DumpHshavocWords(writer, hshavoc, 0x003800, 0x700);
        DumpHshavocWords(writer, hshavoc, 0x008800, 0x400);
        DumpHshavocWords(writer, hshavoc, 0x018500, 0x180);
        DumpHshavocWords(writer, hshavoc, 0x02C000, 0x100);
        DumpHshavocWords(writer, hshavoc, 0x03D000, 0x380);
        DumpHshavocWords(writer, hshavoc, 0x042000, 0x280);
        DumpHshavocWords(writer, hshavoc, 0x0D0000, 0x180);
        DumpHshavocWords(writer, hshavoc, 0x0D0580, 0x500);
        DumpHshavocWords(writer, hshavoc, 0x000E00, 0x80);
    }

    private static void DumpHshavocWords(StreamWriter writer, HshavocAdapter hshavoc, uint start, int length)
    {
        writer.WriteLine($"# 0x{start:X6}-0x{start + (uint)length:X6}");
        for (int offset = 0; offset < length; offset += 16)
        {
            uint address = start + (uint)offset;
            writer.Write($"{address:X6}:");
            for (int word = 0; word < 8; word++)
                writer.Write($" {hshavoc.ReadM68kWord(address + (uint)(word * 2)):X4}");
            writer.WriteLine();
        }
        writer.WriteLine();
    }

    private static int RunCps1Headless(string romPath, int framesToRun, string dumpDir, byte[]? statePayload)
    {
        long loadStart = Stopwatch.GetTimestamp();
        var cps1 = new Cps1DinoAdapter();
        cps1.LoadRom(romPath);
        long loadTicks = Stopwatch.GetTimestamp() - loadStart;

        long stateLoadTicks = 0;
        if (statePayload != null)
        {
            long stateLoadStart = Stopwatch.GetTimestamp();
            using var stateStream = new MemoryStream(statePayload, writable: false);
            using var stateReader = new BinaryReader(stateStream);
            cps1.LoadState(stateReader);
            stateLoadTicks = Stopwatch.GetTimestamp() - stateLoadStart;
        }

        bool traceFrames = Environment.GetEnvironmentVariable("EUTHERDRIVE_HEADLESS_TRACE_FRAMES") == "1";
        bool traceCps1Perf = Environment.GetEnvironmentVariable("EUTHERDRIVE_CPS1_PERF") == "1";
        ReadOnlySpan<byte> fbIn = cps1.GetFrameBuffer(out int wIn, out int hIn, out int sIn);
        var statsIn = GetFrameStats(fbIn, wIn, hIn, sIn);
        ulong lastFingerprint = ComputeFrameFingerprint(fbIn, wIn, hIn, sIn);
        int unchangedFrames = 0;
        long runTicksTotal = 0;
        long runTicksMin = long.MaxValue;
        long runTicksMax = 0;
        long audioSamples = 0;
        long nonZeroAudioSamples = 0;
        int maxAbsAudioSample = 0;

        Console.WriteLine($"[HEADLESS] CPS1 fb_has_content={statsIn.HasContent} nonzero_pixels={statsIn.NonZeroPixels} first_nonzero=({statsIn.FirstX},{statsIn.FirstY}) fp=0x{lastFingerprint:X16}");
        DumpBgraToPpm(fbIn, wIn, hIn, sIn, Path.Combine(dumpDir, "headless_frame0.ppm"));

        for (int frame = 0; frame < framesToRun; frame++)
        {
            cps1.SetInputState(
                up: false,
                down: false,
                left: false,
                right: false,
                a: false,
                b: false,
                c: false,
                start: false,
                x: false,
                y: false,
                z: false,
                mode: false,
                padType: PadType.SixButton);
            long runStart = Stopwatch.GetTimestamp();
            cps1.RunFrame();
            long runTicks = Stopwatch.GetTimestamp() - runStart;
            runTicksTotal += runTicks;
            runTicksMin = Math.Min(runTicksMin, runTicks);
            runTicksMax = Math.Max(runTicksMax, runTicks);

            ReadOnlySpan<short> audio = cps1.GetAudioBuffer(out _, out _);
            audioSamples += audio.Length;
            for (int i = 0; i < audio.Length; i++)
            {
                int sample = audio[i];
                if (sample != 0)
                    nonZeroAudioSamples++;
                int abs = sample == short.MinValue ? short.MaxValue : Math.Abs(sample);
                if (abs > maxAbsAudioSample)
                    maxAbsAudioSample = abs;
            }

            ReadOnlySpan<byte> fb = cps1.GetFrameBuffer(out int w, out int h, out int s);
            var stats = GetFrameStats(fb, w, h, s);
            ulong fingerprint = ComputeFrameFingerprint(fb, w, h, s);
            unchangedFrames = fingerprint == lastFingerprint ? unchangedFrames + 1 : 0;
            lastFingerprint = fingerprint;

            if (traceFrames || frame == 0 || frame == 5 || frame == 10 || ((frame + 1) % 60) == 0)
            {
                Console.WriteLine($"[HEADLESS] Frame {frame}: cps1_fb_has_content={stats.HasContent} nonzero_pixels={stats.NonZeroPixels} first_nonzero=({stats.FirstX},{stats.FirstY}) fp=0x{fingerprint:X16} unchanged={unchangedFrames} frameCounter={cps1.FrameCounter ?? -1}");
                if (traceCps1Perf && cps1.TryGetFramePerfSummary(out string cps1PerfSummary))
                    Console.WriteLine($"[HEADLESS][CPS1-INTERNAL] frame={frame} {cps1PerfSummary}");
            }

            if (frame == 0 || frame == 5 || frame == 10)
                DumpBgraToPpm(fb, w, h, s, Path.Combine(dumpDir, $"headless_frame{frame}.ppm"));
        }

        ReadOnlySpan<byte> fbOut = cps1.GetFrameBuffer(out int wOut, out int hOut, out int sOut);
        var statsOut = GetFrameStats(fbOut, wOut, hOut, sOut);
        ulong finalFingerprint = ComputeFrameFingerprint(fbOut, wOut, hOut, sOut);
        Console.WriteLine($"[HEADLESS] CPS1 final fb_has_content={statsOut.HasContent} nonzero_pixels={statsOut.NonZeroPixels} first_nonzero=({statsOut.FirstX},{statsOut.FirstY}) fp=0x{finalFingerprint:X16}");
        Console.WriteLine($"[HEADLESS] CPS1 audio samples={audioSamples} nonzero_samples={nonZeroAudioSamples} max_abs={maxAbsAudioSample}");
        DumpBgraToPpm(fbOut, wOut, hOut, sOut, Path.Combine(dumpDir, "headless_output.ppm"));
        if (framesToRun > 0 && runTicksTotal > 0)
        {
            double tickMs = 1000.0 / Stopwatch.Frequency;
            double loadMs = loadTicks * tickMs;
            double stateLoadMs = stateLoadTicks * tickMs;
            double avgMs = (runTicksTotal * tickMs) / framesToRun;
            double minMs = runTicksMin * tickMs;
            double maxMs = runTicksMax * tickMs;
            double capacityFps = framesToRun * Stopwatch.Frequency / (double)runTicksTotal;
            double targetFps = cps1.GetTargetFps();
            double headroomFps = capacityFps - targetFps;
            double headroomPercent = targetFps > 0 ? capacityFps / targetFps * 100.0 : 0.0;
            Console.WriteLine(
                $"[HEADLESS][CPS1-PERF] load_ms={loadMs:0.###} state_load_ms={stateLoadMs:0.###} frames={framesToRun} run_avg_ms={avgMs:0.###} run_min_ms={minMs:0.###} run_max_ms={maxMs:0.###} capacity_fps={capacityFps:0.###} target_fps={targetFps:0.###} headroom_fps={headroomFps:+0.###;-0.###;0.###} headroom_pct={headroomPercent:0.#}");
        }
        Console.WriteLine($"[HEADLESS] Completed {framesToRun} frames");
        return 0;
    }

    private static void PrintHeadlessPerf(string label, int framesToRun, long runTicksTotal, long runTicksMin, long runTicksMax, double targetFps)
    {
        if (framesToRun <= 0 || runTicksTotal <= 0)
            return;

        double tickMs = 1000.0 / Stopwatch.Frequency;
        double avgMs = (runTicksTotal * tickMs) / framesToRun;
        double minMs = (runTicksMin == long.MaxValue ? 0 : runTicksMin) * tickMs;
        double maxMs = runTicksMax * tickMs;
        double capacityFps = framesToRun * Stopwatch.Frequency / (double)runTicksTotal;
        double headroomFps = capacityFps - targetFps;
        double headroomPercent = targetFps > 0 ? capacityFps / targetFps * 100.0 : 0.0;
        Console.WriteLine(
            $"[HEADLESS][{label}-PERF] frames={framesToRun} run_avg_ms={avgMs:0.###} run_min_ms={minMs:0.###} run_max_ms={maxMs:0.###} capacity_fps={capacityFps:0.###} target_fps={targetFps:0.###} headroom_fps={headroomFps:+0.###;-0.###;0.###} headroom_pct={headroomPercent:0.#}");
    }

    private static bool IsSnesRomPath(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".smc" or ".sfc";
    }

    private static bool IsNesRomPath(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".nes";
    }

    private static bool IsPsxRomPath(string path)
    {
        return OpticalDiscDetector.Detect(path) == OpticalDiscKind.Psx;
    }

    private static bool IsGbaRomPath(string path)
    {
        string ext = GetEffectiveRomExtension(path);
        return ext is ".gba" or ".agb";
    }

    private static bool Is32XRomPath(string path)
    {
        string ext = GetEffectiveRomExtension(path);
        if (ext == ".32x")
            return true;

        if (!File.Exists(path))
            return false;

        try
        {
            byte[] romData = File.ReadAllBytes(path);
            return Sega32XRomDetector.IsSega32XRom(romData, path);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsGbRomPath(string path)
    {
        string ext = GetEffectiveRomExtension(path);
        return ext is ".gb" or ".gbc";
    }

    private static bool IsMasterSystemRomPath(string path)
    {
        string ext = GetEffectiveRomExtension(path);
        return ext is ".sms" or ".sg" or ".gg";
    }

    private static string GetEffectiveRomExtension(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        if ((ext is ".zip" or ".7z") && RomArchiveExtractor.TryGetArchiveRomEntryExtension(path, out string archiveExt))
            return archiveExt.ToLowerInvariant();

        return ext;
    }

    private static bool IsSegaCdRomPath(string path)
    {
        return OpticalDiscDetector.Detect(path) == OpticalDiscKind.SegaCd;
    }

    private static bool IsN64RomPath(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".z64" or ".n64" or ".v64";
    }

    private static bool IsPceRomPath(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext == ".pce")
            return true;
        if (ext != ".cue")
            return false;

        OpticalDiscKind opticalDiscKind = OpticalDiscDetector.Detect(path);
        return opticalDiscKind is OpticalDiscKind.PceCd or OpticalDiscKind.Unknown;
    }

    private static void DumpSnesFrame(SnesAdapter snes, string path, bool logStats)
    {
        ReadOnlySpan<byte> fb = snes.GetFrameBuffer(out int width, out int height, out int stride);
        bool hasContent = FrameBufferHasContent(fb);
        if (logStats)
        {
            var stats = GetFrameStats(fb, width, height, stride);
            Console.WriteLine($"[HEADLESS] SNES fb_has_content={stats.HasContent} nonzero_pixels={stats.NonZeroPixels} first_nonzero=({stats.FirstX},{stats.FirstY})");
        }
        else
        {
            Console.WriteLine($"[HEADLESS] SNES fb_has_content={hasContent}");
        }
        DumpBgraToPpm(fb, width, height, stride, path);
        Console.WriteLine($"[HEADLESS] Dumped frame to {path}");
    }

    private static void DumpSnesPpuRaw(SnesAdapter snes, string prefix)
    {
        if (snes.System.PPU is not KSNES.PictureProcessing.PPU ppu)
            return;

        WriteU16Array($"{prefix}_vram.bin", ppu.GetVramDebugCopy());
        WriteU16Array($"{prefix}_cgram.bin", ppu.GetCgramDebugCopy());
        WriteU16Array($"{prefix}_oam.bin", ppu.GetOamDebugCopy());
        File.WriteAllBytes($"{prefix}_wram.bin", snes.System.GetWramDebugCopy());

        string? snapshot = snes.GetPpuDebugSnapshot();
        if (!string.IsNullOrEmpty(snapshot))
            File.WriteAllText($"{prefix}_meta.txt", snapshot);

        Console.WriteLine($"[HEADLESS] Dumped SNES PPU raw state to {prefix}_*.bin");
    }

    private static void WriteU16Array(string path, ushort[] data)
    {
        byte[] bytes = new byte[data.Length * sizeof(ushort)];
        Buffer.BlockCopy(data, 0, bytes, 0, bytes.Length);
        File.WriteAllBytes(path, bytes);
    }

    private static string DumpSnesPeek(SnesAdapter snes, string label, IReadOnlyList<int> addresses)
    {
        string values = string.Join(' ', addresses.Select(addr => $"{addr:X6}=0x{snes.System.Peek(addr):X2}"));
        return $"[HEADLESS] Peek {label}: {values}";
    }

    private static string DumpSpcWindow(KSNES.AudioProcessing.IAPU apu, ushort pc)
    {
        byte[] ram = apu.RAM;
        int start = Math.Max(0, pc - 8);
        int end = Math.Min(0xFFFF, pc + 7);
        var bytes = new List<string>(end - start + 1);
        for (int addr = start; addr <= end; addr++)
        {
            string marker = addr == pc ? "*" : "";
            bytes.Add($"{marker}{ram[addr]:X2}");
        }

        string portState =
            $"cpu=({apu.SpcReadPorts[0]:X2},{apu.SpcReadPorts[1]:X2},{apu.SpcReadPorts[2]:X2},{apu.SpcReadPorts[3]:X2}) " +
            $"spc=({apu.SpcWritePorts[0]:X2},{apu.SpcWritePorts[1]:X2},{apu.SpcWritePorts[2]:X2},{apu.SpcWritePorts[3]:X2})";
        return $"pc=0x{pc:X4} [{start:X4}-{end:X4}] {string.Join(' ', bytes)} {portState}";
    }

    private static bool FrameBufferHasContent(ReadOnlySpan<byte> fb)
    {
        for (int i = 0; i + 3 < fb.Length; i += 4)
        {
            if (fb[i] != 0 || fb[i + 1] != 0 || fb[i + 2] != 0)
                return true;
        }
        return false;
    }

    private readonly record struct FrameStats(bool HasContent, int NonZeroPixels, int FirstX, int FirstY);

    private static FrameStats GetFrameStats(ReadOnlySpan<byte> fb, int width, int height, int stride)
    {
        int nonZero = 0;
        int firstX = -1;
        int firstY = -1;
        for (int y = 0; y < height; y++)
        {
            int row = y * stride;
            for (int x = 0; x < width; x++)
            {
                int i = row + x * 4;
                if (fb[i] != 0 || fb[i + 1] != 0 || fb[i + 2] != 0)
                {
                    nonZero++;
                    if (firstX == -1)
                    {
                        firstX = x;
                        firstY = y;
                    }
                }
            }
        }
        return new FrameStats(nonZero > 0, nonZero, firstX, firstY);
    }

    private static void DumpBgraToPpm(ReadOnlySpan<byte> fb, int width, int height, int stride, string path)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        using var bw = new BinaryWriter(fs, Encoding.ASCII, leaveOpen: true);
        bw.Write(Encoding.ASCII.GetBytes($"P6\n{width} {height}\n255\n"));
        byte[] line = new byte[width * 3];
        for (int y = 0; y < height; y++)
        {
            int src = y * stride;
            int dst = 0;
            for (int x = 0; x < width; x++)
            {
                byte b = fb[src++];
                byte g = fb[src++];
                byte r = fb[src++];
                src++; // skip A
                line[dst++] = r;
                line[dst++] = g;
                line[dst++] = b;
            }
            bw.Write(line);
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

        string? coreOverride = Environment.GetEnvironmentVariable("EUTHERDRIVE_HEADLESS_CORE");
        bool useGb = string.Equals(coreOverride, "gb", StringComparison.OrdinalIgnoreCase)
            || string.Equals(coreOverride, "gbc", StringComparison.OrdinalIgnoreCase)
            || string.Equals(coreOverride, "gameboy", StringComparison.OrdinalIgnoreCase)
            || (string.IsNullOrEmpty(coreOverride) && IsGbRomPath(romPath));
        bool usePsx = string.Equals(coreOverride, "psx", StringComparison.OrdinalIgnoreCase)
            || string.Equals(coreOverride, "ps1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(coreOverride, "playstation", StringComparison.OrdinalIgnoreCase)
            || (string.IsNullOrEmpty(coreOverride) && IsPsxRomPath(romPath));
        bool use32X = string.Equals(coreOverride, "32x", StringComparison.OrdinalIgnoreCase)
            || string.Equals(coreOverride, "s32x", StringComparison.OrdinalIgnoreCase)
            || string.Equals(coreOverride, "sega32x", StringComparison.OrdinalIgnoreCase)
            || (string.IsNullOrEmpty(coreOverride) && Is32XRomPath(romPath));
        bool useSegaCd = string.Equals(coreOverride, "segacd", StringComparison.OrdinalIgnoreCase)
            || string.Equals(coreOverride, "sega-cd", StringComparison.OrdinalIgnoreCase)
            || string.Equals(coreOverride, "mega-cd", StringComparison.OrdinalIgnoreCase)
            || (string.IsNullOrEmpty(coreOverride) && IsSegaCdRomPath(romPath));
        bool useCps1 = string.Equals(coreOverride, "cps1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(coreOverride, "arcade-cps1", StringComparison.OrdinalIgnoreCase)
            || (string.IsNullOrEmpty(coreOverride) && Cps1DinoAdapter.IsSupportedArchive(romPath));
        bool useDeco32 = string.Equals(coreOverride, "deco32", StringComparison.OrdinalIgnoreCase)
            || string.Equals(coreOverride, "dataeast-deco32", StringComparison.OrdinalIgnoreCase)
            || string.Equals(coreOverride, "nslasher", StringComparison.OrdinalIgnoreCase)
            || (string.IsNullOrEmpty(coreOverride) && Deco32Adapter.IsSupportedArchive(romPath));
        bool useMcsArcade = string.Equals(coreOverride, "arcade", StringComparison.OrdinalIgnoreCase)
            || string.Equals(coreOverride, "mcs", StringComparison.OrdinalIgnoreCase)
            || string.Equals(coreOverride, "arcade-mcs", StringComparison.OrdinalIgnoreCase)
            || string.Equals(coreOverride, "xsleena", StringComparison.OrdinalIgnoreCase)
            || (string.IsNullOrEmpty(coreOverride) && McsArcadeAdapter.IsLikelyArcadeArchive(romPath));
        bool useHshavoc = string.Equals(coreOverride, "hshavoc", StringComparison.OrdinalIgnoreCase)
            || string.Equals(coreOverride, "high-seas-havoc", StringComparison.OrdinalIgnoreCase)
            || string.Equals(coreOverride, "dataeast-hshavoc", StringComparison.OrdinalIgnoreCase)
            || (string.IsNullOrEmpty(coreOverride) && HshavocAdapter.IsSupportedArchive(romPath));

        if (useHshavoc)
        {
            using var hshavoc = new HshavocAdapter();
            hshavoc.LoadRom(romPath);

            for (int i = 0; i < 30; i++)
                hshavoc.RunFrame();

            byte[] snapshotHshavoc;
            using (var ms = new MemoryStream())
            using (var writer = new BinaryWriter(ms))
            {
                hshavoc.SaveState(writer);
                writer.Flush();
                snapshotHshavoc = ms.ToArray();
            }

            using (var ms = new MemoryStream(snapshotHshavoc))
            using (var reader = new BinaryReader(ms))
            {
                hshavoc.LoadState(reader);
            }

            byte[] snapshotAfterHshavoc;
            using (var ms = new MemoryStream())
            using (var writer = new BinaryWriter(ms))
            {
                hshavoc.SaveState(writer);
                writer.Flush();
                snapshotAfterHshavoc = ms.ToArray();
            }

            if (!snapshotHshavoc.SequenceEqual(snapshotAfterHshavoc))
            {
                Console.Error.WriteLine("[HEADLESS] HSHavoc savestate roundtrip failed: payload mismatch.");
                return 1;
            }

            Console.WriteLine($"[HEADLESS] HSHavoc savestate roundtrip ok. payload_bytes={snapshotHshavoc.Length}");
            return 0;
        }

        if (useCps1)
        {
            var cps1 = new Cps1DinoAdapter();
            cps1.LoadRom(romPath);

            for (int i = 0; i < 10; i++)
                cps1.RunFrame();

            byte[] snapshotCps1;
            using (var ms = new MemoryStream())
            using (var writer = new BinaryWriter(ms))
            {
                cps1.SaveState(writer);
                writer.Flush();
                snapshotCps1 = ms.ToArray();
            }

            using (var ms = new MemoryStream(snapshotCps1))
            using (var reader = new BinaryReader(ms))
            {
                cps1.LoadState(reader);
            }

            byte[] snapshotAfterCps1;
            using (var ms = new MemoryStream())
            using (var writer = new BinaryWriter(ms))
            {
                cps1.SaveState(writer);
                writer.Flush();
                snapshotAfterCps1 = ms.ToArray();
            }

            if (!snapshotCps1.SequenceEqual(snapshotAfterCps1))
            {
                Console.Error.WriteLine("[HEADLESS] CPS1 savestate roundtrip failed: payload mismatch.");
                return 1;
            }

            Console.WriteLine("[HEADLESS] CPS1 savestate roundtrip ok.");
            return 0;
        }

        if (useDeco32)
        {
            var deco32 = new Deco32Adapter();
            deco32.LoadRom(romPath);

            int warmupFrames = ReadPositiveIntEnv("EUTHERDRIVE_HEADLESS_SAVESTATE_WARMUP_FRAMES", 10);
            for (int i = 0; i < warmupFrames; i++)
                deco32.RunFrame();

            byte[] snapshotDeco32;
            using (var ms = new MemoryStream())
            using (var writer = new BinaryWriter(ms))
            {
                deco32.SaveState(writer);
                writer.Flush();
                snapshotDeco32 = ms.ToArray();
            }

            using (var ms = new MemoryStream(snapshotDeco32))
            using (var reader = new BinaryReader(ms))
            {
                deco32.LoadState(reader);
            }

            byte[] snapshotAfterDeco32;
            using (var ms = new MemoryStream())
            using (var writer = new BinaryWriter(ms))
            {
                deco32.SaveState(writer);
                writer.Flush();
                snapshotAfterDeco32 = ms.ToArray();
            }

            if (!snapshotDeco32.SequenceEqual(snapshotAfterDeco32))
            {
                Console.Error.WriteLine("[HEADLESS] Deco32 savestate roundtrip failed: payload mismatch.");
                return 1;
            }

            deco32.RunFrame();
            ReadOnlySpan<byte> fb = deco32.GetFrameBuffer(out int w, out int h, out int s);
            ulong fingerprint = ComputeFrameFingerprint(fb, w, h, s);
            Console.WriteLine($"[HEADLESS] Deco32 savestate roundtrip ok. payload_bytes={snapshotDeco32.Length} next_frame=0x{fingerprint:X16}");
            return 0;
        }

        if (useMcsArcade)
        {
            using var arcade = new McsArcadeAdapter();
            arcade.LoadRom(romPath);

            int warmupFrames = ReadPositiveIntEnv("EUTHERDRIVE_HEADLESS_SAVESTATE_WARMUP_FRAMES", 2);
            for (int i = 0; i < warmupFrames; i++)
                arcade.RunFrame();

            byte[] snapshotMcs;
            using (var ms = new MemoryStream())
            using (var writer = new BinaryWriter(ms))
            {
                arcade.SaveState(writer);
                writer.Flush();
                snapshotMcs = ms.ToArray();
            }

            bool dumpMcsState = Environment.GetEnvironmentVariable("EUTHERDRIVE_HEADLESS_DUMP_MCS_STATE") == "1";
            bool dumpMcsEntryKinds = Environment.GetEnvironmentVariable("EUTHERDRIVE_HEADLESS_DUMP_MCS_ENTRY_KINDS") == "1";
            string[] mcsStateDumpNames =
            {
                "scheduler::0:m_basetime",
                "Video Screen::screen:0:m_frame_number",
                "Video Screen::screen:0:m_vblank_start_time",
                "Video Screen::screen:0:m_vblank_end_time",
                "Motorola MC6809E::maincpu:0:m_localtime",
                "Motorola MC6809E::maincpu:0:m_totalcycles",
                "Motorola MC6809E::maincpu:0:m_state",
                "Motorola MC6809E::maincpu:0:m_pc.w",
                "Motorola MC6809E::maincpu:0:m_opcode",
                "Motorola MC6809::audiocpu:0:m_localtime",
                "Motorola MC6809::audiocpu:0:m_totalcycles",
                "Motorola MC6809::audiocpu:0:m_state",
                "Motorola MC6809::audiocpu:0:m_pc.w",
                "Motorola MC6809::audiocpu:0:m_opcode",
                "timer:scheduler:1:m_start",
                "timer:scheduler:1:m_expire",
                "timer:scheduler:71:m_start",
                "timer:scheduler:71:m_expire",
                "timer:scheduler:277:m_start",
                "timer:scheduler:277:m_expire",
                "timer:scheduler:280:m_start",
                "timer:scheduler:280:m_expire",
                "timer:scheduler:282:m_start",
                "timer:scheduler:282:m_expire"
            };

            if (dumpMcsState)
            {
                DumpMcsStateValues(snapshotMcs, "snapshot", mcsStateDumpNames);
            }
            if (dumpMcsEntryKinds)
                DumpMcsEntryKinds(snapshotMcs, "snapshot");

            arcade.RunFrame();
            ulong expectedNextFrameHash = HashMcsFrameBuffer(arcade);
            byte[] snapshotExpectedNextMcs;
            using (var ms = new MemoryStream())
            using (var writer = new BinaryWriter(ms))
            {
                arcade.SaveState(writer);
                writer.Flush();
                snapshotExpectedNextMcs = ms.ToArray();
            }
            if (dumpMcsState)
                DumpMcsStateValues(snapshotExpectedNextMcs, "expected-next", mcsStateDumpNames);

            int driftFrames = ReadNonNegativeIntEnv("EUTHERDRIVE_HEADLESS_SAVESTATE_DRIFT_FRAMES", 319);
            for (int i = 0; i < driftFrames; i++)
                arcade.RunFrame();

            using (var ms = new MemoryStream(snapshotMcs))
            using (var reader = new BinaryReader(ms))
            {
                arcade.LoadState(reader);
            }

            byte[] snapshotImmediateMcs;
            using (var ms = new MemoryStream())
            using (var writer = new BinaryWriter(ms))
            {
                arcade.SaveState(writer);
                writer.Flush();
                snapshotImmediateMcs = ms.ToArray();
            }
            if (dumpMcsState)
                DumpMcsStateValues(snapshotImmediateMcs, "same-immediate", mcsStateDumpNames);

            if (!snapshotMcs.SequenceEqual(snapshotImmediateMcs))
            {
                Console.Error.WriteLine("[HEADLESS] MCS savestate roundtrip failed: immediate payload mismatch.");
                string[] mismatches = DescribeMcsStateMismatches(snapshotMcs, snapshotImmediateMcs).Take(20).ToArray();
                foreach (string mismatch in mismatches)
                    Console.Error.WriteLine($"[HEADLESS]   {mismatch}");
                if (mismatches.Any(mismatch =>
                        !mismatch.StartsWith("changed Video Screen::screen:0:m_last_partial_scan ", StringComparison.Ordinal)
                        && !mismatch.Contains(":m_start ", StringComparison.Ordinal)
                        && !mismatch.Contains(":m_index ", StringComparison.Ordinal)))
                {
                    return 1;
                }
            }

            arcade.RunFrame();
            ulong actualNextFrameHash = HashMcsFrameBuffer(arcade);
            byte[] snapshotActualNextMcs;
            using (var ms = new MemoryStream())
            using (var writer = new BinaryWriter(ms))
            {
                arcade.SaveState(writer);
                writer.Flush();
                snapshotActualNextMcs = ms.ToArray();
            }
            if (dumpMcsState)
                DumpMcsStateValues(snapshotActualNextMcs, "same-next", mcsStateDumpNames);

            if (expectedNextFrameHash != actualNextFrameHash)
            {
                Console.Error.WriteLine($"[HEADLESS] MCS savestate diagnostic: next-frame framebuffer mismatch. expected=0x{expectedNextFrameHash:X16} actual=0x{actualNextFrameHash:X16}");
                if (!snapshotExpectedNextMcs.SequenceEqual(snapshotActualNextMcs))
                {
                    Console.Error.WriteLine("[HEADLESS]   next-frame payload also diverged:");
                    foreach (string mismatch in DescribeMcsStateMismatches(snapshotExpectedNextMcs, snapshotActualNextMcs).Take(20))
                        Console.Error.WriteLine($"[HEADLESS]   {mismatch}");
                }
                else
                {
                    Console.Error.WriteLine("[HEADLESS]   next-frame payload matches; mismatch is confined to rendered/host video state.");
                }
            }

            for (int i = 0; i < 19; i++)
                arcade.RunFrame();

            byte[] snapshotAfterMcs;
            using (var ms = new MemoryStream())
            using (var writer = new BinaryWriter(ms))
            {
                arcade.SaveState(writer);
                writer.Flush();
                snapshotAfterMcs = ms.ToArray();
            }

            bool matchMcs = snapshotMcs.SequenceEqual(snapshotAfterMcs);
            arcade.Dispose();

            using var coldbootArcade = new McsArcadeAdapter();
            coldbootArcade.LoadRom(romPath);
            using (var ms = new MemoryStream(snapshotMcs))
            using (var reader = new BinaryReader(ms))
            {
                coldbootArcade.LoadState(reader);
            }

            byte[] snapshotColdbootImmediateMcs;
            using (var ms = new MemoryStream())
            using (var writer = new BinaryWriter(ms))
            {
                coldbootArcade.SaveState(writer);
                writer.Flush();
                snapshotColdbootImmediateMcs = ms.ToArray();
            }
            if (dumpMcsState)
                DumpMcsStateValues(snapshotColdbootImmediateMcs, "coldboot-immediate", mcsStateDumpNames);

            bool coldbootMatchMcs = snapshotMcs.SequenceEqual(snapshotColdbootImmediateMcs);
            if (!coldbootMatchMcs)
            {
                Console.Error.WriteLine("[HEADLESS] MCS savestate diagnostic: coldboot immediate payload mismatch.");
                foreach (string mismatch in DescribeMcsStateMismatches(snapshotMcs, snapshotColdbootImmediateMcs).Take(40))
                    Console.Error.WriteLine($"[HEADLESS]   {mismatch}");
            }

            coldbootArcade.RunFrame();
            ulong coldbootNextFrameHash = HashMcsFrameBuffer(coldbootArcade);
            byte[] snapshotColdbootNextMcs;
            using (var ms = new MemoryStream())
            using (var writer = new BinaryWriter(ms))
            {
                coldbootArcade.SaveState(writer);
                writer.Flush();
                snapshotColdbootNextMcs = ms.ToArray();
            }
            if (dumpMcsState)
                DumpMcsStateValues(snapshotColdbootNextMcs, "coldboot-next", mcsStateDumpNames);

            if (expectedNextFrameHash != coldbootNextFrameHash)
            {
                Console.Error.WriteLine($"[HEADLESS] MCS savestate diagnostic: coldboot next-frame framebuffer mismatch. expected=0x{expectedNextFrameHash:X16} actual=0x{coldbootNextFrameHash:X16}");
                if (!snapshotExpectedNextMcs.SequenceEqual(snapshotColdbootNextMcs))
                {
                    Console.Error.WriteLine("[HEADLESS]   coldboot next-frame payload also diverged:");
                    foreach (string mismatch in DescribeMcsStateMismatches(snapshotExpectedNextMcs, snapshotColdbootNextMcs).Take(60))
                        Console.Error.WriteLine($"[HEADLESS]   {mismatch}");
                }
                else
                {
                    Console.Error.WriteLine("[HEADLESS]   coldboot next-frame payload matches; mismatch is confined to rendered/host video state.");
                }
            }

            Console.WriteLine($"[HEADLESS] MCS savestate smoke ok. payload_bytes={snapshotMcs.Length} framebuffer_next=0x{actualNextFrameHash:X16} deterministic_match={matchMcs} coldboot_match={coldbootMatchMcs} coldboot_framebuffer_next=0x{coldbootNextFrameHash:X16}");
            return 0;
        }

        static int ReadPositiveIntEnv(string name, int fallback)
        {
            string? value = Environment.GetEnvironmentVariable(name);
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) && parsed > 0
                ? parsed
                : fallback;
        }

        static int ReadNonNegativeIntEnv(string name, int fallback)
        {
            string? value = Environment.GetEnvironmentVariable(name);
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) && parsed >= 0
                ? parsed
                : fallback;
        }

        static ulong HashMcsFrameBuffer(McsArcadeAdapter arcade)
        {
            ReadOnlySpan<byte> frame = arcade.GetFrameBuffer(out int width, out int height, out int stride);
            ulong hash = 14695981039346656037UL;
            for (int y = 0; y < height; y++)
            {
                ReadOnlySpan<byte> row = frame.Slice(y * stride, width * 4);
                for (int i = 0; i < row.Length; i++)
                {
                    hash ^= row[i];
                    hash *= 1099511628211UL;
                }
            }

            return hash;
        }

        static IEnumerable<string> DescribeMcsStateMismatches(byte[] before, byte[] after)
        {
            Dictionary<string, byte[]> beforeEntries = ReadMcsStateEntries(before);
            Dictionary<string, byte[]> afterEntries = ReadMcsStateEntries(after);
            foreach (string name in beforeEntries.Keys.Union(afterEntries.Keys).OrderBy(name => name, StringComparer.Ordinal))
            {
                if (!beforeEntries.TryGetValue(name, out byte[] beforePayload))
                {
                    yield return $"added {name}";
                    continue;
                }

                if (!afterEntries.TryGetValue(name, out byte[] afterPayload))
                {
                    yield return $"missing {name}";
                    continue;
                }

                if (!beforePayload.SequenceEqual(afterPayload))
                    yield return $"changed {name} before={FormatStatePayload(beforePayload)} after={FormatStatePayload(afterPayload)}";
            }
        }

        static void DumpMcsStateValues(byte[] state, string label, params string[] names)
        {
            Dictionary<string, byte[]> entries = ReadMcsStateEntries(state);
            foreach (string name in names)
            {
                if (entries.TryGetValue(name, out byte[] payload))
                    Console.Error.WriteLine($"[HEADLESS]   {label} {name}={FormatStatePayload(payload)}");
                else
                    Console.Error.WriteLine($"[HEADLESS]   {label} {name}=<missing>");
            }
        }

        static void DumpMcsEntryKinds(byte[] state, string label)
        {
            Dictionary<string, byte[]> entries = ReadMcsStateEntries(state);
            var counts = new SortedDictionary<char, int>();
            foreach (byte[] payload in entries.Values)
            {
                char kind = payload.Length > 0 ? (char)payload[0] : '?';
                counts[kind] = counts.TryGetValue(kind, out int count) ? count + 1 : 1;
            }

            Console.Error.WriteLine($"[HEADLESS] MCS state entry kinds ({label}): {string.Join(", ", counts.Select(pair => $"{pair.Key}={pair.Value}"))}");
            foreach (var entry in entries.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                char kind = entry.Value.Length > 0 ? (char)entry.Value[0] : '?';
                if (kind == 's' || kind == 'n' || entry.Key.StartsWith("memory:", StringComparison.Ordinal))
                    Console.Error.WriteLine($"[HEADLESS]   non-ref {entry.Key}={FormatStatePayload(entry.Value)}");
            }
        }

        static string FormatStatePayload(byte[] payload)
        {
            try
            {
                using var stream = new MemoryStream(payload, writable: false);
                using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);
                byte kind = reader.ReadByte();
                switch (kind)
                {
                    case (byte)'r':
                    case (byte)'s':
                        return $"{payload.Length}:{(char)kind}:{FormatMcsPrimitive(reader.ReadString(), reader)}";
                    case (byte)'i':
                        return $"{payload.Length}:i:{reader.ReadInt32()}";
                    case (byte)'d':
                        return $"{payload.Length}:d:{reader.ReadDouble().ToString(CultureInfo.InvariantCulture)}";
                    case (byte)'a':
                    case (byte)'l':
                    {
                        string typeName = reader.ReadString();
                        if (kind == (byte)'a')
                        {
                            int rank = reader.ReadInt32();
                            for (int axis = 0; axis < rank; axis++)
                                _ = reader.ReadInt32();
                        }

                        int count = reader.ReadInt32();
                        string first = count > 0 ? FormatMcsPrimitive(typeName, reader) : "empty";
                        return $"{payload.Length}:{(char)kind}:{ShortTypeName(typeName)}[{count}] first={first}";
                    }
                    default:
                        return $"{payload.Length}:{BitConverter.ToString(payload, 0, Math.Min(payload.Length, 16))}";
                }
            }
            catch
            {
                return $"{payload.Length}:{BitConverter.ToString(payload, 0, Math.Min(payload.Length, 16))}";
            }
        }

        static string FormatMcsPrimitive(string typeName, BinaryReader reader)
        {
            if (typeName.Contains("System.Boolean", StringComparison.Ordinal))
                return reader.ReadBoolean() ? "true" : "false";
            if (typeName.Contains("System.Byte", StringComparison.Ordinal))
                return reader.ReadByte().ToString(CultureInfo.InvariantCulture);
            if (typeName.Contains("System.SByte", StringComparison.Ordinal))
                return reader.ReadSByte().ToString(CultureInfo.InvariantCulture);
            if (typeName.Contains("System.Int16", StringComparison.Ordinal))
                return reader.ReadInt16().ToString(CultureInfo.InvariantCulture);
            if (typeName.Contains("System.UInt16", StringComparison.Ordinal))
                return reader.ReadUInt16().ToString(CultureInfo.InvariantCulture);
            if (typeName.Contains("System.Int32", StringComparison.Ordinal))
                return reader.ReadInt32().ToString(CultureInfo.InvariantCulture);
            if (typeName.Contains("System.UInt32", StringComparison.Ordinal))
                return reader.ReadUInt32().ToString(CultureInfo.InvariantCulture);
            if (typeName.Contains("System.Int64", StringComparison.Ordinal))
                return reader.ReadInt64().ToString(CultureInfo.InvariantCulture);
            if (typeName.Contains("System.UInt64", StringComparison.Ordinal))
                return reader.ReadUInt64().ToString(CultureInfo.InvariantCulture);
            if (typeName.Contains("System.Single", StringComparison.Ordinal))
                return reader.ReadSingle().ToString(CultureInfo.InvariantCulture);
            if (typeName.Contains("System.Double", StringComparison.Ordinal))
                return reader.ReadDouble().ToString(CultureInfo.InvariantCulture);
            if (typeName.Contains("mame.attotime", StringComparison.Ordinal))
                return $"{reader.ReadInt32()}s/{reader.ReadInt64()}as";
            return ShortTypeName(typeName);
        }

        static string ShortTypeName(string typeName)
        {
            int comma = typeName.IndexOf(',');
            return comma >= 0 ? typeName[..comma] : typeName;
        }

        static Dictionary<string, byte[]> ReadMcsStateEntries(byte[] state)
        {
            using var stream = new MemoryStream(state, writable: false);
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);
            _ = reader.ReadString(); // adapter magic
            int adapterVersion = reader.ReadInt32();
            _ = reader.ReadString(); // driver
            if (adapterVersion >= 2)
            {
                int payloadLength = reader.ReadInt32();
                if (payloadLength < 0 || payloadLength > stream.Length - stream.Position)
                    throw new InvalidDataException("MCS savestate payload length is invalid.");
                byte[] payload = reader.ReadBytes(payloadLength);
                stream.Position -= payload.Length;
            }
            string magic = Encoding.ASCII.GetString(reader.ReadBytes(8));
            if (magic != "MCSSTATE")
                throw new InvalidDataException("MCS savestate payload magic mismatch.");
            _ = reader.ReadInt32(); // MCS state version
            int count = reader.ReadInt32();
            var entries = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            for (int i = 0; i < count; i++)
            {
                string name = reader.ReadString();
                int length = reader.ReadInt32();
                entries[name] = reader.ReadBytes(length);
            }
            return entries;
        }

        if (use32X)
        {
            var s32x = new Sega32XAdapter();
            s32x.LoadRom(romPath);

            for (int i = 0; i < 10; i++)
                s32x.RunFrame();

            byte[] snapshot32X;
            using (var ms = new MemoryStream())
            using (var writer = new BinaryWriter(ms))
            {
                s32x.SaveState(writer);
                writer.Flush();
                snapshot32X = ms.ToArray();
            }

            using (var ms = new MemoryStream(snapshot32X))
            using (var reader = new BinaryReader(ms))
            {
                s32x.LoadState(reader);
            }

            byte[] snapshotAfter32X;
            using (var ms = new MemoryStream())
            using (var writer = new BinaryWriter(ms))
            {
                s32x.SaveState(writer);
                writer.Flush();
                snapshotAfter32X = ms.ToArray();
            }

            bool match32X = snapshot32X.SequenceEqual(snapshotAfter32X);
            Console.WriteLine($"[HEADLESS] Sega 32X roundtrip {(match32X ? "OK" : "MISMATCH")}");
            return match32X ? 0 : 2;
        }

        if (useGb)
        {
            var gb = new GbAdapter();
            gb.LoadRom(romPath);

            for (int i = 0; i < 10; i++)
                gb.RunFrame();

            byte[] snapshotGb;
            using (var ms = new MemoryStream())
            using (var writer = new BinaryWriter(ms))
            {
                gb.SaveState(writer);
                writer.Flush();
                snapshotGb = ms.ToArray();
            }

            using (var ms = new MemoryStream(snapshotGb))
            using (var reader = new BinaryReader(ms))
            {
                gb.LoadState(reader);
            }

            byte[] snapshotAfterGb;
            using (var ms = new MemoryStream())
            using (var writer = new BinaryWriter(ms))
            {
                gb.SaveState(writer);
                writer.Flush();
                snapshotAfterGb = ms.ToArray();
            }

            if (!snapshotGb.SequenceEqual(snapshotAfterGb))
            {
                Console.Error.WriteLine("[HEADLESS] GB savestate roundtrip failed: payload mismatch.");
                return 1;
            }

            Console.WriteLine("[HEADLESS] GB savestate roundtrip ok.");
            return 0;
        }

        if (usePsx)
        {
            ConfigurePsxAdapterFromEnv();

            var psx = new PsxAdapter();
            psx.LoadRom(romPath);

            for (int i = 0; i < 10; i++)
                psx.RunFrame();

            byte[] snapshotPsx;
            using (var ms = new MemoryStream())
            using (var writer = new BinaryWriter(ms))
            {
                psx.SaveState(writer);
                writer.Flush();
                snapshotPsx = ms.ToArray();
            }

            using (var ms = new MemoryStream(snapshotPsx))
            using (var reader = new BinaryReader(ms))
            {
                psx.LoadState(reader);
            }

            byte[] snapshotAfterPsx;
            using (var ms = new MemoryStream())
            using (var writer = new BinaryWriter(ms))
            {
                psx.SaveState(writer);
                writer.Flush();
                snapshotAfterPsx = ms.ToArray();
            }

            if (!snapshotPsx.SequenceEqual(snapshotAfterPsx))
            {
                Console.Error.WriteLine("[HEADLESS] PSX savestate roundtrip failed: payload mismatch.");
                return 1;
            }

            Console.WriteLine("[HEADLESS] PSX savestate roundtrip ok.");
            return 0;
        }

        if (useSegaCd)
        {
            var scd = new SegaCdAdapter();
            scd.LoadRom(romPath);

            for (int i = 0; i < 10; i++)
                scd.RunFrame();

            byte[] snapshotScd;
            using (var ms = new MemoryStream())
            using (var writer = new BinaryWriter(ms))
            {
                scd.SaveState(writer);
                writer.Flush();
                snapshotScd = ms.ToArray();
            }

            using (var ms = new MemoryStream(snapshotScd))
            using (var reader = new BinaryReader(ms))
            {
                scd.LoadState(reader);
            }

            byte[] snapshotAfterScd;
            using (var ms = new MemoryStream())
            using (var writer = new BinaryWriter(ms))
            {
                scd.SaveState(writer);
                writer.Flush();
                snapshotAfterScd = ms.ToArray();
            }

            if (!snapshotScd.SequenceEqual(snapshotAfterScd))
            {
                Console.Error.WriteLine("[HEADLESS] Sega CD savestate roundtrip failed: payload mismatch.");
                return 1;
            }

            Console.WriteLine("[HEADLESS] Sega CD savestate roundtrip ok.");
            return 0;
        }

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

        // DEBUG: Don't run frames before loading savestate
        // for (int i = 0; i < 5; i++)
        //     adapter.StepFrame();

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

        bool payloadMismatch = false;
        if (!snapshot.SequenceEqual(snapshotAfter))
        {
            payloadMismatch = true;
            Console.Error.WriteLine("[HEADLESS] Savestate roundtrip failed: payload mismatch.");
        }

        if (payloadMismatch)
        {
            Console.Error.WriteLine("[HEADLESS] Savestate payload mismatch tolerated; determinism check passed.");
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
            string dumpDir = Environment.GetEnvironmentVariable("EUTHERDRIVE_HEADLESS_DUMP_DIR")
                ?? Path.Combine(Directory.GetCurrentDirectory(), "logs");
            Directory.CreateDirectory(dumpDir);

            string? coreOverride = Environment.GetEnvironmentVariable("EUTHERDRIVE_HEADLESS_CORE");
            bool useNes = string.Equals(coreOverride, "nes", StringComparison.OrdinalIgnoreCase)
                || (string.IsNullOrEmpty(coreOverride) && IsNesRomPath(romPath));
            bool useSnes = string.Equals(coreOverride, "snes", StringComparison.OrdinalIgnoreCase)
                || (string.IsNullOrEmpty(coreOverride) && IsSnesRomPath(romPath));
            bool usePsx = string.Equals(coreOverride, "psx", StringComparison.OrdinalIgnoreCase)
                || string.Equals(coreOverride, "ps1", StringComparison.OrdinalIgnoreCase)
                || string.Equals(coreOverride, "playstation", StringComparison.OrdinalIgnoreCase)
                || (string.IsNullOrEmpty(coreOverride) && IsPsxRomPath(romPath));
            bool useSegaCd = string.Equals(coreOverride, "scd", StringComparison.OrdinalIgnoreCase)
                || string.Equals(coreOverride, "segacd", StringComparison.OrdinalIgnoreCase)
                || string.Equals(coreOverride, "sega-cd", StringComparison.OrdinalIgnoreCase)
                || string.Equals(coreOverride, "mega-cd", StringComparison.OrdinalIgnoreCase)
                || (string.IsNullOrEmpty(coreOverride) && IsSegaCdRomPath(romPath));
            bool useGb = string.Equals(coreOverride, "gb", StringComparison.OrdinalIgnoreCase)
                || string.Equals(coreOverride, "gbc", StringComparison.OrdinalIgnoreCase)
                || string.Equals(coreOverride, "gameboy", StringComparison.OrdinalIgnoreCase)
                || (string.IsNullOrEmpty(coreOverride) && IsGbRomPath(romPath));
            bool useGba = string.Equals(coreOverride, "gba", StringComparison.OrdinalIgnoreCase)
                || string.Equals(coreOverride, "agb", StringComparison.OrdinalIgnoreCase)
                || (string.IsNullOrEmpty(coreOverride) && IsGbaRomPath(romPath));
            bool useSmsGg = string.Equals(coreOverride, "smsgg", StringComparison.OrdinalIgnoreCase)
                || string.Equals(coreOverride, "sms", StringComparison.OrdinalIgnoreCase)
                || string.Equals(coreOverride, "gg", StringComparison.OrdinalIgnoreCase)
                || (string.IsNullOrEmpty(coreOverride) && IsMasterSystemRomPath(romPath));
            bool usePce = string.Equals(coreOverride, "pce", StringComparison.OrdinalIgnoreCase)
                || string.Equals(coreOverride, "pcecd", StringComparison.OrdinalIgnoreCase)
                || string.Equals(coreOverride, "pcengine", StringComparison.OrdinalIgnoreCase)
                || (string.IsNullOrEmpty(coreOverride) && IsPceRomPath(romPath) && !IsSegaCdRomPath(romPath));
            bool useCps1 = string.Equals(coreOverride, "cps1", StringComparison.OrdinalIgnoreCase)
                || string.Equals(coreOverride, "arcade-cps1", StringComparison.OrdinalIgnoreCase)
                || (string.IsNullOrEmpty(coreOverride) && Cps1DinoAdapter.IsSupportedArchive(romPath));
            bool useTmnt = string.Equals(coreOverride, "tmnt", StringComparison.OrdinalIgnoreCase)
                || string.Equals(coreOverride, "tmnt2", StringComparison.OrdinalIgnoreCase)
                || string.Equals(coreOverride, "konami-tmnt", StringComparison.OrdinalIgnoreCase)
                || string.Equals(coreOverride, "konami-tmnt2", StringComparison.OrdinalIgnoreCase)
                || (string.IsNullOrEmpty(coreOverride) && TmntAdapter.IsSupportedArchive(romPath));
            bool useDeco32 = string.Equals(coreOverride, "deco32", StringComparison.OrdinalIgnoreCase)
                || string.Equals(coreOverride, "dataeast-deco32", StringComparison.OrdinalIgnoreCase)
                || string.Equals(coreOverride, "nslasher", StringComparison.OrdinalIgnoreCase)
                || (string.IsNullOrEmpty(coreOverride) && Deco32Adapter.IsSupportedArchive(romPath));
            bool useNeoGeo = string.Equals(coreOverride, "neogeo", StringComparison.OrdinalIgnoreCase)
                || string.Equals(coreOverride, "neo-geo", StringComparison.OrdinalIgnoreCase)
                || (string.IsNullOrEmpty(coreOverride) && NeoGeoAdapter.IsSupportedArchive(romPath));
            bool useMcsArcade = string.Equals(coreOverride, "arcade", StringComparison.OrdinalIgnoreCase)
                || string.Equals(coreOverride, "mcs", StringComparison.OrdinalIgnoreCase)
                || string.Equals(coreOverride, "arcade-mcs", StringComparison.OrdinalIgnoreCase)
                || string.Equals(coreOverride, "xsleena", StringComparison.OrdinalIgnoreCase)
                || (string.IsNullOrEmpty(coreOverride) && !useNeoGeo && McsArcadeAdapter.IsLikelyArcadeArchive(romPath));
            bool useHshavoc = string.Equals(coreOverride, "hshavoc", StringComparison.OrdinalIgnoreCase)
                || string.Equals(coreOverride, "high-seas-havoc", StringComparison.OrdinalIgnoreCase)
                || string.Equals(coreOverride, "dataeast-hshavoc", StringComparison.OrdinalIgnoreCase)
                || (string.IsNullOrEmpty(coreOverride) && HshavocAdapter.IsSupportedArchive(romPath));

            if (useHshavoc)
            {
                using var hshavoc = new HshavocAdapter();
                hshavoc.LoadRom(romPath);

                int? slotOverrideHshavoc = ParseOptionalIntEnv("EUTHERDRIVE_SAVESTATE_SLOT");
                var payloadHshavoc = TryLoadSavestatePayload(savestatePath, hshavoc.RomIdentity, slotOverrideHshavoc, out var hshavocError);
                if (payloadHshavoc == null)
                {
                    Console.Error.WriteLine($"[HEADLESS-ERROR] Savestate load failed: {hshavocError}");
                    return 1;
                }

                using (var hshavocStateStream = new MemoryStream(payloadHshavoc, writable: false))
                using (var hshavocStateReader = new BinaryReader(hshavocStateStream))
                    hshavoc.LoadState(hshavocStateReader);

                Console.WriteLine("[HEADLESS] Savestate loaded successfully (HSHavoc)");
                DumpHshavocCodeIslands(hshavoc, Path.Combine(dumpDir, "hshavoc_state_code_islands.txt"));
                ReadOnlySpan<byte> fbBefore = hshavoc.GetFrameBuffer(out int wBefore, out int hBefore, out int sBefore);
                var statsBefore = GetFrameStats(fbBefore, wBefore, hBefore, sBefore);
                ulong fingerprintBefore = ComputeFrameFingerprint(fbBefore, wBefore, hBefore, sBefore);
                Console.WriteLine(
                    $"[HEADLESS] HSHavoc state before fb_has_content={statsBefore.HasContent} nonzero_pixels={statsBefore.NonZeroPixels} " +
                    $"first_nonzero=({statsBefore.FirstX},{statsBefore.FirstY}) pc=0x{hshavoc.GetM68kPc():X6} z80=0x{hshavoc.GetZ80Pc():X4} " +
                    $"sr=0x{hshavoc.GetM68kStatusRegister():X4} regs={FormatHshavocRegisters(hshavoc)} " +
                    $"vdp={hshavoc.GetVdpDisplayStatus()} op={FormatHshavocWords(hshavoc)} fp=0x{fingerprintBefore:X16}");
                DumpBgraToPpm(fbBefore, wBefore, hBefore, sBefore, Path.Combine(dumpDir, "headless_hshavoc_state_before.ppm"));

                var hshavocInputScript = ParseSnesInputScript(Environment.GetEnvironmentVariable("EUTHERDRIVE_HSHAVOC_HEADLESS_INPUT_SCRIPT"));
                for (int frame = 0; frame < framesToRun; frame++)
                {
                    var input = ResolveSnesInputForFrame(frame, hshavocInputScript);
                    hshavoc.SetInputState(
                        up: input.Up,
                        down: input.Down,
                        left: input.Left,
                        right: input.Right,
                        a: input.A,
                        b: input.B,
                        c: input.X,
                        start: input.Start,
                        x: input.Y,
                        y: input.L,
                        z: input.R,
                        mode: input.Select,
                        padType: PadType.SixButton);
                    hshavoc.RunFrame();

                    ReadOnlySpan<byte> fb = hshavoc.GetFrameBuffer(out int w, out int h, out int s);
                    var stats = GetFrameStats(fb, w, h, s);
                    ulong fingerprint = ComputeFrameFingerprint(fb, w, h, s);
                    if (frame == 0 || frame == 1 || frame == 2 || frame == 5 || frame == 10 || ((frame + 1) % 60) == 0)
                    {
                        Console.WriteLine(
                            $"[HEADLESS] HSHavoc state frame {frame}: fb_has_content={stats.HasContent} nonzero_pixels={stats.NonZeroPixels} " +
                            $"first_nonzero=({stats.FirstX},{stats.FirstY}) pc=0x{hshavoc.GetM68kPc():X6} z80=0x{hshavoc.GetZ80Pc():X4} " +
                            $"sr=0x{hshavoc.GetM68kStatusRegister():X4} regs={FormatHshavocRegisters(hshavoc)} " +
                            $"vdp={hshavoc.GetVdpDisplayStatus()} op={FormatHshavocWords(hshavoc)} fp=0x{fingerprint:X16}");
                        DumpBgraToPpm(fb, w, h, s, Path.Combine(dumpDir, $"headless_hshavoc_state_frame{frame}.ppm"));
                    }
                }

                ReadOnlySpan<byte> fbOut = hshavoc.GetFrameBuffer(out int wOut, out int hOut, out int sOut);
                var statsOut = GetFrameStats(fbOut, wOut, hOut, sOut);
                ulong finalFingerprint = ComputeFrameFingerprint(fbOut, wOut, hOut, sOut);
                DumpBgraToPpm(fbOut, wOut, hOut, sOut, Path.Combine(dumpDir, "headless_output.ppm"));

                if (Environment.GetEnvironmentVariable("EUTHERDRIVE_HEADLESS_HSHAVOC_SNAPSHOT") == "1")
                {
                    string snapPrefix = hshavoc.CaptureDebugSnapshot(dumpDir);
                    Console.WriteLine($"[HEADLESS] HSHavoc snapshot captured: {snapPrefix}");
                }

                Console.WriteLine(
                    $"[HEADLESS] HSHavoc state final fb_has_content={statsOut.HasContent} nonzero_pixels={statsOut.NonZeroPixels} " +
                    $"first_nonzero=({statsOut.FirstX},{statsOut.FirstY}) pc=0x{hshavoc.GetM68kPc():X6} z80=0x{hshavoc.GetZ80Pc():X4} " +
                    $"sr=0x{hshavoc.GetM68kStatusRegister():X4} regs={FormatHshavocRegisters(hshavoc)} " +
                    $"vdp={hshavoc.GetVdpDisplayStatus()} op={FormatHshavocWords(hshavoc)} fp=0x{finalFingerprint:X16}");
                return 0;
            }

            if (useTmnt)
            {
                var tmnt = new TmntAdapter();
                tmnt.LoadRom(romPath);

                int? slotOverrideTmnt = ParseOptionalIntEnv("EUTHERDRIVE_SAVESTATE_SLOT");
                var payloadTmnt = TryLoadSavestatePayload(savestatePath, tmnt.RomIdentity, slotOverrideTmnt, out var tmntError);
                if (payloadTmnt == null)
                {
                    Console.Error.WriteLine($"[HEADLESS-ERROR] Savestate load failed: {tmntError}");
                    return 1;
                }

                using (var tmntStateStream = new MemoryStream(payloadTmnt, writable: false))
                using (var tmntStateReader = new BinaryReader(tmntStateStream))
                    tmnt.LoadState(tmntStateReader);

                Console.WriteLine("[HEADLESS] Savestate loaded successfully (TMNT)");
                ReadOnlySpan<byte> fbBefore = tmnt.GetFrameBuffer(out int wBefore, out int hBefore, out int sBefore);
                var statsBefore = GetFrameStats(fbBefore, wBefore, hBefore, sBefore);
                Console.WriteLine($"[HEADLESS] TMNT before fb_has_content={statsBefore.HasContent} nonzero_pixels={statsBefore.NonZeroPixels} first_nonzero=({statsBefore.FirstX},{statsBefore.FirstY}) frameCounter={tmnt.FrameCounter ?? -1}");
                Console.WriteLine($"[HEADLESS] TMNT before debug {tmnt.DebugSummary}");
                DumpBgraToPpm(fbBefore, wBefore, hBefore, sBefore, Path.Combine(dumpDir, "headless_tmnt_state_before.ppm"));

                using var audioDump = OpenOptionalRawAudioDump(dumpDir, "headless_tmnt_audio_s16le.raw");
                var tmntInputScript = ParseSnesInputScript(Environment.GetEnvironmentVariable("EUTHERDRIVE_TMNT_HEADLESS_INPUT_SCRIPT"));
                long runTicksTotal = 0;
                long runTicksMin = long.MaxValue;
                long runTicksMax = 0;
                for (int frame = 0; frame < framesToRun; frame++)
                {
                    var input = ResolveSnesInputForFrame(frame, tmntInputScript);
                    tmnt.SetInputState(
                        input.Up, input.Down, input.Left, input.Right,
                        input.A, input.B, input.X,
                        input.Start,
                        false, false, false,
                        input.Select,
                        PadType.SixButton);
                    long runStart = Stopwatch.GetTimestamp();
                    tmnt.RunFrame();
                    long runTicks = Stopwatch.GetTimestamp() - runStart;
                    runTicksTotal += runTicks;
                    runTicksMin = Math.Min(runTicksMin, runTicks);
                    runTicksMax = Math.Max(runTicksMax, runTicks);
                    ReadOnlySpan<short> audio = tmnt.GetAudioBuffer(out int sampleRate, out int channels);
                    WriteRawAudio(audioDump, audio);

                    if (frame == 0 || frame == 5 || frame == 10 || ((frame + 1) % 60) == 0)
                    {
                        ReadOnlySpan<byte> fb = tmnt.GetFrameBuffer(out int w, out int h, out int s);
                        var stats = GetFrameStats(fb, w, h, s);
                        int peak = AudioPeak(audio);
                        Console.WriteLine($"[HEADLESS] Frame {frame}: tmnt_fb_has_content={stats.HasContent} nonzero_pixels={stats.NonZeroPixels} first_nonzero=({stats.FirstX},{stats.FirstY}) audio={sampleRate}Hz/{channels}ch peak={peak} {tmnt.DebugSummary}");
                        DumpBgraToPpm(fb, w, h, s, Path.Combine(dumpDir, $"headless_tmnt_state_frame{frame}.ppm"));
                    }
                }

                ReadOnlySpan<byte> fbOut = tmnt.GetFrameBuffer(out int wOut, out int hOut, out int sOut);
                var statsOut = GetFrameStats(fbOut, wOut, hOut, sOut);
                Console.WriteLine($"[HEADLESS] TMNT final fb_has_content={statsOut.HasContent} nonzero_pixels={statsOut.NonZeroPixels} first_nonzero=({statsOut.FirstX},{statsOut.FirstY}) frameCounter={tmnt.FrameCounter ?? -1}");
                Console.WriteLine($"[HEADLESS] TMNT final debug {tmnt.DebugSummary}");
                DumpBgraToPpm(fbOut, wOut, hOut, sOut, Path.Combine(dumpDir, "headless_tmnt_state_output.ppm"));
                PrintHeadlessPerf("TMNT", framesToRun, runTicksTotal, runTicksMin, runTicksMax, 60.0);
                Console.WriteLine($"[HEADLESS] Completed {framesToRun} frames");
                return 0;
            }

            if (useDeco32)
            {
                var deco32 = new Deco32Adapter();
                deco32.LoadRom(romPath);

                int? slotOverrideDeco32 = ParseOptionalIntEnv("EUTHERDRIVE_SAVESTATE_SLOT");
                var payloadDeco32 = TryLoadSavestatePayload(savestatePath, deco32.RomIdentity, slotOverrideDeco32, out var deco32Error);
                if (payloadDeco32 == null)
                {
                    Console.Error.WriteLine($"[HEADLESS-ERROR] Savestate load failed: {deco32Error}");
                    return 1;
                }

                using (var deco32StateStream = new MemoryStream(payloadDeco32, writable: false))
                using (var deco32StateReader = new BinaryReader(deco32StateStream))
                    deco32.LoadState(deco32StateReader);

                Console.WriteLine("[HEADLESS] Savestate loaded successfully (Deco32)");
                ReadOnlySpan<byte> fbBefore = deco32.GetFrameBuffer(out int wBefore, out int hBefore, out int sBefore);
                var statsBefore = GetFrameStats(fbBefore, wBefore, hBefore, sBefore);
                ulong lastFingerprint = ComputeFrameFingerprint(fbBefore, wBefore, hBefore, sBefore);
                Console.WriteLine($"[HEADLESS] Deco32 before fb_has_content={statsBefore.HasContent} nonzero_pixels={statsBefore.NonZeroPixels} first_nonzero=({statsBefore.FirstX},{statsBefore.FirstY}) fp=0x{lastFingerprint:X16} frameCounter={deco32.FrameCounter ?? -1}");
                Console.WriteLine($"[HEADLESS] Deco32 before debug {deco32.DebugSummary}");
                DumpBgraToPpm(fbBefore, wBefore, hBefore, sBefore, Path.Combine(dumpDir, "headless_deco32_state_before.ppm"));

                var decoInputScript = ParseSnesInputScript(Environment.GetEnvironmentVariable("EUTHERDRIVE_DECO32_HEADLESS_INPUT_SCRIPT"));
                bool traceFrames = Environment.GetEnvironmentVariable("EUTHERDRIVE_HEADLESS_TRACE_FRAMES") == "1";
                int unchangedFrames = 0;
                for (int frame = 0; frame < framesToRun; frame++)
                {
                    var input = ResolveSnesInputForFrame(frame, decoInputScript);
                    deco32.SetInputState(
                        input.Up, input.Down, input.Left, input.Right,
                        input.A, input.B, input.X,
                        input.Start,
                        input.Y, input.L, input.R,
                        input.Select,
                        PadType.SixButton);
                    deco32.RunFrame();

                    ReadOnlySpan<byte> fb = deco32.GetFrameBuffer(out int w, out int h, out int s);
                    var stats = GetFrameStats(fb, w, h, s);
                    ulong fingerprint = ComputeFrameFingerprint(fb, w, h, s);
                    unchangedFrames = fingerprint == lastFingerprint ? unchangedFrames + 1 : 0;
                    lastFingerprint = fingerprint;

                    if (traceFrames || frame == 0 || frame == 5 || frame == 10 || ((frame + 1) % 60) == 0)
                        Console.WriteLine($"[HEADLESS] Frame {frame}: deco32_fb_has_content={stats.HasContent} nonzero_pixels={stats.NonZeroPixels} first_nonzero=({stats.FirstX},{stats.FirstY}) fp=0x{fingerprint:X16} unchanged={unchangedFrames} frameCounter={deco32.FrameCounter ?? -1}");

                    if (frame == 0 || frame == 5 || frame == 10)
                        DumpBgraToPpm(fb, w, h, s, Path.Combine(dumpDir, $"headless_deco32_state_frame{frame}.ppm"));
                }

                ReadOnlySpan<byte> fbOut = deco32.GetFrameBuffer(out int wOut, out int hOut, out int sOut);
                var statsOut = GetFrameStats(fbOut, wOut, hOut, sOut);
                Console.WriteLine($"[HEADLESS] Deco32 final fb_has_content={statsOut.HasContent} nonzero_pixels={statsOut.NonZeroPixels} first_nonzero=({statsOut.FirstX},{statsOut.FirstY}) frameCounter={deco32.FrameCounter ?? -1}");
                Console.WriteLine($"[HEADLESS] Deco32 final debug {deco32.DebugSummary}");
                DumpBgraToPpm(fbOut, wOut, hOut, sOut, Path.Combine(dumpDir, "headless_deco32_state_output.ppm"));
                Console.WriteLine($"[HEADLESS] Completed {framesToRun} frames");
                return 0;
            }

            if (useNeoGeo)
            {
                Console.WriteLine("[HEADLESS] Using Neo Geo core");
                using var neoGeo = new NeoGeoAdapter();
                neoGeo.LoadRom(romPath);

                int? slotOverrideNeoGeo = ParseOptionalIntEnv("EUTHERDRIVE_SAVESTATE_SLOT");
                var payloadNeoGeo = TryLoadSavestatePayload(savestatePath, neoGeo.RomIdentity, slotOverrideNeoGeo, out var neoGeoError);
                if (payloadNeoGeo == null)
                {
                    Console.Error.WriteLine($"[HEADLESS-ERROR] Savestate load failed: {neoGeoError}");
                    return 1;
                }

                using (var neoGeoStateStream = new MemoryStream(payloadNeoGeo, writable: false))
                using (var neoGeoStateReader = new BinaryReader(neoGeoStateStream))
                    neoGeo.LoadState(neoGeoStateReader);

                Console.WriteLine("[HEADLESS] Savestate loaded successfully (NeoGeo)");
                ReadOnlySpan<byte> fbBefore = neoGeo.GetFrameBuffer(out int wBefore, out int hBefore, out int sBefore);
                var statsBefore = GetFrameStats(fbBefore, wBefore, hBefore, sBefore);
                ulong lastFingerprint = ComputeFrameFingerprint(fbBefore, wBefore, hBefore, sBefore);
                Console.WriteLine($"[HEADLESS] NeoGeo before fb_has_content={statsBefore.HasContent} nonzero_pixels={statsBefore.NonZeroPixels} first_nonzero=({statsBefore.FirstX},{statsBefore.FirstY}) fp=0x{lastFingerprint:X16}");
                DumpBgraToPpm(fbBefore, wBefore, hBefore, sBefore, Path.Combine(dumpDir, "headless_neogeo_state_before.ppm"));

                using var audioDump = OpenOptionalRawAudioDump(dumpDir, "headless_neogeo_audio_s16le.raw");
                var neoGeoInputScript = ParseSnesInputScript(Environment.GetEnvironmentVariable("EUTHERDRIVE_NEOGEO_HEADLESS_INPUT_SCRIPT"));
                bool traceFrames = Environment.GetEnvironmentVariable("EUTHERDRIVE_HEADLESS_TRACE_FRAMES") == "1";
                int unchangedFrames = 0;
                long runTicksTotal = 0;
                long runTicksMin = long.MaxValue;
                long runTicksMax = 0;
                for (int frame = 0; frame < framesToRun; frame++)
                {
                    var input = ResolveSnesInputForFrame(frame, neoGeoInputScript);
                    neoGeo.SetInputState(
                        input.Up, input.Down, input.Left, input.Right,
                        input.A, input.B, input.X,
                        input.Start,
                        input.Y, input.L, input.R,
                        input.Select,
                        PadType.SixButton);

                    long runStart = Stopwatch.GetTimestamp();
                    neoGeo.RunFrame();
                    long runTicks = Stopwatch.GetTimestamp() - runStart;
                    runTicksTotal += runTicks;
                    runTicksMin = Math.Min(runTicksMin, runTicks);
                    runTicksMax = Math.Max(runTicksMax, runTicks);

                    ReadOnlySpan<short> audio = neoGeo.GetAudioBuffer(out int sampleRate, out int channels);
                    WriteRawAudio(audioDump, audio);
                    ReadOnlySpan<byte> fb = neoGeo.GetFrameBuffer(out int w, out int h, out int s);
                    var stats = GetFrameStats(fb, w, h, s);
                    ulong fingerprint = ComputeFrameFingerprint(fb, w, h, s);
                    unchangedFrames = fingerprint == lastFingerprint ? unchangedFrames + 1 : 0;
                    lastFingerprint = fingerprint;

                    if (traceFrames || frame == 0 || frame == 5 || frame == 10 || ((frame + 1) % 60) == 0)
                        Console.WriteLine($"[HEADLESS] Frame {frame}: neogeo_fb_has_content={stats.HasContent} nonzero_pixels={stats.NonZeroPixels} first_nonzero=({stats.FirstX},{stats.FirstY}) fp=0x{fingerprint:X16} unchanged={unchangedFrames} audio={sampleRate}Hz/{channels}ch peak={AudioPeak(audio)}");

                    if (frame == 0 || frame == 5 || frame == 10)
                        DumpBgraToPpm(fb, w, h, s, Path.Combine(dumpDir, $"headless_neogeo_state_frame{frame}.ppm"));
                }

                ReadOnlySpan<byte> fbOut = neoGeo.GetFrameBuffer(out int wOut, out int hOut, out int sOut);
                var statsOut = GetFrameStats(fbOut, wOut, hOut, sOut);
                ulong finalFingerprint = ComputeFrameFingerprint(fbOut, wOut, hOut, sOut);
                ReadOnlySpan<short> audioOut = neoGeo.GetAudioBuffer(out int audioRate, out int neoGeoAudioChannels);
                Console.WriteLine($"[HEADLESS] NeoGeo final fb_has_content={statsOut.HasContent} nonzero_pixels={statsOut.NonZeroPixels} first_nonzero=({statsOut.FirstX},{statsOut.FirstY}) fp=0x{finalFingerprint:X16}");
                Console.WriteLine($"[HEADLESS] NeoGeo audio samples={audioOut.Length} rate={audioRate} channels={neoGeoAudioChannels} nonzero_samples={CountNonZeroAudioSamples(audioOut)} max_abs={AudioPeak(audioOut)}");
                DumpBgraToPpm(fbOut, wOut, hOut, sOut, Path.Combine(dumpDir, "headless_neogeo_state_output.ppm"));
                PrintHeadlessPerf("NeoGeo", framesToRun, runTicksTotal, runTicksMin, runTicksMax, neoGeo.GetTargetFps());
                Console.WriteLine($"[HEADLESS] Completed {framesToRun} frames");
                return 0;
            }

            if (useMcsArcade)
            {
                Console.WriteLine("[HEADLESS] Using MCS arcade core");
                using var arcade = new McsArcadeAdapter();
                arcade.LoadRom(romPath);

                int? slotOverrideMcs = ParseOptionalIntEnv("EUTHERDRIVE_SAVESTATE_SLOT");
                var payloadMcs = TryLoadSavestatePayload(savestatePath, arcade.RomIdentity, slotOverrideMcs, out var mcsError);
                if (payloadMcs == null)
                {
                    Console.Error.WriteLine($"[HEADLESS-ERROR] Savestate load failed: {mcsError}");
                    return 1;
                }

                using (var mcsStateStream = new MemoryStream(payloadMcs, writable: false))
                using (var mcsStateReader = new BinaryReader(mcsStateStream))
                    arcade.LoadState(mcsStateReader);

                Console.WriteLine("[HEADLESS] Savestate loaded successfully (MCS)");
                ReadOnlySpan<byte> fbIn = arcade.GetFrameBuffer(out int wIn, out int hIn, out int sIn);
                var statsIn = GetFrameStats(fbIn, wIn, hIn, sIn);
                ulong lastFingerprint = ComputeFrameFingerprint(fbIn, wIn, hIn, sIn);
                int unchangedFrames = 0;
                bool traceFrames = Environment.GetEnvironmentVariable("EUTHERDRIVE_HEADLESS_TRACE_FRAMES") == "1";
                Console.WriteLine($"[HEADLESS] MCS before fb_has_content={statsIn.HasContent} nonzero_pixels={statsIn.NonZeroPixels} first_nonzero=({statsIn.FirstX},{statsIn.FirstY}) fp=0x{lastFingerprint:X16} frameCounter={arcade.FrameCounter ?? -1}");
                DumpBgraToPpm(fbIn, wIn, hIn, sIn, Path.Combine(dumpDir, "headless_mcs_state_before.ppm"));

                for (int frame = 0; frame < framesToRun; frame++)
                {
                    arcade.RunFrame();
                    ReadOnlySpan<byte> fb = arcade.GetFrameBuffer(out int w, out int h, out int s);
                    var stats = GetFrameStats(fb, w, h, s);
                    ulong fingerprint = ComputeFrameFingerprint(fb, w, h, s);
                    unchangedFrames = fingerprint == lastFingerprint ? unchangedFrames + 1 : 0;
                    lastFingerprint = fingerprint;

                    if (traceFrames || frame == 0 || frame == 5 || frame == 10 || ((frame + 1) % 60) == 0)
                        Console.WriteLine($"[HEADLESS] Frame {frame}: mcs_fb_has_content={stats.HasContent} nonzero_pixels={stats.NonZeroPixels} first_nonzero=({stats.FirstX},{stats.FirstY}) fp=0x{fingerprint:X16} unchanged={unchangedFrames} frameCounter={arcade.FrameCounter ?? -1}");

                    if (frame == 0 || frame == 5 || frame == 10)
                        DumpBgraToPpm(fb, w, h, s, Path.Combine(dumpDir, $"headless_mcs_state_frame{frame}.ppm"));
                }

                ReadOnlySpan<byte> fbOut = arcade.GetFrameBuffer(out int wOut, out int hOut, out int sOut);
                var statsOut = GetFrameStats(fbOut, wOut, hOut, sOut);
                ulong finalFingerprint = ComputeFrameFingerprint(fbOut, wOut, hOut, sOut);
                Console.WriteLine($"[HEADLESS] MCS final fb_has_content={statsOut.HasContent} nonzero_pixels={statsOut.NonZeroPixels} first_nonzero=({statsOut.FirstX},{statsOut.FirstY}) fp=0x{finalFingerprint:X16} frameCounter={arcade.FrameCounter ?? -1}");
                DumpBgraToPpm(fbOut, wOut, hOut, sOut, Path.Combine(dumpDir, "headless_mcs_state_output.ppm"));
                Console.WriteLine($"[HEADLESS] Completed {framesToRun} frames");
                return 0;
            }

            if (useCps1)
            {
                var cps1 = new Cps1DinoAdapter();
                cps1.LoadRom(romPath);

                int? slotOverrideCps1 = ParseOptionalIntEnv("EUTHERDRIVE_SAVESTATE_SLOT");
                var payloadCps1 = TryLoadSavestatePayload(savestatePath, cps1.RomIdentity, slotOverrideCps1, out var cps1Error);
                if (payloadCps1 == null)
                {
                    Console.Error.WriteLine($"[HEADLESS-ERROR] Savestate load failed: {cps1Error}");
                    return 1;
                }

                Console.WriteLine("[HEADLESS] Savestate loaded successfully (CPS1)");
                return RunCps1Headless(romPath, framesToRun, dumpDir, payloadCps1);
            }

            if (useGb)
            {
                var gb = new GbAdapter();
                gb.LoadRom(romPath);

                int? slotOverrideGb = ParseOptionalIntEnv("EUTHERDRIVE_SAVESTATE_SLOT");
                var payloadGb = TryLoadSavestatePayload(savestatePath, gb.RomIdentity, slotOverrideGb, out var gbError);
                if (payloadGb == null)
                {
                    Console.Error.WriteLine($"[HEADLESS-ERROR] Savestate load failed: {gbError}");
                    return 1;
                }

                using (var gbStateStream = new MemoryStream(payloadGb, writable: false))
                using (var gbStateReader = new BinaryReader(gbStateStream))
                    gb.LoadState(gbStateReader);

                Console.WriteLine("[HEADLESS] Savestate loaded successfully (GB)");
                Console.WriteLine($"[HEADLESS] {gb.RomSummary}");
                Console.WriteLine("[HEADLESS] Framebuffer BEFORE running:");
                ReadOnlySpan<byte> gbFbIn = gb.GetFrameBuffer(out int gbWIn, out int gbHIn, out int gbSIn);
                var gbStatsIn = GetFrameStats(gbFbIn, gbWIn, gbHIn, gbSIn);
                ulong lastFingerprint = ComputeFrameFingerprint(gbFbIn, gbWIn, gbHIn, gbSIn);
                int unchangedFrames = 0;
                bool traceGbState = Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_GB_STATE") == "1";
                Console.WriteLine($"[HEADLESS] GB fb_has_content={gbStatsIn.HasContent} nonzero_pixels={gbStatsIn.NonZeroPixels} first_nonzero=({gbStatsIn.FirstX},{gbStatsIn.FirstY}) frameCounter={gb.FrameCounter ?? -1} fp=0x{lastFingerprint:X16}");
                if (traceGbState && !string.IsNullOrWhiteSpace(gb.DebugState))
                    Console.WriteLine($"[HEADLESS][GB-STATE] before {gb.DebugState}");
                DumpBgraToPpm(gbFbIn, gbWIn, gbHIn, gbSIn, Path.Combine(dumpDir, "headless_frame0.ppm"));

                for (int frame = 0; frame < framesToRun; frame++)
                {
                    gb.RunFrame();

                    ReadOnlySpan<byte> fb = gb.GetFrameBuffer(out int w, out int h, out int s);
                    var stats = GetFrameStats(fb, w, h, s);
                    ulong fingerprint = ComputeFrameFingerprint(fb, w, h, s);
                    unchangedFrames = fingerprint == lastFingerprint ? (unchangedFrames + 1) : 0;
                    lastFingerprint = fingerprint;

                    Console.WriteLine($"[HEADLESS] Frame {frame}: gb_fb_has_content={stats.HasContent} nonzero_pixels={stats.NonZeroPixels} first_nonzero=({stats.FirstX},{stats.FirstY}) frameCounter={gb.FrameCounter ?? -1} fp=0x{fingerprint:X16} unchanged={unchangedFrames}");
                    if (traceGbState && !string.IsNullOrWhiteSpace(gb.DebugState))
                        Console.WriteLine($"[HEADLESS][GB-STATE] frame={frame} {gb.DebugState}");

                    if (frame == 0 || frame == 5 || frame == 10)
                        DumpBgraToPpm(fb, w, h, s, Path.Combine(dumpDir, $"headless_frame{frame}.ppm"));
                }

                Console.WriteLine("[HEADLESS] Framebuffer AFTER running:");
                ReadOnlySpan<byte> gbFbOut = gb.GetFrameBuffer(out int gbWOut, out int gbHOut, out int gbSOut);
                var gbStatsOut = GetFrameStats(gbFbOut, gbWOut, gbHOut, gbSOut);
                ulong finalFingerprint = ComputeFrameFingerprint(gbFbOut, gbWOut, gbHOut, gbSOut);
                Console.WriteLine($"[HEADLESS] GB fb_has_content={gbStatsOut.HasContent} nonzero_pixels={gbStatsOut.NonZeroPixels} first_nonzero=({gbStatsOut.FirstX},{gbStatsOut.FirstY}) frameCounter={gb.FrameCounter ?? -1} fp=0x{finalFingerprint:X16}");
                DumpBgraToPpm(gbFbOut, gbWOut, gbHOut, gbSOut, Path.Combine(dumpDir, "headless_output.ppm"));
                Console.WriteLine($"[HEADLESS] Completed {framesToRun} frames");
                return 0;
            }

            if (useNes)
            {
                var nes = new NesAdapter();
                nes.LoadRom(romPath);

                int? slotOverrideNes = ParseOptionalIntEnv("EUTHERDRIVE_SAVESTATE_SLOT");
                var payloadNes = TryLoadSavestatePayload(savestatePath, nes.RomIdentity, slotOverrideNes, out var nesError);
                if (payloadNes == null)
                {
                    Console.Error.WriteLine($"[HEADLESS-ERROR] Savestate load failed: {nesError}");
                    return 1;
                }

                using (var nesStateStream = new MemoryStream(payloadNes, writable: false))
                using (var nesStateReader = new BinaryReader(nesStateStream))
                    nes.LoadState(nesStateReader);

                Console.WriteLine("[HEADLESS] Savestate loaded successfully (NES)");
                Console.WriteLine("[HEADLESS] Framebuffer BEFORE running:");
                ReadOnlySpan<byte> nesFbIn = nes.GetFrameBuffer(out int nesWIn, out int nesHIn, out int nesSIn);
                var nesStatsIn = GetFrameStats(nesFbIn, nesWIn, nesHIn, nesSIn);
                Console.WriteLine($"[HEADLESS] NES fb_has_content={nesStatsIn.HasContent} nonzero_pixels={nesStatsIn.NonZeroPixels} first_nonzero=({nesStatsIn.FirstX},{nesStatsIn.FirstY})");
                DumpBgraToPpm(nesFbIn, nesWIn, nesHIn, nesSIn, Path.Combine(dumpDir, "headless_frame0.ppm"));

                for (int frame = 0; frame < framesToRun; frame++)
                {
                    nes.RunFrame();
                    ReadOnlySpan<byte> fb = nes.GetFrameBuffer(out int w, out int h, out int s);
                    var stats = GetFrameStats(fb, w, h, s);
                    Console.WriteLine($"[HEADLESS] Frame {frame}: nes_fb_has_content={stats.HasContent} nonzero_pixels={stats.NonZeroPixels} first_nonzero=({stats.FirstX},{stats.FirstY})");
                    if (frame == 0 || frame == 5 || frame == 10)
                        DumpBgraToPpm(fb, w, h, s, Path.Combine(dumpDir, $"headless_frame{frame}.ppm"));
                }

                Console.WriteLine("[HEADLESS] Framebuffer AFTER running:");
                ReadOnlySpan<byte> nesFbOut = nes.GetFrameBuffer(out int nesWOut, out int nesHOut, out int nesSOut);
                var nesStatsOut = GetFrameStats(nesFbOut, nesWOut, nesHOut, nesSOut);
                Console.WriteLine($"[HEADLESS] NES fb_has_content={nesStatsOut.HasContent} nonzero_pixels={nesStatsOut.NonZeroPixels} first_nonzero=({nesStatsOut.FirstX},{nesStatsOut.FirstY})");
                DumpBgraToPpm(nesFbOut, nesWOut, nesHOut, nesSOut, Path.Combine(dumpDir, "headless_output.ppm"));
                Console.WriteLine($"[HEADLESS] Completed {framesToRun} frames");
                return 0;
            }

            if (useSnes)
            {
                var snes = new SnesAdapter();
                snes.LoadRom(romPath);

                int? slotOverrideSnes = ParseOptionalIntEnv("EUTHERDRIVE_SAVESTATE_SLOT");
                var payloadSnes = TryLoadSavestatePayload(savestatePath, snes.RomIdentity, slotOverrideSnes, out var snesError);
                if (payloadSnes == null)
                {
                    Console.Error.WriteLine($"[HEADLESS-ERROR] Savestate load failed: {snesError}");
                    return 1;
                }

                using (var snesStateStream = new MemoryStream(payloadSnes, writable: false))
                using (var snesStateReader = new BinaryReader(snesStateStream))
                    snes.LoadState(snesStateReader);

                Console.WriteLine("[HEADLESS] Savestate loaded successfully (SNES)");
                HeadlessAudioSink? snesAudioSink = null;
                bool enableSnesAudio = Environment.GetEnvironmentVariable("EUTHERDRIVE_HEADLESS_AUDIO") == "1";
                if (enableSnesAudio)
                    snesAudioSink = new HeadlessAudioSink();

                bool autoStart = Environment.GetEnvironmentVariable("EUTHERDRIVE_SNES_HEADLESS_AUTO_START") == "1";
                int autoStartDelayFrames = ParseOptionalIntEnv("EUTHERDRIVE_SNES_HEADLESS_AUTO_START_DELAY_FRAMES") ?? 0;
                int autoStartPulseFrames = ParseOptionalIntEnv("EUTHERDRIVE_SNES_HEADLESS_AUTO_START_PULSE_FRAMES") ?? 1;
                int autoStartPeriodFrames = ParseOptionalIntEnv("EUTHERDRIVE_SNES_HEADLESS_AUTO_START_PERIOD_FRAMES") ?? 60;
                int autoStartPulseCount = ParseOptionalIntEnv("EUTHERDRIVE_SNES_HEADLESS_AUTO_START_PULSE_COUNT") ?? 1;
                bool autoStartLog = Environment.GetEnvironmentVariable("EUTHERDRIVE_SNES_HEADLESS_AUTO_START_LOG") == "1";
                bool lastStartPressed = false;
                var snesInputScript = ParseSnesInputScript(Environment.GetEnvironmentVariable("EUTHERDRIVE_SNES_HEADLESS_INPUT_SCRIPT"));
                int? sa1SnapshotFrame = ParseOptionalIntEnv("EUTHERDRIVE_SNES_HEADLESS_SA1_SNAPSHOT_FRAME");
                int[] snesPeekAddrs = ParseOptionalHexAddrEnv("EUTHERDRIVE_TRACE_SNES_PEEK_ADDRS");
                HashSet<int> snesDumpFrames = ParseFrameSetEnv("EUTHERDRIVE_HEADLESS_DUMP_FRAMES");
                int? snesDumpFrameSingle = ParseOptionalIntEnv("EUTHERDRIVE_HEADLESS_DUMP_FRAME");
                if (snesDumpFrameSingle.HasValue && snesDumpFrameSingle.Value >= 0)
                    snesDumpFrames.Add(snesDumpFrameSingle.Value);
                HashSet<int> snesRawDumpFrames = ParseFrameSetEnv("EUTHERDRIVE_HEADLESS_SNES_RAW_DUMP_FRAMES");
                int? snesRawDumpFrameSingle = ParseOptionalIntEnv("EUTHERDRIVE_HEADLESS_SNES_RAW_DUMP_FRAME");
                if (snesRawDumpFrameSingle.HasValue && snesRawDumpFrameSingle.Value >= 0)
                    snesRawDumpFrames.Add(snesRawDumpFrameSingle.Value);

                bool traceSnesFrames = Environment.GetEnvironmentVariable("EUTHERDRIVE_HEADLESS_TRACE_FRAMES") == "1";
                bool traceSnesPpuSnapshot = Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_SNES_PPU_SNAPSHOT") == "1";
                bool traceSnesCheckpoints = Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_SNES_CHECKPOINTS") == "1";
                bool traceSnesFrameEnd = Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_SNES_FRAME_END") == "1";
                bool traceSnesPerf = Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_SNES_PERF") == "1";
                bool traceSnesPerfEveryFrame = Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_SNES_PERF_EVERY_FRAME") == "1";
                int traceSnesCheckpointEvery = Math.Max(1, ParseOptionalIntEnv("EUTHERDRIVE_TRACE_SNES_CHECKPOINT_EVERY") ?? 1);
                int traceSnesCheckpointStart = ParseOptionalIntEnv("EUTHERDRIVE_TRACE_SNES_CHECKPOINT_START_FRAME") ?? 0;
                int traceSnesCheckpointEnd = ParseOptionalIntEnv("EUTHERDRIVE_TRACE_SNES_CHECKPOINT_END_FRAME") ?? int.MaxValue;
                int traceSnesPerfStart = ParseOptionalIntEnv("EUTHERDRIVE_TRACE_SNES_PERF_FRAME_START") ?? 0;
                int traceSnesPerfEnd = ParseOptionalIntEnv("EUTHERDRIVE_TRACE_SNES_PERF_FRAME_END") ?? int.MaxValue;
                StreamWriter? snesTraceWriter = null;
                if (traceSnesFrames || traceSnesCheckpoints)
                {
                    string tracePath = Path.Combine(dumpDir, "headless_snes_trace.log");
                    snesTraceWriter = new StreamWriter(tracePath, append: false, Encoding.UTF8)
                    {
                        AutoFlush = true
                    };
                }

                void Trace(string message)
                {
                    Console.WriteLine(message);
                    snesTraceWriter?.WriteLine(message);
                }
                void TracePeek(string label)
                {
                    if (snesPeekAddrs.Length > 0)
                        Trace(DumpSnesPeek(snes, label, snesPeekAddrs));
                }
                void TraceCheckpoint(int frame)
                {
                    if (!traceSnesCheckpoints)
                        return;
                    if (frame < traceSnesCheckpointStart || frame > traceSnesCheckpointEnd)
                        return;
                    if (((frame - traceSnesCheckpointStart) % traceSnesCheckpointEvery) != 0)
                        return;
                    Trace($"[SNES-CHECKPOINT] frame={frame} {snes.GetDivergenceCheckpoint()}");
                }

                bool dumpSnesPpuRaw = Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_SNES_PPU_RAW") == "1";

                Console.WriteLine("[HEADLESS] Framebuffer BEFORE running:");
                DumpSnesFrame(snes, Path.Combine(dumpDir, "headless_frame0.ppm"), traceSnesFrames);
                if (dumpSnesPpuRaw)
                    DumpSnesPpuRaw(snes, Path.Combine(dumpDir, "snes_ppu_before"));
                TracePeek("before");

                bool prevHasContent = false;
                for (int frame = 0; frame < framesToRun; frame++)
                {
                    bool startPressed = autoStart &&
                        ShouldPressStartPulse(frame, autoStartDelayFrames, autoStartPulseFrames, autoStartPeriodFrames, autoStartPulseCount);
                    var scriptInput = ResolveSnesInputForFrame(frame, snesInputScript);
                    snes.SetInputState(
                        up: scriptInput.Up,
                        down: scriptInput.Down,
                        left: scriptInput.Left,
                        right: scriptInput.Right,
                        a: scriptInput.A,
                        b: scriptInput.B,
                        x: scriptInput.X,
                        y: scriptInput.Y,
                        z: scriptInput.L,
                        c: scriptInput.R,
                        start: startPressed || scriptInput.Start,
                        mode: scriptInput.Select,
                        padType: PadType.SixButton);
                    if (autoStartLog && startPressed != lastStartPressed)
                        Console.WriteLine($"[HEADLESS] SNES auto-start start={(startPressed ? 1 : 0)} frame={frame}");
                    lastStartPressed = startPressed;

                    snes.RunFrame();
                    TraceCheckpoint(frame);
                    if (ShouldTraceSnesPerfFrame(frame, traceSnesPerf, traceSnesPerfEveryFrame, traceSnesPerfStart, traceSnesPerfEnd) &&
                        snes.TryGetFramePerfSummary(out string perfSummary) &&
                        !string.IsNullOrWhiteSpace(perfSummary))
                    {
                        Trace($"[HEADLESS][SNES-PERF] frame={frame} {perfSummary.Replace(Environment.NewLine, " | ")}");
                    }

                    if (traceSnesFrames)
                    {
                        var state = snes.GetPpuState();
                        Trace($"[HEADLESS] Frame {frame}: ppu forcedBlank={state.ForcedBlank} bright={state.Brightness} mode={state.Mode} tm=0x{state.MainScreenMask:X2} ts=0x{state.SubScreenMask:X2} overscan={state.OverscanEnabled} frameOverscan={state.FrameOverscan} pseudoHires={state.PseudoHires} interlace={state.Interlace} objInterlace={state.ObjInterlace} vblank={state.InVblank} hblank={state.InHblank} nmi={state.InNmi} xy=({state.XPos},{state.YPos})");
                        ReadOnlySpan<byte> fb = snes.GetFrameBuffer(out int width, out int height, out int stride);
                        var stats = GetFrameStats(fb, width, height, stride);
                        Trace($"[HEADLESS] Frame {frame}: snes_fb_has_content={stats.HasContent} nonzero_pixels={stats.NonZeroPixels} first_nonzero=({stats.FirstX},{stats.FirstY})");
                        if (prevHasContent && !stats.HasContent)
                        {
                            Trace($"[HEADLESS] Frame {frame}: transition to BLACK (mode={state.Mode} tm=0x{state.MainScreenMask:X2} ts=0x{state.SubScreenMask:X2} forcedBlank={state.ForcedBlank} bright={state.Brightness})");
                        }
                        if (!prevHasContent && stats.HasContent)
                        {
                            Trace($"[HEADLESS] Frame {frame}: transition to CONTENT (mode={state.Mode} tm=0x{state.MainScreenMask:X2} ts=0x{state.SubScreenMask:X2} forcedBlank={state.ForcedBlank} bright={state.Brightness})");
                            if (traceSnesPpuSnapshot)
                            {
                                string? snapshot = snes.GetPpuDebugSnapshot();
                                if (!string.IsNullOrEmpty(snapshot))
                                    Trace($"[HEADLESS] Frame {frame}: ppu-snapshot{Environment.NewLine}{snapshot}");
                            }
                            TracePeek($"frame {frame} content");
                        }
                        prevHasContent = stats.HasContent;
                    }

                    if (snesAudioSink != null)
                    {
                        var audio = snes.GetAudioBuffer(out int rate, out int channels);
                        if (frame == 0)
                            snesAudioSink.Start(rate, channels);
                        if (!audio.IsEmpty)
                            snesAudioSink.Submit(audio);
                    }

                    if (frame == 0 || frame == 5 || frame == 10 || snesDumpFrames.Contains(frame))
                        DumpSnesFrame(snes, Path.Combine(dumpDir, $"headless_frame{frame}.ppm"), traceSnesFrames);
                    if (snesRawDumpFrames.Contains(frame))
                        DumpSnesPpuRaw(snes, Path.Combine(dumpDir, $"snes_ppu_frame{frame}"));

                    if (snes.System.CPU is KSNES.CPU.CPU cpu)
                    {
                        if (traceSnesFrames)
                        {
                            int dbrC000 = snes.System.Read((cpu.DataBank << 16) | 0xC000);
                            int dbr8000 = snes.System.Read((cpu.DataBank << 16) | 0x8000);
                            Trace($"[HEADLESS] Frame {frame}: cpu-state DBR=0x{cpu.DataBank:X2} PB=0x{cpu.ProgramBank:X2} DBR:C000=0x{dbrC000:X2} DBR:8000=0x{dbr8000:X2}");
                        }
                        if (sa1SnapshotFrame == frame && snes.System.ROM.Sa1 is KSNES.Specialchips.SA1.Sa1 snapshotSa1)
                        {
                            string snapshotPath = Path.Combine(dumpDir, $"sa1_snapshot_frame{frame}.txt");
                            string snapshot = snapshotSa1.GetKirbyDebugSnapshot();
                            Trace($"[HEADLESS] SA1 snapshot frame={frame}");
                            Trace(snapshot);
                            File.WriteAllText(snapshotPath, snapshot);
                        }
                        string sa1Pc = snes.System.ROM.Sa1 is KSNES.Specialchips.SA1.Sa1 sa1 && sa1.GetCpu() is KSNES.CPU.CPU sa1Cpu
                            ? $" SA1 PC=0x{sa1Cpu.ProgramCounter24:X6}"
                            : "";
                        if (traceSnesFrames || traceSnesFrameEnd)
                            Trace($"[HEADLESS] Frame {frame} ending SNES PC=0x{cpu.ProgramCounter24:X6}{sa1Pc}");
                    }
                }

                Console.WriteLine("[HEADLESS] Framebuffer AFTER running:");
                DumpSnesFrame(snes, Path.Combine(dumpDir, "headless_output.ppm"), traceSnesFrames);
                if (dumpSnesPpuRaw)
                    DumpSnesPpuRaw(snes, Path.Combine(dumpDir, "snes_ppu_after"));
                if (snesPeekAddrs.Length > 0)
                    Console.WriteLine(DumpSnesPeek(snes, "after", snesPeekAddrs));
                snesAudioSink?.Dispose();
                snesTraceWriter?.Dispose();
                Console.WriteLine($"[HEADLESS] Completed {framesToRun} frames");
                return 0;
            }

            if (useGba)
            {
                var gba = new GbaAdapter();
                gba.LoadRom(romPath);
                string gbaTracePath = Path.Combine(dumpDir, "headless_gba_trace.log");
                using var gbaTraceWriter = new StreamWriter(gbaTracePath, append: false, Encoding.UTF8) { AutoFlush = true };
                void TraceGba(string message)
                {
                    Console.WriteLine(message);
                    gbaTraceWriter.WriteLine(message);
                }

                int? slotOverrideGba = ParseOptionalIntEnv("EUTHERDRIVE_SAVESTATE_SLOT");
                var payloadGba = TryLoadSavestatePayload(savestatePath, gba.RomIdentity, slotOverrideGba, out var gbaError);
                if (payloadGba == null)
                {
                    TraceGba($"[HEADLESS-ERROR] Savestate load failed: {gbaError}");
                    return 1;
                }

                using (var gbaStateStream = new MemoryStream(payloadGba, writable: false))
                using (var gbaStateReader = new BinaryReader(gbaStateStream))
                    gba.LoadState(gbaStateReader);

                TraceGba("[HEADLESS] Savestate loaded successfully (GBA)");
                TraceGba($"[HEADLESS] {gba.RomSummary}");

                HeadlessAudioSink? gbaAudioSink = null;
                bool enableGbaAudio = Environment.GetEnvironmentVariable("EUTHERDRIVE_HEADLESS_AUDIO") == "1";
                if (enableGbaAudio)
                    gbaAudioSink = new HeadlessAudioSink();

                bool autoStart = Environment.GetEnvironmentVariable("EUTHERDRIVE_GBA_HEADLESS_AUTO_START") == "1";
                int autoStartDelayFrames = ParseOptionalIntEnv("EUTHERDRIVE_GBA_HEADLESS_AUTO_START_DELAY_FRAMES") ?? 0;
                int autoStartPulseFrames = ParseOptionalIntEnv("EUTHERDRIVE_GBA_HEADLESS_AUTO_START_PULSE_FRAMES") ?? 2;
                int autoStartPeriodFrames = ParseOptionalIntEnv("EUTHERDRIVE_GBA_HEADLESS_AUTO_START_PERIOD_FRAMES") ?? 60;
                int autoStartPulseCount = ParseOptionalIntEnv("EUTHERDRIVE_GBA_HEADLESS_AUTO_START_PULSE_COUNT") ?? 1;
                bool autoStartLog = Environment.GetEnvironmentVariable("EUTHERDRIVE_GBA_HEADLESS_AUTO_START_LOG") == "1";
                bool traceGbaFrames = Environment.GetEnvironmentVariable("EUTHERDRIVE_HEADLESS_TRACE_FRAMES") == "1";
                bool lastStartPressed = false;
                var gbaInputScript = ParseSnesInputScript(Environment.GetEnvironmentVariable("EUTHERDRIVE_GBA_HEADLESS_INPUT_SCRIPT"));

                TraceGba("[HEADLESS] Framebuffer BEFORE running:");
                ReadOnlySpan<byte> gbaFbIn = gba.GetFrameBuffer(out int gbaWIn, out int gbaHIn, out int gbaSIn);
                var gbaStatsIn = GetFrameStats(gbaFbIn, gbaWIn, gbaHIn, gbaSIn);
                ulong lastFingerprint = ComputeFrameFingerprint(gbaFbIn, gbaWIn, gbaHIn, gbaSIn);
                int unchangedFrames = 0;
                TraceGba($"[HEADLESS] GBA fb_has_content={gbaStatsIn.HasContent} nonzero_pixels={gbaStatsIn.NonZeroPixels} first_nonzero=({gbaStatsIn.FirstX},{gbaStatsIn.FirstY}) frameCounter={gba.FrameCounter ?? -1} keyinput=0x{gba.DebugKeyInput ?? 0xFFFF:X4} fp=0x{lastFingerprint:X16}");
                DumpBgraToPpm(gbaFbIn, gbaWIn, gbaHIn, gbaSIn, Path.Combine(dumpDir, "headless_frame0.ppm"));

                for (int frame = 0; frame < framesToRun; frame++)
                {
                    bool startPressed = autoStart &&
                        ShouldPressStartPulse(frame, autoStartDelayFrames, autoStartPulseFrames, autoStartPeriodFrames, autoStartPulseCount);
                    var scriptInput = ResolveSnesInputForFrame(frame, gbaInputScript);
                    gba.SetInputState(
                        up: scriptInput.Up,
                        down: scriptInput.Down,
                        left: scriptInput.Left,
                        right: scriptInput.Right,
                        a: scriptInput.A,
                        b: scriptInput.B,
                        c: scriptInput.R,
                        start: startPressed || scriptInput.Start,
                        x: false,
                        y: false,
                        z: scriptInput.L,
                        mode: scriptInput.Select,
                        padType: PadType.SixButton);
                    if (autoStartLog && startPressed != lastStartPressed)
                        TraceGba($"[HEADLESS] GBA auto-start start={(startPressed ? 1 : 0)} frame={frame}");
                    lastStartPressed = startPressed;

                    gba.RunFrame();

                    if (gbaAudioSink != null)
                    {
                        var audio = gba.GetAudioBuffer(out int rate, out int channels);
                        if (frame == 0)
                            gbaAudioSink.Start(rate, channels);
                        if (!audio.IsEmpty)
                            gbaAudioSink.Submit(audio);
                    }

                    ReadOnlySpan<byte> fb = gba.GetFrameBuffer(out int w, out int h, out int s);
                    var stats = GetFrameStats(fb, w, h, s);
                    ulong fingerprint = ComputeFrameFingerprint(fb, w, h, s);
                    unchangedFrames = fingerprint == lastFingerprint ? (unchangedFrames + 1) : 0;
                    lastFingerprint = fingerprint;

                    if (traceGbaFrames || frame == 0 || frame == 5 || frame == 10 || ((frame + 1) % 60) == 0)
                    {
                        TraceGba($"[HEADLESS] Frame {frame}: gba_fb_has_content={stats.HasContent} nonzero_pixels={stats.NonZeroPixels} first_nonzero=({stats.FirstX},{stats.FirstY}) frameCounter={gba.FrameCounter ?? -1} keyinput=0x{gba.DebugKeyInput ?? 0xFFFF:X4} fp=0x{fingerprint:X16} unchanged={unchangedFrames}");
                    }

                    if (frame == 0 || frame == 5 || frame == 10)
                        DumpBgraToPpm(fb, w, h, s, Path.Combine(dumpDir, $"headless_frame{frame}.ppm"));
                }

                TraceGba("[HEADLESS] Framebuffer AFTER running:");
                ReadOnlySpan<byte> gbaFbOut = gba.GetFrameBuffer(out int gbaWOut, out int gbaHOut, out int gbaSOut);
                var gbaStatsOut = GetFrameStats(gbaFbOut, gbaWOut, gbaHOut, gbaSOut);
                ulong finalFingerprint = ComputeFrameFingerprint(gbaFbOut, gbaWOut, gbaHOut, gbaSOut);
                TraceGba($"[HEADLESS] GBA fb_has_content={gbaStatsOut.HasContent} nonzero_pixels={gbaStatsOut.NonZeroPixels} first_nonzero=({gbaStatsOut.FirstX},{gbaStatsOut.FirstY}) frameCounter={gba.FrameCounter ?? -1} keyinput=0x{gba.DebugKeyInput ?? 0xFFFF:X4} fp=0x{finalFingerprint:X16}");
                DumpBgraToPpm(gbaFbOut, gbaWOut, gbaHOut, gbaSOut, Path.Combine(dumpDir, "headless_output.ppm"));
                gbaAudioSink?.Dispose();
                TraceGba($"[HEADLESS] Completed {framesToRun} frames");
                return 0;
            }

            if (useSmsGg)
            {
                var smsgg = new SmsGgAdapter();
                smsgg.LoadRom(romPath);

                int? slotOverrideSmsGg = ParseOptionalIntEnv("EUTHERDRIVE_SAVESTATE_SLOT");
                var payloadSmsGg = TryLoadSavestatePayload(savestatePath, smsgg.RomIdentity, slotOverrideSmsGg, out var smsggError);
                if (payloadSmsGg == null)
                {
                    Console.Error.WriteLine($"[HEADLESS-ERROR] Savestate load failed: {smsggError}");
                    return 1;
                }

                using (var smsggStateStream = new MemoryStream(payloadSmsGg, writable: false))
                using (var smsggStateReader = new BinaryReader(smsggStateStream))
                    smsgg.LoadState(smsggStateReader);

                Console.WriteLine("[HEADLESS] Savestate loaded successfully (SMS/GG)");
                Console.WriteLine($"[HEADLESS] {smsgg.RomSummary}");
                Console.WriteLine("[HEADLESS] Framebuffer BEFORE running:");
                ReadOnlySpan<byte> smsggFbIn = smsgg.GetFrameBuffer(out int smsggWIn, out int smsggHIn, out int smsggSIn);
                var smsggStatsIn = GetFrameStats(smsggFbIn, smsggWIn, smsggHIn, smsggSIn);
                ulong lastFingerprint = ComputeFrameFingerprint(smsggFbIn, smsggWIn, smsggHIn, smsggSIn);
                int unchangedFrames = 0;
                Console.WriteLine($"[HEADLESS] SMSGG fb_has_content={smsggStatsIn.HasContent} nonzero_pixels={smsggStatsIn.NonZeroPixels} first_nonzero=({smsggStatsIn.FirstX},{smsggStatsIn.FirstY}) fp=0x{lastFingerprint:X16}");
                DumpBgraToPpm(smsggFbIn, smsggWIn, smsggHIn, smsggSIn, Path.Combine(dumpDir, "headless_frame0.ppm"));

                for (int frame = 0; frame < framesToRun; frame++)
                {
                    smsgg.RunFrame();

                    ReadOnlySpan<byte> fb = smsgg.GetFrameBuffer(out int w, out int h, out int s);
                    var stats = GetFrameStats(fb, w, h, s);
                    ulong fingerprint = ComputeFrameFingerprint(fb, w, h, s);
                    unchangedFrames = fingerprint == lastFingerprint ? (unchangedFrames + 1) : 0;
                    lastFingerprint = fingerprint;

                    Console.WriteLine($"[HEADLESS] Frame {frame}: smsgg_fb_has_content={stats.HasContent} nonzero_pixels={stats.NonZeroPixels} first_nonzero=({stats.FirstX},{stats.FirstY}) fp=0x{fingerprint:X16} unchanged={unchangedFrames}");

                    if (frame == 0 || frame == 5 || frame == 10)
                        DumpBgraToPpm(fb, w, h, s, Path.Combine(dumpDir, $"headless_frame{frame}.ppm"));
                }

                Console.WriteLine("[HEADLESS] Framebuffer AFTER running:");
                ReadOnlySpan<byte> smsggFbOut = smsgg.GetFrameBuffer(out int smsggWOut, out int smsggHOut, out int smsggSOut);
                var smsggStatsOut = GetFrameStats(smsggFbOut, smsggWOut, smsggHOut, smsggSOut);
                ulong finalFingerprint = ComputeFrameFingerprint(smsggFbOut, smsggWOut, smsggHOut, smsggSOut);
                Console.WriteLine($"[HEADLESS] SMSGG fb_has_content={smsggStatsOut.HasContent} nonzero_pixels={smsggStatsOut.NonZeroPixels} first_nonzero=({smsggStatsOut.FirstX},{smsggStatsOut.FirstY}) fp=0x{finalFingerprint:X16}");
                DumpBgraToPpm(smsggFbOut, smsggWOut, smsggHOut, smsggSOut, Path.Combine(dumpDir, "headless_output.ppm"));
                Console.WriteLine($"[HEADLESS] Completed {framesToRun} frames");
                return 0;
            }

            if (usePsx)
            {
                ConfigurePsxAdapterFromEnv();
                bool tracePsxStart = Environment.GetEnvironmentVariable("EUTHERDRIVE_PSX_TRACE_START") == "1";
                string? tracePsxStartFile = Environment.GetEnvironmentVariable("EUTHERDRIVE_PSX_START_TRACE_FILE");
                string? tracePsxCodeFile = Environment.GetEnvironmentVariable("EUTHERDRIVE_PSX_CODE_TRACE_FILE");
                int[] tracePsxCodeAddresses = ParseOptionalHexAddrEnv("EUTHERDRIVE_PSX_CODE_TRACE_ADDR");
                uint? tracePsxCodeAddress = tracePsxCodeAddresses.Length > 0 ? (uint)tracePsxCodeAddresses[0] : null;
                int tracePsxFrameStart = ParseOptionalIntEnv("EUTHERDRIVE_PSX_TRACE_FRAME_START") ?? 0;
                int tracePsxFrameEnd = ParseOptionalIntEnv("EUTHERDRIVE_PSX_TRACE_FRAME_END") ?? int.MaxValue;
                bool tracePsxEveryFrame = IsEnvEnabled("EUTHERDRIVE_PSX_TRACE_EVERY_FRAME");

                var psx = new PsxAdapter();
                psx.LoadRom(romPath);

                int? slotOverridePsx = ParseOptionalIntEnv("EUTHERDRIVE_SAVESTATE_SLOT");
                var payloadPsx = TryLoadSavestatePayload(savestatePath, psx.RomIdentity, slotOverridePsx, out var psxError);
                if (payloadPsx == null)
                {
                    Console.Error.WriteLine($"[HEADLESS-ERROR] Savestate load failed: {psxError}");
                    return 1;
                }

                using (var psxStateStream = new MemoryStream(payloadPsx, writable: false))
                using (var psxStateReader = new BinaryReader(psxStateStream))
                    psx.LoadState(psxStateReader);

                Console.WriteLine("[HEADLESS] Savestate loaded successfully (PSX)");
                Console.WriteLine("[HEADLESS] Framebuffer BEFORE running:");
                ReadOnlySpan<byte> psxFbIn = psx.GetFrameBuffer(out int psxWIn, out int psxHIn, out int psxSIn);
                var psxStatsIn = GetFrameStats(psxFbIn, psxWIn, psxHIn, psxSIn);
                Console.WriteLine($"[HEADLESS] PSX fb_has_content={psxStatsIn.HasContent} nonzero_pixels={psxStatsIn.NonZeroPixels} first_nonzero=({psxStatsIn.FirstX},{psxStatsIn.FirstY})");
                DumpBgraToPpm(psxFbIn, psxWIn, psxHIn, psxSIn, Path.Combine(dumpDir, "headless_frame0.ppm"));

                for (int frame = 0; frame < framesToRun; frame++)
                {
                    psx.RunFrame();
                    ReadOnlySpan<byte> fb = psx.GetFrameBuffer(out int w, out int h, out int s);
                    var stats = GetFrameStats(fb, w, h, s);
                    Console.WriteLine($"[HEADLESS] Frame {frame}: psx_fb_has_content={stats.HasContent} nonzero_pixels={stats.NonZeroPixels} first_nonzero=({stats.FirstX},{stats.FirstY})");
                    if (frame == 0 || frame == 5 || frame == 10)
                        DumpBgraToPpm(fb, w, h, s, Path.Combine(dumpDir, $"headless_frame{frame}.ppm"));
                    if (ShouldTracePsxFrame(frame, tracePsxStart, tracePsxEveryFrame, tracePsxFrameStart, tracePsxFrameEnd))
                    {
                        if (psx.TryGetDebugState(out string debugState))
                        {
                            string line = $"[HEADLESS][PSX-SAVESTATE] frame={frame} {debugState}";
                            Console.WriteLine(line);
                            if (!string.IsNullOrWhiteSpace(tracePsxStartFile))
                            {
                                Directory.CreateDirectory(Path.GetDirectoryName(tracePsxStartFile) ?? ".");
                                File.AppendAllText(tracePsxStartFile, line + Environment.NewLine);
                            }
                        }
                        if (!string.IsNullOrWhiteSpace(tracePsxCodeFile) && psx.TryGetDebugCodeWindow(out string codeWindow, address: tracePsxCodeAddress))
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(tracePsxCodeFile) ?? ".");
                            File.AppendAllText(tracePsxCodeFile, $"[HEADLESS][PSX-SAVESTATE-CODE] frame={frame}{Environment.NewLine}{codeWindow}");
                        }
                    }
                }

                Console.WriteLine("[HEADLESS] Framebuffer AFTER running:");
                ReadOnlySpan<byte> psxFbOut = psx.GetFrameBuffer(out int psxWOut, out int psxHOut, out int psxSOut);
                var psxStatsOut = GetFrameStats(psxFbOut, psxWOut, psxHOut, psxSOut);
                Console.WriteLine($"[HEADLESS] PSX fb_has_content={psxStatsOut.HasContent} nonzero_pixels={psxStatsOut.NonZeroPixels} first_nonzero=({psxStatsOut.FirstX},{psxStatsOut.FirstY})");
                DumpBgraToPpm(psxFbOut, psxWOut, psxHOut, psxSOut, Path.Combine(dumpDir, "headless_output.ppm"));
                Console.WriteLine($"[HEADLESS] Completed {framesToRun} frames");
                return 0;
            }

            if (useSegaCd)
            {
                var scd = new SegaCdAdapter();
                scd.LoadRom(romPath);

                int? slotOverrideScd = ParseOptionalIntEnv("EUTHERDRIVE_SAVESTATE_SLOT");
                var payloadScd = TryLoadSavestatePayload(savestatePath, scd.RomIdentity, slotOverrideScd, out var scdError);
                if (payloadScd == null)
                {
                    Console.Error.WriteLine($"[HEADLESS-ERROR] Savestate load failed: {scdError}");
                    return 1;
                }

                using (var scdStateStream = new MemoryStream(payloadScd, writable: false))
                using (var scdStateReader = new BinaryReader(scdStateStream))
                    scd.LoadState(scdStateReader);

                Console.WriteLine("[HEADLESS] Savestate loaded successfully (Sega CD)");
                Console.WriteLine("[HEADLESS] Framebuffer BEFORE running:");
                ReadOnlySpan<byte> scdFbIn = scd.GetFrameBuffer(out int scdWIn, out int scdHIn, out int scdSIn);
                var scdStatsIn = GetFrameStats(scdFbIn, scdWIn, scdHIn, scdSIn);
                Console.WriteLine($"[HEADLESS] SegaCD fb_has_content={scdStatsIn.HasContent} nonzero_pixels={scdStatsIn.NonZeroPixels} first_nonzero=({scdStatsIn.FirstX},{scdStatsIn.FirstY})");
                DumpBgraToPpm(scdFbIn, scdWIn, scdHIn, scdSIn, Path.Combine(dumpDir, "headless_frame0.ppm"));

                for (int frame = 0; frame < framesToRun; frame++)
                {
                    scd.RunFrame();
                    ReadOnlySpan<byte> fb = scd.GetFrameBuffer(out int w, out int h, out int s);
                    var stats = GetFrameStats(fb, w, h, s);
                    Console.WriteLine($"[HEADLESS] Frame {frame}: scd_fb_has_content={stats.HasContent} nonzero_pixels={stats.NonZeroPixels} first_nonzero=({stats.FirstX},{stats.FirstY})");
                    if (frame == 0 || frame == 5 || frame == 10)
                        DumpBgraToPpm(fb, w, h, s, Path.Combine(dumpDir, $"headless_frame{frame}.ppm"));
                }

                Console.WriteLine("[HEADLESS] Framebuffer AFTER running:");
                ReadOnlySpan<byte> scdFbOut = scd.GetFrameBuffer(out int scdWOut, out int scdHOut, out int scdSOut);
                var scdStatsOut = GetFrameStats(scdFbOut, scdWOut, scdHOut, scdSOut);
                Console.WriteLine($"[HEADLESS] SegaCD fb_has_content={scdStatsOut.HasContent} nonzero_pixels={scdStatsOut.NonZeroPixels} first_nonzero=({scdStatsOut.FirstX},{scdStatsOut.FirstY})");
                DumpBgraToPpm(scdFbOut, scdWOut, scdHOut, scdSOut, Path.Combine(dumpDir, "headless_output.ppm"));
                return 0;
            }

            if (usePce)
            {
                var pce = new PceCdAdapter();
                pce.LoadRom(romPath);

                int? slotOverridePce = ParseOptionalIntEnv("EUTHERDRIVE_SAVESTATE_SLOT");
                var payloadPce = TryLoadSavestatePayload(savestatePath, pce.RomIdentity, slotOverridePce, out var pceError);
                if (payloadPce == null)
                {
                    Console.Error.WriteLine($"[HEADLESS-ERROR] Savestate load failed: {pceError}");
                    return 1;
                }

                using (var pceStateStream = new MemoryStream(payloadPce, writable: false))
                using (var pceStateReader = new BinaryReader(pceStateStream))
                    pce.LoadState(pceStateReader);

                string? pceTracePath = Environment.GetEnvironmentVariable("EUTHERDRIVE_PCE_TRACE_FILE");
                StreamWriter? pceTraceWriter = null;
                if (!string.IsNullOrWhiteSpace(pceTracePath))
                {
                    string fullPath = Path.IsPathRooted(pceTracePath)
                        ? pceTracePath
                        : Path.Combine(dumpDir, pceTracePath);
                    Directory.CreateDirectory(Path.GetDirectoryName(fullPath) ?? dumpDir);
                    pceTraceWriter = new StreamWriter(fullPath, append: false, Encoding.UTF8);
                    Console.WriteLine($"[HEADLESS] PCE trace file: {fullPath}");
                }

                bool autoRun = Environment.GetEnvironmentVariable("EUTHERDRIVE_PCE_HEADLESS_AUTO_RUN") == "1";
                int autoRunDelayFrames = ParseOptionalIntEnv("EUTHERDRIVE_PCE_HEADLESS_AUTO_RUN_DELAY_FRAMES") ?? 90;
                int autoRunPulseFrames = ParseOptionalIntEnv("EUTHERDRIVE_PCE_HEADLESS_AUTO_RUN_PULSE_FRAMES") ?? 3;
                int autoRunPeriodFrames = ParseOptionalIntEnv("EUTHERDRIVE_PCE_HEADLESS_AUTO_RUN_PERIOD_FRAMES") ?? 90;
                int autoRunPulseCount = ParseOptionalIntEnv("EUTHERDRIVE_PCE_HEADLESS_AUTO_RUN_PULSE_COUNT") ?? 8;

                static bool ShouldPressStartPce(int frame, int delay, int pulse, int period, int count)
                {
                    if (frame < delay || pulse <= 0 || period <= 0 || count <= 0)
                        return false;
                    int rel = frame - delay;
                    int window = rel / period;
                    if (window < 0 || window >= count)
                        return false;
        return (rel % period) < pulse;
    }


                var pceDumpFrames = new HashSet<int>();
                string? pceDumpFramesRaw = Environment.GetEnvironmentVariable("EUTHERDRIVE_HEADLESS_DUMP_FRAMES");
                if (!string.IsNullOrWhiteSpace(pceDumpFramesRaw))
                {
                    foreach (string part in pceDumpFramesRaw.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (int.TryParse(part.Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int frameIndex))
                            pceDumpFrames.Add(frameIndex);
                    }
                }
                int? pceDumpFrameSingle = ParseOptionalIntEnv("EUTHERDRIVE_HEADLESS_DUMP_FRAME");
                if (pceDumpFrameSingle.HasValue)
                    pceDumpFrames.Add(pceDumpFrameSingle.Value);
                bool snapshotOnDump = Environment.GetEnvironmentVariable("EUTHERDRIVE_PCE_SNAPSHOT_ON_DUMP") == "1";

                ReadOnlySpan<byte> fb0 = pce.GetFrameBuffer(out int w0, out int h0, out int s0);
                var stats0 = GetFrameStats(fb0, w0, h0, s0);
                Console.WriteLine($"[HEADLESS] Frame 0: fb_has_content={stats0.HasContent} nonzero_pixels={stats0.NonZeroPixels} first_nonzero=({stats0.FirstX},{stats0.FirstY})");
                DumpBgraToPpm(fb0, w0, h0, s0, Path.Combine(dumpDir, "headless_frame0.ppm"));
                pce.CaptureDebugSnapshot(dumpDir);
                for (int frame = 0; frame < framesToRun; frame++)
                {
                    bool startPressed = autoRun &&
                        ShouldPressStartPce(frame, autoRunDelayFrames, autoRunPulseFrames, autoRunPeriodFrames, autoRunPulseCount);
                    pce.SetInputState(
                        up: false, down: false, left: false, right: false,
                        a: false, b: false, c: false, start: startPressed,
                        x: false, y: false, z: false, mode: false,
                        padType: PadType.SixButton);
                    pce.RunFrame();
                    if (pceTraceWriter != null)
                        pceTraceWriter.WriteLine(pce.BuildDeterminismTraceLine(frame));

                    if (frame == 0 || frame == 5 || frame == 10 || pceDumpFrames.Contains(frame))
                    {
                        ReadOnlySpan<byte> fb = pce.GetFrameBuffer(out int w, out int h, out int s);
                        var stats = GetFrameStats(fb, w, h, s);
                        Console.WriteLine($"[HEADLESS] Frame {frame}: fb_has_content={stats.HasContent} nonzero_pixels={stats.NonZeroPixels} first_nonzero=({stats.FirstX},{stats.FirstY})");
                        string ppmPath = Path.Combine(dumpDir, $"headless_frame{frame}.ppm");
                        DumpBgraToPpm(fb, w, h, s, ppmPath);
                        Console.WriteLine($"[HEADLESS] Dumped frame {frame} to {ppmPath}");
                        if (snapshotOnDump)
                        {
                            string snapPrefix = pce.CaptureDebugSnapshot(dumpDir);
                            Console.WriteLine($"[HEADLESS] PCE snapshot captured: {snapPrefix}");
                        }
                    }
                }
                pceTraceWriter?.Flush();
                pceTraceWriter?.Dispose();
                ReadOnlySpan<byte> fbOut = pce.GetFrameBuffer(out int wOut, out int hOut, out int sOut);
                var statsOut = GetFrameStats(fbOut, wOut, hOut, sOut);
                Console.WriteLine($"[HEADLESS] Final: fb_has_content={statsOut.HasContent} nonzero_pixels={statsOut.NonZeroPixels} first_nonzero=({statsOut.FirstX},{statsOut.FirstY})");
                DumpBgraToPpm(fbOut, wOut, hOut, sOut, Path.Combine(dumpDir, "headless_output.ppm"));
                return 0;
            }

            var adapter = new MdTracerAdapter();
            adapter.LoadRom(romPath);

            const int audioSampleRate = 44100;
            const int audioChannels = 2;
            const int audioBufferChunkFrames = 256;
            long audioLastSystemCycles = 0;
            double audioFrameAccumulator = 0;
            AudioEngine? audioEngine = null;
            int audioTargetFrames = GetHeadlessAudioTargetFrames(audioSampleRate);
            bool audioThrottle = Environment.GetEnvironmentVariable("EUTHERDRIVE_HEADLESS_AUDIO_THROTTLE") != "0";
            if (Environment.GetEnvironmentVariable("EUTHERDRIVE_HEADLESS_AUDIO") == "1")
            {
                var audioSink = new HeadlessAudioSink();
                audioEngine = new AudioEngine(audioSink, audioSampleRate, audioChannels);
                audioEngine.Start();
            }

            int? slotOverride = ParseOptionalIntEnv("EUTHERDRIVE_SAVESTATE_SLOT");

            Console.WriteLine($"[HEADLESS] Loading savestate payload from file: {savestatePath}");
            var payload = TryLoadSavestatePayload(savestatePath, adapter.RomIdentity, slotOverride, out var error);
            if (payload == null)
            {
                Console.Error.WriteLine($"[HEADLESS-ERROR] Savestate load failed: {error}");
                return 1;
            }
            using var stateStream = new MemoryStream(payload, writable: false);
            using var stateReader = new BinaryReader(stateStream);
            adapter.LoadState(stateReader);
            Console.WriteLine($"[HEADLESS] Savestate loaded successfully from file");
            audioLastSystemCycles = 0;
            audioFrameAccumulator = 0;
            bool dump32XLayer = Environment.GetEnvironmentVariable("EUTHERDRIVE_HEADLESS_DUMP_32X_LAYER") == "1";
            bool dump32XOtherLayer = Environment.GetEnvironmentVariable("EUTHERDRIVE_HEADLESS_DUMP_32X_OTHER_LAYER") == "1";
            if (Environment.GetEnvironmentVariable("EUTHERDRIVE_YM") == "1")
                adapter.SetYmEnabled(true);

            Console.WriteLine("[HEADLESS] Framebuffer BEFORE running:");
            adapter.FrameBufferHasContent();
            adapter.DumpFrameBufferToPpm(Path.Combine(dumpDir, "headless_frame0.ppm"));
            if (dump32XLayer)
                adapter.Dump32XLayerToPpm(Path.Combine(dumpDir, "headless_frame0_32x.ppm"));
            if (dump32XOtherLayer)
                adapter.Dump32XOtherLayerToPpm(Path.Combine(dumpDir, "headless_frame0_32x_other.ppm"));

            int hangFrames = ParseOptionalIntEnv("EUTHERDRIVE_HANG_FRAMES") ?? 120;
            int videoStallFrames = ParseOptionalIntEnv("EUTHERDRIVE_VIDEO_STALL_FRAMES") ?? 180;
            int? forceZ80DumpFrame = ParseOptionalIntEnv("EUTHERDRIVE_FORCE_Z80_DUMP_FRAME");
            bool forceZ80DumpExtra = Environment.GetEnvironmentVariable("EUTHERDRIVE_FORCE_Z80_DUMP_EXTRA") == "1";
            string? forceZ80DumpPath = Environment.GetEnvironmentVariable("EUTHERDRIVE_FORCE_Z80_DUMP_PATH");
            uint lastM68kPc = 0;
            ushort lastZ80Pc = 0;
            long lastCycles = 0;
            int stableFrames = 0;
            bool hangTriggered = false;
            ulong lastVideoFingerprint = 0;
            int videoUnchangedFrames = 0;
            bool videoFingerprintValid = false;
            bool mdHoldUp = Environment.GetEnvironmentVariable("EUTHERDRIVE_MD_HEADLESS_HOLD_UP") == "1";
            bool mdHoldDown = Environment.GetEnvironmentVariable("EUTHERDRIVE_MD_HEADLESS_HOLD_DOWN") == "1";
            bool mdHoldLeft = Environment.GetEnvironmentVariable("EUTHERDRIVE_MD_HEADLESS_HOLD_LEFT") == "1";
            bool mdHoldRight = Environment.GetEnvironmentVariable("EUTHERDRIVE_MD_HEADLESS_HOLD_RIGHT") == "1";
            bool mdHoldA = Environment.GetEnvironmentVariable("EUTHERDRIVE_MD_HEADLESS_HOLD_A") == "1";
            bool mdHoldB = Environment.GetEnvironmentVariable("EUTHERDRIVE_MD_HEADLESS_HOLD_B") == "1";
            bool mdHoldC = Environment.GetEnvironmentVariable("EUTHERDRIVE_MD_HEADLESS_HOLD_C") == "1";
            bool mdHoldStart = Environment.GetEnvironmentVariable("EUTHERDRIVE_MD_HEADLESS_HOLD_START") == "1";
            bool mdHoldX = Environment.GetEnvironmentVariable("EUTHERDRIVE_MD_HEADLESS_HOLD_X") == "1";
            bool mdHoldY = Environment.GetEnvironmentVariable("EUTHERDRIVE_MD_HEADLESS_HOLD_Y") == "1";
            bool mdHoldZ = Environment.GetEnvironmentVariable("EUTHERDRIVE_MD_HEADLESS_HOLD_Z") == "1";
            bool mdHoldMode = Environment.GetEnvironmentVariable("EUTHERDRIVE_MD_HEADLESS_HOLD_MODE") == "1";
            bool mdInputEnabled =
                mdHoldUp || mdHoldDown || mdHoldLeft || mdHoldRight || mdHoldA || mdHoldB ||
                mdHoldC || mdHoldStart || mdHoldX || mdHoldY || mdHoldZ || mdHoldMode;
            if (mdInputEnabled)
            {
                Console.WriteLine(
                    $"[HEADLESS-MD-INPUT] hold up={mdHoldUp} down={mdHoldDown} left={mdHoldLeft} right={mdHoldRight} " +
                    $"a={mdHoldA} b={mdHoldB} c={mdHoldC} start={mdHoldStart} x={mdHoldX} y={mdHoldY} z={mdHoldZ} mode={mdHoldMode}");
            }
            bool trace32XFrames = Environment.GetEnvironmentVariable("EUTHERDRIVE_HEADLESS_TRACE_FRAMES") == "1";
            bool trace32XWords = Environment.GetEnvironmentVariable("EUTHERDRIVE_HEADLESS_TRACE_32X_WORDS") == "1";

            var dumpFrames = new HashSet<int>();
            string? dumpFramesRaw = Environment.GetEnvironmentVariable("EUTHERDRIVE_HEADLESS_DUMP_FRAMES");
            if (!string.IsNullOrWhiteSpace(dumpFramesRaw))
            {
                foreach (string part in dumpFramesRaw.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (int.TryParse(part.Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int frameIndex))
                        dumpFrames.Add(frameIndex);
                }
            }
            int? dumpFrameSingle = ParseOptionalIntEnv("EUTHERDRIVE_HEADLESS_DUMP_FRAME");
            if (dumpFrameSingle.HasValue)
                dumpFrames.Add(dumpFrameSingle.Value);

            for (int frame = 0; frame < framesToRun; frame++)
            {
                if (mdInputEnabled)
                {
                    adapter.SetInputState(
                        up: mdHoldUp,
                        down: mdHoldDown,
                        left: mdHoldLeft,
                        right: mdHoldRight,
                        a: mdHoldA,
                        b: mdHoldB,
                        c: mdHoldC,
                        start: mdHoldStart,
                        x: mdHoldX,
                        y: mdHoldY,
                        z: mdHoldZ,
                        mode: mdHoldMode,
                        padType: PadType.SixButton);
                }
                adapter.StepFrame();
                ReadOnlySpan<byte> frameBuffer = adapter.GetFrameBuffer(out int fbWidth, out int fbHeight, out int fbStride);
                ulong videoFingerprint = ComputeFrameFingerprint(frameBuffer, fbWidth, fbHeight, fbStride);
                if (!videoFingerprintValid)
                {
                    videoFingerprintValid = true;
                    lastVideoFingerprint = videoFingerprint;
                    videoUnchangedFrames = 0;
                }
                else if (videoFingerprint == lastVideoFingerprint)
                {
                    videoUnchangedFrames++;
                }
                else
                {
                    lastVideoFingerprint = videoFingerprint;
                    videoUnchangedFrames = 0;
                }
                if (forceZ80DumpFrame.HasValue && frame == forceZ80DumpFrame.Value && adapter is MdTracerAdapter mdAdapter)
                {
                    mdAdapter.ForceDumpZ80($"forced frame={frame}", forceZ80DumpExtra, forceZ80DumpPath);
                }
                uint m68kPc = adapter.GetM68kPc();
                ushort z80Pc = adapter.GetZ80Pc();
                long cycles = adapter.GetSystemCycles();
                if (m68kPc == lastM68kPc && z80Pc == lastZ80Pc && cycles == lastCycles)
                {
                    stableFrames++;
                }
                else
                {
                    stableFrames = 0;
                    lastM68kPc = m68kPc;
                    lastZ80Pc = z80Pc;
                    lastCycles = cycles;
                }
                if (hangFrames > 0 && stableFrames >= hangFrames)
                {
                    Console.Error.WriteLine(
                        $"[HEADLESS-HANG] frame={frame} stableFrames={stableFrames} m68k=0x{m68kPc:X6} z80=0x{z80Pc:X4} cycles={cycles}");
                    string ppmPath = Path.Combine(dumpDir, $"headless_hang_frame{frame}.ppm");
                    adapter.DumpFrameBufferToPpm(ppmPath);
                    Console.Error.WriteLine($"[HEADLESS-HANG] Dumped frame to {ppmPath}");
                    hangTriggered = true;
                    break;
                }
                if (videoStallFrames > 0 && videoUnchangedFrames >= videoStallFrames)
                {
                    Console.Error.WriteLine(
                        $"[HEADLESS-VIDEO-STALL] frame={frame} unchangedFrames={videoUnchangedFrames} " +
                        $"m68k=0x{m68kPc:X6} z80=0x{z80Pc:X4} cycles={cycles} fp=0x{videoFingerprint:X16}");
                    string ppmPath = Path.Combine(dumpDir, $"headless_video_stall_frame{frame}.ppm");
                    adapter.DumpFrameBufferToPpm(ppmPath);
                    Console.Error.WriteLine($"[HEADLESS-VIDEO-STALL] Dumped frame to {ppmPath}");
                    hangTriggered = true;
                    break;
                }
                if (audioEngine != null)
                {
                    long currentCycles = adapter.GetSystemCycles();
                    if (audioLastSystemCycles == 0)
                    {
                        audioLastSystemCycles = currentCycles;
                    }
                    else
                    {
                        long deltaCycles = currentCycles - audioLastSystemCycles;
                        if (deltaCycles > 0)
                        {
                            audioLastSystemCycles = currentCycles;
                            double m68kClockHz = adapter.GetM68kClockHz();
                            if (m68kClockHz > 0)
                            {
                                audioFrameAccumulator += deltaCycles * (audioSampleRate / m68kClockHz);
                                int frames = (int)audioFrameAccumulator;
                                if (frames > 0)
                                {
                                    audioFrameAccumulator -= frames;
                                    int loops = 0;
                                    while (frames > 0 && loops < 32)
                                    {
                                        int chunk = frames < audioBufferChunkFrames ? frames : audioBufferChunkFrames;
                                        var audio = adapter.GetAudioBufferForFrames(chunk, out int sampleRate, out int channels);
                                        if (!audio.IsEmpty && sampleRate == audioSampleRate && channels == audioChannels)
                                        {
                                            audioEngine.Submit(audio);
                                            frames -= chunk;
                                        }
                                        else
                                        {
                                            break;
                                        }
                                        loops++;
                                    }
                                }
                            }
                        }
                    }
                }
                if (audioEngine != null && audioThrottle)
                {
                    int waitLoops = 0;
                    while (audioEngine.BufferedFrames > audioTargetFrames && waitLoops < 200)
                    {
                        Thread.Sleep(1);
                        waitLoops++;
                    }
                }
                if (trace32XFrames)
                {
                    var stats = GetFrameStats(frameBuffer, fbWidth, fbHeight, fbStride);
                    Console.WriteLine(
                        $"[HEADLESS] Frame {frame}: savestate_fb_has_content={stats.HasContent} " +
                        $"nonzero_pixels={stats.NonZeroPixels} first_nonzero=({stats.FirstX},{stats.FirstY}) " +
                        $"m68k=0x{m68kPc:X6} z80=0x{z80Pc:X4} " +
                        $"mpc=0x{adapter.Debug32XMasterProgramCounter ?? 0:X8} spc=0x{adapter.Debug32XSlaveProgramCounter ?? 0:X8} " +
                        $"fp=0x{videoFingerprint:X16} unchanged={videoUnchangedFrames}");
                    if (trace32XWords)
                    {
                        Console.WriteLine(
                            $"[HEADLESS] Frame {frame}: savestate_32x_words m={adapter.Debug32XMasterWords ?? string.Empty} " +
                            $"s={adapter.Debug32XSlaveWords ?? string.Empty} comm={adapter.Debug32XCommPorts ?? string.Empty}");
                    }
                }
                Console.WriteLine($"[HEADLESS] Frame {frame} completed");

                if (frame == 0 || frame == 5 || frame == 10 || dumpFrames.Contains(frame))
                {
                    string ppmPath = Path.Combine(dumpDir, $"headless_frame{frame}.ppm");
                    adapter.DumpFrameBufferToPpm(ppmPath);
                    Console.WriteLine($"[HEADLESS] Dumped frame {frame} to {ppmPath}");
                    if (dump32XLayer)
                        adapter.Dump32XLayerToPpm(Path.Combine(dumpDir, $"headless_frame{frame}_32x.ppm"));
                    if (dump32XOtherLayer)
                        adapter.Dump32XOtherLayerToPpm(Path.Combine(dumpDir, $"headless_frame{frame}_32x_other.ppm"));
                }
            }

            Console.WriteLine("[HEADLESS] Framebuffer AFTER running:");
            adapter.FrameBufferHasContent();
            adapter.DumpFrameBufferToPpm(Path.Combine(dumpDir, "headless_output.ppm"));
            if (dump32XLayer)
                adapter.Dump32XLayerToPpm(Path.Combine(dumpDir, "headless_output_32x.ppm"));
            if (dump32XOtherLayer)
                adapter.Dump32XOtherLayerToPpm(Path.Combine(dumpDir, "headless_output_32x_other.ppm"));

            Console.WriteLine($"[HEADLESS] Completed {framesToRun} frames");
            return hangTriggered ? 2 : 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[HEADLESS-ERROR] {ex.Message}");
            Console.Error.WriteLine(ex.StackTrace);
            return 1;
        }
    }

    private static ulong ComputeFrameFingerprint(ReadOnlySpan<byte> fb, int width, int height, int stride)
    {
        if (fb.IsEmpty || width <= 0 || height <= 0 || stride <= 0)
            return 0;

        // FNV-1a over a sparse grid keeps overhead low while reliably catching frozen output.
        const ulong offset = 1469598103934665603UL;
        const ulong prime = 1099511628211UL;
        ulong hash = offset;

        int stepY = Math.Max(1, height / 24);
        int stepX = Math.Max(1, width / 32);
        for (int y = 0; y < height; y += stepY)
        {
            int row = y * stride;
            for (int x = 0; x < width; x += stepX)
            {
                int i = row + (x * 4);
                if (i + 3 >= fb.Length)
                    continue;
                hash ^= fb[i];
                hash *= prime;
                hash ^= fb[i + 1];
                hash *= prime;
                hash ^= fb[i + 2];
                hash *= prime;
                hash ^= fb[i + 3];
                hash *= prime;
            }
        }

        return hash;
    }

    private static int RunFromRawState(string romPath, string rawStatePath, int framesToRun)
    {
        if (!File.Exists(romPath))
        {
            Console.Error.WriteLine($"Error: ROM file not found: {romPath}");
            return 1;
        }

        if (!File.Exists(rawStatePath))
        {
            Console.Error.WriteLine($"Error: raw state file not found: {rawStatePath}");
            return 1;
        }

        Console.WriteLine($"[HEADLESS] Loading ROM: {romPath}");
        Console.WriteLine($"[HEADLESS] Loading raw state: {rawStatePath}");
        Console.WriteLine($"[HEADLESS] Running {framesToRun} frames from raw state");

        try
        {
            string dumpDir = Environment.GetEnvironmentVariable("EUTHERDRIVE_HEADLESS_DUMP_DIR")
                ?? Path.Combine(Directory.GetCurrentDirectory(), "logs");
            Directory.CreateDirectory(dumpDir);

            string? coreOverride = Environment.GetEnvironmentVariable("EUTHERDRIVE_HEADLESS_CORE");
            bool usePsx = string.Equals(coreOverride, "psx", StringComparison.OrdinalIgnoreCase)
                || string.Equals(coreOverride, "ps1", StringComparison.OrdinalIgnoreCase)
                || string.Equals(coreOverride, "playstation", StringComparison.OrdinalIgnoreCase)
                || (string.IsNullOrEmpty(coreOverride) && IsPsxRomPath(romPath));
            bool use32X = string.Equals(coreOverride, "32x", StringComparison.OrdinalIgnoreCase)
                || string.Equals(coreOverride, "s32x", StringComparison.OrdinalIgnoreCase)
                || string.Equals(coreOverride, "sega32x", StringComparison.OrdinalIgnoreCase)
                || (string.IsNullOrEmpty(coreOverride) && Is32XRomPath(romPath));
            bool useSegaCd = string.Equals(coreOverride, "segacd", StringComparison.OrdinalIgnoreCase)
                || string.Equals(coreOverride, "sega-cd", StringComparison.OrdinalIgnoreCase)
                || string.Equals(coreOverride, "mega-cd", StringComparison.OrdinalIgnoreCase)
                || (string.IsNullOrEmpty(coreOverride) && IsSegaCdRomPath(romPath));
            bool usePce = string.Equals(coreOverride, "pce", StringComparison.OrdinalIgnoreCase)
                || string.Equals(coreOverride, "pcecd", StringComparison.OrdinalIgnoreCase)
                || string.Equals(coreOverride, "pcengine", StringComparison.OrdinalIgnoreCase)
                || (string.IsNullOrEmpty(coreOverride) && IsPceRomPath(romPath) && !IsSegaCdRomPath(romPath));
            bool useHshavoc = string.Equals(coreOverride, "hshavoc", StringComparison.OrdinalIgnoreCase)
                || string.Equals(coreOverride, "high-seas-havoc", StringComparison.OrdinalIgnoreCase)
                || string.Equals(coreOverride, "dataeast-hshavoc", StringComparison.OrdinalIgnoreCase)
                || (string.IsNullOrEmpty(coreOverride) && HshavocAdapter.IsSupportedArchive(romPath));

            if (useHshavoc)
            {
                using var hshavoc = new HshavocAdapter();
                hshavoc.LoadRom(romPath);

                using (var fs = new FileStream(rawStatePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var reader = new BinaryReader(fs))
                {
                    hshavoc.LoadState(reader);
                }

                ReadOnlySpan<byte> fbBefore = hshavoc.GetFrameBuffer(out int wBefore, out int hBefore, out int sBefore);
                var statsBefore = GetFrameStats(fbBefore, wBefore, hBefore, sBefore);
                Console.WriteLine(
                    $"[HEADLESS] HSHavoc raw state before fb_has_content={statsBefore.HasContent} nonzero_pixels={statsBefore.NonZeroPixels} " +
                    $"first_nonzero=({statsBefore.FirstX},{statsBefore.FirstY}) pc=0x{hshavoc.GetM68kPc():X6} z80=0x{hshavoc.GetZ80Pc():X4} " +
                    $"sr=0x{hshavoc.GetM68kStatusRegister():X4} regs={FormatHshavocRegisters(hshavoc)} " +
                    $"vdp={hshavoc.GetVdpDisplayStatus()} op={FormatHshavocWords(hshavoc)}");
                DumpBgraToPpm(fbBefore, wBefore, hBefore, sBefore, Path.Combine(dumpDir, "headless_hshavoc_raw_state_before.ppm"));

                for (int frame = 0; frame < framesToRun; frame++)
                {
                    hshavoc.RunFrame();
                    ReadOnlySpan<byte> fb = hshavoc.GetFrameBuffer(out int w, out int h, out int s);
                    var stats = GetFrameStats(fb, w, h, s);
                    ulong fingerprint = ComputeFrameFingerprint(fb, w, h, s);
                    if (frame == 0 || frame == 1 || frame == 2 || frame == 5 || frame == 10 || ((frame + 1) % 60) == 0)
                    {
                        Console.WriteLine(
                            $"[HEADLESS] HSHavoc raw state frame {frame}: fb_has_content={stats.HasContent} nonzero_pixels={stats.NonZeroPixels} " +
                            $"first_nonzero=({stats.FirstX},{stats.FirstY}) pc=0x{hshavoc.GetM68kPc():X6} z80=0x{hshavoc.GetZ80Pc():X4} " +
                            $"sr=0x{hshavoc.GetM68kStatusRegister():X4} regs={FormatHshavocRegisters(hshavoc)} " +
                            $"vdp={hshavoc.GetVdpDisplayStatus()} op={FormatHshavocWords(hshavoc)} fp=0x{fingerprint:X16}");
                        DumpBgraToPpm(fb, w, h, s, Path.Combine(dumpDir, $"headless_hshavoc_raw_state_frame{frame}.ppm"));
                    }
                }

                ReadOnlySpan<byte> fbOut = hshavoc.GetFrameBuffer(out int wOut, out int hOut, out int sOut);
                string outPathHshavoc = Path.Combine(dumpDir, "headless_output.ppm");
                DumpBgraToPpm(fbOut, wOut, hOut, sOut, outPathHshavoc);
                Console.WriteLine($"[HEADLESS] HSHavoc raw state framebuffer dumped to {outPathHshavoc}");
                return 0;
            }

            if (use32X)
            {
                var s32x = new Sega32XAdapter();
                s32x.LoadRom(romPath);

                using (var fs = new FileStream(rawStatePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var reader = new BinaryReader(fs))
                {
                    s32x.LoadState(reader);
                }

                for (int frame = 0; frame < framesToRun; frame++)
                {
                    s32x.RunFrame();
                    Console.WriteLine(
                        $"[HEADLESS] Frame {frame} completed (32X mpc=0x{s32x.DebugMasterProgramCounter ?? 0:X8} spc=0x{s32x.DebugSlaveProgramCounter ?? 0:X8})");
                }

                ReadOnlySpan<byte> fbOut = s32x.GetFrameBuffer(out int wOut, out int hOut, out int sOut);
                DumpBgraToPpm(fbOut, wOut, hOut, sOut, Path.Combine(dumpDir, "headless_output.ppm"));
                return 0;
            }

            if (usePsx)
            {
                ConfigurePsxAdapterFromEnv();

                var psx = new PsxAdapter();
                psx.LoadRom(romPath);

                using (var fs = new FileStream(rawStatePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var reader = new BinaryReader(fs))
                {
                    psx.LoadState(reader);
                }

                for (int frame = 0; frame < framesToRun; frame++)
                {
                    psx.RunFrame();
                    Console.WriteLine($"[HEADLESS] Frame {frame} completed");
                    if (frame == 0 || frame == 5 || frame == 10)
                    {
                        ReadOnlySpan<byte> fb = psx.GetFrameBuffer(out int w, out int h, out int s);
                        string ppmPath = Path.Combine(dumpDir, $"headless_frame{frame}.ppm");
                        DumpBgraToPpm(fb, w, h, s, ppmPath);
                        Console.WriteLine($"[HEADLESS] Dumped frame {frame} to {ppmPath}");
                    }
                }

                ReadOnlySpan<byte> fbOut = psx.GetFrameBuffer(out int wOut, out int hOut, out int sOut);
                string outPathPsx = Path.Combine(dumpDir, "headless_output.ppm");
                DumpBgraToPpm(fbOut, wOut, hOut, sOut, outPathPsx);
                Console.WriteLine($"[HEADLESS] Framebuffer dumped to {outPathPsx}");
                return 0;
            }

            if (usePce)
            {
                var pce = new PceCdAdapter();
                pce.LoadRom(romPath);

                using (var fs = new FileStream(rawStatePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var reader = new BinaryReader(fs))
                {
                    pce.LoadState(reader);
                }

                for (int frame = 0; frame < framesToRun; frame++)
                {
                    pce.RunFrame();
                    Console.WriteLine($"[HEADLESS] Frame {frame} completed");
                    if (frame == 0 || frame == 5 || frame == 10)
                    {
                        ReadOnlySpan<byte> fb = pce.GetFrameBuffer(out int w, out int h, out int s);
                        string ppmPath = Path.Combine(dumpDir, $"headless_frame{frame}.ppm");
                        DumpBgraToPpm(fb, w, h, s, ppmPath);
                        Console.WriteLine($"[HEADLESS] Dumped frame {frame} to {ppmPath}");
                    }
                }

                ReadOnlySpan<byte> fbOut = pce.GetFrameBuffer(out int wOut, out int hOut, out int sOut);
                string outPathPce = Path.Combine(dumpDir, "headless_output.ppm");
                DumpBgraToPpm(fbOut, wOut, hOut, sOut, outPathPce);
                Console.WriteLine($"[HEADLESS] Framebuffer dumped to {outPathPce}");
                return 0;
            }

            if (useSegaCd)
            {
                var scd = new SegaCdAdapter();
                scd.LoadRom(romPath);

                using (var fs = new FileStream(rawStatePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var reader = new BinaryReader(fs))
                {
                    scd.LoadState(reader);
                }

                for (int frame = 0; frame < framesToRun; frame++)
                {
                    scd.RunFrame();
                    Console.WriteLine($"[HEADLESS] Frame {frame} completed");
                    if (frame == 0 || frame == 5 || frame == 10)
                    {
                        ReadOnlySpan<byte> fb = scd.GetFrameBuffer(out int w, out int h, out int s);
                        string ppmPath = Path.Combine(dumpDir, $"headless_frame{frame}.ppm");
                        DumpBgraToPpm(fb, w, h, s, ppmPath);
                        Console.WriteLine($"[HEADLESS] Dumped frame {frame} to {ppmPath}");
                    }
                }

                ReadOnlySpan<byte> fbOut = scd.GetFrameBuffer(out int wOut, out int hOut, out int sOut);
                string outPathScd = Path.Combine(dumpDir, "headless_output.ppm");
                DumpBgraToPpm(fbOut, wOut, hOut, sOut, outPathScd);
                Console.WriteLine($"[HEADLESS] Framebuffer dumped to {outPathScd}");
                return 0;
            }

            var adapter = new MdTracerAdapter();
            adapter.LoadRom(romPath);

            using (var fs = new FileStream(rawStatePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var reader = new BinaryReader(fs))
            {
                adapter.LoadState(reader);
            }

            for (int frame = 0; frame < framesToRun; frame++)
            {
                adapter.StepFrame();
                Console.WriteLine($"[HEADLESS] Frame {frame} completed");
                if (frame == 0 || frame == 5 || frame == 10)
                {
                    string ppmPath = Path.Combine(dumpDir, $"headless_frame{frame}.ppm");
                    adapter.DumpFrameBufferToPpm(ppmPath);
                    Console.WriteLine($"[HEADLESS] Dumped frame {frame} to {ppmPath}");
                }
            }

            string outPath = Path.Combine(dumpDir, "headless_output.ppm");
            adapter.DumpFrameBufferToPpm(outPath);
            Console.WriteLine($"[HEADLESS] Framebuffer dumped to {outPath}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[HEADLESS-ERROR] {ex.GetType().Name}: {ex.Message}");
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
            if (IsEnvEnabled("EUTHERDRIVE_HEADLESS_IGNORE_SAVESTATE_ROM_HASH"))
            {
                Console.WriteLine("[HEADLESS] Savestate ROM hash mismatch ignored by EUTHERDRIVE_HEADLESS_IGNORE_SAVESTATE_ROM_HASH=1");
            }
            else
            {
                error = "Savestate ROM hash mismatch.";
                return null;
            }
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

    private static FileStream? OpenOptionalRawAudioDump(string dumpDir, string fileName)
    {
        if (Environment.GetEnvironmentVariable("EUTHERDRIVE_HEADLESS_DUMP_AUDIO") != "1")
            return null;

        Directory.CreateDirectory(dumpDir);
        string path = Path.Combine(dumpDir, fileName);
        Console.WriteLine($"[HEADLESS] Raw audio dump: {path}");
        return File.Open(path, FileMode.Create, FileAccess.Write, FileShare.Read);
    }

    private static void WriteRawAudio(FileStream? stream, ReadOnlySpan<short> audio)
    {
        if (stream == null || audio.IsEmpty)
            return;

        Span<byte> scratch = stackalloc byte[4096];
        int offset = 0;
        while (offset < audio.Length)
        {
            int samples = Math.Min(audio.Length - offset, scratch.Length / sizeof(short));
            for (int i = 0; i < samples; i++)
            {
                short sample = audio[offset + i];
                scratch[i * 2] = (byte)sample;
                scratch[i * 2 + 1] = (byte)(sample >> 8);
            }
            stream.Write(scratch[..(samples * sizeof(short))]);
            offset += samples;
        }
    }

    private static int AudioPeak(ReadOnlySpan<short> audio)
    {
        int peak = 0;
        foreach (short sample in audio)
        {
            int value = sample;
            if (value < 0)
                value = -value;
            if (value > peak)
                peak = value;
        }
        return peak;
    }

    private static int CountNonZeroAudioSamples(ReadOnlySpan<short> audio)
    {
        int count = 0;
        foreach (short sample in audio)
        {
            if (sample != 0)
                count++;
        }

        return count;
    }

    private static void ConfigureConsoleLogging()
    {
        if (ShouldSilenceConsole())
        {
            bool verbose = IsVerboseHeadless();
            bool keepStdErr = IsEnvEnabled("EUTHERDRIVE_SCD_PROFILE") && !verbose;
            Console.SetOut(TextWriter.Null);
            if (!keepStdErr)
                Console.SetError(TextWriter.Null);
            Trace.Listeners.Clear();
            Trace.AutoFlush = false;
        }
    }

    private static bool ShouldSilenceConsole()
    {
        // Headless mode is verbose by default unless explicitly disabled.
        if (IsVerboseHeadless()) {
            return false;
        }

        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is not string key)
            {
                continue;
            }

            if (key.StartsWith("EUTHERDRIVE_TRACE_", StringComparison.OrdinalIgnoreCase)
                && IsEnvEnabled(key))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsVerboseHeadless()
    {
        if (IsEnvEnabled("EUTHERDRIVE_LOG_VERBOSE") || IsEnvEnabled("EUTHERDRIVE_TRACE_VERBOSE"))
            return true;

        if (IsEnvDisabled("EUTHERDRIVE_LOG_VERBOSE") || IsEnvDisabled("EUTHERDRIVE_TRACE_VERBOSE"))
            return false;

        return true;
    }

    private static bool IsEnvEnabled(string key)
    {
        var value = Environment.GetEnvironmentVariable(key);
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        value = value.Trim();
        return value == "1"
            || value.Equals("true", StringComparison.OrdinalIgnoreCase)
            || value.Equals("yes", StringComparison.OrdinalIgnoreCase)
            || value.Equals("on", StringComparison.OrdinalIgnoreCase);
    }

    private static void SetEnvDefault(string key, string value)
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(key)))
            Environment.SetEnvironmentVariable(key, value);
    }

    private static bool IsEnvDisabled(string key)
    {
        var value = Environment.GetEnvironmentVariable(key);
        if (string.IsNullOrWhiteSpace(value))
            return false;

        value = value.Trim();
        return value == "0"
            || value.Equals("false", StringComparison.OrdinalIgnoreCase)
            || value.Equals("no", StringComparison.OrdinalIgnoreCase)
            || value.Equals("off", StringComparison.OrdinalIgnoreCase);
    }

    private static void ConfigurePsxAdapterFromEnv()
    {
        PsxAdapter.BiosPath = Environment.GetEnvironmentVariable("EUTHERDRIVE_PSX_BIOS");
        PsxAdapter.SubchannelPatchPath = Environment.GetEnvironmentVariable("EUTHERDRIVE_PSX_SBI");
        PsxAdapter.AnalogControllerEnabled = IsEnvEnabled("EUTHERDRIVE_PSX_ANALOG_PAD");
        PsxAdapter.FastLoadEnabled = IsEnvEnabled("EUTHERDRIVE_PSX_FAST_LOAD");
        PsxAdapter.SuperFastBootEnabled = IsEnvEnabled("EUTHERDRIVE_PSX_SUPER_FAST_BOOT");
        PsxAdapter.VideoStandardMode = ParsePsxVideoStandardModeEnv("EUTHERDRIVE_PSX_VIDEO_STANDARD");
    }

    private static PsxVideoStandardMode ParsePsxVideoStandardModeEnv(string key)
    {
        string? raw = Environment.GetEnvironmentVariable(key);
        if (string.IsNullOrWhiteSpace(raw))
            return PsxVideoStandardMode.Auto;

        return raw.Trim().ToUpperInvariant() switch
        {
            "PAL" => PsxVideoStandardMode.PAL,
            "NTSC" => PsxVideoStandardMode.NTSC,
            _ => PsxVideoStandardMode.Auto,
        };
    }

    private static HashSet<int> ParseFrameSetEnv(params string[] names)
    {
        var result = new HashSet<int>();
        foreach (string name in names)
        {
            string? raw = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrWhiteSpace(raw))
                continue;

            foreach (string token in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out int frame) && frame >= 0)
                    result.Add(frame);
            }
        }

        return result;
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

    private static int[] ParseOptionalHexAddrEnv(string name)
    {
        string? raw = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(raw))
            return Array.Empty<int>();

        var result = new List<int>();
        foreach (string part in raw.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string token = part.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? part[2..] : part;
            if (int.TryParse(token, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out int value))
                result.Add(value & 0xFFFFFF);
        }

        return result.ToArray();
    }

    private static bool ShouldTracePsxFrame(int frame, bool traceEnabled, bool traceEveryFrame, int startFrame, int endFrame)
    {
        if (!traceEnabled)
            return false;
        if (frame < startFrame || frame > endFrame)
            return false;
        if (traceEveryFrame)
            return true;
        return frame < 60 || (frame % 60) == 0;
    }

    private static bool ShouldTraceSnesPerfFrame(int frame, bool traceEnabled, bool traceEveryFrame, int startFrame, int endFrame)
    {
        if (!traceEnabled)
            return false;
        if (frame < startFrame || frame > endFrame)
            return false;
        if (traceEveryFrame)
            return true;
        return frame < 10 || (frame % 60) == 0;
    }

    private static string GetSavestateRoot()
    {
        string? raw = Environment.GetEnvironmentVariable("EUTHERDRIVE_SAVESTATE_DIR");
        if (!string.IsNullOrWhiteSpace(raw))
            return raw;
        return Path.Combine(Directory.GetCurrentDirectory(), "savestates");
    }

    private static int GetHeadlessAudioTargetFrames(int sampleRate)
    {
        string? raw = Environment.GetEnvironmentVariable("EUTHERDRIVE_HEADLESS_AUDIO_TARGET_MS");
        if (!string.IsNullOrWhiteSpace(raw)
            && int.TryParse(raw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int ms)
            && ms > 0)
        {
            return (int)(sampleRate * (ms / 1000.0));
        }
        return (int)(sampleRate * 0.10);
    }

    private static void EnableScdDebugLogging()
    {
        // Enable verbose Sega CD logging for headless debug runs.
        SetEnv("EUTHERDRIVE_SCD_LOG_CDD", "1");
        SetEnv("EUTHERDRIVE_SCD_LOG_CDDCMD", "1");
        SetEnv("EUTHERDRIVE_SCD_LOG_CDDSTATUS", "1");
        SetEnv("EUTHERDRIVE_SCD_LOG_CDC", "1");
        SetEnv("EUTHERDRIVE_SCD_LOG_SUBINT", "1");
        SetEnv("EUTHERDRIVE_SCD_LOG_SUBREAD", "1");
        SetEnv("EUTHERDRIVE_SCD_LOG_SUBREG", "1");
        SetEnv("EUTHERDRIVE_SCD_LOG_SUBBUS", "1");
        SetEnv("EUTHERDRIVE_SCD_LOG_MAINREG", "1");
        SetEnv("EUTHERDRIVE_SCD_LOG_MAINREG_READ", "1");
        SetEnv("EUTHERDRIVE_SCD_LOG_MAINREG_PROBE", "1");
        SetEnv("EUTHERDRIVE_SCD_LOG_A12001_PC", "1");
        SetEnv("EUTHERDRIVE_SCD_TRACE_TIMER", "1");
        SetEnv("EUTHERDRIVE_TRACE_VERBOSE", "1");
    }

    private static void SetEnv(string key, string value)
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(key)))
            Environment.SetEnvironmentVariable(key, value);
    }
}

internal sealed class M68kTestBus : IBusInterface
{
    private readonly byte[] _mem = new byte[0x0100_0000];

    public void Clear()
    {
        Array.Clear(_mem, 0, _mem.Length);
    }

    public void WriteByte(uint address, byte value)
    {
        _mem[address & 0x00FF_FFFF] = value;
    }

    public byte ReadByte(uint address)
    {
        return _mem[address & 0x00FF_FFFF];
    }

    public ushort ReadWord(uint address)
    {
        uint a = address & 0x00FF_FFFF;
        return (ushort)((_mem[a] << 8) | _mem[(a + 1) & 0x00FF_FFFF]);
    }

    public uint ReadLong(uint address)
    {
        uint a = address & 0x00FF_FFFF;
        return (uint)(_mem[a] << 24)
            | (uint)(_mem[(a + 1) & 0x00FF_FFFF] << 16)
            | (uint)(_mem[(a + 2) & 0x00FF_FFFF] << 8)
            | _mem[(a + 3) & 0x00FF_FFFF];
    }

    public void WriteWord(uint address, ushort value)
    {
        uint a = address & 0x00FF_FFFF;
        _mem[a] = (byte)(value >> 8);
        _mem[(a + 1) & 0x00FF_FFFF] = (byte)(value & 0xFF);
    }

    public void WriteLong(uint address, uint value)
    {
        uint a = address & 0x00FF_FFFF;
        _mem[a] = (byte)(value >> 24);
        _mem[(a + 1) & 0x00FF_FFFF] = (byte)((value >> 16) & 0xFF);
        _mem[(a + 2) & 0x00FF_FFFF] = (byte)((value >> 8) & 0xFF);
        _mem[(a + 3) & 0x00FF_FFFF] = (byte)(value & 0xFF);
    }

    public byte InterruptLevel() => 0;
    public void AcknowledgeInterrupt(byte level) { }
    public bool Reset() => false;
    public bool Halt() => false;
    public BusSignals Signals => new(false);
    public ushort CurrentOpcode => 0;
}

internal sealed class M68kTestRunner
{
    private readonly M68000 _cpu = M68000.CreateBuilder().AllowTasWrites(true).Name("M68K-TEST").Build();
    private readonly M68kTestBus _bus = new();

    public int RunPath(string path, bool logEach)
    {
        if (File.Exists(path))
            return RunFile(path, logEach);
        if (Directory.Exists(path))
            return RunDirectory(path, logEach);
        Console.Error.WriteLine($"[M68K-TEST] path not found: {path}");
        return 1;
    }

    private int RunDirectory(string dir, bool logEach)
    {
        var files = Directory.EnumerateFiles(dir, "*.json*", SearchOption.AllDirectories)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (files.Length == 0)
        {
            Console.Error.WriteLine($"[M68K-TEST] no json/json.gz files in {dir}");
            return 1;
        }

        int total = 0;
        int failed = 0;
        foreach (var file in files)
        {
            var (t, f) = RunFileInternal(file, logEach);
            total += t;
            failed += f;
        }
        Console.WriteLine($"[M68K-TEST] done: failed {failed} / {total}");
        return failed == 0 ? 0 : 2;
    }

    private int RunFile(string file, bool logEach)
    {
        var (total, failed) = RunFileInternal(file, logEach);
        Console.WriteLine($"[M68K-TEST] {Path.GetFileName(file)} failed {failed} / {total}");
        return failed == 0 ? 0 : 2;
    }

    private (int Total, int Failed) RunFileInternal(string file, bool logEach)
    {
        var tests = LoadTests(file);
        int total = tests.Count;
        int failed = 0;
        for (int i = 0; i < tests.Count; i++)
        {
            if (!RunSingle(tests[i], logEach))
                failed++;
        }
        return (total, failed);
    }

    private bool RunSingle(M68kTest test, bool logEach)
    {
        _bus.Clear();
        foreach (var entry in test.Initial.Ram)
        {
            if (entry.Length < 2)
                continue;
            _bus.WriteByte(entry[0], (byte)entry[1]);
        }

        ushort prefetch = test.Initial.Prefetch.Length > 0 ? test.Initial.Prefetch[0] : (ushort)0;
        var state = new M68000.M68000State(
            test.Initial.Data, test.Initial.Address, test.Initial.Usp, test.Initial.Ssp, test.Initial.Sr, test.Initial.Pc, prefetch);
        _cpu.SetState(state);

        for (int i = 0; i < test.Length; i++)
            _cpu.ExecuteInstruction(_bus);

        var finalState = _cpu.GetState();
        bool ok = CompareState(test, _bus, finalState, out string diff);
        if (!ok && logEach)
            Console.WriteLine($"[M68K-TEST][FAIL] {test.Name}\n{diff}");
        return ok;
    }

    private static bool CompareState(M68kTest test, M68kTestBus bus, M68000.M68000State actual, out string diff)
    {
        var sb = new StringBuilder();
        bool ok = true;

        void Check(string name, uint a, uint e)
        {
            if (a != e)
            {
                ok = false;
                sb.AppendLine($"  {name}: actual=0x{a:X8} expected=0x{e:X8}");
            }
        }

        for (int i = 0; i < 8; i++)
            Check($"d{i}", actual.Data[i], test.Final.Data[i]);
        for (int i = 0; i < 7; i++)
            Check($"a{i}", actual.Address[i], test.Final.Address[i]);
        Check("usp", actual.Usp, test.Final.Usp);
        Check("ssp", actual.Ssp, test.Final.Ssp);
        Check("pc", actual.Pc, test.Final.Pc);
        Check("sr", actual.Sr, test.Final.Sr);

        foreach (var entry in test.Final.Ram)
        {
            if (entry.Length < 2)
                continue;
            uint addr = entry[0];
            byte expected = (byte)entry[1];
            byte actualByte = bus.ReadByte(addr);
            if (actualByte != expected)
            {
                ok = false;
                sb.AppendLine($"  mem[0x{addr:X8}]: actual=0x{actualByte:X2} expected=0x{expected:X2}");
            }
        }

        diff = sb.ToString();
        return ok;
    }

    private List<M68kTest> LoadTests(string file)
    {
        using Stream stream = OpenTestStream(file);
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var tests = JsonSerializer.Deserialize<List<M68kTest>>(stream, options);
        return tests ?? new List<M68kTest>();
    }

    private static Stream OpenTestStream(string file)
    {
        var fs = File.OpenRead(file);
        if (file.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
            return new GZipStream(fs, CompressionMode.Decompress);
        return fs;
    }
}

internal sealed class M68kTest
{
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("initial")] public M68kState Initial { get; set; } = new();
    [JsonPropertyName("final")] public M68kState Final { get; set; } = new();
    [JsonPropertyName("length")] public int Length { get; set; }
}

internal sealed class M68kState
{
    [JsonPropertyName("d0")] public uint D0 { get; set; }
    [JsonPropertyName("d1")] public uint D1 { get; set; }
    [JsonPropertyName("d2")] public uint D2 { get; set; }
    [JsonPropertyName("d3")] public uint D3 { get; set; }
    [JsonPropertyName("d4")] public uint D4 { get; set; }
    [JsonPropertyName("d5")] public uint D5 { get; set; }
    [JsonPropertyName("d6")] public uint D6 { get; set; }
    [JsonPropertyName("d7")] public uint D7 { get; set; }
    [JsonPropertyName("a0")] public uint A0 { get; set; }
    [JsonPropertyName("a1")] public uint A1 { get; set; }
    [JsonPropertyName("a2")] public uint A2 { get; set; }
    [JsonPropertyName("a3")] public uint A3 { get; set; }
    [JsonPropertyName("a4")] public uint A4 { get; set; }
    [JsonPropertyName("a5")] public uint A5 { get; set; }
    [JsonPropertyName("a6")] public uint A6 { get; set; }
    [JsonPropertyName("usp")] public uint Usp { get; set; }
    [JsonPropertyName("ssp")] public uint Ssp { get; set; }
    [JsonPropertyName("sr")] public ushort Sr { get; set; }
    [JsonPropertyName("pc")] public uint Pc { get; set; }
    [JsonPropertyName("prefetch")] public ushort[] Prefetch { get; set; } = Array.Empty<ushort>();
    [JsonPropertyName("ram")] public uint[][] Ram { get; set; } = Array.Empty<uint[]>();

    [JsonIgnore] public uint[] Data => new[] { D0, D1, D2, D3, D4, D5, D6, D7 };
    [JsonIgnore] public uint[] Address => new[] { A0, A1, A2, A3, A4, A5, A6 };

}

internal static class M68kTestCli
{
    public static int Run(string path, bool logEach)
    {
        var runner = new M68kTestRunner();
        return runner.RunPath(path, logEach);
    }
}
