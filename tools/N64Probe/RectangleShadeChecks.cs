using System.Buffers.Binary;
using System.Reflection;
using Ryu64.MIPS;

internal static class RectangleShadeChecks
{
    internal static void Run()
    {
        var memory = new Memory(new byte[4096]);
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        void Set(string name, object value) => typeof(Memory).GetField(name, flags)!.SetValue(memory, value);
        void Call(string name, params object[] values) => typeof(Memory).GetMethod(name, flags)!.Invoke(memory, values);
        Set("_rdpColorImageAddress", 0x300000u); Set("_rdpColorImageWidth", 320u); Set("_rdpColorImageSize", 2u);
        Set("_rdpScissorX1", 319); Set("_rdpScissorY1", 239);
        Set("_rdpPrimColor", 0xffffffffu);
        Call("ExecuteRdpSetTile", 0xf5100200u, 0u);
        Call("ExecuteRdpSetTileSize", 0xf2000000u, 0x0000c000u);
        // Duke uses (PRIMITIVE - SHADE) * TEXEL + SHADE for RGB and alpha.
        Call("ExecuteRdpSetCombine", 0xfc30b261u, 0x44664924u);
        ushort[] colors = { 0x0001, 0xf801, 0x07c1, 0xffff };
        for (int i = 0; i < colors.Length; i++) BinaryPrimitives.WriteUInt16BigEndian(memory.RDRAM.AsSpan(0x100000 + i * 2), colors[i]);
        Set("_rdpTextureImageAddress", 0x100000u); Set("_rdpTextureImageWidth", 4u); Set("_rdpTextureImageSize", 2u);
        Call("ExecuteRdpSetTile", 0xf5100000u, 0x07000000u);
        Call("ExecuteRdpLoadBlock", 0xf3000000u, 0x07003000u);
        int cases = 0;
        foreach (uint cycle in new uint[] { 0, 1, 2 })
        foreach (int command in new[] { 0x24, 0x25 })
        for (int i = 0; i < colors.Length; i++)
        {
            Set("_rdpOtherModesCycleType", cycle);
            memory.RDRAM[0x300000] = 0x55; memory.RDRAM[0x300001] = 0x55;
            Call("ExecuteRdpTextureRectangle", command, 0xe4000000u, 0u, (uint)(i * 32) << 16, 0u);
            ushort actual = BinaryPrimitives.ReadUInt16BigEndian(memory.RDRAM.AsSpan(0x300000));
            if ((actual & 0xfffe) != (colors[i] & 0xfffe))
                throw new Exception($"Rectangle shade cycle={cycle} flip={command == 0x25} texel={i}: {actual:x4}");
            cases++;
        }
        Console.WriteLine($"rectangleZeroShadeCases={cases} blackRedGreenWhite=passed copyBypass=passed");
    }
}
