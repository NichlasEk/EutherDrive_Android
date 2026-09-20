using System.Buffers.Binary;
using System.Reflection;
using System.Security.Cryptography;
using Ryu64.MIPS;

// Compare every accepted block with ordinary instruction boundaries, including
// serialized RAM/device state. Rejected entries must leave all state untouched.
internal static class CpuBlockChecks
{
    internal static void Run(bool jit = false)
    {
        const BindingFlags cpuFlags = BindingFlags.Static | BindingFlags.NonPublic;
        const BindingFlags memoryFlags = BindingFlags.Instance | BindingFlags.NonPublic;
        if (jit)
        {
            typeof(R4300).GetField("CpuJitHotThreshold", cpuFlags)!.SetValue(null, 1);
            typeof(R4300).GetField("CpuJitSynchronous", cpuFlags)!.SetValue(null, true);
        }
        else typeof(R4300).GetField("CpuJitUnavailable", cpuFlags)!.SetValue(null, true);
        int compiledCases = 0;
        var batch = typeof(R4300).GetMethod("TryAdvanceCpuBlock", cpuFlags)!.CreateDelegate<Func<uint,uint,uint,bool,uint>>();
        var fetch = typeof(R4300).GetMethod("ReadOpcode", cpuFlags)!.CreateDelegate<Func<uint,uint>>();
        var randomAfter = typeof(R4300).GetMethod("GetRandomAfterInstructions", cpuFlags)!.CreateDelegate<Func<uint,uint>>();
        int randomCases = 0;
        for (uint wired = 0; wired < 32; wired++)
        for (uint initialRandom = 0; initialRandom < 64; initialRandom++)
        {
            uint expected = initialRandom | 0x12340000u;
            Registers.COP0.Reg[1] = expected;
            Registers.COP0.Reg[6] = wired | 0x56780000u;
            for (uint done = 0; done <= 512; done++)
            {
                if (randomAfter(done) != expected)
                    throw new Exception($"RANDOM batch wired={wired}, initial={initialRandom}, count={done}");
                uint current = expected & 31;
                expected = current <= wired ? 31 : current - 1;
                randomCases++;
            }
        }
        Registers.COP0.Reg[1] = 31; Registers.COP0.Reg[6] = 0;
        Console.WriteLine($"randomBatchCases={randomCases} passed");
        var count = typeof(R4300).GetField("Count", cpuFlags)!;
        R4300.memory = new Memory(new byte[4096]); OpcodeTable.Init();
        var classify = typeof(R4300).GetMethod("GetCpuBlockOpcodeKind", cpuFlags)!.CreateDelegate<Func<uint,int>>();
        var allowed = new HashSet<string> { "J","JAL","BEQ","BNE","JR","JALR","BLTZ","BGEZ","BLEZ","BGTZ", "ADDIU","SLTI","SLTIU","ANDI","ORI","XORI","LUI","MFC0","MTC0","DADDIU", "LB","LH","LW","LBU","LHU","LWU","SB","SH","SW","LD","SD", "SLL","SRL","SRA","SLLV","SRLV","SRAV","DSLLV","DSRLV","DSRAV", "ADDU","SUBU","AND","OR","XOR","NOR","SLT","SLTU","DADDU","DSUBU", "DSLL","DSRL","DSRA","DSLL32","DSRL32","DSRA32" };
        uint sample = 0x430064;
        int decoded = 0;
        for (int i = 0; i < 1_000_000; i++)
        {
            sample = unchecked(sample * 1664525u + 1013904223u);
            if (classify(sample) < 0) continue;
            var info = OpcodeTable.GetOpcodeInfo(sample);
            if (info.Cycles != 1 || !allowed.Contains(info.Interpret.Method.Name)
                || (info.Interpret.Method.Name == "MTC0" && ((sample >> 11) & 31) != 12))
                throw new Exception($"Unexpected block opcode {sample:x8}: {info.Interpret.Method.Name}/{info.Cycles}");
            decoded++;
        }
        Console.WriteLine($"blockDecodeSamples=1000000 accepted={decoded} cycles=passed reservedBits=passed");
        Registers.R4300.PC = 0x80010000;
        Registers.COP0.Reg[11] = 0;
        R4300.memory.WriteUInt32(0xa4400018, 524);
        R4300.memory.WriteUInt32(0xa440000c, 100);
        R4300.memory.Tick(1);
        for (int r = 1; r < 32; r++) Registers.R4300.Reg[r] = 0xffffffff80020000UL + (ulong)r * 8;
        for (int i = 0; i < 40; i++) BinaryPrimitives.WriteUInt32BigEndian(R4300.memory.RDRAM.AsSpan(0x10000 + i * 4), 0x24420001);
        using var initial = new MemoryStream(); using var iw = new BinaryWriter(initial, System.Text.Encoding.UTF8, true);
        R4300.SaveState(iw); iw.Flush(); using var ir = new BinaryReader(initial, System.Text.Encoding.UTF8, true);
        using var before = new MemoryStream(); using var bw = new BinaryWriter(before, System.Text.Encoding.UTF8, true);
        using var br = new BinaryReader(before, System.Text.Encoding.UTF8, true);
        using var output = new MemoryStream(); using var ow = new BinaryWriter(output, System.Text.Encoding.UTF8, true);
        void Reset() { initial.Position = 0; R4300.LoadState(ir); }
        void Code(int i, uint op) => BinaryPrimitives.WriteUInt32BigEndian(R4300.memory.RDRAM.AsSpan(0x10000 + i * 4), op);
        void Set(string name, object value) => typeof(Memory).GetField(name, memoryFlags)!.SetValue(R4300.memory, value);
        string Hash() { output.SetLength(0); R4300.SaveState(ow); ow.Flush(); return Convert.ToHexString(SHA256.HashData(output.GetBuffer().AsSpan(0, (int)output.Length))); }
        int cases = 0;
        void Check(uint expected, uint budget = 32, bool clearJit = true)
        {
            if (jit && clearJit)
            {
                Array.Clear((Array)typeof(R4300).GetField("CpuJitCache", cpuFlags)!.GetValue(null)!);
                ((System.Collections.IDictionary)typeof(R4300).GetField("CpuJitEntries", cpuFlags)!.GetValue(null)!).Clear();
                ((System.Collections.IDictionary)typeof(R4300).GetField("CpuJitVersions", cpuFlags)!.GetValue(null)!).Clear();
                typeof(R4300).GetField("CpuJitCompilations", cpuFlags)!.SetValue(null, 0);
                typeof(R4300).GetField("CpuJitInstructions", cpuFlags)!.SetValue(null, 0L);
            }
            before.SetLength(0); R4300.SaveState(bw); bw.Flush(); string original = Hash();
            Ryu64.Common.Measure.InstructionCount = 0;
            uint op = fetch(Registers.R4300.PC);
            var historyPosition = typeof(R4300).GetField("_recentInstPos", cpuFlags)!;
            var entries = (Array)typeof(R4300).GetField("_recentInst", cpuFlags)!.GetValue(null)!;
            int historyStart = cases % 2 == 0 ? 0 : entries.Length - 3;
            historyPosition.SetValue(null, historyStart);
            var previousHistory = (Array)entries.Clone();
            uint n = batch(Registers.R4300.PC, op, budget, jit);
            int historyEnd = (int)historyPosition.GetValue(null)!;
            var recorded = (Array)entries.Clone();
            var expectedHistory = new List<(uint, uint)>();
            if (n != expected || Ryu64.Common.Measure.InstructionCount != n)
                throw new Exception($"CPU block case {cases}: accepted {n}, expected {expected}");
            if (jit && (long)typeof(R4300).GetField("CpuJitInstructions", cpuFlags)!.GetValue(null)! > 0) compiledCases++;
            string actual = Hash();
            if (n != 0)
            {
                before.Position = 0; R4300.LoadState(br); Ryu64.Common.Measure.InstructionCount = 0;
                while (Ryu64.Common.Measure.InstructionCount < n)
                {
                    uint stepPc = Registers.R4300.PC, stepWord = fetch(stepPc);
                    if (Ryu64.Common.Measure.InstructionCount != 0) expectedHistory.Add((stepPc, stepWord));
                    R4300.InterpretOpcode(stepWord);
                }
                if (Hash() != actual || Ryu64.Common.Measure.InstructionCount != n)
                    throw new Exception($"CPU block case {cases}: state differs");
            }
            else if (actual != original) throw new Exception($"CPU block case {cases}: rejected block mutated state");
            if (jit)
            {
                if (historyEnd != (historyStart + expectedHistory.Count) % entries.Length)
                    throw new Exception($"CPU JIT case {cases}: history position differs");
                int first = Math.Max(0, expectedHistory.Count - entries.Length);
                for (int h = first; h < expectedHistory.Count; h++)
                {
                    object entry = recorded.GetValue((historyStart + h) % entries.Length)!;
                    var actualEntry = ((uint)entry.GetType().GetField("Pc")!.GetValue(entry)!, (uint)entry.GetType().GetField("Op")!.GetValue(entry)!);
                    if (actualEntry != expectedHistory[h])
                        throw new Exception($"CPU JIT case {cases}: instruction history differs");
                }
                for (int h = expectedHistory.Count; h < entries.Length; h++)
                {
                    int index = (historyStart + h) % entries.Length;
                    if (!recorded.GetValue(index)!.Equals(previousHistory.GetValue(index)))
                        throw new Exception($"CPU JIT case {cases}: unrelated history entry overwritten");
                }
            }
            cases++;
        }
        if (Environment.GetEnvironmentVariable("N64_PROBE_EXPECT_BATCH_DISABLED") == "1")
        { Reset(); Check(0); Console.WriteLine("cpuBlockDisabled=passed"); return; }
        uint[] special = { 0,2,3,4,6,7,20,22,23,33,35,36,37,38,39,42,43,45,47,56,58,59,60,62,63 };
        uint[] shifts = { 0,2,3,56,58,59,60,62,63 };
        var random = new Random(640020);
        foreach (uint fn in special)
        foreach (int zero in new[] { 0, 1 })
        {
            Reset();
            for (int r = 0; r < 32; r++) Registers.R4300.Reg[r] = (ulong)random.NextInt64() ^ ((ulong)random.Next(2) << 63);
            uint op = shifts.Contains(fn) ? (5u << 16 | (zero == 0 ? 6u : 0u) << 11 | 31u << 6 | fn)
                : (4u << 21 | 5u << 16 | (zero == 0 ? 6u : 0u) << 11 | fn);
            Code(0, op); Check(32);
            Reset(); Code(0, op | (shifts.Contains(fn) ? 1u << 21 : 1u << 6)); Check(0);
        }
        foreach (uint primary in new uint[] { 9,10,11,12,13,14,15,25,32,33,35,36,37,39,40,41,43,55,63 })
        foreach (uint immediate in new uint[] { 0,8,0xfff8 })
        { Reset(); Code(0, primary << 26 | (primary == 15 ? 0 : 4u << 21) | 5u << 16 | immediate); Check(32); }
        // Signed payloads, cached/uncached aliases, last valid RAM access,
        // destination/base aliasing and r0 writes, also in branch delay slots.
        foreach (uint primary in new uint[] { 32,33,35,36,37,39,55 })
        foreach (uint target in new uint[] { 0,4,5 })
        foreach (bool delaySlot in new[] { false,true })
        foreach (uint segment in new uint[] { 0x80000000,0xa0000000 })
        {
            Reset();
            int width = primary == 55 ? 8 : primary == 35 || primary == 39 ? 4 : primary == 33 || primary == 37 ? 2 : 1;
            int address = R4300.memory.RDRAM.Length - width;
            for (int b = 0; b < width; b++) R4300.memory.RDRAM[address + b] = (byte)(0x81 + b * 13);
            Registers.R4300.Reg[4] = segment + (uint)address + 8;
            uint load = primary << 26 | 4u << 21 | target << 16 | 0xfff8u;
            if (delaySlot) { Code(0,0x08004004); Code(1,load); }
            else Code(0,load);
            Check(delaySlot ? 2u : 32u, delaySlot ? 2u : 32u);
        }
        // Validated stores retain page epochs and overlapping framebuffer dirtiness.
        foreach (uint address in new uint[] { 0x80020ffc,0xa0021000,0x807ffffc })
        foreach (uint epoch in new uint[] { 17,uint.MaxValue })
        foreach (bool delaySlot in new[] { false,true })
        {
            Reset();
            uint physical = address & 0x1fffffffu;
            typeof(Memory).GetMethod("RegisterFramebufferInfo", memoryFlags)!
                .Invoke(R4300.memory, new object[] { physical - 4,2u,4u,1u });
            Set("_rdramWriteEpoch", epoch);
            Registers.R4300.Reg[4] = address; Registers.R4300.Reg[5] = 0xabcdef1234567890;
            if (delaySlot) { Code(0,0x08004004); Code(1,0xac850000); }
            else { Code(0,0x24420001); Code(1,0xac850000); }
            Check(2,2);
        }
        // Signed conditions read all 64 bits before their delay-slot writes.
        // Include r0 normalization and values whose low word has the opposite sign.
        foreach (uint branch in new uint[] { 0x04800002,0x04810002,0x18800002,0x1c800002 })
        foreach (ulong value in new ulong[] { 0,1,ulong.MaxValue,0x8000000000000000,0x7fffffffffffffff,0x80000000,0xffffffff00000000 })
        foreach (uint delay in new uint[] { 0,0x24840001,0x40806000,0x40044800 })
        {
            Reset(); Registers.R4300.Reg[4] = value;
            Code(0,branch); Code(1,delay); Check(2,2);
            Reset(); Registers.R4300.Reg[4] = value;
            Code(3,branch); Code(4,delay); Check(5,5);
            Reset(); Registers.R4300.Reg[0] = value;
            Code(0,branch & ~(31u << 21)); Code(1,delay); Check(2,2);
        }
        foreach (uint branch in new uint[] { 0x0480ffff,0x0481ffff,0x1880ffff,0x1c80ffff })
        {
            Reset(); Registers.R4300.Reg[4] = (branch == 0x0480ffff || branch == 0x1880ffff) ? ulong.MaxValue : 1;
            Code(0,branch); Code(1,0); Check(2,128); // Preserve self-branch watchdog cadence.
            Reset(); Code(0,branch); Code(1,0x8c850001); Check(0); // Faulting delay.
            Reset(); Code(0,0x10000002); Code(1,branch); Check(0); // Nested branch rejected.
        }
        foreach (uint branch in new uint[] { 0x08004000,0x0c004000,0x10850002,0x1485fffc,0x00800008,0x0080f809,0x00800009 })
        foreach (uint delay in new uint[] { 0,0x24420001,0x3404abcd,0x03e01825,0x24000042,0x8c850000,0xac850000,0xdc850000 })
        foreach (bool equal in new[] { false,true })
        {
            Reset(); Registers.R4300.Reg[5] = equal ? Registers.R4300.Reg[4] : 123;
            Code(0,branch); Code(1,delay); Check(2,2);
            Reset(); Code(3,branch); Code(4,delay); Check(5,5);
        }
        foreach (uint delay in new uint[] { 0x8c850001,0x08004000,0x0000000c })
        { Reset(); Code(0,0x0c004000); Code(1,delay); Check(0);
          Reset(); Code(3,0x0c004000); Code(4,delay); Check(3); }
        // JAL/JALR change the address base before a delay-slot load. Validate
        // against the new link, including aliasing and a discarded r0 link.
        Reset(); Registers.R4300.Reg[31] = 0xa4040000; Code(0,0x0c004000); Code(1,0x8fe50000); Check(2,2);
        Reset(); Registers.R4300.Reg[4] = 0xa4040000; Code(0,0x00802009); Code(1,0x8c850000); Check(2,2);
        Reset(); Registers.R4300.Reg[4] = 0xa4040000; Code(0,0x00802009); Code(1,0xdc850000); Check(2,2);
        Reset(); Registers.R4300.Reg[0] = 0x80020000; Code(0,0x00800009); Code(1,0x8c050000); Check(0);
        // Delay store overwrites the target, which must be fetched after it.
        Reset(); Registers.R4300.Reg[4] = 0xa0010010; Registers.R4300.Reg[5] = 0x3406abcd;
        Code(0,0x08004004); Code(1,0xac850000); Check(32);
        foreach (uint status in new uint[] { 0,1,2,4,0xff00,0xff01,0x2000ff01,0xffffffff })
        {
            Reset(); Registers.R4300.Reg[5] = status; Code(0,0x40856000); Check(32);
            Reset(); Registers.R4300.Reg[5] = status; Code(3,0x08004000); Code(4,0x40856000); Check(5,5);
        }
        foreach (uint reg in new uint[] { 0,1,9,11,12,13,31 })
        foreach (ulong c in new ulong[] { 0,1,0x100000000 })
        {
            Reset(); count.SetValue(null,c); Registers.COP0.Reg[9] = c >> 1;
            Code(0,0x40000000u | 5u << 16 | reg << 11); Check(32);
            Reset(); count.SetValue(null,c); Registers.COP0.Reg[9] = c >> 1;
            Registers.COP0.Reg[1] = 0xabcdef01; Registers.COP0.Reg[6] = 31;
            Code(3,0x08004000); Code(4,0x40000000u | 5u << 16 | reg << 11); Check(5,5);
        }
        Reset(); Code(0, 0x3c250001); Check(0); // Invalid LUI rs.
        foreach (uint op in new uint[] { 0x40824800, 0x42000018, 0x46000000, 0x0000000c, 0xffffffff })
        { Reset(); Code(0, op); Check(0); Reset(); Code(3, op); Check(3); }
        foreach (uint budget in new uint[] { 0,1,2,7,31,32,33,127,128,511,512,1023,1024,2047,2048 }) { Reset(); Check(budget < 2 ? 0 : Math.Min(budget,jit ? 512u : 32u), budget); }
        foreach (uint wired in new uint[] { 0,1,30,31 })
        foreach (uint r in new uint[] { 0,1,15,30,31 })
        { Reset(); Registers.COP0.Reg[6] = wired; Registers.COP0.Reg[1] = r; Check(32); }
        foreach (uint address in new uint[] { 0x80020001,0x80020002,0x80800000,0xa4040000,0x70020000,0xc0020000 })
        { Reset(); Registers.R4300.Reg[4] = address; Code(0,0x8c850000); Check(0); Reset(); Registers.R4300.Reg[4] = address; Code(2,0xac850000); Check(2); }
        Reset(); Registers.R4300.PC = 0xa0010000; Check(32);
        Reset(); Registers.R4300.Reg[4] = 0x80020004; Code(0,0xdc850000); Check(0);
        // A store changes the very next instruction; no cached decoded word is used.
        Reset(); Registers.R4300.Reg[4] = 0xa0010004; Registers.R4300.Reg[5] = 0x3406abcd;
        Code(0,0xac850000); Check(32);
        Reset(); Registers.R4300.Reg[4] = 0xa0010004; Registers.R4300.Reg[5] = 0x0000000c;
        Code(0,0xac850000); Check(1);
        // Base register zero is normalized before effective-address calculation.
        Reset(); Registers.R4300.Reg[0] = 0x80020000; Code(0,0x8c050000); Check(0);
        foreach (uint compare in new uint[] { 0,1,8,16,17 }) { Reset(); Registers.COP0.Reg[11] = compare; Check(compare == 0 || compare > 16 ? 32u : 0); }
        foreach (ulong c in new ulong[] { 1,2,0x1fffffffe,0x1fffffffc,0x1ffffffc0 })
        { Reset(); count.SetValue(null,c); Registers.COP0.Reg[9] = c >> 1; Check(((c + 32) >> 1) < uint.MaxValue ? 32u : 0); }
        (string active,string remaining)[] timers = {
            ("_spDmaDelayArmed","_spDmaDelayRemaining"),("_rspTaskActive","_rspTaskCyclesRemaining"),
            ("_rspInterruptDelayArmed","_rspInterruptDelayRemaining"),("_dpInterruptDelayArmed","_dpInterruptDelayRemaining"),
            ("_piInterruptDelayArmed","_piInterruptDelayRemaining"),("_siInterruptDelayArmed","_siInterruptDelayRemaining"),
            ("_aiInterruptDelayArmed","_aiInterruptDelayRemaining") };
        // Exercise the quiet tick with simultaneous timers, a suspended RSP
        // continuation, and VI interrupts disabled by their line threshold.
        for (int mask = 0; mask < 128; mask++)
        {
            Reset();
            for (int i = 0; i < timers.Length; i++)
            {
                Set(timers[i].active, (mask & (1 << i)) != 0);
                Set(timers[i].remaining, 100u + (uint)i);
            }
            Set("_rspSlicePending", (mask & 4) != 0);
            R4300.memory.SP_STATUS_REG_R[3] = (byte)((mask & 8) != 0 ? 1 : 0);
            if ((mask & 16) != 0)
                BinaryPrimitives.WriteUInt32BigEndian(R4300.memory.VI_INTR_REG_RW, 1023);
            Check(32);
        }
        foreach (var timer in timers)
        foreach (uint distance in new uint[] { 0,1,2,3,31,32,33,100 })
        { Reset(); Set(timer.active,true); Set(timer.remaining,distance); Check(distance < 3 ? 0 : Math.Min(distance-1,32)); }
        foreach (uint distance in new uint[] { 1,2,3,31,32,33,100 })
        { Reset(); Set("_viInterruptCyclesRemaining",distance); Check(distance < 3 ? 0 : Math.Min(distance-1,32));
          Reset(); Set("_viLineCycleAccum",1562500u/525u-distance); Check(distance < 3 ? 0 : Math.Min(distance-1,32)); }
        foreach (uint pc in new uint[] { 0x8001fff0, 0xa001fff0 })
        {
            Reset(); R4300.memory.RDRAM.AsSpan(0x10000,160).CopyTo(R4300.memory.RDRAM.AsSpan(0x1fff0));
            Registers.R4300.PC = pc; Check(32);
        }
        Reset(); Registers.R4300.PC = 0x807ffff8;
        BinaryPrimitives.WriteUInt32BigEndian(R4300.memory.RDRAM.AsSpan(0x7ffff8),0x24420001);
        BinaryPrimitives.WriteUInt32BigEndian(R4300.memory.RDRAM.AsSpan(0x7ffffc),0x24420001); Check(2);
        Reset(); Registers.R4300.PC = 0x807ffffc; Check(0);
        foreach (uint target in new uint[] { 0x80010001, 0x80000184, 0xa4000000, 0x80800000, 0x00010000 })
        { Reset(); Registers.R4300.Reg[4] = target; Code(0,0x00800008); Code(1,0x24420001); Check(2); }
        foreach (int bit in Enumerable.Range(8,8)) { Reset(); Registers.COP0.Reg[13] |= 1UL << bit; Check(0); }
        Reset(); Registers.COP0.Reg[13] |= 0x100; Check(0);
        Reset(); R4300.memory.MI_INTR_REG_R[3] = 1; R4300.memory.MI_INTR_MASK_REG_R[3] = 1; Check(0);
        foreach (string field in new[] { "_executingDelaySlot","_delaySlotExceptionPending" })
        { Reset(); typeof(R4300).GetField(field,cpuFlags)!.SetValue(null,true); Check(0); }
        Reset(); Registers.COP0.Reg[9] = 1; Check(0);
        Reset(); Ryu64.Common.Variables.Debug = true; Check(0); Ryu64.Common.Variables.Debug = false;
        Reset(); Ryu64.Common.Settings.STEP_MODE = true; Check(0); Ryu64.Common.Settings.STEP_MODE = false;
        if (jit)
        {
            var invariant = typeof(R4300).GetMethod("IsInvariantCpuJitLoop", cpuFlags)!
                .CreateDelegate<Func<List<uint>, bool>>();
            // Exercise the actual polling-loop optimization and its rejection
            // boundaries. In particular, a source changed in the delay slot is
            // an input dependency even if it was unchanged before the branch.
            (uint[] body, uint delay, bool stable)[] polling = {
                (new uint[] { 0x3c028002,0x8c420000 },0,true),
                (new uint[] { 0x8c820000 },0,true),
                (new uint[] { 0x80820000 },0,true),
                (new uint[] { 0x84820000 },0,true),
                (new uint[] { 0x90820000 },0,true),
                (new uint[] { 0x94820000 },0,true),
                (new uint[] { 0x9c820000 },0,true),
                (new uint[] { 0xdc820000 },0,true),
                (new uint[] { 0x3c028002,0x3c038002 },0x8c630000,true),
                (new uint[] { 0x24020007,0x00421821,0x00621826 },0x000310c0,true),
                (new uint[] { 0x24020003,0x00431804 },0,false),
                (new uint[] { 0x24020003,0x00821804 },0,true),
                (new uint[] { 0x34020001,0x0044182b },0x24000001,true),
                (new uint[] { 0x24420001 },0,false),
                (new uint[] { 0x8c820000 },0x24840004,false),
                (new uint[] { 0x24430001 },0x24020000,false),
                (new uint[] { 0x40024800 },0,false),
                (new uint[] { 0x24020000 },0x40020800,false),
                (new uint[] { 0x24020001,0xac820000 },0,false)
            };
            foreach (var item in polling)
            foreach (uint budget in new uint[] { 7,32,127,128,511,512 })
            {
                Reset();
                var words = item.body.Concat(new uint[] {
                    0x10000000u | (ushort)(-(item.body.Length + 1)), item.delay }).ToList();
                if (invariant(words) != item.stable)
                    throw new Exception("CPU JIT invariant-loop dependency analysis differs");
                for (int i = 0; i < words.Count; i++) Code(i,words[i]);
                uint length = (uint)words.Count;
                Check(budget % length == length - 1 ? budget - 1 : budget,budget);
            }
            // The proof does not replace runtime guards or the branch test.
            foreach (uint value in new uint[] { 0,1,0x7fffffff,0x80000000,0xffffffff })
            {
                Reset(); BinaryPrimitives.WriteUInt32BigEndian(R4300.memory.RDRAM.AsSpan(0x20000),value);
                Code(0,0x3c028002); Code(1,0x8c420000); Code(2,0x0441fffd); Code(3,0);
                Check(128,128);
            }
            foreach (uint operand in new uint[] { 0x80020001,0xa4400000,0x80800000,0xc0020000 })
            {
                Reset(); Registers.R4300.Reg[4] = operand;
                Code(0,0x24020001); Code(1,0x8c830000); Code(2,0x1000fffd); Code(3,0);
                Check(1,128);
            }
            void PollingChain(uint first, uint second)
            {
                BinaryPrimitives.WriteUInt32BigEndian(R4300.memory.RDRAM.AsSpan(0x20000),first);
                BinaryPrimitives.WriteUInt32BigEndian(R4300.memory.RDRAM.AsSpan(0x20004),second);
                Code(0,0x3c028002); Code(1,0x8c420000); Code(2,0x0441fffd); Code(3,0);
                Code(4,0x3c028002); Code(5,0x8c420004); Code(6,0x0441fff9); Code(7,0);
            }
            var extend = typeof(R4300).GetMethod("ExtendInvariantCpuJitLoop",cpuFlags)!;
            foreach (uint first in new uint[] { 0,0xffffffff })
            foreach (uint second in new uint[] { 1,0x80000000 })
            foreach (uint budget in new uint[] { 3,4,7,8,15,31,127,128,511,512 })
            {
                Reset(); PollingChain(first,second);
                var chain = new List<uint> { 0x3c028002,0x8c420000,0x0441fffd,0 };
                extend.Invoke(null,new object[] { 0x80010000u,chain });
                if (chain.Count != 8) throw new Exception("CPU polling chain was not compiled together");
                bool repeats = (int)first >= 0 || (int)second >= 0;
                Check((repeats || budget < 8) && budget % 4 == 3 ? budget - 1 : budget,budget);
            }
            foreach (uint operand in new uint[] { 0x80020001,0xa4400000,0x80800000,0xc0020000 })
            {
                Reset(); PollingChain(0xffffffff,1); Registers.R4300.Reg[4] = operand;
                Code(5,0x8c820000); Check(5,128);
            }
            foreach (var timer in timers)
            foreach (uint distance in new uint[] { 32,65,100 })
            {
                Reset(); PollingChain(0xffffffff,1);
                Set(timer.active,true); Set(timer.remaining,distance);
                uint budget = distance - 1;
                Check(budget % 4 == 3 ? budget - 1 : budget,128);
            }
            Reset(); PollingChain(0xffffffff,1); Check(32);
            Reset(); PollingChain(0xffffffff,1); Code(5,0x24020000); Check(32,clearJit:false);
            Reset(); PollingChain(0xffffffff,1); Code(5,0x24420001);
            var dependent = new List<uint> { 0x3c028002,0x8c420000,0x0441fffd,0 };
            // A value read before a later write is variant across full cycles.
            Code(4,0x24840001); Code(5,0x8c820000);
            extend.Invoke(null,new object[] { 0x80010000u,dependent });
            if (dependent.Count != 4) throw new Exception("Variant polling chain was incorrectly extended");
            foreach (uint iterations in new uint[] { 1,2,7,32,100 })
            foreach (uint budget in new uint[] { 3,4,7,16,31,32,63,128,511,512 })
            {
                Reset(); Registers.R4300.Reg[2] = 0; Registers.R4300.Reg[3] = iterations;
                Code(0, 0x24420001); Code(1, 0x24630000); Code(2, 0x1443fffd); Code(3, 0);
                uint expected = budget;
                // A terminal branch and its delay slot execute together.
                if (budget < iterations * 4 && budget % 4 == 3) expected--;
                Check(expected, budget);
            }
            foreach (uint branch in new uint[] { 0x0440fffd,0x0441fffd,0x1840fffd,0x1c40fffd })
            foreach (uint budget in new uint[] { 3,4,7,16,31,32,63,128,511,512 })
            {
                bool increasing = branch == 0x0440fffd || branch == 0x1840fffd;
                Reset(); Registers.R4300.Reg[2] = increasing ? unchecked((ulong)-100L) : 100;
                Code(0,increasing ? 0x24420001u : 0x2442ffffu); Code(1,0); Code(2,branch); Code(3,0);
                uint loopInstructions = branch == 0x0441fffdu || branch == 0x1840fffdu ? 404u : 400u;
                Check(budget < loopInstructions && budget % 4 == 3 ? budget - 1 : budget,budget);
            }
            Reset(); Registers.R4300.Reg[4] = 0x807ffff8;
            Code(0, 0x8c850000); Code(1, 0x24840004); Code(2, 0x1480fffd); Code(3, 0);
            Check(8, 128); // Later loop iteration must stop before a non-RAM load.
            Reset(); Check(128, 128);
            Reset(); Set("_piInterruptDelayArmed", true); Set("_piInterruptDelayRemaining", 65u); Check(64, 128);
            Reset(); Set("_viInterruptCyclesRemaining", 65u); Check(64, 128);
            // Reuse compiled code after an interior opcode change, including
            // direct DMA-style writes and restored snapshots with unchanged entry.
            Reset(); Check(32);
            Reset(); Code(3, 0x3406abcd); Check(32, clearJit: false);
            Reset(); Check(32, clearJit: false);
            Reset(); Code(2, 0x0000000c); Check(2, clearJit: false);
            // Stores terminate a compiled region before newly written code is fetched.
            Reset(); Registers.R4300.Reg[4] = 0xa0010008; Registers.R4300.Reg[5] = 0x3406abcd;
            Code(1, 0xac850000); Check(32);
            // A register dependency can make a later load leave RAM: execute only
            // the safe prefix and leave the fault/MMIO instruction to the interpreter.
            Reset(); Code(0, 0x3c048000); Code(1, 0x8c850000); Code(2, 0x3c04a440); Code(3, 0x8c850000); Check(3);
            if (compiledCases < 300) throw new Exception($"Too few compiled cases: {compiledCases}");
            Console.WriteLine($"cpuJitCompiledCases={compiledCases}");
            var resetCache = typeof(R4300).GetMethod("ResetCpuJitCache", cpuFlags)!;
            var compile = typeof(R4300).GetMethod("CompileCpuJit", cpuFlags)!;
            var pending = typeof(R4300).GetField("CpuJitCompilePending", cpuFlags)!;
            var versions = (System.Collections.IDictionary)typeof(R4300).GetField("CpuJitVersions", cpuFlags)!.GetValue(null)!;
            typeof(R4300).GetField("CpuJitSynchronous", cpuFlags)!.SetValue(null, false);
            Reset(); resetCache.Invoke(null, null);
            compile.Invoke(null, new object[] { Registers.R4300.PC });
            object holder = versions.Values.Cast<object>().Single();
            Code(3, 0x3406abcd);
            string changedState = Hash();
            if (!SpinWait.SpinUntil(() => (int)pending.GetValue(null)! == 0, 10000))
                throw new Exception("CPU JIT worker did not complete");
            var stale = (Func<uint,uint,bool,uint>)holder.GetType().GetField("Run", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(holder)!;
            if (stale == null || stale(32, 0, false) != uint.MaxValue || Hash() != changedState)
                throw new Exception("CPU JIT worker/stale code changed machine state");
            Reset(); resetCache.Invoke(null, null);
            compile.Invoke(null, new object[] { Registers.R4300.PC });
            resetCache.Invoke(null, null);
            R4300.memory = new Memory(new byte[4096]); Reset();
            string replacedState = Hash();
            if (!SpinWait.SpinUntil(() => (int)pending.GetValue(null)! == 0, 10000)
                || Hash() != replacedState || versions.Count != 0)
                throw new Exception("CPU JIT worker survived cache reset incorrectly");
            Console.WriteLine("cpuJitWorker=passed staleCode=passed resetDuringCompilation=passed");
        }
        Console.WriteLine($"cpuBlockCases={cases} state=passed rejection=passed eventBoundaries=passed selfModification=passed");
    }
}
