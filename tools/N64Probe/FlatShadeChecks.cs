using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using Ryu64.MIPS;

internal static class FlatShadeChecks
{
    internal static void Run(string reference)
    {
        string actual = Check(typeof(Memory).Assembly, out int count);
        var context = new AssemblyLoadContext("flat-shade-reference", true);
        string expected = Check(context.LoadFromAssemblyPath(Path.GetFullPath(reference)), out int expectedCount);
        if (actual != expected || count != expectedCount) throw new Exception($"Flat/gradient shading differs: {actual}/{expected}");
        context.Unload();
        Console.WriteLine($"flatShadeCases={count} sha256={actual} differential=passed");
    }

    private static string Check(Assembly assembly, out int count)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        Type type = assembly.GetType("Ryu64.MIPS.Memory")!;
        object memory = Activator.CreateInstance(type, new object[] { new byte[4096] })!;
        void Set(string name, object value) => type.GetField(name, flags)!.SetValue(memory, value);
        byte[] ram = (byte[])type.GetField("RDRAM")!.GetValue(memory)!;
        byte[] tmem = (byte[])type.GetField("_rdpTmem", flags)!.GetValue(memory)!;
        Array.Fill(tmem, (byte)255);
        Set("_rdpColorImageAddress", 0x300000u); Set("_rdpColorImageWidth", 32u); Set("_rdpColorImageSize", 2u);
        Set("_rdpScissorX1", 31); Set("_rdpScissorY1", 31);
        Array tiles = (Array)type.GetField("_rdpTiles", flags)!.GetValue(memory)!;
        Type tileType = tiles.GetType().GetElementType()!;
        object tile = Activator.CreateInstance(tileType)!;
        void Tile(string name, object value) => tileType.GetField(name)!.SetValue(tile, value);
        Tile("Size", 2u); Tile("Line", 1u); Tile("Lrs", 4u); Tile("Lrt", 4u);
        Tile("TileSizeSet", true); Tile("ClampS", true); Tile("ClampT", true);
        tiles.SetValue(tile, 0);
        Type shadeType = type.GetNestedType("RdpTriangleShadeCoefficients", BindingFlags.NonPublic)!;
        object depth = Activator.CreateInstance(type.GetNestedType("RdpTriangleDepthCoefficients", BindingFlags.NonPublic)!)!;
        MethodInfo draw = type.GetMethod("DrawRdpTexturedTriangle", flags)!;
        string[] slopes = { "DrDx", "DgDx", "DbDx", "DaDx", "DrDe", "DgDe", "DbDe", "DaDe" };
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        count = 0;
        foreach (int component in new[] { -1, 0, 1, 127, 255, 256 })
        for (int gradient = -1; gradient < slopes.Length; gradient++)
        foreach (bool modulate in new[] { false, true })
        foreach (bool flip in new[] { false, true })
        foreach (double origin in new[] { 0.0, 0.75 })
        {
            Array.Clear(ram, 0x300000, 2048);
            object shade = Activator.CreateInstance(shadeType)!;
            void Shade(string name, int value) => shadeType.GetField(name)!.SetValue(shade, value);
            Shade("R", component << 16); Shade("G", (255 - component) << 16);
            Shade("B", 127 << 16); Shade("A", component << 16);
            if (gradient >= 0) Shade(slopes[gradient], (gradient % 2 == 0 ? 1 : -1) << 16);
            Set("_rdpOtherModesAlphaCompare", flip);
            bool wrote = (bool)draw.Invoke(memory, new object[] { 0x0e, 0u, false, shade, modulate, depth, false, 0,
                flip ? 0.0 : 16.0, 0.25, flip ? 16.0 : 0.0, 0.0, flip ? 16.0 : 0.0, 0.0,
                origin, 8.0, 16.0, flip, 2u })!;
            hash.AppendData(new[] { (byte)(wrote ? 1 : 0) });
            hash.AppendData(ram, 0x300000, 2048);
            count++;
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
}
