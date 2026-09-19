using System.Buffers.Binary;
using System.Reflection;
using Ryu64.MIPS;

internal static class Cop1UsabilityChecks
{
    internal static void Run()
    {
        const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
        var raise = typeof(R4300).GetMethod("RaiseCop1UnusableException", flags)!.CreateDelegate<Action<uint>>();
        var syncCount = typeof(R4300).GetMethod("SyncCountRegisterWrite", flags)!.CreateDelegate<Action<uint>>();
        OpcodeTable.Init();
        var table = (List<OpcodeTable.InstInfo>)typeof(OpcodeTable).GetField("AllInsts", flags)!.GetValue(null)!;
        var instructions = table.Select(x => x.Value).Where(x => (x >> 26) == 0x11 || ((x >> 26) & 0x33) == 0x31).Distinct().ToArray();
        R4300.memory = new Memory(new byte[4096]);
        int cases = 0;
        void Reset(bool enabled, bool exl = false)
        {
            Array.Clear(Registers.R4300.Reg); Array.Clear(Registers.COP0.Reg);
            Array.Clear(Registers.COP1.Reg); Array.Clear(Registers.COP1.Control);
            Registers.R4300.PC = 0x80001000;
            Registers.R4300.Reg[1] = 0xffffffff80002001; // Deliberately misaligned load/store address.
            Registers.R4300.Reg[2] = 0x12345678;
            Registers.COP0.Reg[Registers.COP0.STATUS_REG] = 0x04000001u | (enabled ? 0x20000000u : 0) | (exl ? 2u : 0);
            Registers.COP0.Reg[Registers.COP0.CAUSE_REG] = 0x30000200; // CE must become 1; software interrupt preserved.
            Registers.COP0.Reg[Registers.COP0.EPC_REG] = 0x80007770;
            Registers.COP0.Reg[Registers.COP0.BADVADDR_REG] = 0x87654321;
            Registers.COP1.Reg[1] = 0x1122334455667788;
            syncCount(0);
        }
        foreach (uint opcode in instructions.Concat(new uint[] { 0xc4220000, 0xd4220000, 0xe4220000, 0xf4220000 }))
        foreach (bool delay in new[] { false, true })
        foreach (bool exl in new[] { false, true })
        {
            Reset(false, exl);
            var gpr = (ulong[])Registers.R4300.Reg.Clone();
            var fpr = (ulong[])Registers.COP1.Reg.Clone();
            var control = (uint[])Registers.COP1.Control.Clone();
            byte[] ram = (byte[])R4300.memory.RDRAM.Clone();
            if (delay) BinaryPrimitives.WriteUInt32BigEndian(R4300.memory.RDRAM.AsSpan(0x1004), opcode);
            // The setup write itself is allowed; the faulting instruction must
            // not access memory, even for an invalid/misaligned effective address.
            if (delay) BinaryPrimitives.WriteUInt32BigEndian(ram.AsSpan(0x1004), opcode);
            bool trapped = false;
            try { R4300.InterpretOpcode(delay ? 0x10000003u : opcode); }
            catch (Exception ex) when (ex.GetType().Name == "Cop1UnusableException") { trapped = true; raise(0x80001000); }
            if (!trapped || !gpr.SequenceEqual(Registers.R4300.Reg) || !fpr.SequenceEqual(Registers.COP1.Reg)
                || !control.SequenceEqual(Registers.COP1.Control) || !ram.SequenceEqual(R4300.memory.RDRAM)
                || Registers.R4300.PC != 0x80000180
                || Registers.COP0.Reg[Registers.COP0.EPC_REG] != (exl ? 0x80007770u : 0x80001000u)
                || Registers.COP0.Reg[Registers.COP0.CAUSE_REG] != (0x1000022cu | (delay ? 0x80000000u : 0))
                || Registers.COP0.Reg[Registers.COP0.STATUS_REG] != 0x04000003
                || Registers.COP0.Reg[Registers.COP0.BADVADDR_REG] != 0x87654321)
                throw new Exception($"COP1 exception mismatch opcode={opcode:x8} delay={delay} EXL={exl}");
            cases++;
        }
        // Enabled bit transfers retain all bits; no host float conversion.
        Reset(true); R4300.InterpretOpcode(0x44020800); // MFC1 r2,f1
        if (Registers.R4300.Reg[2] != 0x55667788) throw new Exception("MFC1 changed bits");
        Registers.R4300.Reg[2] = 0xdeadbeef; R4300.InterpretOpcode(0x44820800);
        if (Registers.COP1.Reg[1] != 0x11223344deadbeef) throw new Exception("MTC1 changed bits");
        R4300.InterpretOpcode(0x44020800);
        if (Registers.R4300.Reg[2] != 0xffffffffdeadbeef) throw new Exception("MFC1 did not sign extend");
        // Model two lazy-switched threads. Each traps before touching another
        // thread's FPU state, restores its own state, then retries the same PC.
        foreach (ulong bits in new ulong[] { 0x80190000, 0x47000000 })
        {
            Reset(false);
            try { R4300.InterpretOpcode(0x440b8000); throw new Exception("Missing lazy-FPU trap"); }
            catch (Exception ex) when (ex.GetType().Name == "Cop1UnusableException") { raise(0x80001000); }
            Registers.COP1.Reg[16] = bits;
            Registers.COP0.Reg[Registers.COP0.STATUS_REG] = 0x24000001;
            Registers.R4300.PC = (uint)Registers.COP0.Reg[Registers.COP0.EPC_REG];
            R4300.InterpretOpcode(0x440b8000);
            if (Registers.R4300.Reg[11] != unchecked((ulong)(long)(int)bits) || Registers.R4300.PC != 0x80001004)
                throw new Exception("Lazy FPU retry lost thread state");
        }
        Console.WriteLine($"cop1UnusableCases={cases} encodings={instructions.Length} enabledTransfers=passed lazyThreadRetry=passed");
    }
}
