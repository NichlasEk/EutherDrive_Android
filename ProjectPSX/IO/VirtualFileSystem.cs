using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;

namespace ProjectPSX.IO
{
    public static class VirtualFileSystem
    {
        private static readonly ConcurrentDictionary<string, Entry> Entries = new(StringComparer.OrdinalIgnoreCase);

        public static string Register(string displayName, Func<Stream> openRead, long? length = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
            ArgumentNullException.ThrowIfNull(openRead);

            string safeName = SanitizeFileName(displayName);
            string virtualPath = Path.Combine("/__eutherdrive_virtual__", $"{Guid.NewGuid():N}_{safeName}");
            Entries[virtualPath] = new Entry
            {
                DisplayName = safeName,
                OpenRead = openRead,
                Length = length,
                Lifetime = null
            };

            return virtualPath;
        }

        public static string RegisterSharedStream(string displayName, Stream stream, bool ownsStream = true)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
            ArgumentNullException.ThrowIfNull(stream);

            if (!stream.CanSeek)
                throw new InvalidOperationException("Shared virtual streams must be seekable.");

            string safeName = SanitizeFileName(displayName);
            string virtualPath = Path.Combine("/__eutherdrive_virtual__", $"{Guid.NewGuid():N}_{safeName}");
            var owner = new SharedStreamOwner(stream, ownsStream);

            Entries[virtualPath] = new Entry
            {
                DisplayName = safeName,
                OpenRead = () => new SharedReadStream(owner),
                Length = stream.Length,
                Lifetime = owner
            };

            return virtualPath;
        }

        public static string RegisterBytes(string displayName, byte[] data)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
            ArgumentNullException.ThrowIfNull(data);

            string safeName = SanitizeFileName(displayName);
            string virtualPath = Path.Combine("/__eutherdrive_virtual__", $"{Guid.NewGuid():N}_{safeName}");
            byte[] snapshot = new byte[data.Length];
            Array.Copy(data, snapshot, data.Length);

            Entries[virtualPath] = new Entry
            {
                DisplayName = safeName,
                OpenRead = () => new MemoryStream(snapshot, writable: false),
                Length = snapshot.Length,
                Lifetime = null
            };

            return virtualPath;
        }

        public static void Unregister(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return;

            if (Entries.TryRemove(path, out Entry entry))
                (entry as IDisposable)?.Dispose();
        }

        public static bool IsVirtualPath(string path) => !string.IsNullOrWhiteSpace(path) && Entries.ContainsKey(path);

        public static bool Exists(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return false;

            return Entries.ContainsKey(path) || File.Exists(path);
        }

        public static Stream OpenRead(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("Path is required.", nameof(path));

            if (Entries.TryGetValue(path, out Entry? entry))
                return entry.OpenRead();

            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        }

        public static long GetLength(string path)
        {
            if (Entries.TryGetValue(path, out Entry? entry) && entry.Length.HasValue)
                return entry.Length.Value;

            if (File.Exists(path))
                return new FileInfo(path).Length;

            using Stream stream = OpenRead(path);
            if (!stream.CanSeek)
                throw new InvalidOperationException($"Unable to determine length for non-seekable stream: {path}");

            return stream.Length;
        }

        public static byte[] ReadAllBytes(string path)
        {
            using Stream stream = OpenRead(path);
            if (stream.CanSeek && stream.Length <= int.MaxValue)
            {
                byte[] buffer = new byte[stream.Length];
                int offset = 0;
                while (offset < buffer.Length)
                {
                    int read = stream.Read(buffer, offset, buffer.Length - offset);
                    if (read <= 0)
                        break;
                    offset += read;
                }

                if (offset == buffer.Length)
                    return buffer;

                Array.Resize(ref buffer, offset);
                return buffer;
            }

            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            return ms.ToArray();
        }

        private static string SanitizeFileName(string name)
        {
            string trimmed = Path.GetFileName(string.IsNullOrWhiteSpace(name) ? "disc.bin" : name);
            foreach (char invalid in Path.GetInvalidFileNameChars())
                trimmed = trimmed.Replace(invalid, '_');
            return string.IsNullOrWhiteSpace(trimmed) ? "disc.bin" : trimmed;
        }

        private sealed class SharedStreamOwner : IDisposable
        {
            private readonly Stream _stream;
            private readonly bool _ownsStream;
            private int _disposed;

            public SharedStreamOwner(Stream stream, bool ownsStream)
            {
                _stream = stream;
                _ownsStream = ownsStream;
            }

            public long Length
            {
                get
                {
                    ThrowIfDisposed();
                    lock (_stream)
                        return _stream.Length;
                }
            }

            public int Read(long position, byte[] buffer, int offset, int count)
            {
                ThrowIfDisposed();
                lock (_stream)
                {
                    _stream.Seek(position, SeekOrigin.Begin);
                    return _stream.Read(buffer, offset, count);
                }
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0)
                    return;

                if (_ownsStream)
                    _stream.Dispose();
            }

            private void ThrowIfDisposed()
            {
                if (Volatile.Read(ref _disposed) != 0)
                    throw new ObjectDisposedException(nameof(SharedStreamOwner));
            }
        }

        private sealed class SharedReadStream : Stream
        {
            private readonly SharedStreamOwner _owner;
            private long _position;
            private bool _disposed;

            public SharedReadStream(SharedStreamOwner owner) => _owner = owner;

            public override bool CanRead => !_disposed;
            public override bool CanSeek => !_disposed;
            public override bool CanWrite => false;
            public override long Length => _owner.Length;

            public override long Position
            {
                get => _position;
                set
                {
                    ObjectDisposedException.ThrowIf(_disposed, this);
                    if (value < 0)
                        throw new ArgumentOutOfRangeException(nameof(value));
                    _position = value;
                }
            }

            public override void Flush()
            {
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                int read = _owner.Read(_position, buffer, offset, count);
                _position += read;
                return read;
            }

            public override long Seek(long offset, SeekOrigin origin)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                long target = origin switch
                {
                    SeekOrigin.Begin => offset,
                    SeekOrigin.Current => _position + offset,
                    SeekOrigin.End => _owner.Length + offset,
                    _ => throw new ArgumentOutOfRangeException(nameof(origin))
                };

                if (target < 0)
                    throw new IOException("Attempted to seek before the start of the stream.");

                _position = target;
                return _position;
            }

            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            protected override void Dispose(bool disposing)
            {
                _disposed = true;
                base.Dispose(disposing);
            }
        }

        private sealed class Entry : IDisposable
        {
            public required Func<Stream> OpenRead { get; init; }
            public required string DisplayName { get; init; }
            public long? Length { get; init; }
            public IDisposable? Lifetime { get; init; }

            public void Dispose() => Lifetime?.Dispose();
        }
    }
}
