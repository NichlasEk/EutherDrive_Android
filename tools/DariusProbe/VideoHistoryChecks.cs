using System.Buffers.Binary;
using System.Reflection;
using EutherDrive.Core.Arcade.Taito;

internal static class VideoHistoryChecks
{
    internal static void Run()
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        using var core = new DariusGaidenAdapter();
        Type type = core.GetType();
        object rom = Activator.CreateInstance(type.GetNestedType("TaitoF3RomSet", BindingFlags.NonPublic)!, true)!;
        type.GetField("_roms", flags)!.SetValue(core, rom);
        object bus = type.GetField("_bus", flags)!.GetValue(core)!;
        byte[] Ram(string name) => (byte[])bus.GetType().GetField(name, flags)!.GetValue(bus)!;
        byte[] palette = Ram("_palette"), lines = Ram("_lineRam");
        BinaryPrimitives.WriteUInt32BigEndian(palette.AsSpan(4), 0x00123456);
        // Section 2/subsection 3: latch background palette 1 from scanline 0.
        BinaryPrimitives.WriteUInt16BigEndian(lines.AsSpan(0x400), 0x0008);
        BinaryPrimitives.WriteUInt16BigEndian(lines.AsSpan(0x6600), 1);
        var render = type.GetMethod("RenderUglyVideo", flags)!.CreateDelegate<Action>(core);
        foreach (uint expected in new uint[] { 0xff123456, 0xff000000, 0xff123456 })
        {
            BinaryPrimitives.WriteUInt16BigEndian(lines.AsSpan(0x6600), (ushort)(expected == 0xff000000 ? 0 : 1));
            render();
            var pixels = core.GetFrameBuffer(out _, out _, out _);
            for (int offset = 0; offset < pixels.Length; offset += 4)
                if (BinaryPrimitives.ReadUInt32LittleEndian(pixels.Slice(offset, 4)) != expected)
                    throw new InvalidOperationException($"Background-only frame retained stale pixels: expected={expected:x8} offset={offset}");
        }
        byte[] sprites = Ram("_spriteRam"), work = Ram("_workRam");
        // Hardware list immediately terminates. An old software pointer still
        // addresses a visible tile in the inactive bank, as in the intro states.
        BinaryPrimitives.WriteUInt16BigEndian(sprites.AsSpan(12), 0x8000);
        BinaryPrimitives.WriteUInt32BigEndian(work.AsSpan(0x7360), 0x00608310);
        BinaryPrimitives.WriteUInt16BigEndian(sprites.AsSpan(0x8320), 1);
        BinaryPrimitives.WriteUInt16BigEndian(sprites.AsSpan(0x8324), 64);
        BinaryPrimitives.WriteUInt16BigEndian(sprites.AsSpan(0x8326), 48);
        BinaryPrimitives.WriteUInt16BigEndian(sprites.AsSpan(0x833c), 0x8033);
        type.GetMethod("BuildSpriteList", flags)!.Invoke(core, null);
        var list = (System.Collections.ICollection)type.GetField("_sprites", flags)!.GetValue(core)!;
        if (list.Count != 0)
            throw new InvalidOperationException("Empty hardware sprite list revived stale software-list tiles");
        Console.WriteLine("videoHistoryChecks=passed backgroundClearTransitions=3 pixels=222720 emptySpriteList=passed");
    }
}
