using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using Ryu64.MIPS;

internal static class SamplerChecks
{
    internal static void Run(string reference)
    {
        string actual = Check(typeof(Memory).Assembly, out int count);
        Console.WriteLine($"samplerCases={count} sha256={actual}");
        if (reference == null) return;
        var context = new AssemblyLoadContext("reference-sampler", isCollectible: true);
        var assembly = context.LoadFromAssemblyPath(Path.GetFullPath(reference));
        string expected = Check(assembly, out int referenceCount);
        if (actual != expected || count != referenceCount)
            throw new Exception($"Texture sampling differs from reference: {expected}");
        context.Unload();
        Console.WriteLine("samplerDifferential=passed");
    }

    private static string Check(Assembly assembly, out int count)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var memoryType = assembly.GetType("Ryu64.MIPS.Memory")!;
        object memory = Activator.CreateInstance(memoryType, new object[] { new byte[4096] })!;
        var tileType = memoryType.GetNestedType("RdpTileState", BindingFlags.NonPublic)!;
        var prepare = memoryType.GetMethod("PrepareRdpTextureSampler", flags)!;
        var sample = memoryType.GetMethods(flags).Single(m => m.Name == "SampleRdpTexture" && m.GetParameters().Length == 6);
        var random = new Random(6464);
        random.NextBytes((byte[])memoryType.GetField("_rdpTmem", flags)!.GetValue(memory)!);
        var tlut = (ushort[])memoryType.GetField("_rdpTlut", flags)!.GetValue(memory)!;
        for (int i = 0; i < tlut.Length; i++) tlut[i] = i % 3 == 0 ? (ushort)0 : (ushort)random.Next(65536);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        count = 0;
        for (uint format = 0; format < 8; format++)
        for (uint size = 0; size < 4; size++)
        for (int mode = 0; mode < 32; mode++)
        {
            void Mode(string name, bool value) => memoryType.GetField(name, flags)!.SetValue(memory, value);
            Mode("_rdpOtherModesEnableTlut", (mode & 1) != 0);
            Mode("_rdpOtherModesTlutType", (mode & 2) != 0);
            Mode("_rdpOtherModesSampleType", (mode & 4) != 0);
            Mode("_rdpOtherModesBiLerp0", (mode & 8) != 0);
            object tile = Activator.CreateInstance(tileType)!;
            void Tile(string name, object value) => tileType.GetField(name)!.SetValue(tile, value);
            Tile("Format", format); Tile("Size", size);
            Tile("Tmem", (uint)random.Next(512)); Tile("Line", (uint)random.Next(1, 64));
            Tile("Palette", (uint)random.Next(16));
            bool fast = (mode & 16) != 0;
            Tile("MaskS", fast ? 0u : (uint)random.Next(16));
            Tile("MaskT", fast ? 0u : (uint)random.Next(16));
            Tile("ShiftS", fast ? 0u : (uint)random.Next(16));
            Tile("ShiftT", fast ? 0u : (uint)random.Next(16));
            Tile("ClampS", fast || random.Next(2) == 0); Tile("ClampT", fast || random.Next(2) == 0);
            Tile("MirrorS", random.Next(2) == 0); Tile("MirrorT", random.Next(2) == 0);
            uint originS = (uint)random.Next(8), originT = (uint)random.Next(8);
            Tile("Uls", originS * 4); Tile("Ult", originT * 4);
            Tile("Lrs", (originS + 31) * 4); Tile("Lrt", (originT + 15) * 4);
            Tile("TileSizeSet", true);
            object sampler = prepare.Invoke(memory, new[] { tile })!;
            foreach (int coordinate in new[] { -4096, -1, 0, 1, 15, 31, 63, 4096 })
            foreach ((int fs, int ft) in new[] { (0, 0), (0, 31), (15, 16), (16, 16), (31, 31) })
            {
                object[] args = { sampler, coordinate, coordinate / 2, fs, ft, 0u };
                bool valid = (bool)sample.Invoke(memory, args)!;
                hash.AppendData(new[] { (byte)(valid ? 1 : 0) });
                hash.AppendData(BitConverter.GetBytes((uint)args[5]));
                count++;
            }
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
}
