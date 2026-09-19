using System.Buffers.Binary;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using Ryu64.MIPS;

// Fixed-work renderer benchmark: replay the same complete command tape from
// the same state each time. State restoration and hashing are outside timing.
internal static class RdpBenchmark
{
    private record Command(uint Start, bool Xbus, byte[] Bytes);

    internal static void Run(string directory, string reference)
    {
        if (!File.Exists(Path.Combine(directory, "rdp-final.ppm")))
            throw new InvalidDataException("Capture did not reach FullSync");
        byte[] initial = File.ReadAllBytes(Path.Combine(directory, "rdp-start.bin"));
        var commands = new List<Command>();
        using (var tape = new BinaryReader(File.OpenRead(Path.Combine(directory, "rdp-tape.bin"))))
        {
            while (tape.BaseStream.Position < tape.BaseStream.Length)
            {
                uint start = tape.ReadUInt32();
                bool xbus = tape.ReadBoolean();
                int length = tape.ReadInt32();
                if (length <= 0 || length > 0x20000) throw new InvalidDataException("Invalid RDP tape record");
                byte[] bytes = tape.ReadBytes(length);
                if (bytes.Length != length) throw new EndOfStreamException();
                commands.Add(new Command(start, xbus, bytes));
            }
        }

        string Measure(Assembly assembly, string label)
        {
            var memoryType = assembly.GetType("Ryu64.MIPS.Memory")!;
            object memory = Activator.CreateInstance(memoryType, new object[] { new byte[4096] })!;
            assembly.GetType("Ryu64.MIPS.R4300")!.GetField("memory")!.SetValue(null, memory);
            var load = memoryType.GetMethod("LoadState")!.CreateDelegate<Action<BinaryReader>>(memory);
            var save = memoryType.GetMethod("SaveState")!.CreateDelegate<Action<BinaryWriter>>(memory);
            var execute = memoryType.GetMethod("ExecuteRdpDisplayList", BindingFlags.Instance | BindingFlags.NonPublic)!
                .CreateDelegate<Func<uint, uint, uint>>(memory);
            using var input = new BinaryReader(new MemoryStream(initial, false));
            using var output = new MemoryStream();
            using var writer = new BinaryWriter(output, System.Text.Encoding.UTF8, true);
            var elapsed = new List<double>();
            string digest = "";
            // Give tiered JIT time to settle before measuring small renderer
            // changes; the command tape and restored state stay identical.
            const int warmupIterations = 60, measuredIterations = 40;
            for (int iteration = -warmupIterations; iteration < measuredIterations; iteration++)
            {
                input.BaseStream.Position = 0;
                load(input);
                byte[] rdram = (byte[])memoryType.GetField("RDRAM")!.GetValue(memory)!;
                byte[] sp = (byte[])memoryType.GetField("SP_MEM_RW")!.GetValue(memory)!;
                byte[] dpc = (byte[])memoryType.GetField("DPC_STATUS_REG_R")!.GetValue(memory)!;
                long startTime = Stopwatch.GetTimestamp();
                foreach (var command in commands)
                {
                    if (command.Xbus)
                        for (int i = 0; i < command.Bytes.Length; i++) sp[(command.Start + i) & 0xfff] = command.Bytes[i];
                    else
                        Buffer.BlockCopy(command.Bytes, 0, rdram, checked((int)command.Start), command.Bytes.Length);
                    uint status = BinaryPrimitives.ReadUInt32BigEndian(dpc);
                    BinaryPrimitives.WriteUInt32BigEndian(dpc, command.Xbus ? status | 1u : status & ~1u);
                    uint end = command.Start + (uint)command.Bytes.Length;
                    if (execute(command.Start, end) != end) throw new Exception("Incomplete RDP command");
                }
                double milliseconds = Stopwatch.GetElapsedTime(startTime).TotalMilliseconds;
                output.SetLength(0);
                save(writer);
                writer.Flush();
                string next = Convert.ToHexString(SHA256.HashData(output.GetBuffer().AsSpan(0, (int)output.Length)));
                if (digest != "" && digest != next) throw new Exception("Replay is not deterministic across restored runs");
                digest = next;
                if (iteration >= 0) elapsed.Add(milliseconds);
            }
            elapsed.Sort();
            // Reference ALC loading changes JIT/static-access costs. Validate
            // there, but compare speed using separate default-context processes.
            string timing = label == "reference" ? "validationOnly=True"
                : $"minMs={elapsed[0]:F3} medianMs={(elapsed[measuredIterations / 2 - 1] + elapsed[measuredIterations / 2]) / 2:F3} maxMs={elapsed[^1]:F3}";
            Console.WriteLine($"rdpBench={label} chunks={commands.Count} runs={elapsed.Count} {timing} fullStateSha256={digest}");
            return digest;
        }

        string expected = null;
        if (reference != null)
        {
            var context = new AssemblyLoadContext("reference-rdp-bench", isCollectible: true);
            expected = Measure(context.LoadFromAssemblyPath(Path.GetFullPath(reference)), "reference");
            context.Unload();
        }
        string actual = Measure(typeof(Memory).Assembly, "current");
        if (expected != null && actual != expected) throw new Exception("RDP full state differs from reference");
        if (expected != null) Console.WriteLine("rdpFullStateDifferential=passed");
    }
}
