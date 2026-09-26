#if N64_PERF_PROBE
using System.Buffers.Binary;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Ryu64.MIPS;

// Captures one real, bounded RSP invocation at its actual entry/exit points.
// The replay timer covers ExecuteSlice only; restoring and checking state are
// outside the timer. This intentionally does not measure GPU or CPU progress.
internal sealed class RspSliceCapture : IDisposable
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private readonly Memory memory;
    private readonly string directory;
    private readonly uint taskType;
    private int remaining;
    private bool started, finished, resumeAtStart;
    private uint budgetAtStart;
    private readonly FieldInfo rspField = typeof(Memory).GetField("_rspInterpreter", Private)!;
    private readonly FieldInfo taskField = typeof(Memory).GetField("_activeRspTask", Private)!;
    private readonly IncrementalHash commandHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private int commandCount;

    internal RspSliceCapture(Memory memory, string directory)
    {
        this.memory = memory;
        this.directory = directory;
        taskType = Environment.GetEnvironmentVariable("N64_PROBE_CAPTURE_RSP_TYPE") == "2" ? 2u : 1u;
        remaining = int.TryParse(Environment.GetEnvironmentVariable("N64_PROBE_CAPTURE_RSP_SKIP"), out int skip) ? Math.Max(0, skip) : 0;
        if (File.Exists(Path.Combine(directory, "rsp-slice-start.bin")))
            throw new IOException("Use a fresh RSP slice capture directory");
        memory.PerfRspSliceCapture = Observe;
    }

    private void Observe(bool before, bool resume, uint budget, uint count, string reason)
    {
        if (finished) return;
        object task = taskField.GetValue(memory)!;
        uint type = (uint)task.GetType().GetField("Type")!.GetValue(task)!;
        if (type != taskType) return;
        if (before)
        {
            if (remaining-- > 0) return;
            resumeAtStart = resume;
            budgetAtStart = budget;
            Save("start");
            commandHash.GetHashAndReset();
            commandCount = 0;
            memory.PerfRdpCommand = RecordCommand;
            started = true;
        }
        else if (started)
        {
            memory.PerfRdpCommand = null;
            Save("end");
            File.WriteAllText(Path.Combine(directory, "rsp-slice.json"), JsonSerializer.Serialize(new
            {
                taskType, resume = resumeAtStart, budget = budgetAtStart,
                instructions = count, stopReason = reason,
                rdpCommands = commandCount,
                rdpSha256 = Convert.ToHexString(commandHash.GetHashAndReset()),
#if N64_LIVE_GPU
                gpuState = memory.GpuRenderer != null
#else
                gpuState = false
#endif
            }));
            finished = true;
            Console.WriteLine($"rspSliceCaptured=True type={taskType} instructions={count} stop={reason}");
        }
    }

    private void RecordCommand(uint[] words)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(length, words.Length);
        commandHash.AppendData(length);
        commandHash.AppendData(MemoryMarshal.AsBytes(words.AsSpan()));
        commandCount++;
    }

    private void Save(string suffix)
    {
        using (var writer = new BinaryWriter(File.Create(Path.Combine(directory, $"rsp-slice-{suffix}.bin"))))
            R4300.SaveState(writer);
        File.WriteAllBytes(Path.Combine(directory, $"rsp-slice-{suffix}-registers.bin"),
            RspTaskCapture.SaveRegisters(rspField.GetValue(memory)!));
    }

    public void Dispose() { memory.PerfRspSliceCapture = null; memory.PerfRdpCommand = null; commandHash.Dispose(); }

    private delegate bool ExecuteSlice(out uint count, out string reason, bool resume, uint budget);

#if N64_LIVE_GPU
    // The GPU snapshot is part of a CPU savestate, but this renderer does no
    // GPU work. Full-state equality rejects a slice that actually changes it.
    private sealed class SnapshotGpu : IRdpGpuRenderer
    {
        private byte[] state = Array.Empty<byte>();
        public int Commands { get; private set; }
        public string Status => "rsp-slice-snapshot";
        public void Command(ReadOnlySpan<uint> words) { Commands++; }
        public void RdramWritten(uint address, uint length) { }
        public void Synchronize() { }
        public byte[] SaveState() => (byte[])state.Clone();
        public void LoadState(byte[] value) { state = (byte[])value.Clone(); Commands = 0; }
        public void Dispose() { }
    }
