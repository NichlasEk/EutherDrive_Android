using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using Ryu64.MIPS;

internal static class CombinerChecks
{
    internal static void Run(string reference)
    {
        string actual = Check(typeof(Memory).Assembly, out int count);
        Console.WriteLine($"combinerCases={count} sha256={actual}");
        if (reference == null) return;
        var context = new AssemblyLoadContext("reference-combiner", isCollectible: true);
        string expected = Check(context.LoadFromAssemblyPath(Path.GetFullPath(reference)), out int referenceCount);
        if (actual != expected || count != referenceCount) throw new Exception($"Combiner differs: {expected}");
        context.Unload();
        Console.WriteLine("combinerDifferential=passed");
    }

    private static string Check(Assembly assembly, out int count)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var type = assembly.GetType("Ryu64.MIPS.Memory")!;
        object memory = Activator.CreateInstance(type, new object[] { new byte[4096] })!;
        assembly.GetType("Ryu64.MIPS.R4300")!.GetField("memory")!.SetValue(null, memory);
        var set = type.GetMethod("ExecuteRdpSetCombine", flags)!.CreateDelegate<Action<uint, uint>>(memory);
        var combine = type.GetMethod("ApplyRdpColorCombiner", flags)!.CreateDelegate<Func<uint, uint, uint>>(memory);
        void Field(string name, object value) => type.GetField(name, flags)!.SetValue(memory, value);
        var random = new Random(646464);
        uint Word() => (uint)random.NextInt64(0, 1L << 32);
        var modes = new List<ulong> { 0xfc126203fffffff8, 0xfcfffffffffe7b3d, 0xfc127e24fffff9fc, 0xfc129a25ff37ffff,
            0xfc121824ff33ffff, 0xfcfffffffffe793c, 0xfcfffffffffcf279, 0xfc42ca85ff97ffff };
        // Change every mux bit around two-cycle texture/shade/primitive
        // modulation, including alpha and cycle-1 COMBINED dependencies.
        for (int bit = 0; bit < 56; bit++) modes.Add(0xfc126203fffffff8 ^ (1UL << bit));
        ulong Pack(int a, int b, int c, int d, int aa, int ab, int ac, int ad)
        {
            ulong mode = 0xfc00000000000000;
            mode |= (ulong)a << 52 | (ulong)c << 47 | (ulong)aa << 44 | (ulong)ac << 41;
            mode |= (ulong)a << 37 | (ulong)c << 32 | (ulong)b << 28 | (ulong)b << 24;
            mode |= (ulong)aa << 21 | (ulong)ac << 18 | (ulong)d << 15 | (ulong)ab << 12;
            mode |= (ulong)ad << 9 | (ulong)d << 6 | (ulong)ab << 3 | (ulong)ad;
            return mode;
        }
        for (int a = 0; a < 6; a++)
        for (int c = 0; c < 6; c++)
        {
            modes.Add(Pack(a, 15, c, 7, a, 7, c, 7));
            for (int alpha = 0; alpha < 8; alpha++) modes.Add(Pack(a, 15, c, 7, 7, 7, 7, alpha));
        }
        for (int d = 0; d < 8; d++)
        for (int alpha = 0; alpha < 8; alpha++) modes.Add(Pack(15, 15, 31, d, 7, 7, 7, alpha));
        for (int i = 0; i < 2048; i++) modes.Add((ulong)Word() << 32 | Word());
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var state = new MemoryStream();
        using var writer = new BinaryWriter(state, System.Text.Encoding.UTF8, true);
        using var reader = new BinaryReader(state, System.Text.Encoding.UTF8, true);
        var save = type.GetMethod("SaveState")!.CreateDelegate<Action<BinaryWriter>>(memory);
        var load = type.GetMethod("LoadState")!.CreateDelegate<Action<BinaryReader>>(memory);
        count = 0;
        for (int mode = 0; mode < modes.Count; mode++)
        {
            ulong mux = modes[mode];
            set((uint)(mux >> 32), (uint)mux);
            // Loading an earlier mode over a different live mode must rebuild
            // derived dispatch, rather than retaining the more recent plan.
            if (mode % 137 == 0)
            {
                state.SetLength(0); save(writer); writer.Flush();
                set(Word(), Word());
                state.Position = 0; load(reader);
            }
            for (uint cycle = 0; cycle < 4; cycle++)
            {
                Field("_rdpOtherModesCycleType", cycle);
                for (int sample = 0; sample < (mode == 0 ? 256 : 16); sample++)
                {
                    // Sweep every texel/alpha level through the two separate
                    // rounding steps, including ONE * 255 producing 254.
                    uint texel = mode == 0 ? (uint)sample * 0x01010101u
                        : sample < 4 ? (uint)sample * 0x55555555u : Word();
                    uint shade = sample < 4 ? ~texel : Word();
                    Field("_rdpPrimColor", Word()); Field("_rdpEnvColor", Word());
                    hash.AppendData(BitConverter.GetBytes(combine(texel, shade)));
                    count++;
                }
            }
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
}
