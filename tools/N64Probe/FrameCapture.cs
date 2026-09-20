using System.Text;
using Ryu64.MIPS;

// Sample presentation independently of graphics-task counters. Only changed
// images are written; the index preserves their actual wall-clock timestamps.
internal sealed class FrameCapture : IDisposable
{
    private readonly string _directory;
    private readonly StreamWriter _index;
    private byte[] _previous = Array.Empty<byte>();
    private byte[] _rgb = Array.Empty<byte>();
    private int _width, _height, _bpp, _frames;

    internal FrameCapture(string directory)
    {
        _directory = Path.Combine(directory, "presentation");
        Directory.CreateDirectory(_directory);
        _index = new StreamWriter(Path.Combine(_directory, "frames.tsv"));
        _index.WriteLine("seconds\tframe\tgraphicsTasks\tviOrigin\tstatus");
    }

    internal void Poll(Ryu64Core.Ryu64Core core, double seconds)
    {
        if (!core.TryGetFramebuffer(out var pixels, out int width, out int height, out int bpp)) return;
        var current = pixels.AsSpan(0, width * height * bpp);
        if (_width == width && _height == height && _bpp == bpp && current.SequenceEqual(_previous)) return;
        _width = width; _height = height; _bpp = bpp;
        Array.Resize(ref _previous, current.Length);
        current.CopyTo(_previous);
        Array.Resize(ref _rgb, width * height * 3);
        for (int p = 0; p < width * height; p++)
        {
            if (bpp == 2)
            {
                int c = (pixels[p * 2] << 8) | pixels[p * 2 + 1];
                _rgb[p * 3] = (byte)(((c >> 11) & 31) * 255 / 31);
                _rgb[p * 3 + 1] = (byte)(((c >> 6) & 31) * 255 / 31);
                _rgb[p * 3 + 2] = (byte)(((c >> 1) & 31) * 255 / 31);
            }
            else pixels.AsSpan(p * bpp, 3).CopyTo(_rgb.AsSpan(p * 3));
        }
        string frame = $"{++_frames:D6}.ppm";
        using var file = File.Create(Path.Combine(_directory, frame));
        file.Write(Encoding.ASCII.GetBytes($"P6\n{width} {height}\n255\n"));
        file.Write(_rgb);
        _index.WriteLine(FormattableString.Invariant($"{seconds:F6}\t{frame}\t{core.GraphicsTaskCount}\t{R4300.memory.ReadUInt32(0x04400004):x8}\t{core.LastFramebufferStatus}"));
    }

    public void Dispose() => _index.Dispose();
}