#endif

    internal static void Benchmark(string directory)
    {
        byte[] start = File.ReadAllBytes(Path.Combine(directory, "rsp-slice-start.bin"));
        byte[] end = File.ReadAllBytes(Path.Combine(directory, "rsp-slice-end.bin"));
        byte[] registers = File.ReadAllBytes(Path.Combine(directory, "rsp-slice-start-registers.bin"));
        byte[] endRegisters = File.ReadAllBytes(Path.Combine(directory, "rsp-slice-end-registers.bin"));
        using var metadata = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(directory, "rsp-slice.json")));
        bool resume = metadata.RootElement.GetProperty("resume").GetBoolean();
        uint budget = metadata.RootElement.GetProperty("budget").GetUInt32();
        uint expectedCount = metadata.RootElement.GetProperty("instructions").GetUInt32();
        string expectedReason = metadata.RootElement.GetProperty("stopReason").GetString()!;
        bool gpuState = metadata.RootElement.GetProperty("gpuState").GetBoolean();
        int expectedCommands = metadata.RootElement.GetProperty("rdpCommands").GetInt32();
        string expectedCommandHash = metadata.RootElement.GetProperty("rdpSha256").GetString()!;
        bool liveGpu = Environment.GetEnvironmentVariable("N64_PROBE_RSP_SLICE_LIVE_GPU") == "1";

        var memory = new Memory(new byte[4096]);
        R4300.memory = memory;
#if N64_LIVE_GPU
        if (gpuState)
        {
            if (liveGpu)
            {
                string library = Environment.GetEnvironmentVariable("EUTHERDRIVE_N64_GPU_LIBRARY")
                    ?? throw new InvalidOperationException("Set EUTHERDRIVE_N64_GPU_LIBRARY for the live GPU oracle");
                memory.AttachGpuForState(new Ryu64Core.N64LiveGpu(memory, library, false, 64, true));
            }
            else memory.AttachGpuForState(new SnapshotGpu());
        }
#else
        if (gpuState) throw new InvalidOperationException("GPU-backed fixture requires -p:N64LiveGpu=true");
