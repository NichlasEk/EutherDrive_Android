using System.Buffers.Binary;
using System.Diagnostics;
using Ryu64.MIPS;

#if N64_PERF_PROBE
if (args.Length == 2 && args[0] == "--bench-rsp-slice")
{
    RspSliceCapture.Benchmark(args[1]);
    return;
}
#endif
if (args.Length is 4 or 5 && args[0] == "--bench-state")
{
#if N64_PERF_PROBE
    StateGameplayBenchmark.Run(args[1], args[2], args[3], args.Length == 5 ? int.Parse(args[4]) : 30);
    return;
#else
    throw new InvalidOperationException("Build with -p:N64PerformanceProbe=true for deterministic gameplay");
#endif
}

#if N64_LIVE_GPU && N64_PERF_PROBE
if (args.Length is 3 or 4 or 5 && args[0] == "--check-gpu-state-game")
{
    GpuGameplayStateChecks.Run(args[1], args[2], args.Length >= 4 ? int.Parse(args[3]) : 10,
        args.Length == 5 ? args[4] : null);
    return;
}
#endif

if (args.Length >= 4 && args[0] == "--inspect-cpu-state")
{
    using var inspectCore = new Ryu64Core.Ryu64Core();
    inspectCore.LoadROM(args[1]);
    using (var reader = new BinaryReader(File.OpenRead(args[2]))) R4300.LoadState(reader);
    foreach (string address in args.Skip(3))
    {
        uint start = Convert.ToUInt32(address, 16);
        for (uint i = 0; i < 8; i++)
        {
            uint pc = start + i * 4, physical = (pc >= 0x80000000u && pc < 0xc0000000u ? pc : TLB.TranslateAddress(pc, true)) & 0x1fffffffu;
            if ((ulong)physical + 4 > (ulong)R4300.memory.RDRAM.Length)
                throw new InvalidDataException("Instruction inspection is restricted to RDRAM");
#if N64_LIVE_GPU
            R4300.memory.GpuBeforeRead(physical, 4);
#endif
            uint word = BinaryPrimitives.ReadUInt32BigEndian(R4300.memory.RDRAM.AsSpan((int)physical, 4));
            var desc = new OpcodeTable.OpcodeDesc(word);
            Console.WriteLine($"pc={pc:x8} physical={physical:x8} word={word:x8} op={OpcodeTable.GetOpcodeInfo(word).Interpret.Method.Name} rs={desc.op1} rt={desc.op2} rd={desc.op3} imm={(short)desc.Imm}");
        }
    }
    return;
}

#if N64_LIVE_GPU && N64_RDP_JOURNAL
if (args.Length == 2 && args[0] == "--check-mapped-gpu-loads")
{
    CpuMappedJitChecks.GpuLoads(args[1]);
    return;
}
#endif

if (args.Length == 1 && args[0] == "--check-cpu-jit-mapped")
{
    CpuMappedJitChecks.Run();
    return;
}

if (args.Length == 1 && args[0] == "--check-cpu-jit-cache")
{
    CpuJitCacheChecks.Run();
    return;
}

if (args.Length is 3 or 4 && args[0] == "--bench-sm64")
{
#if N64_PERF_PROBE
    Sm64Benchmark.Run(args[1], args[2], args.Length == 4 ? int.Parse(args[3]) : 90);
    return;
#else
    throw new InvalidOperationException("Build with -p:N64PerformanceProbe=true for deterministic gameplay");
#endif
}

