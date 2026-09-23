using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using Ryu64.MIPS;

// Replay actual game instructions with fixed work and interrupt service. Compare
// the entire serialized CPU/device/RAM state across builds, outside timed work.
internal static class CpuStateBenchmark
{
    internal static void Run(string path, string romPath, bool profile = false)
    {
        uint instructionLimit = 20_000_000;
        string? requestedWork = Environment.GetEnvironmentVariable("N64_PROBE_REPLAY_INSTRUCTIONS");
        if (requestedWork != null && (!uint.TryParse(requestedWork, out instructionLimit)
            || instructionLimit < 32 || instructionLimit > 1_000_000_000))
            throw new ArgumentException("N64_PROBE_REPLAY_INSTRUCTIONS must be between 32 and 1000000000");
        const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
        var fetch = typeof(R4300).GetMethod("ReadOpcode", flags)!.CreateDelegate<Func<uint, uint>>();
        var service = typeof(R4300).GetMethod("ServiceInterrupts", flags)!.CreateDelegate<Func<uint, bool>>();
        var multiply = typeof(R4300).GetMethod("TryAdvanceMultiplyRoutine", flags)?.CreateDelegate<Func<uint, uint, uint>>();
        var idle = typeof(R4300).GetMethod("TryAdvanceIdleBranchLoop", flags)?.CreateDelegate<Func<uint, uint, uint>>();
        var isIdle = typeof(R4300).GetMethod("IsSelfIdleBranch", flags)?.CreateDelegate<Func<uint, uint, bool>>();
        var block = typeof(R4300).GetMethod("TryAdvanceCpuBlock", flags)?.CreateDelegate<Func<uint, uint, uint, bool, uint>>();
        var cop1 = typeof(R4300).GetMethod("RaiseCop1UnusableException", flags)!.CreateDelegate<Action<uint>>();
        var tlb = typeof(R4300).GetMethod("RaiseTlbRefillException", flags)!.CreateDelegate<Action<uint, uint, bool>>();
        var address = typeof(R4300).GetMethod("RaiseAddressErrorException", flags)!.CreateDelegate<Action<uint, bool, uint>>();
        OpcodeTable.Init();
        byte[] rom = File.ReadAllBytes(romPath);
        if (System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(rom) != 0x80371240)
            throw new InvalidDataException("Replay requires a big-endian .z64 ROM");
        R4300.memory = new Memory(rom);
        using var source = new BinaryReader(new MemoryStream(File.ReadAllBytes(path)));
        var timings = new List<double>();
        using var hostCounters = HostCounters.Create();
        string expected = "";
        var fallbackCounts = new Dictionary<string, long>();
        var blockLengths = new Dictionary<uint, long>();
        var blockStops = new Dictionary<uint, long>();
        for (int run = profile ? 0 : -3; run < (profile ? 1 : 5); run++)
        {
            source.BaseStream.Position = 0;
            R4300.LoadState(source);
            Ryu64.Common.Measure.InstructionCount = 0;
            hostCounters?.Start();
            long start = Stopwatch.GetTimestamp();
            while (Ryu64.Common.Measure.InstructionCount < instructionLimit)
            {
                uint pc = Registers.R4300.PC;
                if (service(pc)) continue;
                try
                {
                    uint opcode = fetch(pc);
                    if (opcode == 0xafa40000u && multiply != null
                        && multiply(pc, (uint)(instructionLimit - Ryu64.Common.Measure.InstructionCount)) != 0) continue;
                    if (idle != null && isIdle != null && isIdle(pc, opcode)
                        && idle(pc, (uint)(instructionLimit - Ryu64.Common.Measure.InstructionCount)) != 0) continue;
                    if (block != null && pc >= 0x80004000u && pc < 0xc0000000u)
                    {
                        uint length = block(pc, opcode, (uint)(instructionLimit - Ryu64.Common.Measure.InstructionCount), false);
                        if (profile) blockLengths[length] = blockLengths.GetValueOrDefault(length) + 1;
                        if (profile && length != 0)
                        {
                            uint nextPc = Registers.R4300.PC;
                            uint physical = nextPc & 0x1fffffffu;
                            byte[] ram = R4300.memory.RDRAM;
                            // Observe RAM directly: a diagnostic fetch must not
                            // perform an extra device read or TLB translation.
                            if (nextPc >= 0x80000000u && nextPc < 0xc0000000u && physical + 4 <= ram.Length)
                            {
                                uint next = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(ram.AsSpan((int)physical,4));
                                blockStops[next] = blockStops.GetValueOrDefault(next) + 1;
                            }
                        }
                        if (length != 0) continue;
                    }
                    if (profile)
                    {
                        string name = OpcodeTable.GetOpcodeInfo(opcode).Interpret.Method.Name;
                        fallbackCounts[name] = fallbackCounts.GetValueOrDefault(name) + 1;
                    }
                    R4300.InterpretOpcode(opcode);
                }
                catch (Exception ex) when (ex.GetType().Name == "Cop1UnusableException") { cop1(pc); }
                catch (Ryu64.Common.Exceptions.TLBMissException ex) { tlb(ex.Address, pc, ex.IsStore); }
                catch (Ryu64.Common.Exceptions.AddressErrorException ex) { address(ex.Address, ex.IsStore, pc); }
            }
            double ms = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            hostCounters?.Stop(run >= 0);
            using var output = new MemoryStream();
            using var writer = new BinaryWriter(output);
            R4300.SaveState(writer); writer.Flush();
            string hash = Convert.ToHexString(SHA256.HashData(output.ToArray()));
            if (expected != "" && expected != hash) throw new Exception("Replay is not deterministic");
            expected = hash;
            if (run >= 0) timings.Add(ms);
            Console.WriteLine($"replayRun={run} elapsedMs={ms:F3} jitCompilations={typeof(R4300).GetField("CpuJitCompilations", flags)?.GetValue(null)} invalidations={typeof(R4300).GetField("CpuJitInvalidations", flags)?.GetValue(null)} rejected={typeof(R4300).GetField("CpuJitRejectedCompilations", flags)?.GetValue(null)}");
        }
        hostCounters?.Report();
        var jitInstructions = typeof(R4300).GetField("CpuJitInstructions", flags);
        if (jitInstructions != null)
            Console.WriteLine($"cpuJitInstructions={jitInstructions.GetValue(null)} compilations={typeof(R4300).GetField("CpuJitCompilations", flags)!.GetValue(null)}");
        timings.Sort();
        if (profile)
        {
            Console.WriteLine("fallbackCounts=" + string.Join(",", fallbackCounts.OrderByDescending(x => x.Value).Select(x => $"{x.Key}:{x.Value}")));
            Console.WriteLine("blockLengths=" + string.Join(",", blockLengths.OrderBy(x => x.Key).Select(x => $"{x.Key}:{x.Value}")));
            Console.WriteLine("blockStopOpcodes=" + string.Join(",", blockStops.OrderByDescending(x => x.Value).Take(40).Select(x => $"{x.Key:x8}:{x.Value}")));
        }
        string measurement = profile ? "gameProfile instrumentedMs" : "gameReplay medianMs";
        Console.WriteLine($"{measurement}={timings[timings.Count / 2]:F3} instructions={Ryu64.Common.Measure.InstructionCount} sha256={expected}");
    }
}
