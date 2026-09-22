#if N64_PERF_PROBE
using System.Buffers.Binary;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Ryu64.MIPS;

// The real CPU thread boots the original cartridge. Input, observations and
// the stop request occur at Joybus reads, never at wall-clock poll boundaries.
internal static class Sm64Benchmark
{
    private const ulong Clock = 93_750_000;
    private const BindingFlags JitFlags = BindingFlags.Static | BindingFlags.NonPublic;

    internal static void Run(string romPath, string output, int endSecond)
    {
        if (endSecond < 10 || endSecond > 600) throw new ArgumentOutOfRangeException(nameof(endSecond));
        output = Path.GetFullPath(output);
        if (Directory.Exists(output)) throw new IOException("Benchmark output must be a new directory");
        byte[] rom = File.ReadAllBytes(romPath);
        if (rom.Length < 0x40) throw new InvalidDataException("Truncated cartridge");
        uint order = BinaryPrimitives.ReadUInt32BigEndian(rom);
        if (order == 0x37804012)
            for (int i = 0; i < rom.Length; i += 2) (rom[i], rom[i + 1]) = (rom[i + 1], rom[i]);
        else if (order == 0x40123780)
            for (int i = 0; i < rom.Length; i += 4) Array.Reverse(rom, i, 4);
        else if (order != 0x80371240) throw new InvalidDataException("Unknown cartridge byte order");
        if (BinaryPrimitives.ReadUInt32BigEndian(rom.AsSpan(0x10)) != 0x635a2bff ||
            BinaryPrimitives.ReadUInt32BigEndian(rom.AsSpan(0x14)) != 0x8b022326)
            throw new InvalidDataException("This input sequence and telemetry require original Super Mario 64 USA");
        Directory.CreateDirectory(output);
        string normalized = Path.Combine(output, "input.z64");
        File.WriteAllBytes(normalized, rom);
        using var core = new Ryu64Core.Ryu64Core();
        core.LoadROM(normalized);
        var memory = R4300.memory;
        var points = new List<object>();
        var timer = new Stopwatch();
        using var done = new ManualResetEventSlim();
        using var audioHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var inputHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long audioFrames = 0, reads = 0;
        double audioSeconds = 0;
        bool enteredGame = false;
        int nextSecond = 5;
        Exception? failure = null;
        object Jit(string field) => typeof(R4300).GetField(field, JitFlags)!.GetValue(null)!;
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
                int second = (int)(cycles / Clock);
#if N64_CPU_JIT_PROFILE
                if (second >= 70) R4300.CpuJitProfileActive = true;
#endif
                uint pointer = BinaryPrimitives.ReadUInt32BigEndian(memory.RDRAM.AsSpan(0x32d93c));
                int mario = (int)(pointer & 0x1fffffff);
                uint action = 0;
                float x = 0, y = 0, z = 0;
                if (pointer >= 0x80000000 && mario <= memory.RDRAM.Length - 0xc0)
                {
                    action = BinaryPrimitives.ReadUInt32BigEndian(memory.RDRAM.AsSpan(mario + 0xc));
                    enteredGame |= action != 0;
                    float Float(int offset) => BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32BigEndian(memory.RDRAM.AsSpan(mario + offset)));
                    x = Float(0x3c); y = Float(0x40); z = Float(0x44);
                }
                ushort buttons = (ushort)((second % 4 == 0 ? 0x8000 : 0) |
                    (!enteredGame && second >= 3 && second % 4 == 0 ? 0x1000 : 0));
                sbyte stickY = second >= 45 ? (sbyte)80 : (sbyte)0;
                memory.SetControllerState(buttons, 0, stickY);
                reads++;
                Span<byte> input = stackalloc byte[12];
                BinaryPrimitives.WriteUInt64LittleEndian(input, cycles);
                BinaryPrimitives.WriteUInt16LittleEndian(input[8..], buttons);
                input[10] = 0; input[11] = unchecked((byte)stickY);
                inputHash.AppendData(input);
                if (second >= nextSecond || second >= endSecond)
                {
                    double wall = timer.Elapsed.TotalSeconds;
                    string ramHash = Convert.ToHexString(SHA256.HashData(memory.RDRAM));
                    var point = new { targetSecond = Math.Min(nextSecond, endSecond), cycles, reads, wallSeconds = wall,
                        audioFrames, audioSeconds, audioSha256 = Convert.ToHexString(audioHash.GetCurrentHash()),
                        inputSha256 = Convert.ToHexString(inputHash.GetCurrentHash()), ramSha256 = ramHash,
                        action = action.ToString("x8"), x, y, z,
                        jitInstructions = Jit("CpuJitInstructions"), jitCompilations = Jit("CpuJitCompilations"),
                        jitRejected = Jit("CpuJitRejectedCompilations"), graphicsTasks = memory.RspGraphicsTaskCount,
                        audioTasks = memory.RspAudioTaskCount };
                    points.Add(point);
                    Console.WriteLine("sm64Checkpoint=" + JsonSerializer.Serialize(point));
                    File.WriteAllText(Path.Combine(output, "checkpoints.json"), JsonSerializer.Serialize(points, new JsonSerializerOptions { WriteIndented = true }));
                    if (core.TryGetFramebuffer(out var pixels, out int width, out int height, out int bpp))
                        WriteFrame(Path.Combine(output, $"frame-{second:D3}.ppm"), pixels, width, height, bpp);
                    nextSecond = second + 5;
                }
                if (second >= endSecond) { R4300.R4300_ON = false; done.Set(); }
            }
            catch (Exception ex) { failure = ex; R4300.R4300_ON = false; done.Set(); }
        };
        Console.WriteLine($"sm64Benchmark pid={Environment.ProcessId} endGuestSecond={endSecond} romSha256={Convert.ToHexString(SHA256.HashData(rom))}");
        timer.Start(); core.Start();
        try
        {
            // The CPU hook accounts every AI block. Draining here only bounds
            // host playback storage; it cannot change input or measured work.
            while (!done.Wait(20))
            {
                while (core.GetAudioSamples(out _, out _).Length != 0) { }
                if (!R4300.R4300_ON) throw new InvalidOperationException("CPU stopped before the target controller read");
                if (timer.Elapsed.TotalSeconds > 1200) throw new TimeoutException("Mario did not reach the requested guest time");
            }
        }
        finally { core.Stop(); }
        if (failure != null) throw new InvalidOperationException("Gameplay observation failed", failure);
        if (core.GetCycleCounter() < (ulong)endSecond * Clock || (endSecond >= 70 && !enteredGame) || R4300.GetUnknownOpcodeCount() != 0)
            throw new InvalidOperationException("Mario did not complete the expected gameplay sequence cleanly");
#if N64_LIVE_GPU
        memory.GpuRenderer?.Synchronize();
#endif
        File.WriteAllBytes(Path.Combine(output, "rdram.bin"), memory.RDRAM);
#if N64_CPU_JIT_PROFILE
        File.WriteAllText(Path.Combine(output, "jit-profile.json"), JsonSerializer.Serialize(
            R4300.CpuJitProfile.OrderByDescending(p => p.Value.GetValueOrDefault("attempt"))
                .Select(p => new { pc = p.Key.ToString("x8"), counts = p.Value }), new JsonSerializerOptions { WriteIndented = true }));
#endif
        Console.WriteLine(core.LastPerformanceStatus);
        Console.WriteLine($"sm64Benchmark=passed cycles={core.GetCycleCounter()} reads={reads} enteredGame={enteredGame} " +
            $"ramSha256={Convert.ToHexString(SHA256.HashData(memory.RDRAM))} audioSha256={Convert.ToHexString(audioHash.GetCurrentHash())} " +
            $"inputSha256={Convert.ToHexString(inputHash.GetCurrentHash())}");
    }

    private static void WriteFrame(string path, byte[] pixels, int width, int height, int bpp)
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
