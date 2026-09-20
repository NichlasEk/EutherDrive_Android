using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using Ryu64.MIPS;

internal static class RectangleChecks
{
    // Compare actual pixels against the previous renderer, including negative
    // fractional slopes, copy-cycle division, scissor offsets and flipped axes.
    internal static void Run(string reference)
    {
        string Render(Assembly assembly)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            Type type = assembly.GetType("Ryu64.MIPS.Memory")!;
            object memory = Activator.CreateInstance(type, new object[] { new byte[4096] })!;
            var rectangle = type.GetMethod("ExecuteRdpTextureRectangle", flags)!.CreateDelegate<Action<int,uint,uint,uint,uint>>(memory);
            var tile = type.GetMethod("ExecuteRdpSetTile", flags)!.CreateDelegate<Action<uint,uint>>(memory);
            var size = type.GetMethod("ExecuteRdpSetTileSize", flags)!.CreateDelegate<Action<uint,uint>>(memory);
            var combine = type.GetMethod("ExecuteRdpSetCombine", flags)!.CreateDelegate<Action<uint,uint>>(memory);
            byte[] ram = (byte[])type.GetField("RDRAM")!.GetValue(memory)!;
            byte[] tmem = (byte[])type.GetField("_rdpTmem", flags)!.GetValue(memory)!;
            ushort[] palette = (ushort[])type.GetField("_rdpTlut", flags)!.GetValue(memory)!;
            void Set(string name, object value) => type.GetField(name, flags)!.SetValue(memory, value);
            Set("_rdpColorImageAddress", 0x300000u); Set("_rdpColorImageWidth", 64u);
            Set("_rdpScissorX1", 63); Set("_rdpScissorY1", 63);
            var random = new Random(642009);
            random.NextBytes(tmem);
            for (int i = 0; i < palette.Length; i++) palette[i] = (ushort)random.Next(65536);
            int[] coordinates = { -32768,-32767,-2049,-1025,-1024,-1023,-33,-32,-31,-1,0,1,31,32,33,1023,32766,32767 };
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            for (int i = 0; i < 4096; i++)
            {
                if (i % 17 == 0)
                {
                    random.NextBytes(tmem);
                    for (int entry = 0; entry < palette.Length; entry++)
                        palette[entry] = i % 3 == 0 ? (ushort)0 : (ushort)random.Next(65536);
                }
                Set("_rdpColorImageSize", (i & 1) == 0 ? 2u : 3u);
                Set("_rdpOtherModesCycleType", (uint)(i >> 1 & 3));
                Set("_rdpOtherModesAlphaCompare", (i & 16) != 0);
                Set("_rdpOtherModesForceBlend", (i & 32) != 0);
                Set("_rdpOtherModesEnableTlut", (i & 64) != 0);
                Set("_rdpOtherModesTlutType", (i & 128) != 0);
                Set("_rdpOtherModesCvgTimesAlpha", (i & 256) != 0);
                Set("_rdpScissorX0", random.Next(0, 20)); Set("_rdpScissorY0", random.Next(0, 20));
                Set("_rdpPrimColor", (uint)random.NextInt64(1L << 32));
                Set("_rdpEnvColor", (uint)random.NextInt64(1L << 32));
                Set("_rdpBlendColor", (uint)random.NextInt64(1L << 32));
                combine((uint)random.NextInt64(1L << 32), (uint)random.NextInt64(1L << 32));
                Set("_rdpCombineModeSet", (i & 512) != 0);
                uint format = new uint[] { 0,2,3,4 }[i >> 4 & 3];
                uint textureSize = (uint)(i >> 6 & 3);
                uint addressing = (uint)random.Next(0x1000000);
                tile(format << 21 | textureSize << 19 | (uint)random.Next(1, 16) << 9 | (uint)random.Next(512), addressing);
                uint uls = (uint)random.Next(0, 20), ult = (uint)random.Next(0, 20);
                size(uls << 12 | ult, (uls + 127) << 12 | (ult + 127));
                uint Packed() => (uint)(ushort)coordinates[random.Next(coordinates.Length)] << 16
                    | (ushort)coordinates[random.Next(coordinates.Length)];
                uint x0 = (uint)random.Next(0, 25), y0 = (uint)random.Next(0, 25);
                uint x1 = (uint)random.Next(25, 1024), y1 = (uint)random.Next(25, 1024);
                rectangle((i & 8) == 0 ? 0x24 : 0x25,
                    (x1 * 4 << 12) | y1 * 4, (x0 * 4 << 12) | y0 * 4, Packed(), Packed());
                hash.AppendData(ram.AsSpan(0x300000, 64 * 64 * 4));
            }
            hash.AppendData(BitConverter.GetBytes((long)type.GetField("_rdpPixelWriteCount", flags)!.GetValue(memory)!));
            hash.AppendData(BitConverter.GetBytes((long)type.GetField("_rdpNonZeroPixelWriteCount", flags)!.GetValue(memory)!));
            return Convert.ToHexString(hash.GetHashAndReset());
        }
        var context = new AssemblyLoadContext("rectangle-reference", true);
        string expected = Render(context.LoadFromAssemblyPath(Path.GetFullPath(reference)));
        string actual = Render(typeof(Memory).Assembly);
        context.Unload();
        if (expected != actual) throw new Exception($"Rectangle pixels differ: {expected}/{actual}");
        Console.WriteLine($"rectangleCases=4096 clipping=passed flip=passed fractionalSlopes=passed pixelHash={actual}");
    }
}
