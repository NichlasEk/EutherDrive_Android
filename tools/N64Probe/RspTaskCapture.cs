#nullable enable
using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using Ryu64.MIPS;

// Diagnostic-only capture through existing synchronous task logging. Includes
// private RSP state missing from the emulator's ordinary savestate format.
internal sealed class RspTaskCapture : TextWriter
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private readonly TextWriter previous = Console.Out;
    private readonly string directory;
    private readonly int taskType;
    private bool started, finished;
    public override Encoding Encoding => previous.Encoding;
    internal RspTaskCapture(string directory)
    {
        this.directory = directory;
        taskType = Environment.GetEnvironmentVariable("N64_PROBE_CAPTURE_RSP_TYPE") == "2" ? 2 : 1;
        if (new[] { "rsp-start.bin", "rsp-end.bin", "rsp-start-registers.bin", "rsp-end-registers.bin" }
            .Any(name => File.Exists(Path.Combine(directory, name)))) throw new IOException("Use a fresh capture directory");
        Console.SetOut(this);
    }
    public override void Write(string? value) { previous.Write(value); if (value != null) Observe(value); }
    public override void WriteLine(string? value) { previous.WriteLine(value); if (value != null) Observe(value); }
    private void Observe(string value)
    {
        if (finished) return;
        bool start = !started && value.StartsWith($"[N64IO] RSP interpreter dispatch type={taskType} ");
        bool end = started && value.StartsWith($"[N64IO] RSP interpreter task type={taskType} validTask=True ");
        if (!start && !end) return;
        string suffix = start ? "start" : "end";
        using (var writer = new BinaryWriter(File.Create(Path.Combine(directory, $"rsp-{suffix}.bin")))) R4300.SaveState(writer);
        object rsp = typeof(Memory).GetField("_rspInterpreter", Private)!.GetValue(R4300.memory)!;
        File.WriteAllBytes(Path.Combine(directory, $"rsp-{suffix}-registers.bin"), SaveRegisters(rsp));
        started = true;
        if (end) { finished = true; previous.WriteLine("rspTaskCaptured=True"); }
    }
    protected override void Dispose(bool disposing) { Console.SetOut(previous); base.Dispose(disposing); }

    internal static byte[] SaveRegisters(object rsp)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        foreach (var field in rsp.GetType().GetFields(Private).Where(f => f.Name != "_memory" && !f.IsDefined(typeof(NonSerializedAttribute), false)).OrderBy(f => f.Name, StringComparer.Ordinal))
        {
            writer.Write(field.Name);
            object value = field.GetValue(rsp)!;
            if (value is Array array)
            {
                byte[] bytes = new byte[Buffer.ByteLength(array)];
                Buffer.BlockCopy(array, 0, bytes, 0, bytes.Length);
                writer.Write(bytes.Length);
                writer.Write(bytes);
            }
            else { writer.Write(-1); writer.Write(Convert.ToUInt64(value)); }
        }
        return stream.ToArray();
    }
    private static void LoadRegisters(object rsp, byte[] bytes)
    {
        using var reader = new BinaryReader(new MemoryStream(bytes, false));
        while (reader.BaseStream.Position < reader.BaseStream.Length)
        {
            var field = rsp.GetType().GetField(reader.ReadString(), Private) ?? throw new InvalidDataException("Missing RSP field");
            int size = reader.ReadInt32();
            if (size < 0) field.SetValue(rsp, Convert.ChangeType(reader.ReadUInt64(), field.FieldType));
            else
            {
                var array = (Array)field.GetValue(rsp)!;
                if (size != Buffer.ByteLength(array)) throw new InvalidDataException("RSP array size");
                Buffer.BlockCopy(reader.ReadBytes(size), 0, array, 0, size);
            }
        }
    }
    private delegate bool Execute(out uint count, out string reason);
    internal static void Benchmark(string directory, string? reference)
    {
        byte[] start = File.ReadAllBytes(Path.Combine(directory, "rsp-start.bin"));
        byte[] registers = File.ReadAllBytes(Path.Combine(directory, "rsp-start-registers.bin"));
        byte[] expectedEnd = File.ReadAllBytes(Path.Combine(directory, "rsp-end.bin"));
        byte[] expectedRegisters = File.ReadAllBytes(Path.Combine(directory, "rsp-end-registers.bin"));
        void Measure(Assembly assembly, string label)
        {
            Type memoryType = assembly.GetType("Ryu64.MIPS.Memory")!;
            object memory = Activator.CreateInstance(memoryType, new object[] { new byte[4096] })!;
            Type cpu = assembly.GetType("Ryu64.MIPS.R4300")!;
            cpu.GetField("memory")!.SetValue(null, memory);
            var load = cpu.GetMethod("LoadState")!.CreateDelegate<Action<BinaryReader>>();
            var save = cpu.GetMethod("SaveState")!.CreateDelegate<Action<BinaryWriter>>();
            var rspField = memoryType.GetField("_rspInterpreter", Private)!;
            object rsp = Activator.CreateInstance(rspField.FieldType, new[] { memory })!;
            rspField.SetValue(memory, rsp);
            var execute = rsp.GetType().GetMethod("ExecuteTask")!.CreateDelegate<Execute>(rsp);
            var dispatching = memoryType.GetField("_rspTaskDispatching", Private)!;
            var flush = memoryType.GetMethod("FlushVisibleRdpFramebufferSnapshot", Private)!.CreateDelegate<Action>(memory);
            using var reader = new BinaryReader(new MemoryStream(start, false));
            using var output = new MemoryStream();
            using var writer = new BinaryWriter(output);
            var times = new List<double>();
            uint count = 0;
            const int warmupIterations = 5, measuredIterations = 12;
            for (int iteration = -warmupIterations; iteration < measuredIterations; iteration++)
            {
                reader.BaseStream.Position = 0;
                load(reader);
                LoadRegisters(rsp, registers);
                dispatching.SetValue(memory, true);
                long time = Stopwatch.GetTimestamp();
                bool complete = execute(out count, out string reason);
                double elapsed = Stopwatch.GetElapsedTime(time).TotalMilliseconds;
                dispatching.SetValue(memory, false);
                flush();
                if (!complete || reason != "break") throw new Exception($"Incomplete task: {reason}");
                output.SetLength(0);
                save(writer); writer.Flush();
                byte[] actual = output.ToArray();
                if (!actual.SequenceEqual(expectedEnd))
                    throw new Exception($"{label} CPU/memory differs at byte {Enumerable.Range(0, Math.Min(actual.Length, expectedEnd.Length)).FirstOrDefault(i => actual[i] != expectedEnd[i], -1)} lengths={actual.Length}/{expectedEnd.Length}");
                if (!SaveRegisters(rsp).SequenceEqual(expectedRegisters)) throw new Exception($"{label} RSP registers differ");
                if (iteration >= 0) times.Add(elapsed);
            }
            times.Sort();
            if (Environment.GetEnvironmentVariable("EUTHERDRIVE_N64_RSP_BLOCK_JIT") == "1")
                Console.WriteLine($"blockCompilations={rsp.GetType().GetField("_blockCompilations", Private)?.GetValue(rsp)} blockInstructions={rsp.GetType().GetField("_blockInstructions", Private)?.GetValue(rsp)}");
            // A collectible reference load context changes JIT/static-access
            // costs. Use it for correctness only; compare speed in separate
            // processes with the same harness and each core in the default ALC.
            string timing = label == "reference" ? "validationOnly=True"
                : $"medianMs={(times[5] + times[6]) / 2:F3} minMs={times[0]:F3} maxMs={times[^1]:F3}";
            Console.WriteLine($"rspTaskBench={label} instructions={count} {timing} fullState=identical sha256={Convert.ToHexString(SHA256.HashData(expectedEnd))}");
            if (Environment.GetEnvironmentVariable("EUTHERDRIVE_N64_PROFILE_VECTOR_OPS") == "1"
                && rsp.GetType().GetField("_vectorOpCounts", BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null) is long[] counts)
                Console.WriteLine("vectorOps=" + string.Join(",", counts.Select((calls, op) => (calls, op)).Where(p => p.calls != 0).OrderByDescending(p => p.calls).Select(p => $"{p.op:x2}:{p.calls / (warmupIterations + measuredIterations)}")));
        }
        if (reference != null)
        {
            var context = new AssemblyLoadContext("reference-rsp-task", true);
            Measure(context.LoadFromAssemblyPath(Path.GetFullPath(reference)), "reference");
            context.Unload();
        }
        Measure(typeof(Memory).Assembly, "current");
    }
}
