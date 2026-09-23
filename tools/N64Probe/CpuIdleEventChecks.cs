using System.Buffers.Binary;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using Ryu64.MIPS;

internal static class CpuIdleEventChecks
{
    internal static void Run(string path, bool benchmarkOnly = false)
    {
        const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
        var batch = typeof(R4300).GetMethod("TryAdvanceIdleBranchLoop", flags)!.CreateDelegate<Func<uint, uint, uint>>();
        var isIdle = typeof(R4300).GetMethod("IsSelfIdleBranch", flags)!.CreateDelegate<Func<uint, uint, bool>>();
        var watchdogBudget = typeof(R4300).GetMethod("GetIdleBranchCycleBudget", flags)!.CreateDelegate<Func<ulong, uint>>();
        var fetch = typeof(R4300).GetMethod("ReadOpcode", flags)!.CreateDelegate<Func<uint, uint>>();
        var service = typeof(R4300).GetMethod("ServiceInterrupts", flags)!.CreateDelegate<Func<uint, bool>>();
        var cop1Fault = typeof(R4300).GetMethod("RaiseCop1UnusableException", flags)!.CreateDelegate<Action<uint>>();
        var countField = typeof(R4300).GetField("Count", flags)!;
        R4300.memory = new Memory(new byte[4096]);
        OpcodeTable.Init();
        byte[] state = File.ReadAllBytes(path);
        using var source = new BinaryReader(new MemoryStream(state));
        using var initial = new MemoryStream();
        using var initialWriter = new BinaryWriter(initial, System.Text.Encoding.UTF8, true);
        using var initialReader = new BinaryReader(initial, System.Text.Encoding.UTF8, true);
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output, System.Text.Encoding.UTF8, true);
        void Load()
        {
            source.BaseStream.Position = 0; R4300.LoadState(source);
            Registers.R4300.PC = 0x70001938;
            Registers.COP0.Reg[Registers.COP0.CAUSE_REG] = 0;
        }
        uint Jump(uint pc) => 0x08000000u | ((pc >> 2) & 0x03ffffffu);
        void LoadJump(uint pc)
        {
            Load(); Registers.R4300.PC = pc;
            uint segment = pc & 0xe0000000u;
            uint address = (segment == 0x80000000u || segment == 0xa0000000u
                ? pc : TLB.TranslateAddress(pc)) & 0x1fffffffu;
            BinaryPrimitives.WriteUInt32BigEndian(R4300.memory.RDRAM.AsSpan((int)address), Jump(pc));
            BinaryPrimitives.WriteUInt32BigEndian(R4300.memory.RDRAM.AsSpan((int)address + 4), 0);
        }
        string Hash()
        {
            output.SetLength(0); R4300.SaveState(writer); writer.Flush();
            return Convert.ToHexString(SHA256.HashData(output.GetBuffer().AsSpan(0, (int)output.Length)));
        }
        int cases = 0, accepted = 0;
        void Check(uint maximum = 65536, bool? expected = null, uint? eventDistance = null)
        {
            initial.SetLength(0); R4300.SaveState(initialWriter); initialWriter.Flush();
            Ryu64.Common.Measure.InstructionCount = 0;
            uint cycles = batch(Registers.R4300.PC, maximum);
            string actual = Hash();
            if (Ryu64.Common.Measure.InstructionCount != cycles || (cycles & 1) != 0 || cycles > maximum
                || expected.HasValue && (cycles != 0) != expected.Value
                || eventDistance.HasValue && cycles != 0 && cycles >= eventDistance.Value)
                throw new Exception($"Invalid batch size in case {cases}: {cycles}");
            initial.Position = 0; R4300.LoadState(initialReader);
            Ryu64.Common.Measure.InstructionCount = 0;
            uint opcode = cycles != 0 ? fetch(Registers.R4300.PC) : 0;
            for (uint i = 0; i < cycles; i += 2) R4300.InterpretOpcode(opcode);
            if (actual != Hash() || Ryu64.Common.Measure.InstructionCount != cycles)
                throw new Exception($"Idle batch state mismatch case {cases}, cycles={cycles}");
            cases++; if (cycles != 0) accepted++;
        }
        if (Environment.GetEnvironmentVariable("N64_PROBE_EXPECT_IDLE_DISABLED") == "1")
        {
            Load(); Check(expected: false);
            Console.WriteLine("idleEventDisabled=passed");
            return;
        }
        if (!benchmarkOnly)
        {
            foreach (uint wired in new uint[] { 0, 1, 16, 30, 31 })
            foreach (uint random in new uint[] { 0, 1, 15, 30, 31 })
            foreach (uint maximum in new uint[] { 0, 2, 4, 30, 64, 65536 })
            {
                Load(); Registers.R4300.Reg[0] = 123;
                Registers.COP0.Reg[Registers.COP0.WIRED_REG] = wired;
                Registers.COP0.Reg[Registers.COP0.RANDOM_REG] = random;
                Check(maximum);
            }
            foreach (uint count in new uint[] { 0, 1, 0xfffffffc, 0xfffffffe, 0xffffffff })
            foreach (uint parity in new uint[] { 0, 1 })
            foreach (uint distance in new uint[] { 0, 1, 2, 10 })
            {
                Load(); countField.SetValue(null, (ulong)count * 2 + parity);
                Registers.COP0.Reg[Registers.COP0.COUNT_REG] = count;
                Registers.COP0.Reg[Registers.COP0.COMPARE_REG] = unchecked(count + distance);
                Check();
            }
            (string Armed, string Remaining)[] timers = {
                ("_spDmaDelayArmed", "_spDmaDelayRemaining"), ("_rspTaskActive", "_rspTaskCyclesRemaining"),
                ("_rspInterruptDelayArmed", "_rspInterruptDelayRemaining"), ("_dpInterruptDelayArmed", "_dpInterruptDelayRemaining"),
                ("_piInterruptDelayArmed", "_piInterruptDelayRemaining"), ("_siInterruptDelayArmed", "_siInterruptDelayRemaining"),
                ("_aiInterruptDelayArmed", "_aiInterruptDelayRemaining") };
            void SetMemory(string name, object value) => typeof(Memory).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(R4300.memory, value);
            foreach (var timer in timers)
            foreach (uint distance in new uint[] { 0, 1, 2, 3, 4, 5, 8, 1024 })
            {
                Load(); SetMemory(timer.Armed, true); SetMemory(timer.Remaining, distance);
                Check(eventDistance: distance);
            }
            foreach (uint distance in new uint[] { 0, 1, 2, 3, 4, 5, 8, 1024 })
            {
                Load(); SetMemory("_viInterruptCyclesRemaining", distance); Check(eventDistance: distance);
            }
            foreach (uint opcode in new uint[] { 0x24820001, 0x70000000, 0x1000ffff })
            {
                Load(); uint address = TLB.TranslateAddress(0x7000193c) & 0x1fffffff;
                BinaryPrimitives.WriteUInt32BigEndian(R4300.memory.RDRAM.AsSpan((int)address), opcode);
                Check(expected: false);
            }
            Load(); TLB.Reset(); Check(expected: false);
            Load(); Registers.COP0.Reg[Registers.COP0.CAUSE_REG] = 0x8000; Check(expected: false);
            Load(); Registers.COP0.Reg[Registers.COP0.COUNT_REG] ^= 1; Check(expected: false);
            Load(); Ryu64.Common.Variables.Debug = true;
            try { Check(expected: false); } finally { Ryu64.Common.Variables.Debug = false; }
            Load(); Ryu64.Common.Settings.STEP_MODE = true;
            try { Check(expected: false); } finally { Ryu64.Common.Settings.STEP_MODE = false; }
            foreach (uint line in new uint[] { 0, 1, 2960, 2961, uint.MaxValue })
            {
                Load(); SetMemory("_viLineCycleAccum", line); Check();
            }
            Load(); SetMemory("_viFrameDelayCycles", 0u); Check(expected: false);
            Load(); Registers.R4300.PC += 2; Check(expected: false);
            Load();
            uint branchAddress = TLB.TranslateAddress(0x70001938) & 0x1fffffff;
            BinaryPrimitives.WriteUInt32BigEndian(R4300.memory.RDRAM.AsSpan((int)branchAddress), 0x1000fffe);
            Check(expected: false);

            // Self J + NOP has the same two instruction boundaries, including
            // cached/uncached aliases. No JAL, non-self target or delay-slot
            // work may be discarded, and live code must be checked every time.
            foreach (uint pc in new uint[] { 0x70001938, 0x80010000, 0xa0010000, 0x80010ff8, 0xa0010ff8 })
            foreach (uint maximum in new uint[] { 0, 2, 3, 4, 7, 32, 65536 })
            {
                LoadJump(pc); Registers.R4300.Reg[0] = 123; Check(maximum);
            }
            foreach (uint pc in new uint[] { 0x80010000, 0xa0010000 })
            {
                foreach (uint delay in new uint[] { 0x24820001, 0xac820000, 0x40806000, 0x08004000 })
                {
                    LoadJump(pc);
                    BinaryPrimitives.WriteUInt32BigEndian(R4300.memory.RDRAM.AsSpan((int)(pc & 0x1fffffffu) + 4), delay);
                    Check(expected: false);
                }
                foreach (uint branch in new uint[] { Jump(pc + 4), Jump(pc) | 0x04000000, 0x03e00008, 0x1000fffe })
                {
                    LoadJump(pc);
                    BinaryPrimitives.WriteUInt32BigEndian(R4300.memory.RDRAM.AsSpan((int)(pc & 0x1fffffffu)), branch);
                    Check(expected: false);
                }
                foreach (var timer in timers)
                foreach (uint distance in new uint[] { 0, 4, 1024 })
                {
                    LoadJump(pc); SetMemory(timer.Armed, true); SetMemory(timer.Remaining, distance);
                    Check(eventDistance: distance);
                }
                LoadJump(pc); TLB.Reset(); Check(expected: true);
                LoadJump(pc); Check(32, expected: true);
                BinaryPrimitives.WriteUInt32BigEndian(R4300.memory.RDRAM.AsSpan((int)(pc & 0x1fffffffu)), Jump(pc + 4));
                Check(expected: false);
            }
            foreach (uint pc in new uint[] { 0x80010ffc, 0xa0010ffc })
            {
                LoadJump(pc); Check(expected: false);
            }
            foreach (uint pc in new uint[] { 0x80800000, 0xa0800000, 0xa4000000 })
            {
                Load(); Registers.R4300.PC = pc; Check(expected: false);
            }
            foreach (uint pc in new uint[] { 0x0ffffffc, 0x7ffffffc, 0x8ffffffc, 0xaffffffc, 0xfffffffc })
                if (isIdle(pc, Jump(pc))) throw new Exception("J incorrectly reused the old 256 MiB region");

            // Counter work in the delay slot is preserved exactly, including
            // ADDIU sign extension, 64-bit wrap and the last pair before events.
            foreach (uint pc in new uint[] { 0x70001938, 0x80000518, 0xa0010000 })
            foreach (bool jump in new[] { false, true })
            foreach (uint kind in new uint[] { 9, 25 })
            foreach (short step in new short[] { 1, -1, short.MinValue })
            foreach (ulong value in new ulong[] { 0, 0x7fffffff, 0xffffffff, ulong.MaxValue })
            foreach (uint maximum in new uint[] { 3, 4, 1024 })
            {
                LoadJump(pc);
                uint physical = (pc == 0x70001938 ? TLB.TranslateAddress(pc) : pc) & 0x1fffffff;
                BinaryPrimitives.WriteUInt32BigEndian(R4300.memory.RDRAM.AsSpan((int)physical), jump ? Jump(pc) : 0x1000ffff);
                BinaryPrimitives.WriteUInt32BigEndian(R4300.memory.RDRAM.AsSpan((int)physical + 4),
                    (kind << 26) | (4u << 21) | (4u << 16) | (ushort)step);
                Registers.R4300.Reg[0] = 123; Registers.R4300.Reg[4] = value;
                Check(maximum, expected: maximum >= 4);
            }
            foreach (uint delay in new uint[] { 0x20840001, 0x60840001, 0x24850001, 0x24800001, 0x24000001, 0x64000001 })
            {
                LoadJump(0x80000518);
                BinaryPrimitives.WriteUInt32BigEndian(R4300.memory.RDRAM.AsSpan(0x51c), delay);
                Check(expected: false);
            }
            foreach (var timer in timers)
            foreach (uint distance in new uint[] { 0, 1, 4, 17, 1024 })
            {
                LoadJump(0x80000518);
                BinaryPrimitives.WriteUInt32BigEndian(R4300.memory.RDRAM.AsSpan(0x51c), 0x64840001);
                SetMemory(timer.Armed, true); SetMemory(timer.Remaining, distance);
                Check(eventDistance: distance);
            }

            // Compare the batch budget to the next report reached by stepping
            // the original watchdog one branch at a time.
            foreach (ulong count in new ulong[] { 0, 4_967_231, 4_999_997, 4_999_999, 5_000_000, 19_999_998, 19_999_999, 20_000_000, 39_999_999 })
            {
                uint pairs = 32768;
                for (uint i = 1; i <= pairs; i++)
                    if (count + i == 5_000_000 || (count + i) % 20_000_000 == 0) { pairs = i; break; }
                if (watchdogBudget(count) != pairs * 2) throw new Exception("Idle batch skipped a watchdog boundary");
            }
            if (accepted == 0) throw new Exception("No idle batches were exercised");
            Console.WriteLine($"idleEventCases={cases} accepted={accepted} fullStateDifferential=passed selfJump=passed watchdogBoundaries=passed");

        }

