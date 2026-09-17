using System.Reflection;

internal static class TextureLodChecks
{
    public static void Run(Assembly core)
    {
        Type backend = core.GetType("EutherDrive.Core.Arcade.Vegas.VoodooBringupBackend", true)!;
        const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
        var compute = backend.GetMethod("ComputeMameTexturePixelLod", flags, null,
            new[] { typeof(int), typeof(long), typeof(int), typeof(int), typeof(bool),
                typeof(bool), typeof(int), typeof(int), typeof(int), typeof(uint) }, null)!
            .CreateDelegate<Func<int, long, int, int, bool, bool, int, int, int, uint, int>>();
        var log = backend.GetMethod("MameFastLog2", flags)!
            .CreateDelegate<Func<double, int, int>>();
        byte[] dither = { 0, 8, 2, 10, 12, 4, 14, 6, 3, 11, 1, 9, 15, 7, 13, 5 };
        long[] weights = { long.MinValue, -1, 0, 1, 256, 1L << 32, long.MaxValue };
        int[] bases = { int.MinValue, -2048, 0, 2048, int.MaxValue };
        uint[] masks = { 0x1ff, 0xaa, 0x155, 0 };
        int cases = 0;
        // All register-representable min/max pairs, both perspective modes,
        // every dither position and representative overflow/weight boundaries.
        for (int low = 0; low < 64; low++)
        for (int high = 0; high < 64; high++)
        foreach (uint mask in masks)
        for (int variant = 0; variant < 64; variant++)
        {
            int basis = bases[variant % bases.Length];
            long weight = weights[variant % weights.Length];
            bool perspective = (variant & 16) != 0;
            bool useDither = (variant & 32) != 0;
            int x = variant & 3, y = (variant >> 2) & 3;
            int bias = ((variant & 1) == 0 ? -32 : 31) << 6;
            int value = basis;
            if (perspective) value = unchecked(value - log(weight, 32));
            value = unchecked(value + bias);
            if (useDither) value = unchecked(value + (dither[(y << 2) | x] << 4));
            int expected = Math.Clamp(Math.Min(Math.Max(value, low << 6), high << 6) >> 8, 0, 8);
            expected = Math.Clamp(expected + (int)((~mask >> expected) & 1u), 0, 8);
            int actual = compute(basis, weight, x, y, perspective, useDither, bias, low << 6, high << 6, mask);
            if (actual != expected)
                throw new InvalidOperationException($"LOD mismatch low={low} high={high} variant={variant} mask={mask:x}: {actual} != {expected}");
            cases++;
        }
        Console.WriteLine($"textureLodChecks cases={cases} PASS");
    }
}
