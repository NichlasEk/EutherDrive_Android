using System.Diagnostics;
using System.Security.Cryptography;
using Ryu64.MIPS;

// Fixed CPU work including normal instruction timing and memory ticks. Compare
// separate processes/core DLLs, not reference AssemblyLoadContext timings.
internal static class CpuDispatchBenchmark
{
    internal static void Run()
    {
        OpcodeTable.Init();
        R4300.memory = new Memory(new byte[4096]);
        Registers.R4300.PC = 0x80000000;
        for (int i = 1; i < 32; i++) Registers.R4300.Reg[i] = (ulong)i;
        using var initial = new MemoryStream();
        using var writer = new BinaryWriter(initial, System.Text.Encoding.UTF8, true);
        R4300.SaveState(writer);
        writer.Flush();
        using var reader = new BinaryReader(initial, System.Text.Encoding.UTF8, true);
        using var output = new MemoryStream();
        using var resultWriter = new BinaryWriter(output, System.Text.Encoding.UTF8, true);
        uint[] instructions = { 0x24420001, 0x00431826, 0x34645678, 0x00042880,
            0x0045302b, 0x3c078000, 0x8ce80000, 0xace80004 };
        var elapsed = new List<double>();
        string digest = "";
        for (int run = -2; run < 8; run++)
        {
            initial.Position = 0;
            R4300.LoadState(reader);
            long start = Stopwatch.GetTimestamp();
            for (int i = 0; i < 1_000_000; i++) R4300.InterpretOpcode(instructions[i & 7]);
            double milliseconds = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            if (run >= 0) elapsed.Add(milliseconds);
            output.SetLength(0);
            R4300.SaveState(resultWriter);
            resultWriter.Flush();
            string next = Convert.ToHexString(SHA256.HashData(output.GetBuffer().AsSpan(0, (int)output.Length)));
            if (digest != "" && next != digest) throw new Exception("CPU dispatch replay is nondeterministic");
            digest = next;
        }
        elapsed.Sort();
        Console.WriteLine($"cpuDispatchBench instructions=1000000 runs=8 medianMs={(elapsed[3] + elapsed[4]) / 2:F3} minMs={elapsed[0]:F3} maxMs={elapsed[^1]:F3} fullStateSha256={digest}");
    }
}
