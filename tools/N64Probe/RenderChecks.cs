using System.Reflection;
using System.Buffers.Binary;
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
        Set("_rdpOtherModesAlphaCompare", true);
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

        var combine = typeof(Memory).GetMethod("ApplyRdpColorCombiner", instance)!.CreateDelegate<Func<uint, uint, uint>>(memory);
        void Mux(uint first, uint second)
        {
            ulong mux = 0xfc00000000000000UL | 15UL << 52 | 31UL << 47 | 7UL << 44 | 7UL << 41
                | 15UL << 37 | 31UL << 32 | 15UL << 28 | 15UL << 24 | 7UL << 21 | 7UL << 18
                | (ulong)first << 15 | 7UL << 12 | (ulong)first << 9 | (ulong)second << 6 | 7UL << 3 | second;
            Call("ExecuteRdpSetCombine", (uint)(mux >> 32), (uint)mux);
        }
        Mux(4, 3); // Different values in the two banks: shade then primitive.
        Set("_rdpPrimColor", 0x12345678u);
        Set("_rdpOtherModesCycleType", 0u);
        if (combine(0xffabcdef, 0xabcdef99) != 0x12345678) throw new Exception("One-cycle used mux bank zero");
        checks++;
        Mux(4, 0); // Feed SHADE through COMBINED in two-cycle mode.
        Set("_rdpOtherModesCycleType", 1u);
        if (combine(0xffabcdef, 0xabcdef99) != 0xabcdef99) throw new Exception("Two-cycle COMBINED failed");
        checks++;
        Mux(4, 3);
        Set("_rdpOtherModesCycleType", 0u);
        Set("_rdpPrimColor", 0x000000ffu);
        if (combine(0xffabcdef, 0xffffffff) != 0x000000ff) throw new Exception("Black combiner output was replaced by texture");
        checks++;
        foreach (int triangle in new[] { 0x0c, 0x0e })
        {
            Array.Fill(memory.RDRAM, (byte)0x55, (int)color, 320 * 240 * 2);
            Call("ExecuteRdpTriangle", triangle, command, false);
            int pixel = (int)color + (5 * 320 + 5) * 2;
            if (memory.RDRAM[pixel] != 0 || memory.RDRAM[pixel + 1] != 1)
                throw new Exception($"Triangle {triangle:x} did not write opaque black primitive color");
            checks++;
        }
        Set("_rdpOtherModesCycleType", 2u);
        if (combine(0x87654321, 0xffffffff) != 0x87654321) throw new Exception("Copy did not bypass combiner");
        checks++;
        foreach (int direction in new[] { 1, 0, -1 })
        {
            uint start = direction < 0 ? 7u * 32u << 16 : 0u;
            uint derivative = (uint)(ushort)(direction * 4096) << 16 | 0x400u;
            Call("ExecuteRdpTextureRectangle", 0x24, 0xe401c000u, 0u, start, derivative);
            for (int x = 0; x < 8; x++)
            {
                int source = direction < 0 ? 7 - x : direction * x;
                uint c = (uint)(source * 17) >> 3;
                ushort expected = (ushort)(c << 11 | c << 6 | c << 1 | 1);
                ushort actual = BinaryPrimitives.ReadUInt16BigEndian(memory.RDRAM.AsSpan((int)color + 2 * x));
                if (actual != expected) throw new Exception($"Copy step {direction}, pixel {x}: {actual:x4} != {expected:x4}");
                checks++;
            }
        }

        // SM64 uses low RDRAM for depth. A presentation-origin heuristic must
        // neither suppress its clear nor silently disable Z compare/update.
        foreach (uint zBase in new[] { 0u, 0x400u, 0x1000u })
        {
            Set("_rdpColorImageAddress", zBase);
            Set("_rdpFillColor", 0xfffcfffcu);
            Call("ExecuteRdpFillRectangle", 0xf603c03cu, 0u); // 16 x 16 clear
            int sample = 5 * 320 + 5;
            ushort WordAt(uint origin) => BinaryPrimitives.ReadUInt16BigEndian(memory.RDRAM.AsSpan((int)origin + sample * 2));
            if (WordAt(zBase) != 0xfffc) throw new Exception($"Low Z clear suppressed at {zBase:x}");
            checks++;
            Set("_rdpMaskImageAddress", zBase);
            Set("_rdpColorImageAddress", color);
            Set("_rdpOtherModesCycleType", 0u);
            Set("_rdpOtherModesZCompare", true);
            Set("_rdpOtherModesZUpdate", true);
            Set("_rdpPrimColor", 0xff0000ffu);
            Word(96, 0x10000u << 13); // Constant near Z, shade + depth triangle.
            Call("ExecuteRdpTriangle", 0x0d, command, false);
            if (WordAt(color) != 0xf801) throw new Exception("Near triangle failed Z test");
            if (WordAt(zBase) == 0xfffc) throw new Exception("Low Z update suppressed");
            checks += 2;
            Set("_rdpPrimColor", 0x000000ffu);
            Word(96, 0x30000u << 13); // Opaque black background behind the red triangle.
            Call("ExecuteRdpTriangle", 0x0d, command, false);
            if (WordAt(color) != 0xf801) throw new Exception("Far black triangle erased foreground");
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
