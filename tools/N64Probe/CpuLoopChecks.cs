using System.Buffers.Binary;
using System.Reflection;
using System.Security.Cryptography;
using Ryu64.MIPS;

internal static class CpuLoopChecks
{
    internal static void Run()
    {
        Func<uint, bool> Bind(string name) => typeof(R4300)
            .GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!
            .CreateDelegate<Func<uint, bool>>();
        var zero = Bind("TryFastForwardInitialZeroLoop");
        var bytes = Bind("TryFastForwardByteZeroUntilPointerLoop");
        var pairs = Bind("TryFastForwardPairStoreUntilPointerLoop");
        var gated = Bind("TryFastForwardMemoryLoops");
        bool Original(uint pc) => zero(pc) || bytes(pc) || pairs(pc);
        uint[][] patterns =
        [
            [0x2129fff8, 0xad000000, 0xad000004, 0x1520fffc, 0x21080008],
            [0x2529fff8, 0xad000000, 0xad000004, 0x1520fffc, 0x21080008],
            [0xad000000, 0xad000004, 0x21080008, 0x2129fff8, 0x1520fffb, 0],
            [0xad000000, 0xad000004, 0x21080008, 0x2529fff8, 0x1520fffb, 0],
            [0x8c8b0004, 0x24a50001, 0x00ab082b, 0x5420fffc, 0xa0a00000],
            [0x24420008, 0x0043082b, 0x24080000, 0x24090000, 0xac49fffc, 0x1420fffa, 0xac48fff8]
        ];
        R4300.memory = new Memory(new byte[4096]);
        using var initial = new MemoryStream();
        using var result = new MemoryStream();
        using var writer = new BinaryWriter(initial, System.Text.Encoding.UTF8, true);
        using var resultWriter = new BinaryWriter(result, System.Text.Encoding.UTF8, true);
        using var reader = new BinaryReader(initial, System.Text.Encoding.UTF8, true);
        int cases = 0, accepted = 0;
        void Check(uint pc, bool? expected = null)
        {
            Registers.R4300.PC = pc;
            initial.SetLength(0);
            R4300.SaveState(writer);
            writer.Flush();
            (bool, string, ulong) Execute(Func<uint, bool> run)
            {
                initial.Position = 0;
                R4300.LoadState(reader);
                Ryu64.Common.Measure.InstructionCount = 0;
                bool found = run(pc);
                result.SetLength(0);
                R4300.SaveState(resultWriter);
                resultWriter.Flush();
                return (found, Convert.ToHexString(SHA256.HashData(result.GetBuffer().AsSpan(0, (int)result.Length))),
                    Ryu64.Common.Measure.InstructionCount);
            }
            var reference = Execute(Original);
            var actual = Execute(gated);
            if (reference != actual || expected.HasValue && actual.Item1 != expected.Value)
                throw new Exception($"CPU loop gate mismatch pc={pc:x8} case={cases}: {reference} != {actual}");
            cases++;
            if (actual.Item1) accepted++;
            initial.Position = 0;
            R4300.LoadState(reader);
        }
        void Setup(uint segment)
        {
            Array.Fill(R4300.memory.RDRAM, (byte)0xa5, 0, 0x4000);
            Array.Clear(Registers.R4300.Reg);
            Registers.R4300.Reg[2] = segment + 0x2000;
            Registers.R4300.Reg[3] = segment + 0x2040;
            Registers.R4300.Reg[4] = segment + 0x3000;
            Registers.R4300.Reg[5] = segment + 0x2000;
            Registers.R4300.Reg[8] = segment + 0x2000;
            Registers.R4300.Reg[9] = 0x40;
            BinaryPrimitives.WriteUInt32BigEndian(R4300.memory.RDRAM.AsSpan(0x3004), segment + 0x2040);
        }
        foreach (uint segment in new uint[] { 0x80000000, 0xa0000000 })
        foreach (var pattern in patterns)
        {
            Setup(segment);
            void Write(int i, uint value) => BinaryPrimitives.WriteUInt32BigEndian(R4300.memory.RDRAM.AsSpan(0x1000 + i * 4), value);
            for (int i = 0; i < pattern.Length; i++) Write(i, pattern[i]);
            for (int entry = 0; entry < pattern.Length; entry++)
            {
                uint pc = segment + 0x1000u + (uint)entry * 4;
                Check(pc, true);
                // Mutate every word in the live code, then restore it, at the
                // same PC. The prefilter must never bypass full validation.
                for (int corrupt = 0; corrupt < pattern.Length; corrupt++)
                {
                    Write(corrupt, pattern[corrupt] ^ 1u);
                    Check(pc, false);
                    Write(corrupt, pattern[corrupt]);
                }
                Check(pc, true);
            }
        }
        Setup(0x80000000);
        foreach (uint pc in new uint[] { 0, 0x1000, 0x80000000, 0x80000001, 0x807ffffc,
            0x80800000, 0x9ffffffc, 0xa0000000, 0xa07ffffc, 0xbffffffc, 0xc0001000, 0xffffffff }) Check(pc, false);
        Console.WriteLine($"CPU loop gate: {cases} full-state differential cases passed ({accepted} accepted loops).");
    }
}
