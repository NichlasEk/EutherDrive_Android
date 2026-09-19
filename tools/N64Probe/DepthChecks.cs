using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using Ryu64.MIPS;

internal static class DepthChecks
{
    private delegate bool Compare(uint pixel, long z, uint dz, uint encodedDz);

    internal static void Run(string reference)
    {
        string Check(Assembly assembly)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            Type type = assembly.GetType("Ryu64.MIPS.Memory")!;
            object memory = Activator.CreateInstance(type, new object[] { new byte[4096] })!;
            var compare = type.GetMethod("PassRdpDepthTest", flags)!.CreateDelegate<Compare>(memory);
            byte[] ram = (byte[])type.GetField("RDRAM")!.GetValue(memory)!;
            byte[] hidden = (byte[])type.GetField("_rdpHiddenBits", flags)!.GetValue(memory)!;
            void Set(string name, object value) => type.GetField(name, flags)!.SetValue(memory, value);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var random = new Random(64919);
            byte[] result = new byte[4];
            for (uint mode = 0; mode < 4; mode++)
            for (int controls = 0; controls < 4; controls++)
            {
                Set("_rdpOtherModesZMode", mode);
                Set("_rdpOtherModesZCompare", (controls & 1) != 0);
                Set("_rdpOtherModesZUpdate", (controls & 2) != 0);
                for (int i = 0; i < 65536; i++)
                for (int hiddenBits = 0; hiddenBits < 4; hiddenBits++)
                {
                    // Sweep every encoded depth, all hidden-bit combinations,
                    // and depths on either side of clipping/wrapping boundaries.
                    uint address = i % 31 == 0 ? (uint)ram.Length - 2 : 128u;
                    Set("_rdpMaskImageAddress", address);
                    uint index = address >> 1;
                    ram[address] = (byte)(i >> 8);
                    ram[address + 1] = (byte)i;
                    hidden[index] = (byte)hiddenBits;
                    long z = i % 8 == 0 ? ((long)(i & 0x7ffff) << 13) - 1
                        : random.NextInt64(-1L << 34, 1L << 34);
                    uint dz = i % 2 == 0 ? 1u << (i % 16) : (uint)random.Next(1, 65536);
                    bool pass = compare(i % 127 == 0 ? 1u : 0u, z, dz, (uint)(i & 15));
                    result[0] = pass ? (byte)1 : (byte)0;
                    result[1] = ram[address]; result[2] = ram[address + 1]; result[3] = hidden[index];
                    hash.AppendData(result);
                }
            }
            // Also compare every write, including neighboring-pixel calls.
            hash.AppendData(ram); hash.AppendData(hidden);
            return Convert.ToHexString(hash.GetHashAndReset());
        }

        var context = new AssemblyLoadContext("reference-depth", true);
        string expected = Check(context.LoadFromAssemblyPath(Path.GetFullPath(reference)));
        string actual = Check(typeof(Memory).Assembly);
        if (actual != expected) throw new Exception($"Depth state differs: {actual}/{expected}");
        context.Unload();
        Console.WriteLine($"depthCases=4194304 sha256={actual} differential=passed");
    }
}