#if N64_LIVE_GPU && N64_RDP_JOURNAL
if (args.Length == 2 && args[0] == "--check-gpu-cpu-frames")
{
    N64GpuCpuFramebufferChecks.Run(args[1]);
    return;
}
if (args.Length == 3 && args[0] == "--check-gpu-savestates")
{
    N64GpuSavestateChecks.Run(args[1], args[2]);
    return;
}
if (args.Length == 4 && args[0] == "--check-live-gpu")
{
    N64LiveGpuChecks.Run(args[1], args[2], args[3]);
    return;
}
if (args.Length == 4 && args[0] == "--check-live-gpu-lifecycle")
{
    N64LiveGpuChecks.Lifecycle(args[1], args[2], args[3]);
    return;
}
#endif
if (args.Length == 2 && args[0] == "--check-gpu-abi")
{
    N64GpuChecks.Run(args[1]);
    return;
}
if (args.Length == 2 && args[0] == "--check-gpu-textures")
{
    N64GpuTextureChecks.Run(args[1]);
    return;
}
if (args.Length == 2 && args[0] == "--check-gpu-textures-live")
{
    N64GpuTextureChecks.Run(args[1], true);
    return;
}
if (args.Length == 2 && args[0] == "--check-gpu-readback")
{
    N64GpuReadbackChecks.Run(args[1]);
    return;
}
if (args.Length == 2 && args[0] == "--check-journal-production")
{
    RdpJournalIsolationChecks.Run(args[1]);
    return;
}

#if N64_RDP_JOURNAL
if (args.Length == 2 && args[0] == "--capture-gpu-hazards")
{
    N64GpuHazards.Capture(args[1]);
    return;
}
if (args.Length is 6 or 7 && args[0] == "--replay-gpu-journal")
{
    if (args[5] is not ("strict" or "batched" or "ranges" or "textures") || (args.Length == 7 && args[6] != "--bench"))
        throw new ArgumentException("Usage: --replay-gpu-journal LIBRARY JOURNAL_DIR REFERENCE_DIR NEW_OUTPUT strict|batched|ranges|textures [--bench]");
    N64GpuJournalReplay.Run(args[1], args[2], args[3], args[4], args[5], args.Length == 7);
    return;
}
if (args.Length == 2 && args[0] == "--replay-rdp-journal")
{
    RdpJournalReplay.Run(args[1]);
    return;
}
if (args.Length == 1 && args[0] == "--check-rdp-journal")
{
    RdpJournalChecks.Run();
    return;
}
#endif
if (args.Length == 1 && args[0] == "--check-rdp-export")
{
    RdpDumpExportChecks.Run();
    return;
}

if (args.Length == 3 && args[0] == "--export-rdp-dump")
{
    RdpDumpExport.Run(args[1], args[2]);
    return;
}

if (args.Length == 2 && args[0] == "--check-triangle-modes")
{
    TriangleModeChecks.Run(args[1]);
    return;
}

if (args.Length == 1 && args[0] == "--check-rdp-streaming")
{
    RdpStreamingChecks.Run();
    return;
}
if (args.Length == 2 && args[0] == "--check-rectangles")
{
    RectangleChecks.Run(args[1]);
    return;
}
if (args.Length == 1 && args[0] == "--check-cpu-jit")
{
    CpuBlockChecks.Run(jit: true);
    return;
}
if (args.Length == 2 && args[0] == "--check-tlb-cache")
{
    TlbCacheChecks.Run(args[1]);
    return;
}
if (args.Length == 1 && args[0] == "--check-rsp-scheduling")
{
    RspSchedulingChecks.Run();
    return;
}
if (args.Length == 2 && args[0] == "--check-framebuffer-writes")
{
    FramebufferWriteChecks.Run(args[1]);
    return;
}
if (args.Length == 1 && args[0] == "--check-sprites")
{
    SpriteRenderChecks.Run();
    return;
}
if (args.Length == 1 && args[0] == "--bench-cop1-block-thread")
{
    CpuThreadBenchmark.Run(cpuBlock: true, cop1Block: true);
    return;
}
if (args.Length == 2 && args[0] == "--check-instruction-translation")
{
    TlbCacheChecks.Run(args[1], instruction: true);
    return;
}
if (args.Length == 2 && (args[0] == "--check-word-access" || args[0] == "--check-opcode-fetch"))
{
    MemoryWordAccessChecks.Run(args[1], instructionFetch: args[0] == "--check-opcode-fetch");
    return;
}
if (args.Length == 1 && args[0] == "--bench-block-thread")
{
    CpuThreadBenchmark.Run(cpuBlock: true);
    return;
}
if (args.Length == 2 && args[0] == "--check-block-decode")
{
    CpuBlockDecodeChecks.Run(args[1]);
    return;
}
if (args.Length == 2 && args[0] == "--check-byte-access")
{
    MemoryWordAccessChecks.Run(args[1], byteAccess: true);
    return;
}
if (args.Length == 1 && args[0] == "--check-cpu-blocks")
{
    CpuBlockChecks.Run();
    return;
}
if (args.Length == 1 && args[0] == "--bench-multiply-thread")
{
    CpuThreadBenchmark.Run(multiplyRoutine: true);
    return;
}
if (args.Length == 1 && args[0] == "--check-multiply-batch")
{
    CpuMultiplyBatchChecks.Run();
    return;
}
if (args.Length == 1 && args[0] == "--check-double-multiply")
{
    CpuDoubleMultiplyChecks.Run();
    return;
}
if (args.Length == 2 && args[0] == "--check-primary-dispatch")
{
    CpuPrimaryDispatchChecks.Run(args[1]);
    return;
}
if (args.Length == 1 && args[0] == "--check-low-vi")
{
    LowViFramebufferChecks.Run();
    return;
}
if (args.Length == 1 && args[0] == "--check-read64")
{
    MemoryRead64Checks.Run();
    return;
}
if (args.Length == 3 && (args[0] == "--bench-cpu-state" || args[0] == "--profile-cpu-state"))
{
    CpuStateBenchmark.Run(args[1], args[2], profile: args[0] == "--profile-cpu-state");
    return;
}