#endif
        object rsp = typeof(Memory).GetField("_rspInterpreter", Private)!.GetValue(memory)!;
        var execute = rsp.GetType().GetMethod("ExecuteSlice", Private)!.CreateDelegate<ExecuteSlice>(rsp);
        using var input = new MemoryStream(start, false);
        using var reader = new BinaryReader(input);
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output);
        var samples = new List<double>();
        int warmup = liveGpu ? 0 : 100, measured = liveGpu ? 1 : 1000;
        int spOffset = -1, ramOffset = -1;
        int[] aiOffsets = Array.Empty<int>();
        using var commandHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        int commandCount = 0;
        Action<uint[]> recordCommand = words =>
        {
            Span<byte> length = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(length, words.Length);
            commandHash.AppendData(length);
            commandHash.AppendData(MemoryMarshal.AsBytes(words.AsSpan()));
            commandCount++;
        };
        for (int iteration = -warmup; iteration < measured; iteration++)
        {
            input.Position = 0;
            R4300.LoadState(reader);
            RspTaskCapture.LoadRegisters(rsp, registers);
            if (iteration == -warmup)
            {
                spOffset = start.AsSpan().IndexOf(memory.SP_MEM_RW);
                ramOffset = start.AsSpan().IndexOf(memory.RDRAM.AsSpan(0, 4096));
                output.SetLength(0);
                R4300.SaveState(writer); writer.Flush();
                byte[] restored = output.ToArray();
                if (!restored.AsSpan().SequenceEqual(start))
                {
                    int first = Enumerable.Range(0, Math.Min(restored.Length, start.Length)).FirstOrDefault(i => restored[i] != start[i], -1);
                    throw new InvalidDataException($"RSP slice start does not round-trip at byte {first}, lengths {restored.Length}/{start.Length}");
                }
                for (int i = 0; i < memory.AI_LEN_REG_RW.Length; i++) memory.AI_LEN_REG_RW[i] ^= 0xff;
                output.SetLength(0);
                R4300.SaveState(writer); writer.Flush();
                byte[] marked = output.ToArray();
                aiOffsets = Enumerable.Range(0, start.Length).Where(i => start[i] != marked[i]).ToArray();
                for (int i = 0; i < memory.AI_LEN_REG_RW.Length; i++) memory.AI_LEN_REG_RW[i] ^= 0xff;
                if (aiOffsets.Length != 4) throw new InvalidDataException("Could not locate serialized AI length register");
            }
            // Hash the command stream once for correctness. Subsequent timed
            // iterations keep that diagnostic work off the RSP hot path.
            bool checkCommands = iteration == -warmup;
            memory.PerfRdpCommand = checkCommands ? recordCommand : null;
            if (checkCommands) { commandHash.GetHashAndReset(); commandCount = 0; }
            long begin = Stopwatch.GetTimestamp();
            bool complete = execute(out uint count, out string reason, resume, budget);
            double elapsed = Stopwatch.GetElapsedTime(begin).TotalMilliseconds;
            if (complete != (expectedReason != "slice") || count != expectedCount || reason != expectedReason)
                throw new InvalidDataException($"RSP slice control differs: {count}/{reason}, expected {expectedCount}/{expectedReason}");
            if (checkCommands)
            {
                string actualCommandHash = Convert.ToHexString(commandHash.GetHashAndReset());
                if (commandCount != expectedCommands || actualCommandHash != expectedCommandHash)
                    throw new InvalidDataException($"RDP commands differ: {commandCount}/{actualCommandHash}, expected {expectedCommands}/{expectedCommandHash}");
            }
            if (!RspTaskCapture.SaveRegisters(rsp).AsSpan().SequenceEqual(endRegisters))
                throw new InvalidDataException("RSP slice registers differ");
            output.SetLength(0);
            R4300.SaveState(writer); writer.Flush();
            byte[] actual = output.ToArray();
            // Graphics slices publish GPU-owned RDRAM and renderer snapshots.
            // The snapshot renderer checks CPU/SP/MMIO, RSP registers, and the
            // exact command stream. A separate live-GPU replay checks all bytes.
            int compareLimit = gpuState && !liveGpu && expectedCommands > 0 ? ramOffset : end.Length;
            bool acceptable = true;
            int mismatch = -1;
            for (int i = 0; i < actual.Length && i < compareLimit; i++)
            {
                if (actual[i] == end[i] || (i >= aiOffsets[0] && i <= aiOffsets[3])) continue;
                acceptable = false; mismatch = i; break;
            }
            if (actual.Length != end.Length || !acceptable)
            {
                File.WriteAllBytes(Path.Combine(directory, "rsp-slice-replay-mismatch.bin"), actual);
                throw new InvalidDataException($"RSP slice state differs at byte {mismatch}, SP offset {spOffset}, RAM offset {ramOffset}, lengths {actual.Length}/{end.Length}");
            }
#if N64_LIVE_GPU
            if (gpuState && !liveGpu && ((SnapshotGpu)memory.GpuRenderer!).Commands != expectedCommands)
                throw new InvalidDataException("Snapshot renderer command count differs");
#endif
            if (iteration >= 0) samples.Add(elapsed);
        }
        samples.Sort();
        Console.WriteLine($"rspSliceBench=passed instructions={expectedCount} stop={expectedReason} " +
            $"medianMs={(samples[(measured - 1) / 2] + samples[measured / 2]) / 2:F4} minMs={samples[0]:F4} maxMs={samples[^1]:F4} " +
            $"oracle={(gpuState && !liveGpu && expectedCommands > 0 ? "cpu-sp-rsp-rdp" : "full-except-ai-len")} " +
            $"rdpCommands={expectedCommands} sha256={Convert.ToHexString(SHA256.HashData(end))}");
#if N64_RSP_PROFILE
        Console.WriteLine("rspSliceProfile=" + memory.GetRspBlockProfile());
#endif
    }
}
#endif
