using System.Buffers.Binary;
using System.Reflection;
using Ryu64.MIPS;

internal static class SpriteRenderChecks
{
    internal static void Run()
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var memory = new Memory(new byte[4096]);
        R4300.memory = memory;
        void Set(string name, object value) => typeof(Memory).GetField(name, flags)!.SetValue(memory, value);
        object Call(string name, params object[] values) => typeof(Memory).GetMethod(name, flags)!.Invoke(memory, values)!;
        var tmem = (byte[])typeof(Memory).GetField("_rdpTmem", flags)!.GetValue(memory)!;
        var decode = typeof(Memory).GetMethod("DecodeRdpIntensityTexture", flags)!;
        int intensityCases = 0;
        // Both nibbles, both TMEM row parities, and every intensity level.
        foreach (uint size in new uint[] { 0, 1 })
        foreach (uint row in new uint[] { 0, 1 })
        foreach (uint column in new uint[] { 0, 1 })
        for (uint value = 0; value < (size == 0 ? 16 : 256); value++)
        {
            Array.Fill(tmem, (byte)(size == 0 ? value * 17 : value));
            object[] args = { 0u, column, row, size, 0u, 0u };
            if (!(bool)decode.Invoke(memory, args)!) throw new Exception("Intensity sample rejected");
            uint intensity = size == 0 ? value * 17 : value;
            if ((uint)args[5] != intensity * 0x01010101u)
                throw new Exception($"Intensity alpha size={size}, value={value}: {args[5]:x8}");
            intensityCases++;
        }
        // TLUT-enabled intensity formats must still use palette alpha.
        Set("_rdpOtherModesEnableTlut", true);
        foreach (uint size in new uint[] { 0, 1 })
        foreach (ushort color in new ushort[] { 0xf800, 0xf801 })
        {
            Array.Clear(tmem);
            BinaryPrimitives.WriteUInt16BigEndian(tmem.AsSpan(0x800), color);
            object[] args = { 0u, 0u, 0u, size, 0u, 0u };
            decode.Invoke(memory, args);
            if ((uint)args[5] != (color == 0xf800 ? 0xff000000u : 0xff0000ffu))
                throw new Exception("Intensity TLUT alpha changed");
            intensityCases++;
        }
        Set("_rdpOtherModesEnableTlut", false);
        Set("_rdpColorImageAddress", 0x300000u); Set("_rdpColorImageWidth", 8u);
        Set("_rdpScissorX1", 7); Set("_rdpScissorY1", 7);
        Set("_rdpMaskImageAddress", 0x400u);
        Set("_rdpOtherModesZMode", 2u); // Strict translucent depth compare, as Duke uses.
        Set("_rdpOtherModesAlphaCompare", true);
        Call("ExecuteRdpSetTile", 0xf5100200u, 0u);
        Call("ExecuteRdpSetTileSize", 0xf2000000u, 0u);
        var compressed = (ushort[])typeof(Memory).GetField("RdpZCompressTable", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        var hidden = (byte[])typeof(Memory).GetField("_rdpHiddenBits", flags)!.GetValue(memory)!;
        int depthCases = 0;
        foreach (uint cycle in new uint[] { 0, 1, 2 })
        foreach (int command in new[] { 0x24, 0x25 })
        foreach (bool primitive in new[] { false, true })
        foreach (bool compare in new[] { false, true })
        foreach (bool update in new[] { false, true })
        foreach (bool near in new[] { false, true })
        foreach (bool transparent in new[] { false, true })
        foreach (uint dz in new uint[] { 0, 1, 4, 256, 32768 })
        {
            Set("_rdpColorImageSize", 2u);
            Set("_rdpOtherModesCycleType", cycle); Set("_rdpOtherModesZSourceSel", primitive);
            Set("_rdpOtherModesZCompare", compare); Set("_rdpOtherModesZUpdate", update);
            uint z = near ? 0x1000u : 0x6000u;
            Set("_rdpPrimitiveDepth", z); Set("_rdpPrimitiveDeltaZ", dz);
            BinaryPrimitives.WriteUInt16BigEndian(tmem.AsSpan(2), transparent ? (ushort)0x07c0 : (ushort)0x07c1);
            BinaryPrimitives.WriteUInt16BigEndian(memory.RDRAM.AsSpan(0x300000), 0xf801);
            ushort oldDepth = compressed[0x20000];
            BinaryPrimitives.WriteUInt16BigEndian(memory.RDRAM.AsSpan(0x400), oldDepth);
            hidden[0x200] = 0;
            Call("ExecuteRdpTextureRectangle", command, 0xe4000000u, 0u, 0u, 0u);
            bool active = cycle < 2 && primitive;
            bool visible = !transparent && (!active || !compare || near);
            ushort actual = BinaryPrimitives.ReadUInt16BigEndian(memory.RDRAM.AsSpan(0x300000));
            if (actual != (visible ? 0x07c1 : 0xf801))
                throw new Exception($"Rectangle occlusion case {depthCases}: color={actual:x4}");
            uint encodedDz = dz <= 1 ? 0u : dz == 4 ? 2u : dz == 256 ? 8u : 15u;
            bool wroteDepth = visible && active && update;
            ushort expectedDepth = wroteDepth ? (ushort)(compressed[z << 3] | (encodedDz >> 2)) : oldDepth;
            if (BinaryPrimitives.ReadUInt16BigEndian(memory.RDRAM.AsSpan(0x400)) != expectedDepth
                || hidden[0x200] != (wroteDepth ? encodedDz & 3 : 0))
                throw new Exception($"Rectangle depth update case {depthCases}");
            depthCases++;
        }
        int blendCases = 0;
        foreach (uint cycle in new uint[] { 0, 1 })
        foreach (bool passthrough in new[] { false, true })
        foreach (uint alpha in new uint[] { 0, 1, 64, 128, 254, 255 })
        {
            Call("ExecuteRdpSetOtherModes", 0xef000000u | cycle << 20,
                passthrough ? 0x0f0a4040u : 0x00504040u);
            uint source = 0x80604000u | alpha, destination = 0x204060ffu;
            BinaryPrimitives.WriteUInt32BigEndian(memory.RDRAM.AsSpan(0x300000), destination);
            Call("WriteRdpRgbaPixel", 0x300000u, source, 4u);
            uint actual = BinaryPrimitives.ReadUInt32BigEndian(memory.RDRAM.AsSpan(0x300000));
            uint expected = source;
            if (!passthrough && alpha < 255)
            {
                expected = 255;
                foreach (int shift in new[] { 8, 16, 24 })
                    expected |= ((((source >> shift) & 255) * alpha
                        + ((destination >> shift) & 255) * (255 - alpha) + 127) / 255) << shift;
            }
            if (actual != expected) throw new Exception($"Blender passthrough={passthrough} alpha={alpha}: {actual:x8}!={expected:x8}");
            // Effective blending mode must also survive a savestate round trip.
            using var state = new MemoryStream();
            using var writer = new BinaryWriter(state, System.Text.Encoding.UTF8, true);
            memory.SaveState(writer); writer.Flush();
            Call("ExecuteRdpSetOtherModes", 0xef000000u, 0u);
            state.Position = 0;
            using var reader = new BinaryReader(state, System.Text.Encoding.UTF8, true);
            memory.LoadState(reader);
            BinaryPrimitives.WriteUInt32BigEndian(memory.RDRAM.AsSpan(0x300000), destination);
            Call("WriteRdpRgbaPixel", 0x300000u, source, 4u);
            if (BinaryPrimitives.ReadUInt32BigEndian(memory.RDRAM.AsSpan(0x300000)) != expected)
                throw new Exception("Blender savestate mode lost");
            blendCases++;
        }
        Console.WriteLine($"intensityAlphaCases={intensityCases} rectangleDepthCases={depthCases} blenderCases={blendCases} passed");
    }
}
