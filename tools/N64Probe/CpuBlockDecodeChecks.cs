using System.Reflection;
using System.Runtime.Loader;
using Ryu64.MIPS;

internal static class CpuBlockDecodeChecks
{
    internal static void Run(string reference)
    {
        const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
        var context = new AssemblyLoadContext("block-decode-reference", true);
        var oldCpu = context.LoadFromAssemblyPath(Path.GetFullPath(reference)).GetType("Ryu64.MIPS.R4300")!;
        var expected = oldCpu.GetMethod("GetCpuBlockOpcodeKind", flags)!.CreateDelegate<Func<uint,int>>();
        var actual = typeof(R4300).GetMethod("GetCpuBlockOpcodeKind", flags)!.CreateDelegate<Func<uint,int>>();
        long cases = 0;
        void Check(uint opcode)
        {
            int before = expected(opcode), after = actual(opcode);
            if (before != after) throw new Exception($"Block decode {opcode:x8}: {before} != {after}");
            cases++;
        }
        // Every SPECIAL encoding, including all reserved-bit combinations.
        for (uint opcode = 0; opcode < (1u << 26); opcode++) Check(opcode);
        for (uint primary = 1; primary < 64; primary++)
        {
            Check(primary << 26);
            for (int bit = 0; bit < 26; bit++)
            {
                Check((primary << 26) | (1u << bit));
                Check((primary << 26) | (0x03ffffffu ^ (1u << bit)));
            }
        }
        // Exhaust the CP0 register selectors and vary every ignored low bit.
        for (uint rs = 0; rs < 32; rs++)
        for (uint rd = 0; rd < 32; rd++)
        for (uint low = 0; low < 2048; low++)
            Check(0x40000000u | (rs << 21) | ((low & 31) << 16) | (rd << 11) | low);
        uint sample = 64002026;
        for (int n = 0; n < 2_000_000; n++)
        {
            sample = unchecked(sample * 1664525u + 1013904223u);
            Check(sample);
        }
        context.Unload();
        Console.WriteLine($"blockDecodeDifferentialCases={cases} passed");
    }
}
