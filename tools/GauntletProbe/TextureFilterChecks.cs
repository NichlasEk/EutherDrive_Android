using System.Linq.Expressions;
using System.Reflection;

internal static class TextureFilterChecks
{
    public static void Run(Assembly core)
    {
        Type backend = core.GetType("EutherDrive.Core.Arcade.Vegas.VoodooBringupBackend", true)!;
        Type rgba = backend.GetNestedType("TextureRgba", BindingFlags.NonPublic)!;
        var ctor = rgba.GetConstructor(new[] { typeof(byte), typeof(byte), typeof(byte), typeof(byte) })!;
        var method = backend.GetMethod("BilinearTextureRgba", BindingFlags.Static | BindingFlags.NonPublic)!;
        var args = Enumerable.Range(0, 6).Select(i => Expression.Parameter(i < 4 ? typeof(uint) : typeof(int), $"p{i}")).ToArray();
        var value = Expression.Variable(rgba);
        Expression Color(Expression packed) => Expression.New(ctor, Enumerable.Range(0, 4)
            .Select(i => Expression.Convert(Expression.RightShift(packed, Expression.Constant(i * 8)), typeof(byte))));
        Expression packedResult = Expression.Constant(0u);
        string[] channels = { "R", "G", "B", "A" };
        for (int i = 0; i < 4; i++)
            packedResult = Expression.Or(packedResult, Expression.LeftShift(
                Expression.Convert(Expression.Property(value, channels[i]), typeof(uint)), Expression.Constant(i * 8)));
        var compute = Expression.Lambda<Func<uint, uint, uint, uint, int, int, uint>>(
            Expression.Block(new[] { value }, Expression.Assign(value,
                Expression.Call(method, Color(args[0]), Color(args[1]), Color(args[2]), Color(args[3]), args[4], args[5])), packedResult), args).Compile();
        long cases = 0;
        void Check(uint a, uint b, uint c, uint d, int x, int y)
        {
            uint expected = 0;
            for (int channel = 0; channel < 4; channel++)
            {
                int shift = channel * 8;
                int sum = (byte)(a >> shift) * (256 - x) * (256 - y) +
                    (byte)(b >> shift) * x * (256 - y) +
                    (byte)(c >> shift) * (256 - x) * y +
                    (byte)(d >> shift) * x * y;
                expected |= (uint)Math.Clamp((sum + 0x8000) >> 16, 0, 255) << shift;
            }
            uint actual = compute(a, b, c, d, x, y);
            if (actual != expected)
                throw new InvalidOperationException($"Bilinear mismatch x={x} y={y}: {actual:x8} != {expected:x8}");
            cases++;
        }
        // Every fraction (including the endpoint), all 16 extrema patterns.
        // Rotate channel patterns to detect cross-lane leakage or channel swaps.
        for (int x = 0; x <= 256; x++)
        for (int y = 0; y <= 256; y++)
        for (int pattern = 0; pattern < 16; pattern++)
        {
            uint Corner(int corner)
            {
                uint color = 0;
                for (int channel = 0; channel < 4; channel++)
                    if ((pattern & (1 << ((corner + channel) & 3))) != 0)
                        color |= 255u << (channel * 8);
                return color;
            }
            Check(Corner(0), Corner(1), Corner(2), Corner(3), x, y);
        }
        var random = new Random(0x47444c);
        uint NextColor() => (uint)random.NextInt64(1L << 32);
        for (int i = 0; i < 100_000; i++)
            Check(NextColor(), NextColor(), NextColor(), NextColor(), random.Next(257), random.Next(257));
        Console.WriteLine($"textureFilterChecks cases={cases} PASS");
    }
}
