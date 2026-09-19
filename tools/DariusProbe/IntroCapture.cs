using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using EutherDrive.Core.Arcade.Taito;

// Diagnostic output only: never writes to the loaded savestate.
internal sealed class IntroCapture : IDisposable
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
    private readonly DariusGaidenAdapter _core;
    private readonly string _directory;
    private readonly StreamWriter _trace;
    private object Field(string name) => typeof(DariusGaidenAdapter).GetField(name, Flags)!.GetValue(_core)!;

    internal IntroCapture(DariusGaidenAdapter core, string directory)
    {
        _core = core;
        _directory = directory;
        Directory.CreateDirectory(directory);
        _trace = new StreamWriter(Path.Combine(directory, "frames.csv"));
        _trace.WriteLine("frame,emulatedFrame,trails,bank,spritePixels,videoHash,reefHash");
    }

    internal void Capture(int frame)
    {
        var pixels = _core.GetFrameBuffer(out int width, out int height, out int stride);
        var reef = (ushort[])Field("_spriteReefPalette");
        _trace.WriteLine($"{frame},{Field("_frameCounter")},{Field("_spriteTrails")},{Field("_spriteBank")},{Field("_spriteReefOffsetCount")},{Convert.ToHexString(SHA256.HashData(pixels))},{Convert.ToHexString(SHA256.HashData(System.Runtime.InteropServices.MemoryMarshal.AsBytes(reef.AsSpan())))}");
        if (frame == 0)
        {
            object bus = Field("_bus");
            foreach (string name in new[] { "_palette", "_spriteRam", "_playfieldRam", "_textRam", "_charRam", "_lineRam", "_pivotRam", "_control0", "_control1" })
                File.WriteAllBytes(Path.Combine(_directory, name + ".bin"), (byte[])bus.GetType().GetField(name, Flags)!.GetValue(bus)!);
        }
        if (frame >= 6 && frame % 30 != 0) return;
        using var output = File.Create(Path.Combine(_directory, $"frame{frame:D4}.ppm"));
        output.Write(Encoding.ASCII.GetBytes($"P6\n{width} {height}\n255\n"));
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            int offset = y * stride + x * 4;
            output.WriteByte(pixels[offset + 2]);
            output.WriteByte(pixels[offset + 1]);
            output.WriteByte(pixels[offset]);
        }
    }

    public void Dispose() => _trace.Dispose();
}
