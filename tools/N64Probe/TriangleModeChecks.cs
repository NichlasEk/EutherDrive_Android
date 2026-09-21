using System.Buffers.Binary;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using Ryu64.MIPS;

internal static class TriangleModeChecks
{
    internal static void Run(string reference)
    {
        var context = new AssemblyLoadContext("triangle-mode-reference", true);
        var actual = new Scene(typeof(Memory).Assembly);
        var expected = new Scene(context.LoadFromAssemblyPath(Path.GetFullPath(reference)));
        const int cases = 9216;
        for (int i = 0; i < cases; i++)
        {
            string a = actual.Draw(i), b = expected.Draw(i);
            if (a != b) throw new Exception($"Triangle mode case {i}: {a} != {b}");
        }
        string result = actual.StateHash();
        if (result != expected.StateHash()) throw new Exception("Triangle mode final memory/device state differs");
        context.Unload();
        Console.WriteLine($"triangleModeCases={cases} sha256={result} differential=passed");
    }

    private sealed class Scene
    {
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
        private readonly Type type, shadeType, depthType, tileType;
        private readonly object memory;
        private readonly byte[] ram, hidden, tmem;
        private readonly Array tiles;
        private readonly MethodInfo draw;
        private readonly Action<BinaryWriter> save;

        internal Scene(Assembly assembly)
        {
            type = assembly.GetType("Ryu64.MIPS.Memory")!;
            memory = Activator.CreateInstance(type, new object[] { new byte[4096] })!;
            ram = (byte[])type.GetField("RDRAM")!.GetValue(memory)!;
            hidden = (byte[])type.GetField("_rdpHiddenBits", Flags)!.GetValue(memory)!;
            tmem = (byte[])type.GetField("_rdpTmem", Flags)!.GetValue(memory)!;
            tiles = (Array)type.GetField("_rdpTiles", Flags)!.GetValue(memory)!;
            tileType = tiles.GetType().GetElementType()!;
            shadeType = type.GetNestedType("RdpTriangleShadeCoefficients", BindingFlags.NonPublic)!;
            depthType = type.GetNestedType("RdpTriangleDepthCoefficients", BindingFlags.NonPublic)!;
            draw = type.GetMethod("DrawRdpTexturedTriangle", Flags)!;
            save = type.GetMethod("SaveState")!.CreateDelegate<Action<BinaryWriter>>(memory);
            Set("_rdpColorImageAddress", 0x300000u);
            Set("_rdpColorImageWidth", 32u);
            Set("_rdpScissorX1", 31); Set("_rdpScissorY1", 31);
        }

        private void Set(string name, object value) => type.GetField(name, Flags)!.SetValue(memory, value);