if (args.Length == 1 && args[0] == "--check-controller-ports")
{
    ControllerPortChecks.Run();
    return;
}
if (args.Length == 1 && args[0] == "--check-rectangle-shade")
{
    RectangleShadeChecks.Run();
    return;
}

if (args.Length == 1 && args[0] == "--check-cop1-usability")
{
    Cop1UsabilityChecks.Run();
    return;
}

if (args.Length == 2 && (args[0] == "--check-idle-events" || args[0] == "--bench-idle-events"))
{
    CpuIdleEventChecks.Run(args[1], args[0] == "--bench-idle-events");
    return;
}

if (args.Length >= 2 && args[0] == "--bench-cpu-idle")
{
    CpuIdleBenchmark.Run(args[1], args.Length > 2 ? args[2] : null);
    return;
}

if (args.Length == 1 && args[0] == "--check-video")
{
    VideoChecks.Run();
    return;
}

if (args.Length == 2 && args[0] == "--check-depth")
{
    DepthChecks.Run(args[1]);
    return;
}

if (args.Length == 2 && args[0] == "--check-cpu-diagnostics")
{
    CpuDiagnosticsChecks.Run(args[1]);
    return;
}

if (args.Length == 1 && args[0] == "--bench-cpu-thread")
{
    CpuThreadBenchmark.Run();
    return;
}

if (args.Length == 2 && args[0] == "--check-rsp-blocks")
{
    RspBlockChecks.Run(args[1]);
    return;
}

if (args.Length == 2 && args[0] == "--check-flat-shade")
{
    FlatShadeChecks.Run(args[1]);
    return;
}

if (args.Length == 1 && args[0] == "--bench-cpu-dispatch")
{
    CpuDispatchBenchmark.Run();
    return;
}

if (args.Length >= 1 && args[0] == "--check-rsp-scalar")
{
    RspScalarChecks.Run(args.Length > 1 ? args[1] : null);
    return;
}

if (args.Length == 1 && args[0] == "--check-filter")
{
    TextureFilterChecks.Run();
    return;
}

if (args.Length >= 2 && args[0] == "--bench-rsp-task")
{
    RspTaskCapture.Benchmark(args[1], args.Length > 2 ? args[2] : null);
    return;
}

if (args.Length == 1 && args[0] == "--check-audio")
{
    AudioChecks.Run();
    return;
}

