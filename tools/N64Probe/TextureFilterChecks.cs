using System.Reflection;
using Ryu64.MIPS;

internal static class TextureFilterChecks
{
    internal static void Run()
    {
        const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
        var expand = typeof(Memory).GetMethod("Rgba5551ToRgba8888", flags)!.CreateDelegate<Func<ushort, uint>>();
        for (int color = 0; color < 65536; color++)
        {
            uint expected = 0;
            for (int channel = 0; channel < 3; channel++)
            {
                uint value = (uint)(color >> (11 - channel * 5)) & 31;
                expected |= ((value << 3) | (value >> 2)) << (24 - channel * 8);
            }
            if ((color & 1) != 0) expected |= 255;
            if (expand((ushort)color) != expected) throw new Exception($"RGBA5551 conversion: {color:x4}");
        }
        Console.WriteLine("rgba5551Checks=65536 passed");
        var lower = typeof(Memory).GetMethod("BlendRdpTexelsTriangleLower", flags)!
            .CreateDelegate<Func<uint, uint, uint, int, int, uint>>();
        var upper = typeof(Memory).GetMethod("BlendRdpTexelsTriangleUpper", flags)!
            .CreateDelegate<Func<uint, uint, uint, int, int, uint>>();
        int count = 0;
        void Check(uint origin, uint alongS, uint alongT)
        {
            for (int s = 0; s < 32; s++)
            for (int t = 0; t < 32; t++)
            {
                bool isUpper = s + t >= 32;
                int fs = isUpper ? 32 - s : s, ft = isUpper ? 32 - t : t;
                uint expected = 0;
                for (int shift = 0; shift < 32; shift += 8)
                {
                    int a = (int)((origin >> shift) & 255), b = (int)((alongS >> shift) & 255), c = (int)((alongT >> shift) & 255);
                    int value = a + (((b - a) * (fs << 3) + (c - a) * (ft << 3) + 128) >> 8);
                    expected |= (uint)Math.Clamp(value, 0, 255) << shift;
                }
                uint actual = isUpper ? upper(alongT, alongS, origin, s, t) : lower(origin, alongS, alongT, s, t);
                if (actual != expected) throw new Exception($"Texture filtering differs: {origin:x8}/{alongS:x8}/{alongT:x8} {s},{t}: {actual:x8}/{expected:x8}");
                count++;
            }
        }
        byte[] edges = { 0, 1, 7, 8, 127, 128, 254, 255 };
        foreach (byte a in edges)
        foreach (byte b in edges)
        foreach (byte c in edges) Check(a * 0x01010101u, b * 0x01010101u, c * 0x01010101u);
        var random = new Random(64003);
        for (int i = 0; i < 512; i++)
            Check((uint)random.NextInt64(1L << 32), (uint)random.NextInt64(1L << 32), (uint)random.NextInt64(1L << 32));
        Console.WriteLine($"textureFilterChecks={count} passed");
    }
}
