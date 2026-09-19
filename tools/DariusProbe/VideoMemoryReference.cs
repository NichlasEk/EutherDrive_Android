using System.Buffers.Binary;
using System.Reflection;
using EutherDrive.Core.Arcade.Taito;

internal static class VideoMemoryReference
{
    internal static void Run(string rom, string inputDirectory, string outputDirectory)
    {
        if (Path.GetFullPath(inputDirectory) == Path.GetFullPath(outputDirectory))
            throw new ArgumentException("Use a separate output directory for the reference render");
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        using var core = new DariusGaidenAdapter();
        core.LoadRom(rom);
        Type type = core.GetType();
        type.GetMethod("ResetVideoRuntime", flags)!.Invoke(core, null);
        object bus = type.GetField("_bus", flags)!.GetValue(core)!;
        var write = bus.GetType().GetMethod("WriteWord")!.CreateDelegate<Action<uint, ushort>>(bus);
        foreach (var (name, address, length) in new (string, uint, int)[] {
            ("_palette", 0x440000, 0x8000), ("_spriteRam", 0x600000, 0x10000),
            ("_playfieldRam", 0x610000, 0xc000), ("_textRam", 0x61c000, 0x2000),
            ("_charRam", 0x61e000, 0x2000), ("_lineRam", 0x620000, 0x10000),
            ("_pivotRam", 0x630000, 0x10000), ("_control0", 0x660000, 16), ("_control1", 0x660010, 16) })
        {
            byte[] data = File.ReadAllBytes(Path.Combine(inputDirectory, name + ".bin"));
            if (data.Length != length) throw new InvalidDataException($"Unexpected {name} length");
            for (int offset = 0; offset < data.Length; offset += 2)
                write(address + (uint)offset, BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset, 2)));
        }
        var render = type.GetMethod("RenderUglyVideo", flags)!.CreateDelegate<Action>(core);
        using var capture = new IntroCapture(core, outputDirectory);
        for (int frame = 0; frame < 6; frame++)
        {
            render();
            capture.Capture(frame);
        }
        Console.WriteLine("videoMemoryReference=rendered CPU=not-run frames=6");
    }
}
