using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using Ryu64.MIPS;

// Replay actual game instructions with fixed work and interrupt service. Compare
// the entire serialized CPU/device/RAM state across builds, outside timed work.
internal static class CpuStateBenchmark
{
    internal static void Run(string path, string romPath)
    {
        const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
        var fetch = typeof(R4300).GetMethod("ReadOpcode", flags)!.CreateDelegate<Func<uint, uint>>();
        var service = typeof(R4300).GetMethod("ServiceInterrupts", flags)!.CreateDelegate<Func<uint, bool>>();
        var multiply = typeof(R4300).GetMethod("TryAdvanceMultiplyRoutine", flags)?.CreateDelegate<Func<uint, uint, uint>>();
        var cop1 = typeof(R4300).GetMethod("RaiseCop1UnusableException", flags)!.CreateDelegate<Action<uint>>();
        OpcodeTable.Init();
        byte[] rom = File.ReadAllBytes(romPath);
        if (System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(rom) != 0x80371240)
            throw new InvalidDataException("Replay requires a big-endian .z64 ROM");
        R4300.memory = new Memory(rom);
        using var source = new BinaryReader(new MemoryStream(File.ReadAllBytes(path)));
        var timings = new List<double>();
        string expected = "";
        for (int run = -3; run < 5; run++)
        {
            source.BaseStream.Position = 0;
            R4300.LoadState(source);
            Ryu64.Common.Measure.InstructionCount = 0;
            long start = Stopwatch.GetTimestamp();
            while (Ryu64.Common.Measure.InstructionCount < 20_000_000)
            {
                uint pc = Registers.R4300.PC;
                if (service(pc)) continue;
                uint opcode = fetch(pc);
                if (opcode == 0xafa40000u && multiply != null
                    && multiply(pc, (uint)(20_000_000 - Ryu64.Common.Measure.InstructionCount)) != 0) continue;
                try { R4300.InterpretOpcode(opcode); }
                catch (Exception ex) when (ex.GetType().Name == "Cop1UnusableException") { cop1(pc); }
            }
            double ms = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            using var output = new MemoryStream();
            using var writer = new BinaryWriter(output);
            R4300.SaveState(writer); writer.Flush();
            string hash = Convert.ToHexString(SHA256.HashData(output.ToArray()));
            if (expected != "" && expected != hash) throw new Exception("Replay is not deterministic");
            expected = hash;
            if (run >= 0) timings.Add(ms);
        }
        timings.Sort();
        Console.WriteLine($"gameReplay medianMs={timings[timings.Count / 2]:F3} instructions={Ryu64.Common.Measure.InstructionCount} sha256={expected}");
    }
}
