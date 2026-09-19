using System.Buffers.Binary;
using System.Reflection;
using EutherDrive.Core.Arcade.Taito;

internal static class F3MixerChecks
{
    internal static void Run()
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        using var core = new DariusGaidenAdapter();
        Type type = core.GetType();
        T Field<T>(string name) => (T)type.GetField(name, flags)!.GetValue(core)!;
        object bus = Field<object>("_bus");
        byte[] palette = (byte[])bus.GetType().GetField("_palette", flags)!.GetValue(bus)!;
        uint[] colors = [0, 0xffffff, 0x123456, 0xfe817f, 0x010203, 0xe07020];
        for (int i = 0; i < colors.Length; i++) BinaryPrimitives.WriteUInt32BigEndian(palette.AsSpan(i * 4), colors[i]);
        byte[] sourceWeights = Field<byte[]>("_mixSrcBlend"), destinationWeights = Field<byte[]>("_mixDstBlend");
        ushort[] sourcePalettes = Field<ushort[]>("_mixSrcPalette"), destinationPalettes = Field<ushort[]>("_mixDstPalette");
        byte[] modes = Field<byte[]>("_mixSrcBlendMode");
        uint[] expected = new uint[colors.Length * colors.Length * 9 * 9];
        int count = 0;
        for (ushort s = 0; s < colors.Length; s++)
        for (ushort d = 0; d < colors.Length; d++)
        for (byte sw = 0; sw <= 8; sw++)
        for (byte dw = 0; dw <= 8; dw++)
        {
            sourcePalettes[count] = s; destinationPalettes[count] = d;
            sourceWeights[count] = sw; destinationWeights[count] = dw;
            modes[count] = sw == 0 ? (byte)0xff : (byte)1;
            uint color = 0xff000000;
            for (int shift = 0; shift < 24; shift += 8)
                color |= (uint)Math.Min(255, ((((colors[s] >> shift) & 255) * sw + ((colors[d] >> shift) & 255) * dw) / 8)) << shift;
            expected[count++] = color;
        }
        foreach (string name in new[] { "RenderMameMixBufferToFrame", "RenderMameMixBufferToFrameBytes", "RenderMameMixBufferToFrameWithStats" })
        {
            var method = type.GetMethod(name, flags)!;
            if (method.GetParameters().Length == 0) method.Invoke(core, null);
            else method.Invoke(core, new object[] { 777 });
            byte[] pixels = Field<byte[]>("_frameBuffer");
            for (int i = 0; i < count; i++)
                if (BinaryPrimitives.ReadUInt32LittleEndian(pixels.AsSpan(i * 4)) != expected[i])
                    throw new InvalidOperationException($"{name} pixel={i} weights={sourceWeights[i]}/{destinationWeights[i]} expected={expected[i]:x8}");
        }
        Console.WriteLine($"f3MixerChecks=passed cases={count * 3}");
    }
}
