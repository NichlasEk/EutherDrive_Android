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
        var pollingMethods = new[] { "CompareLoadPollingLoop", "BranchLinkIdleLoop", "IdleLoop" }
            .Select(n => Bind("TryFastForward" + n)).ToArray();
        Func<uint, bool> originalRuntime = pc => zero(pc) || bytes(pc) || pairs(pc)
            || pollingMethods.Any(run => run(pc));
        var gated = Bind("TryFastForwardRuntimeLoops");
        Func<uint, bool> original = originalRuntime;
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
            var reference = Execute(original);
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
        var bootMethods = new[] { "BootChecksumLoop", "BootClearLoop", "BootAssetDecode", "Ipl3CacheLoop",
            "Ipl3CopyLoop", "Ipl3StoreDelayLoop", "Ipl3SpStoreFillLoop" }.Select(n => Bind("TryFastForward" + n)).ToArray();
        original = pc => bootMethods.Any(run => run(pc));
        gated = Bind("TryFastForwardBootLoops");
        Setup(0x80000000);
        foreach (uint boundary in new uint[] { 0, 0x80000000, 0x80000184, 0x80000268, 0x8000026c,
            0x80000280, 0x80000284, 0x800012c4, 0x80003074, 0x80004000, 0xa4000000,
            0xa4000428, 0xa4000434, 0xa4000448, 0xa4000454, 0xa4000498, 0xa40004ac, 0xa4001000, 0xffffffff })
        foreach (int delta in new[] { -4, -1, 0, 1, 4 }) Check(unchecked(boundary + (uint)delta), false);

        // Both fixed cache windows and a generic IPL3 loop must survive the
        // shared region prefilter, including all four entry positions.
        foreach (uint address in new uint[] { 0x428, 0x448, 0x900 })
        {
            Setup(0x80000000);
            uint stride = address == 0x428 ? 32u : 16u;
            uint cache = address == 0x428 ? 0xbd080000u : address == 0x448 ? 0xbd010000u : 0xbd000000u;
            uint[] code = { cache, 0x0109082b, 0x1420fffd, 0x25080000 | stride };
            for (int i = 0; i < code.Length; i++)
                BinaryPrimitives.WriteUInt32BigEndian(R4300.memory.SP_MEM_RW.AsSpan((int)address + i * 4), code[i]);
            Registers.R4300.Reg[8] = 0x80010000;
            Registers.R4300.Reg[9] = 0x80010100;
            for (uint offset = 0; offset < 16; offset += 4) Check(0xa4000000 + address + offset, true);
        }
        Setup(0x80000000);
        uint[] clear = { 0x24840020, 0xac80ffe0, 0xac80ffe4, 0xac80ffe8, 0xac80ffec,
            0xac80fff0, 0xac80fff4, 0xac80fff8, 0x1487fff7 };
        for (int i = 0; i < clear.Length; i++)
            BinaryPrimitives.WriteUInt32BigEndian(R4300.memory.RDRAM.AsSpan(0x3074 + i * 4), clear[i]);
        Registers.R4300.Reg[4] = 0x80002000;
        Registers.R4300.Reg[7] = 0x80002040;
        Check(0x80003074, true);

        original = originalRuntime;
        gated = Bind("TryFastForwardRuntimeLoops");
        foreach (uint segment in new uint[] { 0x80000000, 0xa0000000 })
        foreach (uint[] code in new uint[][] {
            [0x01e4082a, 0x5420fffe, 0x8c4f0000],
            [0x0411ffff, 0], [0x1000ffff, 0] })
        {
            Setup(segment);
            Registers.R4300.Reg[4] = 10;
            BinaryPrimitives.WriteUInt32BigEndian(R4300.memory.RDRAM.AsSpan(0x2000), 5);
            void Write(int i, uint word) => BinaryPrimitives.WriteUInt32BigEndian(
                R4300.memory.RDRAM.AsSpan(0x1000 + i * 4), word);
            for (int i = 0; i < code.Length; i++) Write(i, code[i]);
            Check(segment + 0x1000, true);
            for (int i = 0; i < code.Length; i++)
            {
                Write(i, code[i] ^ 1u);
                Check(segment + 0x1000, false);
                Write(i, code[i]);
                Check(segment + 0x1000, true);
            }
            if (code.Length == 3)
            {
                foreach (uint value in new uint[] { 9, 10, 11, 0xffffffff })
                {
                    BinaryPrimitives.WriteUInt32BigEndian(R4300.memory.RDRAM.AsSpan(0x2000), value);
                    Check(segment + 0x1000, unchecked((int)value) < 10);
                }
                Registers.R4300.Reg[2] = 0x1000;
                Check(segment + 0x1000, false);
            }
        }
        Setup(0x80000000);
        BinaryPrimitives.WriteUInt32BigEndian(R4300.memory.RDRAM.AsSpan(0x810), 0x1000ffff);
        BinaryPrimitives.WriteUInt32BigEndian(R4300.memory.RDRAM.AsSpan(0x814), 0);
        Check(0x80000810, true);
        Check(0x80000814, true);
        Check(0xa0000810, true);
        Check(0xa0000814, false);
        BinaryPrimitives.WriteUInt32BigEndian(R4300.memory.RDRAM.AsSpan(0x810), 0);
        Check(0x80000814, false);
        foreach (uint pc in new uint[] { 0, 0x1000, 0x80000001, 0x807ffffc, 0x807ffffd,
            0x80800000, 0xa07ffffc, 0xa4000000, 0xb0000000, 0xbfc00000, 0xc0001000, 0xffffffff })
            Check(pc, false);
        Console.WriteLine($"CPU loop gates: {cases} full-state differential cases passed ({accepted} accepted loops).");
    }
}
