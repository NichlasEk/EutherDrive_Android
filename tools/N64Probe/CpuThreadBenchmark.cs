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
    internal static void Run(bool multiplyRoutine = false, bool cpuBlock = false, bool cop1Block = false)
    {
        int iterations = multiplyRoutine ? 250_000 : 1_000_000;
        ulong instructionTotal = (ulong)iterations * (multiplyRoutine ? 17UL : 10UL) + 1;
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
        int codeBase = cpuBlock ? 0x10000 : 0x1000;
        Registers.R4300.PC = 0x80000000u + (uint)codeBase;
        if (cpuBlock) R4300.memory.WriteUInt32(0xa4400018, 524);
        Registers.R4300.Reg[9] = (ulong)iterations;
        uint[] code = { 0x24420001, 0x00431826, 0x34645678, 0x00042880,
            0x3c078000, 0x8ce80000, 0xace80004, 0x2529ffff, 0x1520fff7, 0, 15 };
        if (cpuBlock)
        {
            code = new uint[] { 0x24420001,0x00431826,0x34645678,0x00042880,
                0x3c078000,0x8ce80000,0xace80004,0x400b0800,0x2529ffff,0x1520fff6,0x400a4800,15 };
            instructionTotal = (ulong)iterations * 11 + 1;
        }
        if (cop1Block)
        {
            code = new uint[] { 0x24420001,0x00431826,0x34645678,0x00042880,
                0x3c078000,0x8ce80000,0xace80004,0x44881000,0x46021100,0x440c2000,
                0x400b0800,0x2529ffff,0x1520fff3,0x400a4800,15 };
            Registers.COP0.Reg[12] = 0x24000000;
            R4300.memory.WriteUInt32(0x80000000,0x3f800000);
            instructionTotal = (ulong)iterations * 14 + 1;
        }
        if (multiplyRoutine)
        {
            code = new uint[] { 0x0c000440, 0, 0x2529ffff, 0x1520fffc, 0, 15 };
            uint[] multiply = { 0xafa40000, 0xafa50004, 0xafa60008, 0xafa7000c,
                0xdfaf0008, 0xdfae0000, 0x01cf001d, 0x00001012,
                0x0002183c, 0x0003183f, 0x03e00008, 0x0002103f };
            for (int i = 0; i < multiply.Length; i++)
                BinaryPrimitives.WriteUInt32BigEndian(R4300.memory.RDRAM.AsSpan(0x1100 + i * 4), multiply[i]);
            Registers.R4300.Reg[29] = 0xffffffff80002000;
            Registers.R4300.Reg[4] = 0x12345678;
            Registers.R4300.Reg[5] = 0x87654321;
            Registers.R4300.Reg[6] = 0xffeeddcc;
            Registers.R4300.Reg[7] = 0x12345678;
            R4300.memory.WriteUInt32(0xa4400018, 524);
        }
        for (int i = 0; i < code.Length; i++)
            BinaryPrimitives.WriteUInt32BigEndian(R4300.memory.RDRAM.AsSpan(codeBase + i * 4), code[i]);
        using var initial = new MemoryStream();
        using var writer = new BinaryWriter(initial, System.Text.Encoding.UTF8, true);
        R4300.SaveState(writer);
        writer.Flush();
        using var reader = new BinaryReader(initial, System.Text.Encoding.UTF8, true);
        using var result = new MemoryStream();
        using var resultWriter = new BinaryWriter(result, System.Text.Encoding.UTF8, true);
        var timings = new List<double>();
        using var hostCounters = HostCounters.Create();
        string digest = "", historyDigest = "";
        try
        {
            for (int run = -3; run < 6; run++)
            {
                initial.Position = 0;
                R4300.LoadState(reader);
                Ryu64.Common.Measure.InstructionCount = 0;
                if (multiplyRoutine || cpuBlock)
                {
                    typeof(R4300).GetField("_recentInstPos", flags)!.SetValue(null, 0);
                    Array.Clear((Array)typeof(R4300).GetField("_recentInst", flags)!.GetValue(null)!);
                }
                R4300.R4300_ON = true;
                hostCounters?.Start();
                long start = Stopwatch.GetTimestamp();
                startCpu();
                var thread = (Thread)threadField.GetValue(null)!;
                if (!thread.Join(TimeSpan.FromSeconds(30))) throw new TimeoutException("CPU benchmark did not stop");
                double elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                hostCounters?.Stop(run >= 0);
                if (Registers.R4300.PC != (0x80000000u + (uint)codeBase + (multiplyRoutine ? 0x18u : cop1Block ? 0x3cu : cpuBlock ? 0x30u : 0x2cu)) || (!multiplyRoutine && Registers.R4300.Reg[2] != (ulong)iterations)
                    || Registers.R4300.Reg[9] != 0 || R4300.GetUnknownOpcodeCount() != 0
                    || Ryu64.Common.Measure.InstructionCount != instructionTotal)
                    throw new Exception("CPU benchmark did not complete the expected instruction sequence");
                if (cop1Block && Registers.R4300.Reg[12] != 0x40000000)
                    throw new Exception("COP1 thread fixture did not produce 2.0f");
                result.SetLength(0);
                R4300.SaveState(resultWriter);
                resultWriter.Flush();
                string hash = Convert.ToHexString(SHA256.HashData(result.GetBuffer().AsSpan(0, (int)result.Length)));
                if (digest != "" && hash != digest) throw new Exception("CPU thread replay is nondeterministic");
                digest = hash;
                if (multiplyRoutine || cpuBlock)
                {
                    using var history = new MemoryStream(); using var historyWriter = new BinaryWriter(history);
                    historyWriter.Write((int)typeof(R4300).GetField("_recentInstPos", flags)!.GetValue(null)!);
                    foreach (object entry in (Array)typeof(R4300).GetField("_recentInst", flags)!.GetValue(null)!)
                    {
                        historyWriter.Write((uint)entry.GetType().GetField("Pc")!.GetValue(entry)!);
                        historyWriter.Write((uint)entry.GetType().GetField("Op")!.GetValue(entry)!);
                    }
                    historyWriter.Flush();
                    string h = Convert.ToHexString(SHA256.HashData(history.ToArray()));
                    if (historyDigest != "" && historyDigest != h) throw new Exception("CPU history differs between runs");
                    historyDigest = h;
                }
                if (run >= 0) timings.Add(elapsed);
            }
        }
        finally
        {
            R4300.StopR4300();
            sync[index] = original;
        }
        hostCounters?.Report();
        timings.Sort();
        Console.WriteLine($"cpuThreadBench instructions={instructionTotal} runs=6 " +
            $"medianMs={(timings[2] + timings[3]) / 2:F3} minMs={timings[0]:F3} maxMs={timings[^1]:F3} " +
            $"fullStateSha256={digest} historySha256={historyDigest}");
    }
}