if (args.Length >= 1 && args[0] == "--check-opcodes")
{
    OpcodeChecks.Run(args.Length > 1 ? args[1] : null);
    return;
}
if (args.Length >= 1 && args[0] == "--check-combiner")
{
    CombinerChecks.Run(args.Length > 1 ? args[1] : null);
    return;
}
if (args.Length >= 2 && args[0] == "--bench-rdp")
{
    RdpBenchmark.Run(args[1], args.Length > 2 ? args[2] : null);
    return;
}
if (args.Length >= 1 && args[0] == "--check-sampler")
{
    SamplerChecks.Run(args.Length > 1 ? args[1] : null);
    return;
}
if (args.Length == 1 && args[0] == "--check-cpu-loops")
{
    CpuLoopChecks.Run();
    return;
}
if (args.Length >= 2 && args[0] == "--replay-rdp")
{
    RdpCapture.Replay(args[1], args.Length > 2 ? args[2] : Path.Combine(args[1], "replay"));
    return;
}
if (args.Length >= 1 && args[0] == "--check-dma")
{
    DmaChecks.Run(args.Length > 1 ? args[1] : null);
    return;
}
if (args.Length == 1 && args[0] == "--check-snapshots")
{
    SnapshotChecks.Run();
    return;
}
if (args.Length >= 1 && args[0] == "--check-rsp")
{
    RspChecks.Run(args.Length > 1 ? args[1] : null);
    return;
}
if (args.Length == 1 && args[0] == "--check-eeprom")
{
    EepromChecks.Run();
    return;
}
if (args.Length == 1 && args[0] == "--check-render")
{
    RenderChecks.Run();
    return;
}
if (args.Length < 2) throw new ArgumentException("Usage: N64Probe ROM OUTPUT_DIR [seconds=30] [state.bin]");
string output = Path.GetFullPath(args[1]);
Directory.CreateDirectory(output);
bool captureRdp = Environment.GetEnvironmentVariable("N64_PROBE_CAPTURE_RDP") == "1";
bool captureRsp = Environment.GetEnvironmentVariable("N64_PROBE_CAPTURE_RSP") == "1";
if (captureRsp) Environment.SetEnvironmentVariable("EUTHERDRIVE_TRACE_N64_RSP_TASK_DMEM", "1");
if (captureRdp) Environment.SetEnvironmentVariable("EUTHERDRIVE_TRACE_N64_RDP_COMMANDS", "1");
byte[] rom = File.ReadAllBytes(args[0]);
uint header = BinaryPrimitives.ReadUInt32BigEndian(rom);
if (header == 0x37804012)
    for (int i = 0; i < rom.Length; i += 2) (rom[i], rom[i + 1]) = (rom[i + 1], rom[i]);
else if (header == 0x40123780)
    for (int i = 0; i < rom.Length; i += 4) Array.Reverse(rom, i, 4);
else if (header != 0x80371240) throw new InvalidDataException("Unknown N64 ROM byte order");
string normalized = Path.Combine(output, "input.z64");
File.WriteAllBytes(normalized, rom);
using var core = new Ryu64Core.Ryu64Core();
core.LoadROM(normalized);
if (args.Length > 3) core.LoadState(args[3]);
#if N64_RDP_JOURNAL
string? journalFramesText = Environment.GetEnvironmentVariable("N64_PROBE_RDP_JOURNAL_FRAMES");
using var journal = journalFramesText != null
    ? new RdpJournal(R4300.memory, Path.Combine(output, "journal"), int.Parse(journalFramesText), args.Length <= 3) : null;
#else
if (Environment.GetEnvironmentVariable("N64_PROBE_RDP_JOURNAL_FRAMES") != null)
    throw new InvalidOperationException("Build with -p:N64RdpJournalCapture=true to capture an RDP journal");
#endif
using var rdpCapture = captureRdp ? new RdpCapture(output) : null;
using var rspCapture = captureRsp ? new RspTaskCapture(output) : null;
#if N64_PERF_PROBE
using var rspSliceCapture = Environment.GetEnvironmentVariable("N64_PROBE_CAPTURE_RSP_SLICE") == "1"
    ? new RspSliceCapture(R4300.memory, output) : null;
