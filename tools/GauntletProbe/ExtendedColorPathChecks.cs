using System.Reflection;

internal static class ExtendedColorPathChecks
{
    public static void Run(Assembly assembly)
    {
        Type backend = assembly.GetType("EutherDrive.Core.Arcade.Vegas.VoodooBringupBackend", true)!;
        var gate = backend.GetMethod("IsGpuExtendedColorState", BindingFlags.Static | BindingFlags.NonPublic)!
            .CreateDelegate<Func<uint, uint, uint, int, bool>>();
        var first = new HashSet<(uint, uint, uint)> {
            (0xb4779, 0x8c24110f, 0x8c241acf), (0xb4779, 0x80000009, 0x8c24110f) };
        var second = new HashSet<(uint, uint, uint)>(first) {
            (0xb4779, 0x8c24110f, 0x8c24110f), (0xb4779, 0x8c24190f, 0x8c241acf),
            (0xb4379, 0x8c24190f, 0x8c241acf) };
        int gateCases = 0;
        foreach (int level in new[] { -1, 0, 1, 2, 3 })
        foreach (uint fbz in new uint[] { 0xb4779, 0xb4379, 0, 0xb4778 })
        foreach (uint tm0 in new uint[] { 0x8c24110f, 0x80000009, 0x8c24190f, 0x8c241acf, 0 })
        foreach (uint tm1 in new uint[] { 0x8c24110f, 0x80000009, 0x8c24190f, 0x8c241acf, 0 })
        {
            bool expected = level == 1 ? first.Contains((fbz, tm0, tm1)) :
                level == 2 && second.Contains((fbz, tm0, tm1));
            if (gate(fbz, tm0, tm1, level) != expected)
                throw new InvalidOperationException("Extended color state allowlist mismatch");
            gateCases++;
        }
        Console.WriteLine($"extendedColorStateChecks cases={gateCases} PASS");
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
