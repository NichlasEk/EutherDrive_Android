#if N64_LIVE_GPU && N64_PERF_PROBE
using System.Buffers.Binary;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Ryu64.MIPS;

// Compare uninterrupted execution with a restored checkpoint on the real CPU
// thread. Both branches see identical neutral input at guest-cycle boundaries.
internal static class GpuGameplayStateChecks
{
    private const ulong Clock = 93_750_000;
    internal static void Run(string rom, string output, int warmSeconds)
    {
        if (warmSeconds < 1 || warmSeconds > 120) throw new ArgumentOutOfRangeException(nameof(warmSeconds));
        if (Directory.Exists(output)) throw new IOException("Use a new output directory");
        Directory.CreateDirectory(output);
        using var core = new Ryu64Core.Ryu64Core(); core.LoadROM(rom);
        if (R4300.memory.GpuRenderer == null) throw new Exception("Set EUTHERDRIVE_N64_GPU_LIBRARY for this check");
        var memory = R4300.memory;
        string Advance(string name, int seconds, Action start)
        {
            ulong begin = core.GetCycleCounter(), until = begin + (ulong)seconds * Clock;
            int nextSecond = 1;
            long reads = 0, audioFrames = 0;
            using var done = new ManualResetEventSlim();
            using var audio = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using var input = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var frames = new List<object>();
            Exception failure = null;
            memory.SetControllerState(0, 0, 0);
            memory.PerfAudioCapture = (pcm, rate) =>
            {
                Span<byte> header = stackalloc byte[8];
                BinaryPrimitives.WriteUInt32LittleEndian(header, rate);
                BinaryPrimitives.WriteInt32LittleEndian(header[4..], pcm.Length);
                audio.AppendData(header); audio.AppendData(MemoryMarshal.AsBytes(pcm.AsSpan()));
                audioFrames += pcm.Length / 2;
            };
            memory.PerfControllerRead = () =>
            {
                try
                {
                    ulong cycles = core.GetCycleCounter();
                    memory.SetControllerState(0, 0, 0); reads++;
                    input.AppendData(BitConverter.GetBytes(cycles));
                    if (cycles >= begin + (ulong)nextSecond * Clock)
                    {
                        if (core.TryGetFramebuffer(out var pixels, out int width, out int height, out int bpp))
                        {
                            frames.Add(new { cycles, width, height, bpp, sha256 = Convert.ToHexString(SHA256.HashData(pixels)) });
                            StateGameplayBenchmark.WriteFrame(Path.Combine(output, $"{name}-{nextSecond:D2}.ppm"), pixels, width, height, bpp);
                        }
                        nextSecond++;
                    }
                    if (cycles >= until) { R4300.R4300_ON = false; done.Set(); }
                }
                catch (Exception ex) { failure = ex; R4300.R4300_ON = false; done.Set(); }
            };
            var timer = Stopwatch.StartNew();
            start();
            try
            {
                while (!done.Wait(20))
                {
                    while (core.GetAudioSamples(out _, out _).Length != 0) { }
                    if (!R4300.R4300_ON) throw new Exception("CPU stopped before checkpoint");
                    if (timer.Elapsed.TotalSeconds > 600) throw new TimeoutException("Game did not reach checkpoint");
                }
            }
            finally { core.Stop(); memory.PerfAudioCapture = null; memory.PerfControllerRead = null; }
            if (failure != null) throw new Exception("GPU gameplay observation failed", failure);
            if (core.GetCycleCounter() < until || R4300.GetUnknownOpcodeCount() != 0) throw new Exception("GPU gameplay execution failed");
            using var state = new MemoryStream();
            using (var writer = new BinaryWriter(state, System.Text.Encoding.UTF8, true)) R4300.SaveState(writer);
            File.WriteAllBytes(Path.Combine(output, name + "-cpu.bin"), state.ToArray());
            var result = new { cycles = core.GetCycleCounter(), reads, audioFrames,
                audioSha256 = Convert.ToHexString(audio.GetCurrentHash()), inputSha256 = Convert.ToHexString(input.GetCurrentHash()),
                frames, cpuSha256 = Convert.ToHexString(SHA256.HashData(state.ToArray())), graphicsTasks = memory.RspGraphicsTaskCount };
            string json = JsonSerializer.Serialize(result);
            File.WriteAllText(Path.Combine(output, name + ".json"), json + "\n");
            Console.WriteLine($"gpuStateGamePhase={name} {json}");
            if (memory.RdpCommandCount == 0) throw new Exception("No RDP commands executed");
            return json;
        }
        Advance("warm", warmSeconds, core.Start);
        string slot = Path.Combine(output, "checkpoint.bin");
        core.SaveState(slot);
        // Start after Stop intentionally resets this core. Invoke the exact
        // continuation used by SaveState's finally block for the reference.
        var resume = typeof(Ryu64Core.Ryu64Core).GetMethod("ResumeLoadedExecution", BindingFlags.Instance | BindingFlags.NonPublic)!;
        string expected = Advance("uninterrupted", 5, () => resume.Invoke(core, null));
        core.LoadState(slot);
        if (memory.GpuRenderer == null) throw new Exception("Savestate detached the GPU");
        string actual = Advance("restored", 5, core.Start);
        if (actual != expected) throw new Exception("GPU savestate continuation differs; see phase JSON and CPU snapshots");
        Console.WriteLine("gpuStateGame=passed cpuDevicesRamHiddenTmem=exact audioInputFrames=exact backend=GPU");
    }
}
#endif
