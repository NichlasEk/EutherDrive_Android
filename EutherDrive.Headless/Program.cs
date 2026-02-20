// Headless test harness for EutherDrive core
// Usage: dotnet run --project EutherDrive.Headless -- /path/to/rom.md [frames]
//        dotnet run --project EutherDrive.Headless -- --test-interlace2
// Default: runs 120 frames

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using EutherDrive.Core;
using EutherDrive.Core.SegaCd;
using EutherDrive.Core.MdTracerCore;
using EutherDrive.Core.Savestates;
using EutherDrive.Audio;

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
            bool useSnes = string.Equals(coreOverride, "snes", StringComparison.OrdinalIgnoreCase)
                || (string.IsNullOrEmpty(coreOverride) && IsSnesRomPath(romPath));
            bool useSegaCd = string.Equals(coreOverride, "segacd", StringComparison.OrdinalIgnoreCase)
                || string.Equals(coreOverride, "scd", StringComparison.OrdinalIgnoreCase)
                || (string.IsNullOrEmpty(coreOverride) && IsSegaCdRomPath(romPath));
            if (string.Equals(coreOverride, "md", StringComparison.OrdinalIgnoreCase))
            {
                useSnes = false;
                useSegaCd = false;
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

                bool traceSnesFrames = Environment.GetEnvironmentVariable("EUTHERDRIVE_HEADLESS_TRACE_FRAMES") == "1";
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
                Console.WriteLine("[HEADLESS] Framebuffer BEFORE running:");
                DumpSnesFrame(snes, Path.Combine(dumpDir, "headless_frame0.ppm"), traceSnesFrames);

                bool prevHasContent = false;
                for (int frame = 0; frame < framesToRun; frame++)
                {
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
                }

                Console.WriteLine("[HEADLESS] Framebuffer AFTER running:");
                DumpSnesFrame(snes, Path.Combine(dumpDir, "headless_output.ppm"), traceSnesFrames);
                snesAudioSink?.Dispose();
                snesTraceWriter?.Dispose();
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
                if (frame == 0 || frame == 5 || frame == 10)
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
            string dumpDir = Environment.GetEnvironmentVariable("EUTHERDRIVE_HEADLESS_DUMP_DIR")
                ?? Path.GetDirectoryName(romPath)
                ?? ".";
            Directory.CreateDirectory(dumpDir);

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
            int? forceZ80DumpFrame = ParseOptionalIntEnv("EUTHERDRIVE_FORCE_Z80_DUMP_FRAME");
            bool forceZ80DumpExtra = Environment.GetEnvironmentVariable("EUTHERDRIVE_FORCE_Z80_DUMP_EXTRA") == "1";
            string? forceZ80DumpPath = Environment.GetEnvironmentVariable("EUTHERDRIVE_FORCE_Z80_DUMP_PATH");
            uint lastM68kPc = 0;
            ushort lastZ80Pc = 0;
            long lastCycles = 0;
            int stableFrames = 0;
            bool hangTriggered = false;

            for (int frame = 0; frame < framesToRun; frame++)
            {
                adapter.StepFrame();
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

                if (frame == 0 || frame == 5 || frame == 10)
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
            bool keepStdErr = IsEnvEnabled("EUTHERDRIVE_SCD_PROFILE") && !IsEnvEnabled("EUTHERDRIVE_LOG_VERBOSE");
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
        if (IsEnvEnabled("EUTHERDRIVE_LOG_VERBOSE"))
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