        internal string Draw(int index)
        {
            var random = new Random(1901 + index);
            foreach (int start in new[] { 0x1000, 0x300000, ram.Length - 4096 })
            {
                random.NextBytes(ram.AsSpan(start, 4096));
                random.NextBytes(hidden.AsSpan(start >> 1, 2048));
            }
            random.NextBytes(tmem);
            if (index % 7 == 0)
                for (int i = 1; i < tmem.Length; i += 2) tmem[i] &= 254;
            uint zAddress = (index / 4 % 5) switch
            {
                0 => 0x1000u, 1 => 0x300000u, 2 => 0x300100u,
                3 => (uint)ram.Length - 512, _ => uint.MaxValue - 127
            };
            bool modulate = index >= 4096 || index % 13 != 0;
            bool useDepth = index >= 4096 ? (index & 1) != 0 : index % 17 != 0;
            uint pixelBytes = index < 4096 && index % 5 == 0 ? 4u : 2u;
            Set("_rdpScissorX0", 0); Set("_rdpScissorY0", 0);
            Set("_rdpMaskImageAddress", zAddress);
            Set("_rdpOtherModesZMode", (uint)(index & 3));
            Set("_rdpOtherModesZCompare", index % 11 != 0);
            Set("_rdpOtherModesZUpdate", index % 3 != 0);
            Set("_rdpOtherModesZSourceSel", index % 13 == 0);
            Set("_rdpPrimitiveDepth", (uint)random.Next(0x8000));
            Set("_rdpOtherModesAlphaCompare", index / 24 % 4 == 1);
            Set("_rdpOtherModesCvgTimesAlpha", index / 24 % 4 == 2);
            Set("_rdpOtherModesCycleType", (uint)(index / 96 % 3));
            Set("_rdpOtherModesCvgDest", (uint)(index / 288 % 4));
            Set("_rdpOtherModesForceBlend", index % 17 == 0);
            Set("_rdpOtherModesSampleType", index % 3 != 0);
            Set("_rdpOtherModesBiLerp0", true);
            Set("_rdpOtherModesPerspectiveTexture", index % 2 == 0);
            Set("_rdpOtherModesEnableTlut", index % 19 == 0);
            Set("_rdpOtherModesTlutType", index % 23 == 0);
            Set("_rdpColorImageSize", index % 5 == 0 ? 3u : 2u);
            Set("_rdpCombineModeSet", index % 3 == 1);
            Set("_rdpCombineFast1", 4 + index % 3);
            Set("_rdpEnvColor", (uint)random.NextInt64(1L << 32));

            object tile = Activator.CreateInstance(tileType)!;
            void Tile(string name, object value) => tileType.GetField(name)!.SetValue(tile, value);
            int coordinates = index / 4 % 6;
            Tile("Format", index % 29 == 0 ? 3u : 0u);
            Tile("Size", index % 31 == 0 ? 1u : 2u);
            Tile("Line", (uint)random.Next(1, 9)); Tile("Tmem", (uint)random.Next(512));
            Tile("Uls", 8u); Tile("Ult", 12u); Tile("Lrs", 68u); Tile("Lrt", 72u);
            Tile("TileSizeSet", true);
            Tile("ClampS", coordinates < 3); Tile("ClampT", coordinates < 3);
            Tile("MaskS", coordinates < 3 ? 0u : 4u); Tile("MaskT", coordinates < 3 ? 0u : 4u);
            Tile("MirrorS", coordinates == 4); Tile("ShiftT", coordinates == 5 ? 2u : 0u);
            if (index >= 4096)
            {
                Set("_rdpOtherModesPerspectiveTexture", index / 2 % 2 == 0);
                Set("_rdpOtherModesCycleType", 0u); Set("_rdpCombineModeSet", true);
                Set("_rdpCombineFast1", 4); Set("_rdpOtherModesEnableTlut", false);
                Set("_rdpOtherModesSampleType", true); Set("_rdpOtherModesBiLerp0", true);
                Set("_rdpOtherModesForceBlend", false); Set("_rdpOtherModesAlphaCompare", false);
                Set("_rdpOtherModesCvgTimesAlpha", false); Set("_rdpColorImageSize", 2u);
                Tile("Format", 0u); Tile("Size", 2u);
                Tile("ClampS", (index & 1) == 0); Tile("ClampT", (index & 1) == 0);
                Tile("MaskS", 4u); Tile("MaskT", 4u);
                Tile("MirrorS", false); Tile("MirrorT", false); Tile("ShiftT", 0u);
            }
            // Mix the forced-blend pipeline with the two opaque paths, then
            // perturb one eligibility condition at a time near their boundaries.
            if (index >= 4096 && (index & 1) != 0 && index / 4 % 3 == 2)
            {
                Set("_rdpCombineFast1", 5); Set("_rdpOtherModesForceBlend", true);
            }
            int boundary = index >= 8192 ? (index - 8192) / 2 % 24 : -1;
            switch (boundary)
            {
                case 0: modulate = false; break;
                case 2: pixelBytes = 4u; Set("_rdpColorImageSize", 3u); break;
                case 3: Set("_rdpCombineModeSet", false); break;
                case 4: Set("_rdpOtherModesCycleType", 1u); break;
                case 5: Set("_rdpCombineFast1", 6); break;
                case 6: Set("_rdpOtherModesEnableTlut", true); break;
                case 7: Set("_rdpOtherModesForceBlend", true); Set("_rdpCombineFast1", 4); break;
                case 8: Set("_rdpOtherModesAlphaCompare", true); break;
                case 9: Set("_rdpOtherModesCvgTimesAlpha", true); break;
                case 10: Set("_rdpOtherModesSampleType", false); break;
                case 11: Set("_rdpOtherModesBiLerp0", false); break;
                case 12: Tile("Format", 3u); break;
                case 13: Tile("Size", 1u); break;
                case 14: Tile("ShiftS", 1u); break;
                case 15: Tile("ShiftT", 1u); break;
                case 16: Tile("MirrorS", true); break;
                case 17: Tile("ClampT", !((index & 1) == 0)); break;
                case 18: Tile("MaskS", 0u); break;
                case 19: Tile("MaskT", 31u); break;
                case 22: Set("_rdpScissorX0", 32); break;
                case 23: Tile("Lrs", 4u); Tile("Lrt", 8u); Tile("Line", 0u); break;
            }
            tiles.SetValue(tile, 0);

            // The command's texture coefficients occupy eight double words.
            // Pack high and low halves independently, as the RDP does.
            Array.Clear(ram, 0, 256);
            void Coefficients(int highOffset, int lowOffset, int s, int t, int w)
            {
                ulong hi = ((ulong)(uint)s & 0xffff0000UL) << 32
                    | ((ulong)(uint)t & 0xffff0000UL) << 16 | ((uint)w & 0xffff0000u);
                ulong lo = ((ulong)(uint)s & 0xffffUL) << 48
                    | ((ulong)(uint)t & 0xffffUL) << 32 | ((ulong)(uint)w & 0xffffUL) << 16;
                BinaryPrimitives.WriteUInt64BigEndian(ram.AsSpan(highOffset), hi);
                BinaryPrimitives.WriteUInt64BigEndian(ram.AsSpan(lowOffset), lo);
            }
            Coefficients(96, 112, random.Next(-2048, 2048) << 16, random.Next(-2048, 2048) << 16, 0x40000000);
            Coefficients(104, 120, random.Next(-64, 64) << 16, random.Next(-64, 64) << 16, random.Next(-1024, 1024));
            Coefficients(128, 144, random.Next(-64, 64) << 16, random.Next(-64, 64) << 16, random.Next(-1024, 1024));
            object shade = Activator.CreateInstance(shadeType)!;
            foreach (string component in new[] { "R", "G", "B", "A" })
                shadeType.GetField(component)!.SetValue(shade, random.Next(-32, 288) << 16);
            foreach (string slope in new[] { "DrDx", "DgDx", "DbDx", "DaDx", "DrDe", "DgDe", "DbDe", "DaDe" })
                shadeType.GetField(slope)!.SetValue(shade, index >= 4096 || index % 2 == 0 ? 0 : random.Next(-8, 8) << 16);
            if (boundary == 1) shadeType.GetField("DrDx")!.SetValue(shade, 1);
            if (boundary == 20) shadeType.GetField("DaDe")!.SetValue(shade, 1);
            object depth = Activator.CreateInstance(depthType)!;
            void Depth(string name, object value) => depthType.GetField(name)!.SetValue(depth, value);
            Depth("Z", unchecked(random.Next(0x80000) << 13));
            Depth("DzDx", random.Next(-256, 256) << 13);
            Depth("DzDe", random.Next(-256, 256) << 13);
            Depth("DzPix", (uint)random.Next(0x10000));
            bool flip = (index & 1) == 0;
            bool wrote = (bool)draw.Invoke(memory, new object[]
            {
                0x0f, 0u, false, shade, modulate, depth, useDepth, boundary == 21 ? 8 : 0,
                flip ? 0.0 : 30.0, 0.125, flip ? 30.0 : 0.0, -0.125,
                flip ? 24.0 : 4.0, -0.5, 0.75, 18.0, 30.0, flip, pixelBytes
            })!;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            hash.AppendData(new[] { (byte)(wrote ? 1 : 0) });
            foreach (int start in new[] { 0x1000, 0x300000, ram.Length - 4096 })
            {
                hash.AppendData(ram, start, 4096);
                hash.AppendData(hidden, start >> 1, 2048);
            }
            return Convert.ToHexString(hash.GetHashAndReset());
        }

        internal string StateHash()
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);
            save(writer); writer.Flush();
            return Convert.ToHexString(SHA256.HashData(stream.GetBuffer().AsSpan(0, (int)stream.Length)));
        }
    }
}
