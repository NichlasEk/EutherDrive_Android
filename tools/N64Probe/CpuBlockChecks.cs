using System.Buffers.Binary;
using System.Reflection;
using System.Security.Cryptography;
using Ryu64.MIPS;

// Compare every accepted block with ordinary instruction boundaries, including
// serialized RAM/device state. Rejected entries must leave all state untouched.
internal static class CpuBlockChecks
{
    internal static void Run()
    {
        const BindingFlags cpuFlags = BindingFlags.Static | BindingFlags.NonPublic;
        const BindingFlags memoryFlags = BindingFlags.Instance | BindingFlags.NonPublic;
        var batch = typeof(R4300).GetMethod("TryAdvanceCpuBlock", cpuFlags)!.CreateDelegate<Func<uint,uint,uint,bool,uint>>();
        var fetch = typeof(R4300).GetMethod("ReadOpcode", cpuFlags)!.CreateDelegate<Func<uint,uint>>();
        var count = typeof(R4300).GetField("Count", cpuFlags)!;
        R4300.memory = new Memory(new byte[4096]); OpcodeTable.Init();
        var classify = typeof(R4300).GetMethod("GetCpuBlockOpcodeKind", cpuFlags)!.CreateDelegate<Func<uint,int>>();
        var allowed = new HashSet<string> { "J","JAL","BEQ","BNE","JR","JALR", "ADDIU","SLTI","SLTIU","ANDI","ORI","XORI","LUI","MFC0","MTC0","DADDIU", "LB","LH","LW","LBU","LHU","LWU","SB","SH","SW","LD","SD", "SLL","SRL","SRA","SLLV","SRLV","SRAV","DSLLV","DSRLV","DSRAV", "ADDU","SUBU","AND","OR","XOR","NOR","SLT","SLTU","DADDU","DSUBU", "DSLL","DSRL","DSRA","DSLL32","DSRL32","DSRA32" };
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
        void Check(uint expected, uint budget = 32)
        {
            before.SetLength(0); R4300.SaveState(bw); bw.Flush(); string original = Hash();
            Ryu64.Common.Measure.InstructionCount = 0;
            uint op = fetch(Registers.R4300.PC);
            uint n = batch(Registers.R4300.PC, op, budget, false);
            if (n != expected || Ryu64.Common.Measure.InstructionCount != n)
                throw new Exception($"CPU block case {cases}: accepted {n}, expected {expected}");
            string actual = Hash();
            if (n != 0)
            {
                before.Position = 0; R4300.LoadState(br); Ryu64.Common.Measure.InstructionCount = 0;
                while (Ryu64.Common.Measure.InstructionCount < n) R4300.InterpretOpcode(fetch(Registers.R4300.PC));
                if (Hash() != actual || Ryu64.Common.Measure.InstructionCount != n)
                    throw new Exception($"CPU block case {cases}: state differs");
            }
            else if (actual != original) throw new Exception($"CPU block case {cases}: rejected block mutated state");
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
        foreach (uint budget in new uint[] { 0,1,2,7,31,32,33 }) { Reset(); Check(budget < 2 ? 0 : Math.Min(budget,32), budget); }
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
        Console.WriteLine($"cpuBlockCases={cases} state=passed rejection=passed eventBoundaries=passed selfModification=passed");
    }
}
