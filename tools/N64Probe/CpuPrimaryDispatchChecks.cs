using System.Buffers.Binary;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using Ryu64.MIPS;

internal static class CpuPrimaryDispatchChecks
{
    internal static void Run(string reference)
    {
        var expected = new AssemblyLoadContext("primary-reference", true);
        var oldResults = Check(expected.LoadFromAssemblyPath(Path.GetFullPath(reference)));
        expected.Unload();
        var newResults = Check(typeof(R4300).Assembly);
        if (!oldResults.SequenceEqual(newResults))
            throw new Exception($"Primary dispatch differs in case {Enumerable.Range(0, oldResults.Count).First(i => oldResults[i] != newResults[i])}");
        Console.WriteLine($"primaryDispatchCases={newResults.Count} state=passed exceptions=passed delaySlots=passed");
    }

    private static List<string> Check(Assembly assembly)
    {
        Type cpu = assembly.GetType("Ryu64.MIPS.R4300")!;
        Type memoryType = assembly.GetType("Ryu64.MIPS.Memory")!;
        var memory = Activator.CreateInstance(memoryType, new object[] { new byte[4096] })!;
        cpu.GetField("memory")!.SetValue(null, memory);
        assembly.GetType("Ryu64.MIPS.OpcodeTable")!.GetMethod("Init")!.Invoke(null, null);
        var interpret = cpu.GetMethod("InterpretOpcode")!.CreateDelegate<Action<uint>>();
        var save = cpu.GetMethod("SaveState")!.CreateDelegate<Action<BinaryWriter>>();
        var load = cpu.GetMethod("LoadState")!.CreateDelegate<Action<BinaryReader>>();
        var pc = assembly.GetType("Ryu64.MIPS.Registers+R4300")!.GetField("PC")!;
        var regs = (ulong[])assembly.GetType("Ryu64.MIPS.Registers+R4300")!.GetField("Reg")!.GetValue(null)!;
        var cop0 = (ulong[])assembly.GetType("Ryu64.MIPS.Registers+COP0")!.GetField("Reg")!.GetValue(null)!;
        var ram = (byte[])memoryType.GetField("RDRAM")!.GetValue(memory)!;
        pc.SetValue(null, 0x80001000u);
        for (int i = 0; i < regs.Length; i++) regs[i] = 0xffffffff80002000UL + (ulong)i * 8;
        regs[0] = 123; regs[2] = regs[1]; // Exercise zero-register normalization and taken BEQ.
        cop0[11] = 1; // Compare crosses on a branch + delay slot.
        for (int i = 0x1800; i < 0x2400; i++) ram[i] = (byte)(i * 37);
        using var initial = new MemoryStream();
        using var initialWriter = new BinaryWriter(initial, System.Text.Encoding.UTF8, true);
        save(initialWriter); initialWriter.Flush();
        using var initialReader = new BinaryReader(initial, System.Text.Encoding.UTF8, true);
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output, System.Text.Encoding.UTF8, true);
        var results = new List<string>();
        uint[] operands = { 0, 0x00220000, 0x00220001, 0x00220004, 0x0022fff8, 0x03ffffff };
        foreach (uint primary in new uint[] { 2, 3, 4, 5, 9, 12, 13, 35, 43, 55 })
        foreach (uint operand in operands)
        foreach (uint delay in new uint[] { 0, 0x24630001, 0x8c230001 })
        {
            initial.Position = 0; load(initialReader);
            BinaryPrimitives.WriteUInt32BigEndian(ram.AsSpan(0x1004), delay);
            Ryu64.Common.Measure.InstructionCount = 0;
            string fault = "";
            try { interpret(primary << 26 | operand); }
            catch (Exception ex) { fault = ex.GetType().FullName + ":" + ex.Message; }
            output.SetLength(0); save(writer); writer.Flush();
            results.Add(fault + ":" + Ryu64.Common.Measure.InstructionCount + ":" +
                Convert.ToHexString(SHA256.HashData(output.GetBuffer().AsSpan(0, (int)output.Length))));
        }
        return results;
    }
}
