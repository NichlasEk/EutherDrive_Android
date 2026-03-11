// Headless test harness for EutherDrive core
// Usage: dotnet run --project EutherDrive.Headless -- /path/to/rom.md [frames]
//        dotnet run --project EutherDrive.Headless -- --test-interlace2
// Default: runs 120 frames

using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.IO.Compression;
using EutherDrive.Core;
using EutherDrive.Core.SegaCd;
using EutherDrive.Core.MdTracerCore;
using EutherDrive.Core.Savestates;
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
            Console.Error.WriteLine("  --load-raw-state: Load raw MdTracer state and run frames");
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
            bool useN64 = string.Equals(coreOverride, "n64", StringComparison.OrdinalIgnoreCase)
                || (string.IsNullOrEmpty(coreOverride) && IsN64RomPath(romPath));
            bool useSegaCd = string.Equals(coreOverride, "segacd", StringComparison.OrdinalIgnoreCase)
                || string.Equals(coreOverride, "scd", StringComparison.OrdinalIgnoreCase)
                || (string.IsNullOrEmpty(coreOverride) && IsSegaCdRomPath(romPath));
            bool usePce = string.Equals(coreOverride, "pce", StringComparison.OrdinalIgnoreCase)
                || string.Equals(coreOverride, "pcecd", StringComparison.OrdinalIgnoreCase)
                || string.Equals(coreOverride, "pcengine", StringComparison.OrdinalIgnoreCase);
            if (string.Equals(coreOverride, "md", StringComparison.OrdinalIgnoreCase))
            {
                useNes = false;
                useSnes = false;
                useN64 = false;
                useSegaCd = false;
                usePce = false;
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
                int[] snesPeekAddrs = ParseOptionalHexAddrEnv("EUTHERDRIVE_TRACE_SNES_PEEK_ADDRS");
                int? sa1SnapshotFrameSavestate = ParseOptionalIntEnv("EUTHERDRIVE_SNES_HEADLESS_SA1_SNAPSHOT_FRAME");

                bool traceSnesFrames = Environment.GetEnvironmentVariable("EUTHERDRIVE_HEADLESS_TRACE_FRAMES") == "1";
                bool traceSnesPpuSnapshot = Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_SNES_PPU_SNAPSHOT") == "1";
                bool traceSpcWindow = Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_SNES_SPC_WINDOW") == "1";
                StreamWriter? snesTraceWriter = null;
                if (traceSnesFrames)
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
                Console.WriteLine("[HEADLESS] Framebuffer BEFORE running:");
                DumpSnesFrame(snes, Path.Combine(dumpDir, "headless_frame0.ppm"), traceSnesFrames);
                TracePeek("before");

                bool prevHasContent = false;
                for (int frame = 0; frame < framesToRun; frame++)
                {
                    bool startPressed = autoStart &&
                        ShouldPressStartPulse(frame, autoStartDelayFrames, autoStartPulseFrames, autoStartPeriodFrames, autoStartPulseCount);
                    snes.SetInputState(
                        up: false,
                        down: false,
                        left: false,
                        right: false,
                        a: false,
                        b: false,
                        x: false,
                        y: false,
                        z: false,
                        c: false,
                        start: startPressed,
                        mode: false,
                        padType: PadType.SixButton);
                    if (autoStartLog && startPressed != lastStartPressed)
                        Console.WriteLine($"[HEADLESS] SNES auto-start start={(startPressed ? 1 : 0)} frame={frame}");
                    lastStartPressed = startPressed;

                    snes.RunFrame();

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

                    if (frame == 0 || frame == 5 || frame == 10)
                    {
                        DumpSnesFrame(snes, Path.Combine(dumpDir, $"headless_frame{frame}.ppm"), traceSnesFrames);
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
                        TraceFrameEnd($"[HEADLESS] Frame {frame} ending SNES PC=0x{cpu.ProgramCounter24:X6}{sa1Pc}{spcPc}");
                        if (traceSpcWindow && spcPcValue.HasValue)
                        {
                            TraceFrameEnd($"[HEADLESS] Frame {frame} SPC window {DumpSpcWindow(snes.System.APU, spcPcValue.Value)}");
                        }
                    }
                }

                Console.WriteLine("[HEADLESS] Framebuffer AFTER running:");
                DumpSnesFrame(snes, Path.Combine(dumpDir, "headless_output.ppm"), traceSnesFrames);
                if (snesPeekAddrs.Length > 0)
                    Console.WriteLine(DumpSnesPeek(snes, "after", snesPeekAddrs));
                snesAudioSink?.Dispose();
                snesTraceWriter?.Dispose();
                Console.WriteLine($"[HEADLESS] Completed {framesToRun} frames");
                return 0;
            }

            if (useN64)
            {
                Console.WriteLine("[HEADLESS] Using N64 core");
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

                for (int frame = 0; frame < framesToRun; frame++)
                {
                    n64.RunFrame();

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
                }

                Console.WriteLine("[HEADLESS] Framebuffer AFTER running:");
                ReadOnlySpan<byte> fbOut = n64.GetFrameBuffer(out int wOut, out int hOut, out int sOut);
                var statsOut = GetFrameStats(fbOut, wOut, hOut, sOut);
                Console.WriteLine($"[HEADLESS] N64 fb_has_content={statsOut.HasContent} nonzero_pixels={statsOut.NonZeroPixels} first_nonzero=({statsOut.FirstX},{statsOut.FirstY})");
                DumpBgraToPpm(fbOut, wOut, hOut, sOut, Path.Combine(dumpDir, "headless_output.ppm"));
                n64AudioSink?.Dispose();
                // Stop R4300 thread before exit to avoid background runaway logs after frame loop.
                n64.Reset();
                Console.WriteLine($"[HEADLESS] Completed {framesToRun} frames");
                return 0;
            }

            if (useSegaCd)
            {
                Console.WriteLine("[HEADLESS] Using Sega CD core");
                var scd = new SegaCdAdapter();
                scd.LoadRom(romPath);

                Console.WriteLine("[HEADLESS] Framebuffer BEFORE running:");
                ReadOnlySpan<byte> fb0 = scd.GetFrameBuffer(out int w0, out int h0, out int s0);
                var stats0 = GetFrameStats(fb0, w0, h0, s0);
                Console.WriteLine($"[HEADLESS] SegaCD fb_has_content={stats0.HasContent} nonzero_pixels={stats0.NonZeroPixels} first_nonzero=({stats0.FirstX},{stats0.FirstY})");
                DumpBgraToPpm(fb0, w0, h0, s0, Path.Combine(dumpDir, "headless_frame0.ppm"));

                for (int frame = 0; frame < framesToRun; frame++)
                {
                    scd.RunFrame();

                    if (frame == 0 || frame == 5 || frame == 10)
                    {
                        ReadOnlySpan<byte> fb = scd.GetFrameBuffer(out int w, out int h, out int s);
                        var stats = GetFrameStats(fb, w, h, s);
                        Console.WriteLine($"[HEADLESS] Frame {frame}: fb_has_content={stats.HasContent} nonzero_pixels={stats.NonZeroPixels} first_nonzero=({stats.FirstX},{stats.FirstY})");
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

                    if (frame == 0 || frame == 5 || frame == 10)
                    {
                        ReadOnlySpan<byte> fb = pce.GetFrameBuffer(out int w, out int h, out int s);
                        var stats = GetFrameStats(fb, w, h, s);
                        Console.WriteLine($"[HEADLESS] Frame {frame}: fb_has_content={stats.HasContent} nonzero_pixels={stats.NonZeroPixels} first_nonzero=({stats.FirstX},{stats.FirstY})");
                        string ppmPath = Path.Combine(dumpDir, $"headless_frame{frame}.ppm");
                        DumpBgraToPpm(fb, w, h, s, ppmPath);
                        Console.WriteLine($"[HEADLESS] Dumped frame {frame} to {ppmPath}");
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

    private static bool IsSegaCdRomPath(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is ".cue" or ".iso" or ".bin" or ".chd")
        {
            try
            {
                return SegaCdDiscInfo.IsSegaCdDisc(path);
            }
            catch
            {
                return false;
            }
        }
        return false;
    }

    private static bool IsN64RomPath(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".z64" or ".n64" or ".v64";
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
            bool usePce = string.Equals(coreOverride, "pce", StringComparison.OrdinalIgnoreCase)
                || string.Equals(coreOverride, "pcecd", StringComparison.OrdinalIgnoreCase)
                || string.Equals(coreOverride, "pcengine", StringComparison.OrdinalIgnoreCase);

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
                int? sa1SnapshotFrame = ParseOptionalIntEnv("EUTHERDRIVE_SNES_HEADLESS_SA1_SNAPSHOT_FRAME");
                int[] snesPeekAddrs = ParseOptionalHexAddrEnv("EUTHERDRIVE_TRACE_SNES_PEEK_ADDRS");

                bool traceSnesFrames = Environment.GetEnvironmentVariable("EUTHERDRIVE_HEADLESS_TRACE_FRAMES") == "1";
                bool traceSnesPpuSnapshot = Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_SNES_PPU_SNAPSHOT") == "1";
                StreamWriter? snesTraceWriter = null;
                if (traceSnesFrames)
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

                Console.WriteLine("[HEADLESS] Framebuffer BEFORE running:");
                DumpSnesFrame(snes, Path.Combine(dumpDir, "headless_frame0.ppm"), traceSnesFrames);
                TracePeek("before");

                bool prevHasContent = false;
                for (int frame = 0; frame < framesToRun; frame++)
                {
                    bool startPressed = autoStart &&
                        ShouldPressStartPulse(frame, autoStartDelayFrames, autoStartPulseFrames, autoStartPeriodFrames, autoStartPulseCount);
                    snes.SetInputState(
                        up: false,
                        down: false,
                        left: false,
                        right: false,
                        a: false,
                        b: false,
                        x: false,
                        y: false,
                        z: false,
                        c: false,
                        start: startPressed,
                        mode: false,
                        padType: PadType.SixButton);
                    if (autoStartLog && startPressed != lastStartPressed)
                        Console.WriteLine($"[HEADLESS] SNES auto-start start={(startPressed ? 1 : 0)} frame={frame}");
                    lastStartPressed = startPressed;

                    snes.RunFrame();

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

                    if (frame == 0 || frame == 5 || frame == 10)
                        DumpSnesFrame(snes, Path.Combine(dumpDir, $"headless_frame{frame}.ppm"), traceSnesFrames);

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
                        Trace($"[HEADLESS] Frame {frame} ending SNES PC=0x{cpu.ProgramCounter24:X6}{sa1Pc}");
                    }
                }

                Console.WriteLine("[HEADLESS] Framebuffer AFTER running:");
                DumpSnesFrame(snes, Path.Combine(dumpDir, "headless_output.ppm"), traceSnesFrames);
                if (snesPeekAddrs.Length > 0)
                    Console.WriteLine(DumpSnesPeek(snes, "after", snesPeekAddrs));
                snesAudioSink?.Dispose();
                snesTraceWriter?.Dispose();
                Console.WriteLine($"[HEADLESS] Completed {framesToRun} frames");
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

                pce.GetFrameBuffer(out int w0, out int h0, out int s0);
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
                }
                pceTraceWriter?.Flush();
                pceTraceWriter?.Dispose();
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
            if (Environment.GetEnvironmentVariable("EUTHERDRIVE_YM") == "1")
                adapter.SetYmEnabled(true);

            Console.WriteLine("[HEADLESS] Framebuffer BEFORE running:");
            adapter.FrameBufferHasContent();
            adapter.DumpFrameBufferToPpm(Path.Combine(dumpDir, "headless_frame0.ppm"));

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
                Console.WriteLine($"[HEADLESS] Frame {frame} completed");

                if (frame == 0 || frame == 5 || frame == 10 || dumpFrames.Contains(frame))
                {
                    string ppmPath = Path.Combine(dumpDir, $"headless_frame{frame}.ppm");
                    adapter.DumpFrameBufferToPpm(ppmPath);
                    Console.WriteLine($"[HEADLESS] Dumped frame {frame} to {ppmPath}");
                }
            }

            Console.WriteLine("[HEADLESS] Framebuffer AFTER running:");
            adapter.FrameBufferHasContent();
            adapter.DumpFrameBufferToPpm(Path.Combine(dumpDir, "headless_output.ppm"));

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

    private static void ConfigureConsoleLogging()
    {
        if (ShouldSilenceConsole())
        {
            bool verbose = IsEnvEnabled("EUTHERDRIVE_LOG_VERBOSE") || IsEnvEnabled("EUTHERDRIVE_TRACE_VERBOSE");
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
        // If any trace flag is set, enable all console output
        if (IsEnvEnabled("EUTHERDRIVE_LOG_VERBOSE") || IsEnvEnabled("EUTHERDRIVE_TRACE_VERBOSE"))
        {
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