#endif
if (Environment.GetEnvironmentVariable("N64_PROBE_REPLAY_RDP") == "1")
{
    var memory = R4300.memory;
    uint start = memory.ReadUInt32(0x04100000);
    uint end = memory.ReadUInt32(0x04100004);
    var execute = typeof(Memory).GetMethod("ExecuteRdpDisplayList", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
    execute.Invoke(memory, new object[] { start, end });
    uint color = memory.LastRdpColorImageAddress;
    var pixels = new byte[320 * 240 * 2];
    Buffer.BlockCopy(memory.RDRAM, (int)color, pixels, 0, pixels.Length);
    WriteFrame(Path.Combine(output, "replay.ppm"), pixels, 320, 240, 2);
    Console.WriteLine($"replay=0x{start:x8}-0x{end:x8} color=0x{color:x8}");
    return;
}
int seconds = args.Length > 2 ? int.Parse(args[2]) : 30;
bool sm64Input = Environment.GetEnvironmentVariable("N64_PROBE_SM64_INPUT") == "1";
bool sm64BootInput = Environment.GetEnvironmentVariable("N64_PROBE_SM64_BOOT_INPUT") == "1";
bool sm64EnteredGame = false;
bool sm64Us = BinaryPrimitives.ReadUInt32BigEndian(rom.AsSpan(0x10)) == 0x635a2bff
    && BinaryPrimitives.ReadUInt32BigEndian(rom.AsSpan(0x14)) == 0x8b022326;
if ((sm64Input || sm64BootInput) && !sm64Us) throw new ArgumentException("SM64 input/telemetry requires the original USA cartridge");
using var probeProcess = Process.GetCurrentProcess();
TimeSpan cpuStart = probeProcess.TotalProcessorTime;
bool fireInput = Environment.GetEnvironmentVariable("N64_PROBE_FIRE_INPUT") == "1";
if (fireInput) core.SetInputState(new Ryu64Core.InputState { Z = true });
core.Start();
var timer = Stopwatch.StartNew();
bool captureAudio = Environment.GetEnvironmentVariable("N64_PROBE_CAPTURE_AUDIO") == "1";
using var frameCapture = Environment.GetEnvironmentVariable("N64_PROBE_CAPTURE_FRAMES") == "1" ? new FrameCapture(output) : null;
using var audioFile = captureAudio ? new BinaryWriter(File.Create(Path.Combine(output, "audio-44100-stereo-s16le.pcm"))) : null;
var audioResampler = new Ryu64Core.StereoResampler();
long audioSamples = 0, audioNonzero = 0;
int audioPeak = 0;
try
{
    for (int second = 0; second < seconds; second++)
    {
        if (!captureAudio && frameCapture == null) Thread.Sleep(1000);
        else for (int poll = 0; poll < 50; poll++)
        {
            Thread.Sleep(20);
            frameCapture?.Poll(core, timer.Elapsed.TotalSeconds);
            if (!captureAudio) continue;
            short[] pcm = core.GetAudioSamples(out uint rate, out _);
            pcm = audioResampler.Convert(pcm, rate, 44100);
            foreach (short sample in pcm)
            {
                audioFile!.Write(sample);
                audioSamples++;
                if (sample != 0) audioNonzero++;
                audioPeak = Math.Max(audioPeak, Math.Abs((int)sample));
            }
        }
        if (captureAudio) Console.WriteLine($"audio samples={audioSamples} nonzero={audioNonzero} peak={audioPeak} duration={audioSamples / 88200.0:F3}s");
        if (Environment.GetEnvironmentVariable("N64_PROBE_AUTO_INPUT") == "1")
            core.SetInputState(new Ryu64Core.InputState { Start = second >= 15 && second % 10 < 2, A = second >= 35 && second % 10 is >= 5 and < 7 });
        if (Environment.GetEnvironmentVariable("N64_PROBE_MOVE_INPUT") == "1")
            core.SetInputState(new Ryu64Core.InputState {
                StickY = second >= 10 && second < 45 ? (sbyte)80 : (sbyte)0,
                StickX = second >= 45 && second < 60 ? (sbyte)60 : (sbyte)0 });
        if (sm64Input || sm64BootInput)
        {
            var ram = R4300.memory.RDRAM;
            uint pointer = BinaryPrimitives.ReadUInt32BigEndian(ram.AsSpan(0x32d93c));
            int mario = (int)(pointer & 0x1fffffff);
            int inputSecond = sm64BootInput ? (int)(core.GetCycleCounter() / 93_750_000) : second;
            bool move = inputSecond >= (sm64BootInput ? 45 : 20);
            bool jump = inputSecond % 4 == 0;
            if (pointer >= 0x80000000 && mario <= ram.Length - 0xc0)
            {
                uint action = BinaryPrimitives.ReadUInt32BigEndian(ram.AsSpan(mario + 0xc));
                sm64EnteredGame |= action != 0;
                float Float(int offset) => BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32BigEndian(ram.AsSpan(mario + offset)));
                Console.WriteLine($"mario action={action:x8} pos={Float(0x3c):F2},{Float(0x40):F2},{Float(0x44):F2} velocity={Float(0x54):F2} inputY={(move ? 80 : 0)} inputA={jump}");
            }
            core.SetInputState(new Ryu64Core.InputState { Start = sm64BootInput && !sm64EnteredGame && inputSecond >= 3 && inputSecond % 4 == 0,
                A = jump, StickY = move ? (sbyte)80 : (sbyte)0 });
        }
        Console.WriteLine($"seconds={timer.Elapsed.TotalSeconds:F2} cpuSeconds={(probeProcess.TotalProcessorTime - cpuStart).TotalSeconds:F3} {core.LastExecutionStatus}");
#if N64_RDP_JOURNAL
        if (journal != null && (journal.Completed || journal.Failure != null)) break;
#endif
        if (second % 5 == 4 || fireInput)
        {
            Console.WriteLine(core.LastPerformanceStatus);
            uint origin = R4300.memory.ReadUInt32(0x04400004) & 0xffffff;
            int viWidth = (int)(R4300.memory.ReadUInt32(0x04400008) & 0xfff);
            int viBpp = (R4300.memory.ReadUInt32(0x04400000) & 3) == 3 ? 4 : 2;
            if (viWidth > 0 && origin + viWidth * 240 * viBpp <= R4300.memory.RDRAM.Length)
            {
                var vi = new byte[viWidth * 240 * viBpp];
                Buffer.BlockCopy(R4300.memory.RDRAM, (int)origin, vi, 0, vi.Length);
                WriteFrame(Path.Combine(output, $"vi-{second + 1:D4}.ppm"), vi, viWidth, 240, viBpp);
            }
            if (core.TryGetFramebuffer(out var pixels, out int width, out int height, out int bpp))
            {
                WriteFrame(Path.Combine(output, $"frame-{second + 1:D4}.ppm"), pixels, width, height, bpp);
            }
            Console.WriteLine(core.LastFramebufferStatus);
        }
    }
    core.Stop();
    core.SaveState(Path.Combine(output, "state.bin"));
}
finally { core.Stop(); }
#if N64_RDP_JOURNAL
journal?.RequireComplete();
#endif
File.WriteAllBytes(Path.Combine(output, "rdram.bin"), R4300.memory.RDRAM);
Console.WriteLine(core.LastPerformanceStatus);
#if N64_LIVE_GPU
Console.WriteLine(R4300.memory.GpuHazardSummary);
#endif

