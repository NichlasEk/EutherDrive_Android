#if N64_PERF_PROBE
using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Ryu64.MIPS;

// Reuse an unmodified core savestate with neutral input at every controller
// read. Work and observations follow guest time on the real CPU thread.
internal static class StateGameplayBenchmark
{
    private const ulong Clock = 93_750_000;

    internal static void Run(string romPath, string statePath, string output, int endSecond)
    {
        if (endSecond < 5 || endSecond > 600 || endSecond % 5 != 0)
            throw new ArgumentOutOfRangeException(nameof(endSecond));
        output = Path.GetFullPath(output);
        if (Directory.Exists(output)) throw new IOException("Benchmark output must be a new directory");
        byte[] rom = File.ReadAllBytes(romPath);
        if (rom.Length < 0x40 || BinaryPrimitives.ReadUInt32BigEndian(rom) != 0x80371240)
            throw new InvalidDataException("State benchmark requires a big-endian .z64 cartridge");
        using var core = new Ryu64Core.Ryu64Core();
        core.LoadROM(romPath);
        core.LoadState(statePath);
        Directory.CreateDirectory(output);
        var memory = R4300.memory;
        ulong startCycle = core.GetCycleCounter();
        var timer = new Stopwatch();
        var points = new List<object>();
        using var done = new ManualResetEventSlim();
        using var audioHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var inputHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long audioFrames = 0, reads = 0;
        double audioSeconds = 0;
        int nextSecond = 5;
        bool phaseProfile = Environment.GetEnvironmentVariable("N64_PROBE_PHASE_PROFILE") == "1";
        Exception? failure = null;
        memory.SetControllerState(0, 0, 0);
        memory.PerfAudioCapture = (pcm, rate) =>
        {
            audioFrames += pcm.Length / 2;
            audioSeconds += pcm.Length / (2.0 * rate);
            Span<byte> header = stackalloc byte[8];
            BinaryPrimitives.WriteUInt32LittleEndian(header, rate);
            BinaryPrimitives.WriteInt32LittleEndian(header[4..], pcm.Length);
            audioHash.AppendData(header);
            audioHash.AppendData(MemoryMarshal.AsBytes(pcm.AsSpan()));
        };
        memory.PerfControllerRead = () =>
        {
            try
            {
                ulong cycles = core.GetCycleCounter();
                int second = (int)((cycles - startCycle) / Clock);
#if N64_CPU_JIT_PROFILE
                if (second >= 5) R4300.CpuJitProfileActive = true;
#endif
                memory.SetControllerState(0, 0, 0);
                reads++;
                Span<byte> input = stackalloc byte[12];
                input.Clear();
                BinaryPrimitives.WriteUInt64LittleEndian(input, cycles);
                inputHash.AppendData(input);
                if (second >= nextSecond)
                {
                    double wallSeconds = timer.Elapsed.TotalSeconds;
                    if (phaseProfile)
                        Console.WriteLine("statePerformance=" + JsonSerializer.Serialize(new {
                            targetSecond = nextSecond, wallSeconds, summary = core.LastPerformanceStatus }));
                    string ramSha256 = Convert.ToHexString(SHA256.HashData(memory.RDRAM));
                    string? frameSha256 = null;
                    int width = 0, height = 0, bpp = 0;
                    if (core.TryGetFramebuffer(out var pixels, out width, out height, out bpp))
                    {
                        frameSha256 = Convert.ToHexString(SHA256.HashData(pixels));
                        WriteFrame(Path.Combine(output, $"frame-{nextSecond:D3}.ppm"), pixels, width, height, bpp);
                    }
                    var point = new { targetSecond = nextSecond, cycles, reads, wallSeconds,
                        audioFrames, audioSeconds, audioSha256 = Convert.ToHexString(audioHash.GetCurrentHash()),
                        inputSha256 = Convert.ToHexString(inputHash.GetCurrentHash()), ramSha256, frameSha256,
                        width, height, bpp, graphicsTasks = memory.RspGraphicsTaskCount, audioTasks = memory.RspAudioTaskCount };
                    points.Add(point);
                    Console.WriteLine("stateCheckpoint=" + JsonSerializer.Serialize(point));
                    File.WriteAllText(Path.Combine(output, "checkpoints.json"), JsonSerializer.Serialize(points, new JsonSerializerOptions { WriteIndented = true }));
                    nextSecond += 5;
                }
                if (second >= endSecond) { R4300.R4300_ON = false; done.Set(); }
            }
            catch (Exception ex) { failure = ex; R4300.R4300_ON = false; done.Set(); }
        };
        Console.WriteLine($"stateBenchmark pid={Environment.ProcessId} startCycle={startCycle} endGuestSecond={endSecond} " +
            $"romSha256={Convert.ToHexString(SHA256.HashData(rom))} stateSha256={Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(statePath)))}");
        timer.Start(); core.Start();
        try
        {
            while (!done.Wait(20))
            {
                while (core.GetAudioSamples(out _, out _).Length != 0) { }
                if (!R4300.R4300_ON) throw new InvalidOperationException("CPU stopped before the target controller read");
                if (timer.Elapsed.TotalSeconds > 1200) throw new TimeoutException("State benchmark did not reach the requested guest time");
            }
        }
        finally { core.Stop(); }
        if (failure != null) throw new InvalidOperationException("Gameplay observation failed", failure);
        if (core.GetCycleCounter() - startCycle < (ulong)endSecond * Clock || R4300.GetUnknownOpcodeCount() != 0)
            throw new InvalidOperationException("State benchmark did not finish cleanly");
        File.WriteAllBytes(Path.Combine(output, "rdram.bin"), memory.RDRAM);
        using (var writer = new BinaryWriter(File.Create(Path.Combine(output, "cpu-state.bin")))) R4300.SaveState(writer);
#if N64_CPU_JIT_PROFILE
        File.WriteAllText(Path.Combine(output, "jit-profile.json"), JsonSerializer.Serialize(
            R4300.CpuJitProfile.OrderByDescending(p => p.Value.GetValueOrDefault("attempt"))
                .Select(p => new { pc = p.Key.ToString("x8"), counts = p.Value }), new JsonSerializerOptions { WriteIndented = true }));
#endif
        if (phaseProfile)
        {
            const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic;
            var stats = new Dictionary<string, object?>();
            foreach (string name in new[] { "CpuJitCompilations", "CpuJitInstructions", "CpuJitInvalidations", "CpuJitRejectedCompilations", "CpuJitUnavailable" })
                stats[name] = typeof(R4300).GetField(name, flags)!.GetValue(null);
            Console.WriteLine("cpuJitTotals=" + JsonSerializer.Serialize(stats));
        }
        Console.WriteLine(core.LastPerformanceStatus);
#if N64_RSP_PROFILE
        Console.WriteLine("rspBlockProfile=" + memory.GetRspBlockProfile());
#endif
#if N64_CPU_DISPATCH_PROFILE
        Console.WriteLine("cpuDispatchProfile=" + R4300.GetCpuDispatchProfile());
#endif
        Console.WriteLine($"stateBenchmark=passed cycles={core.GetCycleCounter()} reads={reads}");
    }

    internal static void WriteFrame(string path, byte[] pixels, int width, int height, int bpp)
    {
        using var stream = File.Create(path);
        stream.Write(System.Text.Encoding.ASCII.GetBytes($"P6\n{width} {height}\n255\n"));
        var rgb = new byte[width * height * 3];
        for (int i = 0; i < width * height; i++)
        {
            if (bpp == 2)
            {
                int color = (pixels[i * 2] << 8) | pixels[i * 2 + 1];
                rgb[i * 3] = (byte)(((color >> 11) & 31) * 255 / 31);
                rgb[i * 3 + 1] = (byte)(((color >> 6) & 31) * 255 / 31);
                rgb[i * 3 + 2] = (byte)(((color >> 1) & 31) * 255 / 31);
            }
            else pixels.AsSpan(i * bpp, 3).CopyTo(rgb.AsSpan(i * 3));
        }
        stream.Write(rgb);
    }
}
#endif
