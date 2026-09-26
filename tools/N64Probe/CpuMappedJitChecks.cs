using System.Buffers.Binary;
using System.Reflection;
using Ryu64.MIPS;

internal static class CpuMappedJitChecks
{
#if N64_LIVE_GPU && N64_RDP_JOURNAL
    internal static void GpuLoads(string library)
    {
        if (Environment.GetEnvironmentVariable("EUTHERDRIVE_N64_CPU_JIT_MAPPED_LOADS") != "1"
            || Environment.GetEnvironmentVariable("EUTHERDRIVE_N64_CPU_JIT_MAPPED") == "0")
            throw new Exception("Enable mapped JIT and mapped loads");
        const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
        OpcodeTable.Init(); var memory = new Memory(new byte[4096]); R4300.memory = memory;
        using var gpu = new Ryu64Core.N64LiveGpu(memory, library, true); memory.AttachGpu(gpu);
        typeof(Memory).GetField("_rspTaskDispatching", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(memory, true);
        typeof(R4300).GetField("CpuJitSynchronous", flags)!.SetValue(null, true);
        TLB.Reset();
        void Map(uint index, uint address, uint physical)
        {
            Registers.COP0.Reg[0] = index; Registers.COP0.Reg[10] = address;
            Registers.COP0.Reg[5] = 0; Registers.COP0.Reg[2] = (physical >> 6) | 7;
            Registers.COP0.Reg[3] = ((physical + 0x1000) >> 6) | 7; TLB.WriteTLBEntryIndexed();
        }
        Map(0, 0xe0000000, 0x10000); Map(1, 0xe1000000, 0x300000);
        void Cmd(params uint[] words) => memory.JournalReplayCommand(words);
        Cmd(0xef300000, 0); Cmd(0xff10003f, 0x300000); Cmd(0xfe000000, 0x500000); Cmd(0xed000000, 0x00100080);
        int checks = 0;
        foreach (var (opcode, expected) in new (uint, ulong)[] {
            (0x80820000, 0xffffffffffffffff), (0x90820000, 0xff),
            (0x84820000, 0xffffffffffffff01), (0x94820000, 0xff01),
            (0x8c820000, 0xffffffffff01ff01), (0x9c820000, 0xff01ff01),
            (0xdc820000, 0xff01ff01ff01ff01) })
        {
            memory.WriteUInt32(0x80010000, opcode); memory.WriteUInt32(0x80010004, 0x03e00008); memory.WriteUInt32(0x80010008, 0);
            var run = (Func<uint,uint,bool,uint>)typeof(R4300).GetMethod("CompileCpuJit", flags)!.Invoke(null, new object[] { 0xe0000000u })!;
            Registers.R4300.Reg[4] = 0xe1000000; Registers.R4300.Reg[31] = 0xe0000200; Registers.R4300.PC = 0xe0000000;
            Cmd(0xf7000000, 0xff01ff01); Cmd(0xf60fc07c, 0);
            long hazards = memory.GpuReadHazards;
            if (run(3, 0, false) != 3 || Registers.R4300.PC != 0xe0000200
                || Registers.R4300.Reg[2] != expected || memory.GpuReadHazards != hazards + 1)
                throw new Exception("Mapped JIT load missed GPU readback or returned stale data");
            checks++;
        }
        if (!gpu.Status.Contains("errors=0")) throw new Exception(gpu.Status);
        Console.WriteLine($"mappedGpuLoads=passed cases={checks} readHazards=exact validationErrors=0");
    }
#endif

    internal static void Run()
    {
        if (Environment.GetEnvironmentVariable("EUTHERDRIVE_N64_CPU_JIT_MAPPED") == "0")
            throw new Exception("Mapped JIT must not be disabled for this suite");
        const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
        var batch = typeof(R4300).GetMethod("TryAdvanceCpuBlock", flags)!.CreateDelegate<Func<uint,uint,uint,bool,uint>>();
        var fetch = typeof(R4300).GetMethod("ReadOpcode", flags)!.CreateDelegate<Func<uint,uint>>();
        var compile = typeof(R4300).GetMethod("CompileCpuJit", flags)!.CreateDelegate<Func<uint,Func<uint,uint,bool,uint>>>();
        var reset = typeof(R4300).GetMethod("ResetCpuJitCache", flags)!.CreateDelegate<Action>();
        typeof(R4300).GetField("CpuJitSynchronous", flags)!.SetValue(null, true);
        typeof(R4300).GetField("CpuJitHotThreshold", flags)!.SetValue(null, 1);
        OpcodeTable.Init(); R4300.memory = new Memory(new byte[4096]);
        Registers.COP0.Reg[11] = 0;
        R4300.memory.WriteUInt32(0xa4400018, 524);
        R4300.memory.WriteUInt32(0xa440000c, 100); R4300.memory.Tick(1);
        byte[] Save() { using var s = new MemoryStream(); using var w = new BinaryWriter(s); R4300.SaveState(w); return s.ToArray(); }
        void Load(byte[] bytes) { using var r = new BinaryReader(new MemoryStream(bytes)); R4300.LoadState(r); }
        void Map(uint physical, uint asid = 3, uint mask = 0, uint index = 0, bool global = false)
        {
            Registers.COP0.Reg[0] = index; Registers.COP0.Reg[10] = 0xe0000000u | asid;
            Registers.COP0.Reg[5] = mask;
            Registers.COP0.Reg[2] = (physical >> 6) | (global ? 7u : 6u);
            Registers.COP0.Reg[3] = ((physical + 0x1000) >> 6) | (global ? 7u : 6u);
            TLB.WriteTLBEntryIndexed();
        }
        void Word(uint physical, uint word) => BinaryPrimitives.WriteUInt32BigEndian(R4300.memory.RDRAM.AsSpan((int)physical, 4), word);
        byte[] initial = Save(); int cases = 0;
        void Setup(uint pc = 0xe0000000, uint mask = 0)
        {
            Load(initial); TLB.Reset(); Map(0x10000, mask: mask); reset(); Registers.R4300.PC = pc;
            Registers.R4300.Reg[31] = 0xe0000100;
            for (int i = 0; i < 32; i++) Word(0x10000u + (uint)i * 4, 0x24420001);
        }
        void Check(uint expected, uint budget = 16)
        {
            byte[] before = Save(); uint pc = Registers.R4300.PC;
            Ryu64.Common.Measure.InstructionCount = 0;
            var pos = typeof(R4300).GetField("_recentInstPos", flags)!;
            var entries = (Array)typeof(R4300).GetField("_recentInst", flags)!.GetValue(null)!;
            int start = cases % 2 == 0 ? 0 : entries.Length - 3;
            pos.SetValue(null, start);
            uint n = batch(pc, fetch(pc), budget, true);
            int finish = (int)pos.GetValue(null)!;
            var recorded = (Array)entries.Clone();
            var expectedHistory = new List<(uint, uint)>();
            if (n != expected || Ryu64.Common.Measure.InstructionCount != n)
                throw new Exception($"Mapped case {cases}: {n} != {expected}");
            byte[] actual = Save(); Load(before); Ryu64.Common.Measure.InstructionCount = 0;
            while (Ryu64.Common.Measure.InstructionCount < n)
            {
                uint stepPc = Registers.R4300.PC, word = fetch(stepPc);
                if (Ryu64.Common.Measure.InstructionCount != 0) expectedHistory.Add((stepPc, word));
                R4300.InterpretOpcode(word);
            }
            if (finish != (start + expectedHistory.Count) % entries.Length) throw new Exception("History length differs");
            for (int h = 0; h < expectedHistory.Count; h++)
            {
                object entry = recorded.GetValue((start + h) % entries.Length)!;
                var actualEntry = ((uint)entry.GetType().GetField("Pc")!.GetValue(entry)!, (uint)entry.GetType().GetField("Op")!.GetValue(entry)!);
                if (actualEntry != expectedHistory[h]) throw new Exception("Virtual instruction history differs");
            }
            if (!actual.AsSpan().SequenceEqual(Save()) || Ryu64.Common.Measure.InstructionCount != n)
                throw new Exception($"Mapped case {cases}: state differs");
            cases++;
        }
        if (Environment.GetEnvironmentVariable("EUTHERDRIVE_N64_CPU_JIT_MAPPED_CACHE_ONLY") != "0")
        {
            Setup(); Check(0); // A non-CACHE mapped entry must remain interpreted.
            for (uint operation = 0; operation < 32; operation++)
            {
                Setup(); Registers.R4300.Reg[4] = 0xa4400001;
                Word(0x10000, 47u << 26 | 4u << 21 | operation << 16 | 0xffff); Check(16);
            }
            foreach (uint budget in new uint[] { 4, 13, 64, 512 })
            {
                Setup(); Registers.R4300.Reg[8] = 0; Registers.R4300.Reg[9] = 0x10000;
                Word(0x10000, 0xbd010000); Word(0x10004, 0x0109082b);
                Word(0x10008, 0x1420fffd); Word(0x1000c, 0x25080010);
                Check(budget / 4 * 4, budget);
            }
            Setup(); Word(0x10000, 0xbd010000); Word(0x10004, 0x0109082b);
            Word(0x10008, 0x1420fffd); Word(0x1000c, 0x25080010);
            Registers.R4300.Reg[8] = 128; Registers.R4300.Reg[9] = 64; Check(4, 64);
            Console.WriteLine($"mappedCacheEntryChecks=passed cases={cases} fullState=exact history=exact otherEntries=rejected");
            return;
        }
        var random = new Random(640926);
        for (int test = 0; test < 100; test++)
        {
            Setup();
            for (uint i = 0; i < 16; i++) Word(0x10000 + i * 4, 0x24000000 | (uint)random.Next(32) << 21 | (uint)random.Next(32) << 16 | (uint)random.Next(65536));
            Check(16);
        }
        foreach (uint branch in new uint[] { 0x10000002, 0x14400002, 0x08000040, 0x0c000040, 0x03e00008, 0x03e0f809 })
        {
            Setup(); Word(0x10000, branch); Word(0x10004, 0x27e20001); Check(2);
        }
        Setup(); Word(0x10000, 0x2442ffff); Word(0x10004, 0x1440fffe); Word(0x10008, 0); Registers.R4300.Reg[2] = 5; Check(15, 15);
        Setup(); Registers.R4300.Reg[4] = 0x80020000; Word(0x10000, 0x8c820000); Check(16);
        Setup(); Registers.R4300.Reg[4] = 0xe0000200; Word(0x10000, 0x8c820000); Check(Environment.GetEnvironmentVariable("EUTHERDRIVE_N64_CPU_JIT_MAPPED_LOADS") == "1" ? 16u : 0u); // mapped data exits
        Setup(); Registers.R4300.Reg[4] = 0x80010000; Word(0x10004, 0xac820000); Check(2); // self-modifying store ends block
        Setup(0xe0000ff8); Word(0x10ff8, 0x24420001); Word(0x10ffc, 0x24630002); Check(2);
        Setup(0xe0000ffc); Word(0x10ffc, 0x10000001); Check(0); // delay slot crosses page
        Setup(0xe0001000); Word(0x11000, 0x24420001); Word(0x11004, 0x03e00008); Word(0x11008, 0); Check(3);
        foreach (uint mask in new uint[] { 0x6000, 0x1e000 }) { Setup(mask: mask); Check(16); }
        if (Environment.GetEnvironmentVariable("EUTHERDRIVE_N64_CPU_JIT_MAPPED_LOADS") == "1")
        {
            foreach (uint op in new uint[] { 32, 33, 35, 36, 37, 39, 55 })
            foreach (uint offset in new uint[] { 0x200, 0xff8, 0x1000 })
            foreach (uint target in new uint[] { 0, 2, 4 })
            {
                Setup(); Registers.R4300.Reg[4] = 0xe0000000 + offset;
                BinaryPrimitives.WriteUInt64BigEndian(R4300.memory.RDRAM.AsSpan((int)(0x10000 + offset), 8), 0x8123456789abcdef);
                Word(0x10000, op << 26 | 4u << 21 | target << 16); Check(16);
            }
            foreach (uint address in new uint[] { 0xe0000201, 0xe0002000, 0xa4400000, 0x80800000 })
            {
                Setup(); Registers.R4300.Reg[4] = address;
                Word(0x10000, 0x24630001); Word(0x10004, 0x8c820000); Check(1);
            }
            Setup(); Map(0x04400000, index: 1); // Earlier valid RAM mapping still wins.
            Registers.R4300.Reg[4] = 0xe0000200; Word(0x10000, 0x8c820000); Check(16);
            Setup(); Map(0x04400000); Registers.R4300.PC = 0xe0000000;
            var resolve = typeof(R4300).GetMethod("TryResolveCpuJitLoad", flags)!;
            object[] mmio = { 0xe0000000u, 4u, 0u };
            byte[] beforeMmio = Save();
            if ((bool)resolve.Invoke(null, mmio)! || !beforeMmio.AsSpan().SequenceEqual(Save()))
                throw new Exception("Mapped MMIO was accepted or changed state");
            cases++;
        }
        if (Environment.GetEnvironmentVariable("EUTHERDRIVE_N64_CPU_JIT_MAPPED_CACHE") != "0")
        {
            for (uint operation = 0; operation < 32; operation++)
            {
                Setup(); Registers.R4300.Reg[4] = 0xa4400001;
                Word(0x10000, 47u << 26 | 4u << 21 | operation << 16 | 0xffff); Check(16);
            }
            foreach (uint budget in new uint[] { 4, 13, 64, 512 })
            {
                Setup(); Registers.R4300.Reg[8] = 0; Registers.R4300.Reg[9] = 0x10000;
                Word(0x10000, 0xbd010000); Word(0x10004, 0x0109082b);
                Word(0x10008, 0x1420fffd); Word(0x1000c, 0x25080010);
                Check(budget / 4 * 4, budget);
            }
            Setup(); Word(0x10000, 0x10000001); Word(0x10004, 0xbd010000); Check(0); // CACHE delay remains interpreted
        }
        void Reject(Func<uint,uint,bool,uint> stale)
        {
            byte[] before = Save();
            if (stale(16, 0, false) != uint.MaxValue || !before.AsSpan().SequenceEqual(Save()))
                throw new Exception("Stale mapped block ran or mutated state");
            cases++;
        }
        Setup(); var code = compile(0xe0000000); Map(0x20000); Reject(code);
        Setup(); code = compile(0xe0000000); Registers.COP0.Reg[10] = 4; Reject(code);
        Setup(); code = compile(0xe0000000); TLB.Reset(); Reject(code);
        Setup(); code = compile(0xe0000000); Word(0x10004, 0x24630002); Reject(code);
        Setup(); code = compile(0xe0000000); Map(0x20000); byte[] remapped = Save(); Map(0x10000); Load(remapped); Reject(code);
        Setup(); Map(0x20000, index: 1); Check(16); // ordered overlapping entries
        Setup(); Map(0x10000, global: true); Check(16);
        Setup(); code = compile(0xe0000000); Registers.COP0.Reg[2] = 0x400; TLB.WriteTLBEntryIndexed(); Reject(code);
        Setup(); Word(0x10000, 0x40826000); Check(0); // CP0 writes remain interpreted
        Setup(); typeof(R4300).GetField("CpuJitSynchronous", flags)!.SetValue(null, false);
        compile(0xe0000000);
        var versions = (System.Collections.IDictionary)typeof(R4300).GetField("CpuJitVersions", flags)!.GetValue(null)!;
        object holder = versions.Values.Cast<object>().Single();
        Map(0x20000);
        var pending = typeof(R4300).GetField("CpuJitCompilePending", flags)!;
        if (!SpinWait.SpinUntil(() => (int)pending.GetValue(null)! == 0, 10000)) throw new Exception("Compile worker timeout");
        code = (Func<uint,uint,bool,uint>)holder.GetType().GetField("Run", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(holder)!;
        Reject(code);
        Console.WriteLine($"mappedCpuJitChecks=passed cases={cases} fullState=exact remap=passed asid=passed reset=passed load=passed aliases=passed pageBoundary=passed");
    }
}
