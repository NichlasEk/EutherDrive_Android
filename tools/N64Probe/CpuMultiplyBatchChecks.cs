using System.Buffers.Binary;
using System.Reflection;
using System.Security.Cryptography;
using Ryu64.MIPS;

internal static class CpuMultiplyBatchChecks
{
    internal static void Run()
    {
        const BindingFlags cpuFlags = BindingFlags.Static | BindingFlags.NonPublic;
        const BindingFlags memoryFlags = BindingFlags.Instance | BindingFlags.NonPublic;
        var batch = typeof(R4300).GetMethod("TryAdvanceMultiplyRoutine", cpuFlags)!.CreateDelegate<Func<uint, uint, uint>>();
        var count = typeof(R4300).GetField("Count", cpuFlags)!;
        uint[] code = { 0xafa40000, 0xafa50004, 0xafa60008, 0xafa7000c, 0xdfaf0008, 0xdfae0000,
            0x01cf001d, 0x00001012, 0x0002183c, 0x0003183f, 0x03e00008, 0x0002103f };
        R4300.memory = new Memory(new byte[4096]); OpcodeTable.Init();
        Registers.R4300.PC = 0x80010000;
        Registers.R4300.Reg[29] = 0xffffffff80020000;
        Registers.R4300.Reg[31] = 0xffffffff80030000;
        Registers.COP0.Reg[Registers.COP0.COMPARE_REG] = 0;
        R4300.memory.WriteUInt32(0xa4400018, 524);
        R4300.memory.WriteUInt32(0xa440000c, 100);
        R4300.memory.Tick(1);
        for (int i = 0; i < code.Length; i++) BinaryPrimitives.WriteUInt32BigEndian(R4300.memory.RDRAM.AsSpan(0x10000 + i * 4), code[i]);
        using var initial = new MemoryStream();
        using var initialWriter = new BinaryWriter(initial, System.Text.Encoding.UTF8, true);
        R4300.SaveState(initialWriter); initialWriter.Flush();
        using var initialReader = new BinaryReader(initial, System.Text.Encoding.UTF8, true);
        using var before = new MemoryStream();
        using var beforeWriter = new BinaryWriter(before, System.Text.Encoding.UTF8, true);
        using var beforeReader = new BinaryReader(before, System.Text.Encoding.UTF8, true);
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output, System.Text.Encoding.UTF8, true);
        string Hash() { output.SetLength(0); R4300.SaveState(writer); writer.Flush(); return Convert.ToHexString(SHA256.HashData(output.GetBuffer().AsSpan(0, (int)output.Length))); }
        void Reset() { initial.Position = 0; R4300.LoadState(initialReader); }
        void Set(string name, object value) => typeof(Memory).GetField(name, memoryFlags)!.SetValue(R4300.memory, value);
        int cases = 0;
        void Check(bool accepted, uint budget = 12)
        {
            before.SetLength(0); R4300.SaveState(beforeWriter); beforeWriter.Flush();
            string original = Hash(); Ryu64.Common.Measure.InstructionCount = 0;
            uint n = batch(Registers.R4300.PC, budget); string actual = Hash();
            if (n != (accepted ? 12 : 0) || Ryu64.Common.Measure.InstructionCount != n)
                throw new Exception($"Batch acceptance case {cases}: {n}, wanted {accepted}");
            if (accepted)
            {
                before.Position = 0; R4300.LoadState(beforeReader); Ryu64.Common.Measure.InstructionCount = 0;
                for (int i = 0; i < 11; i++) R4300.InterpretOpcode(code[i]); // JR executes instruction 12 itself.
                if (Hash() != actual || Ryu64.Common.Measure.InstructionCount != 12) throw new Exception($"Batch state mismatch case {cases}");
            }
            else if (original != actual) throw new Exception($"Rejected batch changed state case {cases}");
            cases++;
        }
        if (Environment.GetEnvironmentVariable("N64_PROBE_EXPECT_BATCH_DISABLED") == "1")
        { Reset(); Check(false); Console.WriteLine("multiplyBatchDisabled=passed"); return; }
        var random = new Random(64320);
        for (int i = 0; i < 64; i++)
        {
            Reset();
            for (int r = 4; r <= 7; r++) Registers.R4300.Reg[r] = (ulong)random.NextInt64() ^ ((ulong)random.Next(2) << 63);
            Registers.R4300.Reg[0] = 42;
            Registers.COP0.Reg[Registers.COP0.RANDOM_REG] = (uint)(i & 31);
            Registers.COP0.Reg[Registers.COP0.WIRED_REG] = (uint)(i / 2);
            Check(true);
        }
        for (int i = 0; i < code.Length; i++)
        { Reset(); R4300.memory.RDRAM[0x10000 + i * 4 + 3] ^= 1; Check(false); }
        foreach (ulong sp in new ulong[] { 0x80020001, 0x80010000, 0x80010028, 0x8000fff8, 0x807ffff8, 0xa4040000, 0x70020000 })
        { Reset(); Registers.R4300.Reg[29] = sp; Check(false); }
        foreach (uint budget in new uint[] { 0, 1, 11, 12, 100 }) { Reset(); Check(budget >= 12, budget); }
        Reset(); Registers.R4300.PC = 0xa0010000; Registers.R4300.Reg[29] = 0xffffffffa0020000; Check(true);
        foreach (uint pc in new uint[] { 0x80010001, 0x807ffff0, 0x70010000 })
        { Reset(); Registers.R4300.PC = pc; Check(false); }
        Reset(); Registers.R4300.Reg[29] = 0xffffffffa0010000; Check(false); // Aliased self-modification.
        foreach (uint wired in new uint[] { 0, 1, 30, 31 })
        foreach (uint r in new uint[] { 0, 1, 15, 30, 31 })
        { Reset(); Registers.COP0.Reg[6] = wired; Registers.COP0.Reg[1] = r; Check(true); }
        foreach (uint compare in new uint[] { 0, 1, 8, 9, 10, 11 })
        { Reset(); Registers.COP0.Reg[11] = compare; Check(compare == 0 || compare > 9); }
        foreach (ulong c in new ulong[] { 1, 2, 0x1fffffffe, 0x1fffffffc, 0x1ffffffe0 })
        { Reset(); count.SetValue(null, c); Registers.COP0.Reg[9] = c >> 1; Check(((c + 19) >> 1) < uint.MaxValue); }
        (string active, string remaining)[] timers = {
            ("_spDmaDelayArmed", "_spDmaDelayRemaining"), ("_rspTaskActive", "_rspTaskCyclesRemaining"),
            ("_rspInterruptDelayArmed", "_rspInterruptDelayRemaining"), ("_dpInterruptDelayArmed", "_dpInterruptDelayRemaining"),
            ("_piInterruptDelayArmed", "_piInterruptDelayRemaining"), ("_siInterruptDelayArmed", "_siInterruptDelayRemaining"),
            ("_aiInterruptDelayArmed", "_aiInterruptDelayRemaining") };
        foreach (var timer in timers)
        foreach (uint distance in new uint[] { 1, 18, 19, 20, 100 })
        { Reset(); Set(timer.active, true); Set(timer.remaining, distance); Check(distance > 19); }
        foreach (uint distance in new uint[] { 1, 18, 19, 20, 100 })
        { Reset(); Set("_viInterruptCyclesRemaining", distance); Check(distance > 19); }
        foreach (uint remaining in new uint[] { 1, 18, 19, 20 })
        { Reset(); Set("_viLineCycleAccum", 1562500u / 525u - remaining); Check(remaining > 19); }
        Reset(); Registers.COP0.Reg[13] |= 0x100; Check(false);
        Reset(); R4300.memory.MI_INTR_REG_R[3] = 1; R4300.memory.MI_INTR_MASK_REG_R[3] = 1; Check(false);
        foreach (string field in new[] { "_executingDelaySlot", "_delaySlotExceptionPending" })
        { Reset(); typeof(R4300).GetField(field, cpuFlags)!.SetValue(null, true); Check(false); }
        Reset(); Registers.COP0.Reg[9] = 1; Check(false); // Internal Count mismatch.
        Reset(); Ryu64.Common.Variables.Debug = true; Check(false); Ryu64.Common.Variables.Debug = false;
        Reset(); Ryu64.Common.Settings.STEP_MODE = true; Check(false); Ryu64.Common.Settings.STEP_MODE = false;
        Console.WriteLine($"multiplyBatchCases={cases} fullState=passed rejectionUnchanged=passed events=passed");
    }
}
