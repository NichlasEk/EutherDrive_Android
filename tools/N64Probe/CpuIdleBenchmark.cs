using System.Diagnostics;
using System.Buffers.Binary;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using Ryu64.MIPS;

internal static class CpuIdleBenchmark
{
    internal static void Run(string statePath, string reference)
    {
        byte[] state = File.ReadAllBytes(statePath);
        string Measure(Assembly assembly, string label)
        {
            var cpu = assembly.GetType("Ryu64.MIPS.R4300")!;
            var memory = assembly.GetType("Ryu64.MIPS.Memory")!;
            cpu.GetField("memory")!.SetValue(null, Activator.CreateInstance(memory, new object[] { new byte[4096] }));
            assembly.GetType("Ryu64.MIPS.OpcodeTable")!.GetMethod("Init")!.Invoke(null, null);
            var interpret = cpu.GetMethod("InterpretOpcode")!.CreateDelegate<Action<uint>>();
            var load = cpu.GetMethod("LoadState")!.CreateDelegate<Action<BinaryReader>>();
            var save = cpu.GetMethod("SaveState")!.CreateDelegate<Action<BinaryWriter>>();
            var pc = assembly.GetType("Ryu64.MIPS.Registers+R4300")!.GetField("PC")!;
            var fetch = cpu.GetMethod("ReadOpcode", BindingFlags.Static | BindingFlags.NonPublic)!.CreateDelegate<Func<uint, uint>>();
            using var input = new BinaryReader(new MemoryStream(state));
            using var output = new MemoryStream();
            using var writer = new BinaryWriter(output);
            var times = new List<double>();
            string digest = "";
            const int warmups = 40, measurements = 12;
            for (int run = -warmups; run < measurements; run++)
            {
                input.BaseStream.Position = 0;
                load(input);
                pc.SetValue(null, 0x70001938u);
                if (fetch(0x70001938) != 0x1000ffff || fetch(0x7000193c) != 0)
                    throw new Exception("Saved state does not contain the expected mapped idle loop");
                Ryu64.Common.Measure.InstructionCount = 0;
                long start = Stopwatch.GetTimestamp();
                // Fixed instruction work: device ticks and CP0 state advance
                // normally; the outer thread's interrupt service is excluded.
                for (int i = 0; i < 500_000; i++) interpret(0x1000ffff);
                double elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                if ((uint)pc.GetValue(null)! != 0x70001938 || Ryu64.Common.Measure.InstructionCount != 1_000_000)
                    throw new Exception("Idle benchmark instruction sequence changed");
                output.SetLength(0); save(writer); writer.Flush();
                string hash = Convert.ToHexString(SHA256.HashData(output.GetBuffer().AsSpan(0, (int)output.Length)));
                if (digest != "" && hash != digest) throw new Exception("Idle replay is nondeterministic");
                digest = hash;
                if (run >= 0) times.Add(elapsed);
            }
            times.Sort();
            Console.WriteLine($"idleBench={label} instructions=1000000 "
                + (label == "reference" ? "validationOnly=True" : $"medianMs={(times[5] + times[6]) / 2:F3} minMs={times[0]:F3} maxMs={times[^1]:F3}")
                + $" fullStateSha256={digest}");
            // Exercise idle instructions at Count/RANDOM boundaries,
            // with live delay-slot writes, unsupported code and a TLB miss.
            Type registers = assembly.GetType("Ryu64.MIPS.Registers+R4300")!;
            Type cop0 = assembly.GetType("Ryu64.MIPS.Registers+COP0")!;
            Type tlb = assembly.GetType("Ryu64.MIPS.TLB")!;
            var translate = tlb.GetMethod("TranslateAddress", new[] { typeof(uint) })!.CreateDelegate<Func<uint, uint>>();
            using var edges = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            int edgeCases = 0;
            foreach (uint opcode in new uint[] { 0, 0x1000ffff })
            foreach (uint count in new uint[] { 0, 1, 0xfffffffe, 0xffffffff })
            foreach (uint delay in new uint[] { 0, 0x24420001, 0xac820000, 0x70000000, 0xffffffff })
            {
                input.BaseStream.Position = 0; load(input);
                var regs = (ulong[])registers.GetField("Reg")!.GetValue(null)!;
                var control = (ulong[])cop0.GetField("Reg")!.GetValue(null)!;
                void Control(string name, ulong value) => control[(int)cop0.GetField(name)!.GetRawConstantValue()!] = value;
                pc.SetValue(null, 0x70001938u);
                regs[0] = 123; regs[4] = 0xffffffff80005000;
                Control("COUNT_REG", count); Control("COMPARE_REG", unchecked(count + 1));
                Control("WIRED_REG", count & 31); Control("RANDOM_REG", count & 31);
                cpu.GetField("Count", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, (ulong)count * 2 + 1);
                var mem = cpu.GetField("memory")!.GetValue(null)!;
                var ram = (byte[])memory.GetField("RDRAM")!.GetValue(mem)!;
                uint physical = translate(0x7000193c) & 0x1fffffff;
                BinaryPrimitives.WriteUInt32BigEndian(ram.AsSpan((int)physical), delay);
                if (delay == 0xffffffff) tlb.GetMethod("Reset")!.Invoke(null, null);
                Ryu64.Common.Measure.InstructionCount = 0;
                try { for (int i = 0; i < 8; i++) interpret(opcode); }
                catch (Exception ex) { edges.AppendData(System.Text.Encoding.UTF8.GetBytes(ex.GetType().FullName!)); }
                output.SetLength(0); save(writer); writer.Flush();
                edges.AppendData(output.GetBuffer().AsSpan(0, (int)output.Length));
                edges.AppendData(BitConverter.GetBytes(Ryu64.Common.Measure.InstructionCount));
                edgeCases++;
            }
            string edgeHash = Convert.ToHexString(edges.GetHashAndReset());
            Console.WriteLine($"idleEdgeCases={edgeCases} label={label} sha256={edgeHash}");
            return digest + ":" + edgeHash;
        }
        string expected = null;
        if (reference != null)
        {
            var context = new AssemblyLoadContext("reference-idle", true);
            expected = Measure(context.LoadFromAssemblyPath(Path.GetFullPath(reference)), "reference");
            context.Unload();
        }
        string actual = Measure(typeof(Memory).Assembly, "current");
        if (expected != null && actual != expected) throw new Exception("Mapped idle CPU/memory state differs");
        if (expected != null) Console.WriteLine("idleDifferential=passed");
    }
}
