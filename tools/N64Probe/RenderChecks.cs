using System.Reflection;
using Ryu64.MIPS;

internal static class RenderChecks
{
    internal static void Run()
    {
        const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
        var shade = typeof(Memory).GetMethod("RdpShadeToRgba", flags)!.CreateDelegate<Func<long, long, long, long, uint>>();
        int checks = 0;
        for (int value = 0; value < 256; value++)
        {
            uint rgba = shade(value << 16, value << 16, value << 16, value << 16);
            uint expected = (uint)value * 0x01010101u;
            if (rgba != expected) throw new Exception($"Shade {value}: {rgba:x8} != {expected:x8}");
            checks++;
        }
        if (shade(-65536, 256L << 16, (127L << 16) | 0xffff, 0) != 0x00ff7f00)
            throw new Exception("Shade clamp/fraction/zero alpha failed");
        checks++;

        var depth = typeof(Memory).GetMethod("RdpDepthFixedToComparator", flags)!.CreateDelegate<Func<long, uint>>();
        foreach (uint z in new uint[] { 0, 1, 0x10000, 0x20000, 0x3fffe, 0x3ffff, 0x40000, 0x5ffff, 0x60000, 0x7ffff })
        {
            uint expected = (z & 0x60000) == 0x40000 ? 0x3ffff : z & 0x3ffff;
            if (depth((long)z << 13) != expected) throw new Exception($"Depth conversion {z:x}");
            checks++;
        }

        var memory = new Memory(new byte[0x1000]);
        var texture = typeof(Memory).GetMethod("RdpTriangleTextureFixedToTexels", flags)!;
        foreach (int coordinate in new[] { -32768, -1025, -33, -32, -1, 0, 1, 31, 32, 1023, 32767 })
        {
            object[] values = { (long)coordinate << 16, (long)coordinate << 16, 0L, false, 0, 0, 0, 0 };
            texture.Invoke(null, values);
            if ((int)values[4] != coordinate >> 5 || (int)values[5] != coordinate >> 5
                || (int)values[6] != (coordinate & 31) || (int)values[7] != (coordinate & 31))
                throw new Exception($"Nonperspective texture conversion {coordinate}");
            checks++;
        }

        const BindingFlags instance = BindingFlags.Instance | BindingFlags.NonPublic;
        void Set(string name, object value) => typeof(Memory).GetField(name, instance)!.SetValue(memory, value);
        void Call(string name, params object[] values) => typeof(Memory).GetMethod(name, instance)!.Invoke(memory, values);
        const uint color = 0x300000, command = 0x200000;
        Set("_rdpColorImageAddress", color);
        Set("_rdpColorImageWidth", 320u);
        Set("_rdpColorImageSize", 2u);
        Set("_rdpScissorX1", 319);
        Set("_rdpScissorY1", 239);
        Call("ExecuteRdpSetTile", 0xf5100200u, 0u);
        Call("ExecuteRdpSetTileSize", 0xf2000000u, 0x0001c01cu);
        void Word(uint offset, uint value) => System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(memory.RDRAM.AsSpan((int)(command + offset)), value);
        Word(0, 0xce800040); Word(4, 0x00400000); // 16 scanlines
        Word(8, 16u << 16); Word(24, 16u << 16); // 16-pixel wide span
        Word(32, 0x00ff0000); Word(36, 0x000000ff); // Red shade, opaque
        Array.Fill(memory.RDRAM, (byte)0x55, (int)color, 320 * 240 * 2);
        Call("ExecuteRdpTriangle", 0x0e, command, false);
        if (memory.RDRAM.AsSpan((int)color, 320 * 240 * 2).ContainsAnyExcept((byte)0x55))
            throw new Exception("Transparent texture was replaced with solid shade");
        checks++;

        // libultra loads 8-bit textures via a 16-bit LoadBlock. Both views of
        // TMEM must agree on byte order, including odd-row bank swapping.
        Set("_rdpTextureImageAddress", 0x100000u);
        Set("_rdpTextureImageWidth", 1u);
        Set("_rdpTextureImageSize", 2u);
        for (int i = 0; i < 16; i++) memory.RDRAM[0x100000 + i] = (byte)((i << 4) | (15 - i));
        Call("ExecuteRdpSetTile", 0xf5100000u, 0x07000000u);
        Call("ExecuteRdpLoadBlock", 0xf3000000u, 0x07007800u); // 8 halfwords, DXT=2048
        Call("ExecuteRdpSetTile", 0xf5680200u, 0u); // IA8, one 64-bit word per row
        var tile = ((Array)typeof(Memory).GetField("_rdpTiles", instance)!.GetValue(memory)!).GetValue(0)!;
        var decode = typeof(Memory).GetMethod("DecodeRdpTextureColor", instance)!;
        for (int i = 0; i < 16; i++)
        {
            object[] values = { tile, i % 8, i / 8, 0u };
            decode.Invoke(memory, values);
            uint intensity = (uint)i * 17, alpha = (uint)(15 - i) * 17;
            uint expected = intensity * 0x01010100u | alpha;
            if ((uint)values[3] != expected) throw new Exception($"Cross-size TMEM texel {i}: {values[3]:x8} != {expected:x8}");
            checks++;
        }

        R4300.memory = memory;
        var refresh = typeof(R4300).GetMethod("RefreshRcpInterruptPending", flags)!.CreateDelegate<Func<ulong>>();
        for (int intr = 0; intr < 64; intr++)
            for (int mask = 0; mask < 64; mask++)
            {
                memory.MI_INTR_REG_R[3] = (byte)intr;
                memory.MI_INTR_MASK_REG_R[3] = (byte)mask;
                const ulong initial = 0x8300; // Timer + software pending bits must survive.
                Registers.COP0.Reg[Registers.COP0.CAUSE_REG] = initial;
                ulong result = refresh();
                bool expected = (memory.ReadUInt32(0xa4300008) & memory.ReadUInt32(0xa430000c) & 63) != 0;
                if (result != (initial | (expected ? 0x400UL : 0))) throw new Exception("MI/IP2 mismatch");
                checks++;
            }
        Console.WriteLine($"renderAndInterruptChecks=passed cases={checks}");
    }
}