        // Replay actual fetched instructions with interrupt delivery. Both paths
        // see the same input state and stop at the same instruction boundary.
        foreach (int shape in benchmarkOnly ? new[] { 0 } : new[] { 0, 1, 2, 3, 4, 5 })
        {
            string expectedHash = "";
            ulong expectedInstructions = 0;
            foreach (bool fast in new[] { false, true })
            {
                var timings = new List<double>();
                ulong batched = 0;
                string hash = "";
                for (int run = benchmarkOnly ? -20 : 0; run < (benchmarkOnly ? 8 : 1); run++)
                {
                    Load(); Ryu64.Common.Measure.InstructionCount = 0;
                    if (shape != 0)
                    {
                        uint physical = TLB.TranslateAddress(0x70001938) & 0x1fffffffu;
                        LoadJump(shape == 1 ? 0x70001938u : physical | (shape == 2 ? 0x80000000u : 0xa0000000u));
                        if (shape >= 4)
                            BinaryPrimitives.WriteUInt32BigEndian(R4300.memory.RDRAM.AsSpan((int)physical + 4),
                                shape == 4 ? 0x24840001u : 0x6484ffffu);
                    }
                    long start = Stopwatch.GetTimestamp();
                    batched = 0;
                    while (Ryu64.Common.Measure.InstructionCount < 2_000_000)
                    {
                        uint pc = Registers.R4300.PC;
                        if (service(pc)) continue;
                        uint opcode = fetch(pc);
                        uint cycles = fast && isIdle(pc, opcode) ? batch(pc, (uint)(2_000_000 - Ryu64.Common.Measure.InstructionCount)) : 0;
                        if (cycles != 0) batched += cycles;
                        else
                        {
                            try { R4300.InterpretOpcode(opcode); }
                            catch (Exception ex) when (ex.GetType().Name == "Cop1UnusableException") { cop1Fault(pc); }
                        }
                    }
                    double ms = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                    hash = Hash();
                    if (expectedHash == "") { expectedHash = hash; expectedInstructions = Ryu64.Common.Measure.InstructionCount; }
                    else if (hash != expectedHash || Ryu64.Common.Measure.InstructionCount != expectedInstructions)
                        throw new Exception("Interrupt-serviced replay differs");
                    if (run >= 0) timings.Add(ms);
                }
                timings.Sort();
                Console.WriteLine($"idleEventReplay shape={shape} fast={fast} medianMs={timings[timings.Count / 2]:F3} runs={timings.Count} instructions={Ryu64.Common.Measure.InstructionCount} batched={batched} sha256={hash}");
            }
        }
    }
}
