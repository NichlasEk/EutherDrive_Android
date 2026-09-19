using System.Reflection;
using EutherDrive.Core.Arcade.Taito;

internal static class GfxPlaneChecks
{
    internal static void Run()
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        using var core = new DariusGaidenAdapter();
        Type type = core.GetType();
        object bus = type.GetField("_bus", flags)!.GetValue(core)!;
        int[] xOffsets = [20, 16, 28, 24, 4, 0, 12, 8];
        int checks = 0;
        foreach (bool pivot in new[] { false, true })
        {
            byte[] ram = (byte[])bus.GetType().GetField(pivot ? "_pivotRam" : "_charRam", flags)!.GetValue(bus)!;
            var decode = type.GetMethod(pivot ? "DecodeF3PivotPixel" : "DecodeF3CharPixel", flags)!
                .CreateDelegate<Func<int, int, int, int>>(core);
            Array.Fill(ram, (byte)0x5a);
            foreach (int code in new[] { 0, 1, ram.Length / 32 - 1 })
            for (int y = 0; y < 8; y++)
            for (int x = 0; x < 8; x++)
            for (int value = 0; value < 256; value++)
            {
                int bitOffset = code * 256 + y * 32 + xOffsets[x];
                ram[(bitOffset >> 3) ^ 1] = (byte)value;
                int expected = 0;
                // MAME gfx_layout planes {0,1,2,3}; first plane carries bit 3.
                // Keep the reference bit-by-bit, independent of nibble extraction.
                for (int plane = 0; plane < 4; plane++)
                {
                    int bit = bitOffset + plane;
                    if ((ram[(bit >> 3) ^ 1] & (0x80 >> (bit & 7))) != 0)
                        expected |= 8 >> plane;
                }
                if (decode(code, x, y) != expected)
                    throw new InvalidOperationException($"F3 pen mismatch pivot={pivot} code={code} x={x} y={y} value={value:x2}");
                checks++;
            }
            if (decode(-1, 0, 0) != 0 || decode(ram.Length / 32, 0, 0) != 0)
                throw new InvalidOperationException("Invalid tile should be transparent");
            checks += 2;
        }
        Console.WriteLine($"gfxPlaneChecks=passed cases:{checks}");
    }
}
