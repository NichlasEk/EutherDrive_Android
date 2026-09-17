using System.Reflection;

internal static class ExtendedColorPathChecks
{
    public static void Run(Assembly assembly)
    {
        Type backend = assembly.GetType("EutherDrive.Core.Arcade.Vegas.VoodooBringupBackend", true)!;
        Type state = backend.GetNestedType("FbzColorPathState", BindingFlags.NonPublic)!;
        var ctor = state.GetConstructors().Single();
        const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
        var rgb = backend.GetMethod("ApplyFbzColorPathRgbMame", flags, null,
            new[] { typeof(ushort), typeof(ushort), typeof(byte), typeof(int), state.MakeByRefType() }, null)!;
        var alpha = backend.GetMethod("ComputeFbzColorPathAlpha", flags, null,
            new[] { typeof(byte), typeof(int), typeof(int), typeof(long), state.MakeByRefType() }, null)!;
        object Build(int otherAlpha) => ctor.Invoke(new object[] { 0x0c602c19u, (ushort)0x731c, (ushort)0xa564, 97, otherAlpha });
        object[] rgbArgs = { (ushort)0, (ushort)0x1357, (byte)87, 0, Build(113) };
        int rgbCases = 0, alphaCases = 0;
        foreach (int a in new[] { 0, 1, 127, 128, 254, 255 })
        for (int texel = 0; texel <= ushort.MaxValue; texel++)
        {
            rgbArgs[0] = (ushort)texel; rgbArgs[3] = a;
            int r = ((texel >> 11) & 31) * 255 / 31 * (a + 1) / 256;
            int g = ((texel >> 5) & 63) * 255 / 63 * (a + 1) / 256;
            int b = (texel & 31) * 255 / 31 * (a + 1) / 256;
            ushort expected = (ushort)((r >> 3) << 11 | (g >> 2) << 5 | b >> 3);
            if ((ushort)rgb.Invoke(null, rgbArgs)! != expected)
                throw new InvalidOperationException($"Extended RGB mismatch texel={texel:x4} alpha={a}");
            rgbCases++;
        }
        for (int other = 0; other < 256; other++)
        {
            object[] alphaArgs = { (byte)0, 73, int.MinValue, long.MaxValue, Build(other) };
            for (int texture = 0; texture < 256; texture++)
            {
                alphaArgs[0] = (byte)texture;
                if ((int)alpha.Invoke(null, alphaArgs)! != other * (texture + 1) / 256)
                    throw new InvalidOperationException("Extended output-alpha mismatch");
                alphaCases++;
            }
        }
        Console.WriteLine($"extendedColorPathChecks rgbCases={rgbCases} alphaCases={alphaCases} PASS");
    }
}
