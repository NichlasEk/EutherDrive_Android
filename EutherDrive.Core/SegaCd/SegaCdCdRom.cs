using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace EutherDrive.Core.SegaCd;

internal readonly struct CdTime : IComparable<CdTime>
{
    public static readonly CdTime Zero = new(0, 0, 0);

    public readonly int Minutes;
    public readonly int Seconds;
    public readonly int Frames;

    public CdTime(int minutes, int seconds, int frames)
    {
        if (frames < 0) frames = 0;
        if (seconds < 0) seconds = 0;
        if (minutes < 0) minutes = 0;

        int totalFrames = (minutes * 60 + seconds) * 75 + frames;
        Minutes = totalFrames / (60 * 75);
        int rem = totalFrames % (60 * 75);
        Seconds = rem / 75;
        Frames = rem % 75;
    }

    public int ToFrames() => (Minutes * 60 + Seconds) * 75 + Frames;

    public static CdTime FromFrames(int frames)
    {
        if (frames <= 0)
            return Zero;
        return new CdTime(0, 0, frames);
    }

    public CdTime AddFrames(int frames) => FromFrames(ToFrames() + frames);

    public CdTime SaturatingSub(CdTime other)
    {
        int diff = ToFrames() - other.ToFrames();
        return diff <= 0 ? Zero : FromFrames(diff);
    }

    public int CompareTo(CdTime other) => ToFrames().CompareTo(other.ToFrames());

    public override string ToString() => $"{Minutes:D2}:{Seconds:D2}:{Frames:D2}";

    public static bool operator <=(CdTime a, CdTime b) => a.CompareTo(b) <= 0;
    public static bool operator >=(CdTime a, CdTime b) => a.CompareTo(b) >= 0;
    public static bool operator <(CdTime a, CdTime b) => a.CompareTo(b) < 0;
    public static bool operator >(CdTime a, CdTime b) => a.CompareTo(b) > 0;
}

internal enum CdTrackType
{
    Audio,
    Data
}

internal sealed class CdTrack
{
    public int Number { get; set; }
    public CdTrackType TrackType { get; set; }
    public CdTime StartTime { get; set; }
    public CdTime EndTime { get; set; }

    public CdTime EffectiveStartTime() => StartTime;
}

internal sealed class CdCue
{
    private readonly List<CdTrack> _tracks = new();

    public IReadOnlyList<CdTrack> Tracks => _tracks;

    public CdTrack LastTrack => _tracks.Count == 0 ? new CdTrack() : _tracks[_tracks.Count - 1];

    public CdTrack Track(int number)
    {
        foreach (var track in _tracks)
        {
            if (track.Number == number)
                return track;
        }
        return LastTrack;
    }

    public CdTrack? FindTrackByTime(CdTime time)
    {
        foreach (var track in _tracks)
        {
            if (time >= track.StartTime && time < track.EndTime)
                return track;
        }
        return _tracks.Count > 0 ? _tracks[_tracks.Count - 1] : null;
    }

    public static CdCue FromIsoLength(int sectorCount)
    {
        var cue = new CdCue();
        var start = new CdTime(0, 2, 0);
        var end = start.AddFrames(sectorCount);
        cue._tracks.Add(new CdTrack
        {
            Number = 1,
            TrackType = CdTrackType.Data,
            StartTime = start,
            EndTime = end
        });
        return cue;
    }

    public static CdCue Parse(string cuePath)
    {
        var cue = new CdCue();
        if (!File.Exists(cuePath))
            return cue;

        CdTrack? current = null;
        foreach (var rawLine in File.ReadLines(cuePath))
        {
            string line = rawLine.Trim();
            if (line.Length == 0)
                continue;

            if (line.StartsWith("TRACK", StringComparison.OrdinalIgnoreCase))
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 3 && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int num))
                {
                    current = new CdTrack
                    {
                        Number = num,
                        TrackType = parts[2].StartsWith("AUDIO", StringComparison.OrdinalIgnoreCase) ? CdTrackType.Audio : CdTrackType.Data
                    };
                    cue._tracks.Add(current);
                }
            }
            else if (line.StartsWith("INDEX 01", StringComparison.OrdinalIgnoreCase) && current != null)
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 3)
                {
                    var timeParts = parts[2].Split(':');
                    if (timeParts.Length == 3
                        && int.TryParse(timeParts[0], out int mm)
                        && int.TryParse(timeParts[1], out int ss)
                        && int.TryParse(timeParts[2], out int ff))
                    {
                        // Add 2 second (150 frame) lead-in to get absolute time
                        var t = new CdTime(mm, ss, ff).AddFrames(150);
                        current.StartTime = t;
                    }
                }
            }
        }

        // Ensure track start times are monotonic; set missing to previous
        CdTime last = new CdTime(0, 2, 0);
        foreach (var track in cue._tracks)
        {
            if (track.StartTime.ToFrames() == 0)
                track.StartTime = last;
            if (track.StartTime < last)
                track.StartTime = last;
            last = track.StartTime;
        }

        return cue;
    }

    public void FinalizeEndTimes(CdTime discEnd)
    {
        for (int i = 0; i < _tracks.Count; i++)
        {
            _tracks[i].EndTime = i + 1 < _tracks.Count ? _tracks[i + 1].StartTime : discEnd;
        }
    }
}

internal sealed class CdRom
{
    public const int BytesPerSector = 2352;

    private readonly string _path;
    private readonly int _sectorSize;
    private readonly int _dataOffset;
    private readonly FileStream? _stream;
    private readonly byte[]? _data;
    private readonly object _lock = new();
    private readonly CdCue _cue;

    public CdCue Cue => _cue;

