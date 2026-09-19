using System.Buffers.Binary;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using Ryu64.MIPS;

// Exercise the real CPU loop with fixed work, including fetch, polling-loop
// detection, dispatch and timing. A probe-only SYNC handler stops at an exact
// instruction boundary; the production core needs no benchmark hooks.
internal static class CpuThreadBenchmark
{
    internal static void Run()
    {
        const int iterations = 1_000_000;
        const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
        OpcodeTable.Init();
        var table = (OpcodeTable.InstInfo[][])typeof(OpcodeTable).GetField("FastLookup", flags)!.GetValue(null)!;
        var sync = table[15];
        int index = Array.FindIndex(sync, info => info.Value == 15);
        if (index < 0) throw new Exception("Missing SYNC instruction");
        var original = sync[index];
        var replacement = original;
        replacement.Interpret = desc => { original.Interpret(desc); R4300.R4300_ON = false; };
        sync[index] = replacement;
        var startCpu = typeof(R4300).GetMethod("StartCpuThread", flags)!.CreateDelegate<Action>();
        var threadField = typeof(R4300).GetField("CpuThread", flags)!;
        R4300.memory = new Memory(new byte[4096]);
        Registers.R4300.PC = 0x80001000;
        Registers.R4300.Reg[9] = iterations;
        uint[] code = { 0x24420001, 0x00431826, 0x34645678, 0x00042880,
            0x3c078000, 0x8ce80000, 0xace80004, 0x2529ffff, 0x1520fff7, 0, 15 };
        for (int i = 0; i < code.Length; i++)
            BinaryPrimitives.WriteUInt32BigEndian(R4300.memory.RDRAM.AsSpan(0x1000 + i * 4), code[i]);
        using var initial = new MemoryStream();
        using var writer = new BinaryWriter(initial, System.Text.Encoding.UTF8, true);
        R4300.SaveState(writer);
        writer.Flush();
        using var reader = new BinaryReader(initial, System.Text.Encoding.UTF8, true);
        using var result = new MemoryStream();
        using var resultWriter = new BinaryWriter(result, System.Text.Encoding.UTF8, true);
        var timings = new List<double>();
        string digest = "";
        try
        {
            for (int run = -3; run < 6; run++)
            {
                initial.Position = 0;
                R4300.LoadState(reader);
                Ryu64.Common.Measure.InstructionCount = 0;
                R4300.R4300_ON = true;
                long start = Stopwatch.GetTimestamp();
                startCpu();
                var thread = (Thread)threadField.GetValue(null)!;
                if (!thread.Join(TimeSpan.FromSeconds(30))) throw new TimeoutException("CPU benchmark did not stop");
                double elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                if (Registers.R4300.PC != 0x8000102c || Registers.R4300.Reg[2] != iterations
                    || Registers.R4300.Reg[9] != 0 || R4300.GetUnknownOpcodeCount() != 0
                    || Ryu64.Common.Measure.InstructionCount != iterations * 10UL + 1)
                    throw new Exception("CPU benchmark did not complete the expected instruction sequence");
                result.SetLength(0);
                R4300.SaveState(resultWriter);
                resultWriter.Flush();
                string hash = Convert.ToHexString(SHA256.HashData(result.GetBuffer().AsSpan(0, (int)result.Length)));
                if (digest != "" && hash != digest) throw new Exception("CPU thread replay is nondeterministic");
                digest = hash;
                if (run >= 0) timings.Add(elapsed);
            }
        }
        finally
        {
            R4300.StopR4300();
            sync[index] = original;
        }
        timings.Sort();
        Console.WriteLine($"cpuThreadBench instructions={iterations * 10 + 1} runs=6 " +
            $"medianMs={(timings[2] + timings[3]) / 2:F3} minMs={timings[0]:F3} maxMs={timings[^1]:F3} " +
            $"fullStateSha256={digest}");
    }
}
