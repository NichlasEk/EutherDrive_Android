using System.Buffers.Binary;
using System.Numerics;
using System.Reflection;
using Ryu64.MIPS;

// Exercise decoded CPU instructions against an independent, arbitrary-precision
// product, including the signed multiply used after Resident Evil 2's movie.
internal static class CpuDoubleMultiplyChecks
{
    internal static void Run()
    {
        R4300.memory = new Memory(new byte[4096]);
        OpcodeTable.Init();
        Registers.COP0.Reg[Registers.COP0.COMPARE_REG] = 0;
        var classify = typeof(R4300).GetMethod("GetCpuBlockOpcodeKind",
            BindingFlags.Static | BindingFlags.NonPublic)!.CreateDelegate<Func<uint, int>>();
        BigInteger mask = (BigInteger.One << 64) - 1;
        int products = 0, reserved = 0;

        void Check(ulong left, ulong right, int rs = 12, int rt = 9, bool signed = true)
        {
            for (int r = 1; r < 32; r++) Registers.R4300.Reg[r] = 0x1234567800000000UL + (ulong)r;
            Registers.R4300.Reg[rs] = left;
            Registers.R4300.Reg[rt] = right;
            Registers.R4300.Reg[0] = 0;
            left = Registers.R4300.Reg[rs]; right = Registers.R4300.Reg[rt];
            ulong[] original = (ulong[])Registers.R4300.Reg.Clone();
            Registers.R4300.Reg[0] = 42; // Ordinary dispatch must normalize r0.
            Registers.R4300.PC = 0x80010000;
            Registers.R4300.HI = 0xfeedfeedfeedfeed;
            Registers.R4300.LO = 0xdeadbeefdeadbeef;
            Registers.COP0.Reg[Registers.COP0.CAUSE_REG] = 0;
            uint opcode = (uint)(rs << 21 | rt << 16 | (signed ? 0x1c : 0x1d));
            var info = OpcodeTable.GetOpcodeInfo(opcode);
            if (info.Interpret.Method.Name != (signed ? "DMULT" : "DMULTU") || info.Cycles != 8)
                throw new Exception($"Wrong multiply decode: {opcode:x8}");
            if (classify(opcode) >= 0) throw new Exception("Multiply entered a one-cycle CPU block");
            BigInteger product = signed
                ? new BigInteger(unchecked((long)left)) * new BigInteger(unchecked((long)right))
                : new BigInteger(left) * new BigInteger(right);
            ulong before = R4300.GetCycleCounter();
            long unknown = R4300.GetUnknownOpcodeCount();
            R4300.InterpretOpcode(opcode);
            if (Registers.R4300.LO != (ulong)(product & mask)
                || Registers.R4300.HI != (ulong)((product >> 64) & mask)
                || !Registers.R4300.Reg.SequenceEqual(original)
                || Registers.R4300.PC != 0x80010004 || R4300.GetCycleCounter() - before != 8
                || Registers.COP0.Reg[Registers.COP0.CAUSE_REG] != 0
                || R4300.GetUnknownOpcodeCount() != unknown)
                throw new Exception($"Multiply mismatch {opcode:x8}: {left:x16} * {right:x16}");
            R4300.InterpretOpcode(0x00001012); // MFLO v0
            R4300.InterpretOpcode(0x00001810); // MFHI v1
            if (Registers.R4300.Reg[2] != (ulong)(product & mask)
                || Registers.R4300.Reg[3] != (ulong)((product >> 64) & mask))
                throw new Exception("Multiply result was not visible to MFLO/MFHI");
            products++;
        }

        // The exact instruction word that previously raised RI after film skip.
        Check(unchecked((ulong)-123L), 0x123456789);
        ulong[] values = { 0, 1, 2, 0x7fffffff, 0x80000000, 0xffffffff,
            0x100000000, 0x100000001, 0x7fffffffffffffff, 0x8000000000000000,
            0xffffffff00000000, 0xffffffff80000000, 0xfffffffffffffffe, ulong.MaxValue };
        foreach (ulong status in new ulong[] { 0, 0x80 }) // 32- and 64-bit kernel modes.
        {
            Registers.COP0.Reg[Registers.COP0.STATUS_REG] = status;
            foreach (ulong left in values)
            foreach (ulong right in values)
            { Check(left, right); Check(left, right, signed: false); }
        }
        for (int rs = 0; rs < 32; rs++)
        for (int rt = 0; rt < 32; rt++)
            Check(0x8000000012345678, 0xfffffffedcba9876, rs, rt);
        var random = new Random(430064);
        byte[] bytes = new byte[16];
        for (int i = 0; i < 4096; i++)
        {
            random.NextBytes(bytes);
            Check(BinaryPrimitives.ReadUInt64LittleEndian(bytes),
                BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(8)), signed: (i & 1) == 0);
        }
        for (int bit = 6; bit < 16; bit++)
        {
            try { OpcodeTable.GetOpcodeInfo(0x0189001c | (1u << bit)); }
            catch (NotImplementedException) { reserved++; continue; }
            throw new Exception($"DMULT accepted reserved bit {bit}");
        }

        // A branch executes the eight-cycle multiply in its delay slot. The
        // saved instruction boundary must also continue exactly after restore.
        byte[] Save()
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);
            R4300.SaveState(writer); writer.Flush(); return stream.ToArray();
        }
        foreach (bool taken in new[] { false, true })
        {
            Registers.R4300.PC = 0x80010000;
            Registers.R4300.Reg[1] = taken ? 0UL : 1UL;
            Registers.R4300.Reg[12] = 0x8000000000000000;
            Registers.R4300.Reg[9] = ulong.MaxValue;
            R4300.memory.WriteUInt32(0x80010004, 0x0189001c);
            byte[] saved = Save();
            ulong before = R4300.GetCycleCounter();
            R4300.InterpretOpcode(0x10200003); // BEQ r1,r0,+3
            if (Registers.R4300.PC != (taken ? 0x80010010u : 0x80010008u)
                || Registers.R4300.HI != 0 || Registers.R4300.LO != 0x8000000000000000
                || R4300.GetCycleCounter() - before != 9)
                throw new Exception($"DMULT branch delay mismatch: taken={taken}");
            byte[] expected = Save();
            using var reader = new BinaryReader(new MemoryStream(saved));
            R4300.LoadState(reader);
            R4300.InterpretOpcode(0x10200003);
            if (!Save().SequenceEqual(expected)) throw new Exception("DMULT restored continuation differs");
        }
        Console.WriteLine($"doubleMultiplyProducts={products} oracle=BigInteger reservedBits={reserved} delaySlots=2 fullStateRestore=passed cycles=passed");
    }
}