    private CdRom(string path, int sectorSize, int dataOffset, CdCue cue, byte[]? data)
    {
        _path = path;
        _sectorSize = sectorSize;
        _dataOffset = dataOffset;
        _cue = cue;
        _data = data;
        if (data == null)
            _stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
    }

    public static CdRom? Open(string path, bool preload = false)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        string dataPath = path;
        CdCue cue;
        if (path.EndsWith(".cue", StringComparison.OrdinalIgnoreCase))
        {
            cue = CdCue.Parse(path);
            dataPath = ResolveCueDataPath(path) ?? path;
        }
        else
        {
            cue = CdCue.FromIsoLength(GuessSectorCount(path));
        }

        if (!File.Exists(dataPath))
            return null;

        int sectorSize = GuessSectorSize(dataPath);
        int dataOffset = sectorSize == 2352 ? 16 : 0;

        byte[]? data = null;
        if (preload)
        {
            try
            {
                data = File.ReadAllBytes(dataPath);
            }
            catch
            {
                data = null;
            }
        }

        var rom = new CdRom(dataPath, sectorSize, dataOffset, cue, data);
        int sectorCount = GuessSectorCount(dataPath, sectorSize);
        var discEnd = new CdTime(0, 2, 0).AddFrames(sectorCount);
        cue.FinalizeEndTimes(discEnd);

        return rom;
    }

    public bool ReadSector(CdTime time, Span<byte> buffer)
    {
        int lba = time.ToFrames() - 150;
        if (lba < 0)
        {
            buffer.Fill(0);
            return false;
        }

        long offset = (long)lba * _sectorSize;
        if (offset < 0)
        {
            buffer.Fill(0);
            return false;
        }

        lock (_lock)
        {
            long dataLength = _data?.Length ?? _stream?.Length ?? 0;
            if (offset + _sectorSize > dataLength)
            {
                buffer.Fill(0);
                return false;
            }

            if (_data != null)
            {
                int start = (int)offset;
                if (_sectorSize == 2352)
                {
                    _data.AsSpan(start, _sectorSize).CopyTo(buffer);
                    return true;
                }

                // 2048-byte sector; synthesize 2352
                buffer.Fill(0);
                // Sync (12 bytes)
                buffer[0] = 0x00;
                for (int i = 1; i < 11; i++)
                    buffer[i] = 0xFF;
                buffer[11] = 0x00;

                // Header (BCD MSF) + mode
                int msfFrames = lba + 150;
                int minutes = msfFrames / (60 * 75);
                int rem = msfFrames % (60 * 75);
                int seconds = rem / 75;
                int frames = rem % 75;

                buffer[12] = ToBcd(minutes);
                buffer[13] = ToBcd(seconds);
                buffer[14] = ToBcd(frames);
                buffer[15] = 0x01; // Mode 1

                _data.AsSpan(start, 2048).CopyTo(buffer.Slice(16, 2048));
                return true;
            }

            _stream!.Seek(offset, SeekOrigin.Begin);
            if (_sectorSize == 2352)
            {
                int read = _stream.Read(buffer);
                if (read != buffer.Length)
                {
                    buffer.Slice(read).Fill(0);
                    return false;
                }
                return true;
            }

            // 2048-byte sector; synthesize 2352
            buffer.Fill(0);
            // Sync (12 bytes)
            buffer[0] = 0x00;
            for (int i = 1; i < 11; i++)
                buffer[i] = 0xFF;
            buffer[11] = 0x00;

            // Header (BCD MSF) + mode
            int msfFrames2 = lba + 150;
            int minutes2 = msfFrames2 / (60 * 75);
            int rem2 = msfFrames2 % (60 * 75);
            int seconds2 = rem2 / 75;
            int frames2 = rem2 % 75;

            buffer[12] = ToBcd(minutes2);
            buffer[13] = ToBcd(seconds2);
            buffer[14] = ToBcd(frames2);
            buffer[15] = 0x01; // Mode 1

            Span<byte> data = buffer.Slice(16, 2048);
            int read2048 = _stream.Read(data);
            if (read2048 != 2048)
            {
                if (read2048 > 0)
                    data.Slice(read2048).Fill(0);
                return false;
            }
            return true;
        }
    }

    private static int GuessSectorSize(string path)
    {
        long length = new FileInfo(path).Length;
        if (length % 2352 == 0)
            return 2352;
        return 2048;
    }

    private static int GuessSectorCount(string path, int? sectorSizeOverride = null)
    {
        long length = new FileInfo(path).Length;
        int sectorSize = sectorSizeOverride ?? GuessSectorSize(path);
        if (sectorSize <= 0)
            return 0;
        return (int)(length / sectorSize);
    }

    private static string? ResolveCueDataPath(string cuePath)
    {
        string baseDir = Path.GetDirectoryName(cuePath) ?? "";
        foreach (var rawLine in File.ReadLines(cuePath))
        {
            string line = rawLine.Trim();
            if (!line.StartsWith("FILE", StringComparison.OrdinalIgnoreCase))
                continue;

            int firstQuote = line.IndexOf('"');
            if (firstQuote >= 0)
            {
                int secondQuote = line.IndexOf('"', firstQuote + 1);
                if (secondQuote > firstQuote)
                {
                    string fileName = line.Substring(firstQuote + 1, secondQuote - firstQuote - 1);
                    string candidate = Path.Combine(baseDir, fileName);
                    if (File.Exists(candidate))
                        return candidate;
                }
            }
            else
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2)
                {
                    string candidate = Path.Combine(baseDir, parts[1]);
                    if (File.Exists(candidate))
                        return candidate;
                }
            }
        }

        return null;
    }

    private static byte ToBcd(int value)
    {
        if (value < 0) value = 0;
        int tens = value / 10;
        int ones = value % 10;
        return (byte)((tens << 4) | ones);
    }
}
