using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;

namespace ProjectPSX.IO
{
    public static class VirtualFileSystem
    {
        private const string VirtualRoot = "/__eutherdrive_virtual__";
        private static readonly ConcurrentDictionary<string, Entry> Entries = new(StringComparer.OrdinalIgnoreCase);

        public static string Register(string displayName, Func<Stream> openRead, long? length = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
            ArgumentNullException.ThrowIfNull(openRead);

            string safeName = SanitizeFileName(displayName);
            string virtualPath = Path.Combine(VirtualRoot, $"{Guid.NewGuid():N}_{safeName}");
            return RegisterAtPath(virtualPath, safeName, openRead, length);
        }

        public static string RegisterAtPath(string virtualPath, string displayName, Func<Stream> openRead, long? length = null, IDisposable? lifetime = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(virtualPath);
            ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
            ArgumentNullException.ThrowIfNull(openRead);

            string normalizedPath = NormalizePath(virtualPath);
            Entries[normalizedPath] = new Entry
            {
                DisplayName = SanitizeFileName(displayName),
                OpenRead = openRead,
                Length = length,
                Lifetime = lifetime
            };

            return normalizedPath;
        }

        public static string RegisterSharedStream(string displayName, Stream stream, bool ownsStream = true)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
            ArgumentNullException.ThrowIfNull(stream);

            if (!stream.CanSeek)
                throw new InvalidOperationException("Shared virtual streams must be seekable.");

            string safeName = SanitizeFileName(displayName);
            string virtualPath = Path.Combine(VirtualRoot, $"{Guid.NewGuid():N}_{safeName}");
            return RegisterSharedStreamAtPath(virtualPath, safeName, stream, ownsStream);
        }

        public static string RegisterSharedStreamAtPath(string virtualPath, Stream stream, bool ownsStream = true)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(virtualPath);
            ArgumentNullException.ThrowIfNull(stream);

            string displayName = Path.GetFileName(virtualPath);
            return RegisterSharedStreamAtPath(virtualPath, displayName, stream, ownsStream);
        }

        public static string RegisterSharedStreamAtPath(string virtualPath, string displayName, Stream stream, bool ownsStream = true)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(virtualPath);
            ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
            ArgumentNullException.ThrowIfNull(stream);

            if (!stream.CanSeek)
                throw new InvalidOperationException("Shared virtual streams must be seekable.");

            var owner = new SharedStreamOwner(stream, ownsStream);
            return RegisterAtPath(
                virtualPath,
                displayName,
                () => new SharedReadStream(owner),
                stream.Length,
                owner);
        }

        public static string RegisterBytes(string displayName, byte[] data)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
            ArgumentNullException.ThrowIfNull(data);

            string safeName = SanitizeFileName(displayName);
            string virtualPath = Path.Combine(VirtualRoot, $"{Guid.NewGuid():N}_{safeName}");
            return RegisterBytesAtPath(virtualPath, safeName, data);
        }

        public static string RegisterBytesAtPath(string virtualPath, byte[] data)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(virtualPath);
            ArgumentNullException.ThrowIfNull(data);

            string displayName = Path.GetFileName(virtualPath);
            return RegisterBytesAtPath(virtualPath, displayName, data);
        }

        public static string RegisterBytesAtPath(string virtualPath, string displayName, byte[] data)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(virtualPath);
            ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
            ArgumentNullException.ThrowIfNull(data);

            byte[] snapshot = new byte[data.Length];
            Array.Copy(data, snapshot, data.Length);

            return RegisterAtPath(
                virtualPath,
                displayName,
                () => new MemoryStream(snapshot, writable: false),
                snapshot.Length);
        }

        public static void Unregister(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return;

            string normalizedPath = NormalizePath(path);
            if (Entries.TryRemove(normalizedPath, out Entry entry))
                (entry as IDisposable)?.Dispose();
        }

        public static bool IsVirtualPath(string path)
        {
            return !string.IsNullOrWhiteSpace(path) && Entries.ContainsKey(NormalizePath(path));
        }

        public static bool DirectoryExists(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return false;

            if (Directory.Exists(path))
                return true;

            string normalized = NormalizeDirectoryPath(path);
            return Entries.Keys.Any(entryPath => IsDirectChildOfDirectory(entryPath, normalized));
        }

        public static bool Exists(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return false;

            string normalizedPath = NormalizePath(path);
            return Entries.ContainsKey(normalizedPath) || File.Exists(normalizedPath);
        }

        public static string[] GetFiles(string directoryPath, string searchPattern)
        {
            if (string.IsNullOrWhiteSpace(directoryPath))
                return Array.Empty<string>();

            if (Directory.Exists(directoryPath))
                return Directory.GetFiles(directoryPath, searchPattern);

            string normalized = NormalizeDirectoryPath(directoryPath);
            return Entries.Keys
                .Where(path => IsDirectChildOfDirectory(path, normalized))
                .Where(path => MatchesSearchPattern(Path.GetFileName(path), searchPattern))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        public static Stream OpenRead(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("Path is required.", nameof(path));

            string normalizedPath = NormalizePath(path);
            if (Entries.TryGetValue(normalizedPath, out Entry? entry))
                return entry.OpenRead();

            return new FileStream(normalizedPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        }

        public static long GetLength(string path)
        {
            string normalizedPath = NormalizePath(path);
            if (Entries.TryGetValue(normalizedPath, out Entry? entry) && entry.Length.HasValue)
                return entry.Length.Value;

            if (File.Exists(normalizedPath))
                return new FileInfo(normalizedPath).Length;

            using Stream stream = OpenRead(normalizedPath);
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

        private static string NormalizePath(string path)
        {
            string normalized = path.Replace('\\', Path.DirectorySeparatorChar);
            return normalized.Length > 1
                ? normalized.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                : normalized;
        }

        private static string NormalizeDirectoryPath(string path)
        {
            string normalized = NormalizePath(path);
            return normalized.EndsWith(Path.DirectorySeparatorChar)
                ? normalized.TrimEnd(Path.DirectorySeparatorChar)
                : normalized;
        }

        private static bool IsDirectChildOfDirectory(string path, string directoryPath)
        {
            string? parent = Path.GetDirectoryName(NormalizePath(path));
            if (string.IsNullOrWhiteSpace(parent))
                return false;

            return string.Equals(
                NormalizeDirectoryPath(parent),
                NormalizeDirectoryPath(directoryPath),
                StringComparison.OrdinalIgnoreCase);
        }

        private static bool MatchesSearchPattern(string fileName, string searchPattern)
        {
            if (string.IsNullOrWhiteSpace(searchPattern) || searchPattern == "*" || searchPattern == "*.*")
                return true;

            string regexPattern = "^" + Regex.Escape(searchPattern)
                .Replace(@"\*", ".*")
                .Replace(@"\?", ".") + "$";
            return Regex.IsMatch(fileName, regexPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
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
