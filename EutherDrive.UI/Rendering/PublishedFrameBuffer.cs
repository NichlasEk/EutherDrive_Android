using System;

namespace EutherDrive.Rendering;

// The producer owns the core lock. The UI only holds this short copy lock;
// it never waits for the next emulated frame to finish.
internal sealed class PublishedFrameBuffer
{
    private readonly object _sync = new();
    private object? _owner;
    private byte[] _bytes = Array.Empty<byte>();
    private int _length, _width, _height, _stride;
    private long _frame;

    public void Publish(object owner, ReadOnlySpan<byte> source, int width, int height, int stride, long frame)
    {
        int length = checked(stride * height);
        if (width <= 0 || height <= 0 || stride < checked(width * 4) || source.Length < length)
            throw new ArgumentException("Invalid published framebuffer dimensions.");
        lock (_sync)
        {
            if (_bytes.Length < length) _bytes = new byte[length];
            source[..length].CopyTo(_bytes);
            _length = length;
            _width = width;
            _height = height;
            _stride = stride;
            _frame = frame;
            _owner = owner;
        }
    }

    public bool TryCopy(object owner, ref byte[] destination, out int width, out int height, out int stride, out long frame)
    {
        lock (_sync)
        {
            width = _width;
            height = _height;
            stride = _stride;
            frame = _frame;
            if (!ReferenceEquals(owner, _owner)) return false;
            if (destination.Length < _length) destination = new byte[_length];
            _bytes.AsSpan(0, _length).CopyTo(destination);
            return true;
        }
    }

    public void Clear()
    {
        lock (_sync) _owner = null;
    }
}
