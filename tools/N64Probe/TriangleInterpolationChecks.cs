using System.Buffers.Binary;
using System.Reflection;
using Ryu64.MIPS;

internal static class TriangleInterpolationChecks
{
    internal static int Run()
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        Type type = typeof(Memory);
        Type shadeType = type.GetNestedType("RdpTriangleShadeCoefficients", BindingFlags.NonPublic)!;
        Type depthType = type.GetNestedType("RdpTriangleDepthCoefficients", BindingFlags.NonPublic)!;
        var compressedZ = (ushort[])type.GetField("RdpZCompressTable", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        int checks = 0;
        foreach (double yh in new[] { 0.0, 0.75, -0.25 })
        foreach (double slope in new[] { 0.0, 0.5 })
        foreach (bool flip in new[] { true, false })
        foreach (bool primitiveDepth in new[] { false, true })
        {
            var memory = new Memory(new byte[4096]);
            void Set(string name, object value) => type.GetField(name, flags)!.SetValue(memory, value);
            Set("_rdpColorImageAddress", 0x300000u);
            Set("_rdpColorImageWidth", 32u);
            Set("_rdpColorImageSize", 2u);
            Set("_rdpScissorX1", 31);
            Set("_rdpScissorY1", 31);
            Set("_rdpMaskImageAddress", 0x400u);
            Set("_rdpOtherModesZUpdate", true);
            Set("_rdpOtherModesZSourceSel", primitiveDepth);
            Set("_rdpPrimitiveDepth", 0x2000u);
            object shade = Activator.CreateInstance(shadeType)!;
            void Shade(string name, int value) => shadeType.GetField(name)!.SetValue(shade, value);
            Shade("R", (flip ? 0 : 256) << 16);
            Shade("A", 255 << 16);
            Shade("DrDx", 16 << 16);
            Shade("DrDe", (int)(16 * slope * 65536));
            Shade("DgDe", 8 << 16);
            object depth = Activator.CreateInstance(depthType)!;
            void Depth(string name, int value) => depthType.GetField(name)!.SetValue(depth, value);
            Depth("Z", (0x10000 + (flip ? 0 : 16 * 64)) << 13);
            Depth("DzDx", 64 << 13);
            Depth("DzDe", (int)((32 + 64 * slope) * 8192));
            // A clipped triangle span with its major edge on either side. Both
            // describe the same planar shade: R=16*x, G=8*(y-floor(yh)).
            type.GetMethod("DrawRdpShadedTriangle", flags)!.Invoke(memory, new object[]
            {
                flip ? 0.0 : 16.0, slope, flip ? 16.0 : 0.0, 0.0,
                flip ? 16.0 : 0.0, 0.0, yh, 16.0, 16.0, flip, shade, depth, true, 2u
            });
            for (int y = 2; y < 12; y++)
            for (int x = 7; x < 12; x++)
            {
                int red = (int)((x + 0.5) * 16) >> 3;
                int green = (int)((y + 0.5 - Math.Floor(yh)) * 8) >> 3;
                ushort expected = (ushort)(red << 11 | green << 6 | 1);
                ushort actual = BinaryPrimitives.ReadUInt16BigEndian(memory.RDRAM.AsSpan(0x300000 + (y * 32 + x) * 2));
                if (actual != expected)
                    throw new Exception($"Shade plane: flip={flip} yh={yh} slope={slope} pixel={x},{y}: {actual:x4}!={expected:x4}");
                checks++;
                int z = primitiveDepth ? 0x10000 : (int)(0x10000 + (x + 0.5) * 64 + (y + 0.5 - Math.Floor(yh)) * 32);
                ushort actualZ = BinaryPrimitives.ReadUInt16BigEndian(memory.RDRAM.AsSpan(0x400 + (y * 32 + x) * 2));
                if (actualZ != compressedZ[z])
                    throw new Exception($"Depth plane: primitive={primitiveDepth} flip={flip} yh={yh} slope={slope} pixel={x},{y}: {actualZ:x4}!={compressedZ[z]:x4}");
                checks++;
            }
        }
        return checks;
    }
}