var jitFlags = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic;
Console.WriteLine($"cpuJit instructions={typeof(R4300).GetField("CpuJitInstructions", jitFlags)!.GetValue(null)} " +
    $"compilations={typeof(R4300).GetField("CpuJitCompilations", jitFlags)!.GetValue(null)} " +
    $"rejected={typeof(R4300).GetField("CpuJitRejectedCompilations", jitFlags)!.GetValue(null)} " +
    $"invalidations={typeof(R4300).GetField("CpuJitInvalidations", jitFlags)!.GetValue(null)} " +
    $"peakMemoryBytes={probeProcess.PeakWorkingSet64}");

static void WriteFrame(string path, byte[] pixels, int width, int height, int bpp)
{
    using var frame = File.Create(path);
    frame.Write(System.Text.Encoding.ASCII.GetBytes($"P6\n{width} {height}\n255\n"));
    for (int p = 0; p < width * height; p++)
    {
        if (bpp == 2)
        {
            int c = (pixels[p * 2] << 8) | pixels[p * 2 + 1];
            frame.WriteByte((byte)(((c >> 11) & 31) * 255 / 31));
            frame.WriteByte((byte)(((c >> 6) & 31) * 255 / 31));
            frame.WriteByte((byte)(((c >> 1) & 31) * 255 / 31));
        }
        else frame.Write(pixels, p * bpp, 3);
    }
}
