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
    public CdTime? Index00Time { get; set; }
    public int FileIndex { get; set; } = -1;

    public CdTime EffectiveStartTime() => Index00Time ?? StartTime;
}

internal sealed class CdCue
{
    private readonly List<CdTrack> _tracks = new();
    private readonly List<string> _files = new();
    private int[] _fileStartFrames = Array.Empty<int>();

    public IReadOnlyList<CdTrack> Tracks => _tracks;
    public IReadOnlyList<string> Files => _files;
    public IReadOnlyList<int> FileStartFrames => _fileStartFrames;

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
        if (_tracks.Count == 0)
            return null;

        CdTrack first = _tracks[0];
        if (time < first.EffectiveStartTime())
            return first;

        foreach (var track in _tracks)
        {
            CdTime start = track.EffectiveStartTime();
            if (time >= start && time < track.EndTime)
                return track;
        }
        return null;
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
        int currentFileIndex = -1;
        foreach (var rawLine in File.ReadLines(cuePath))
        {
            string line = rawLine.Trim();
            if (line.Length == 0)
                continue;

            if (line.StartsWith("FILE", StringComparison.OrdinalIgnoreCase))
            {
                int firstQuote = line.IndexOf('"');
                if (firstQuote >= 0)
                {
                    int secondQuote = line.IndexOf('"', firstQuote + 1);
                    if (secondQuote > firstQuote)
                    {
                        string fileName = line.Substring(firstQuote + 1, secondQuote - firstQuote - 1);
                        string fullPath = ResolveCueFilePath(cuePath, fileName);
                        cue._files.Add(fullPath);
                        currentFileIndex = cue._files.Count - 1;
                    }
                }
            }
            else if (line.StartsWith("TRACK", StringComparison.OrdinalIgnoreCase))
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 3 && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int num))
                {
                    current = new CdTrack
                    {
                        Number = num,
                        TrackType = parts[2].StartsWith("AUDIO", StringComparison.OrdinalIgnoreCase) ? CdTrackType.Audio : CdTrackType.Data
                    };
                    current.FileIndex = currentFileIndex;
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
                        current.StartTime = new CdTime(mm, ss, ff);
                    }
                }
            }
            else if (line.StartsWith("INDEX 00", StringComparison.OrdinalIgnoreCase) && current != null)
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
                        current.Index00Time = new CdTime(mm, ss, ff);
                    }
                }
            }
        }

        cue._fileStartFrames = ApplyCueFileOffsets(cuePath, cue._files, cue._tracks);

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

    private static string ResolveCueFilePath(string cuePath, string fileName)
    {
        string dir = Path.GetDirectoryName(cuePath) ?? string.Empty;
        return Path.GetFullPath(Path.Combine(dir, fileName));
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

    private static int[] ApplyCueFileOffsets(string cuePath, List<string> files, List<CdTrack> tracks)
    {
        if (files.Count == 0 || tracks.Count == 0)
            return Array.Empty<int>();

        var fileStartFramesAbs = new int[files.Count];
        int acc = 0;
        for (int i = 0; i < files.Count; i++)
        {
            fileStartFramesAbs[i] = acc + 150;
            string path = files[i];
            int sectorSize = GuessSectorSize(path);
            int sectorCount = GuessSectorCount(path, sectorSize);
            acc += sectorCount;
        }

        foreach (var track in tracks)
        {
            int fileIdx = track.FileIndex;
            int fileStartAbs = fileIdx >= 0 && fileIdx < fileStartFramesAbs.Length ? fileStartFramesAbs[fileIdx] : 150;
            int startFrames = fileStartAbs + track.StartTime.ToFrames();
            track.StartTime = CdTime.FromFrames(startFrames);
            if (track.Index00Time.HasValue)
            {
                int indexFrames = fileStartAbs + track.Index00Time.Value.ToFrames();
                track.Index00Time = CdTime.FromFrames(indexFrames);
            }
        }

        return fileStartFramesAbs;
    }

    public void FinalizeEndTimes(CdTime discEnd)
    {
        for (int i = 0; i < _tracks.Count; i++)
        {
            _tracks[i].EndTime = i + 1 < _tracks.Count ? _tracks[i + 1].EffectiveStartTime() : discEnd;
        }
    }
}

internal sealed class CdRom
{
    public const int BytesPerSector = 2352;
    private static readonly bool LogDisc = string.Equals(
        Environment.GetEnvironmentVariable("EUTHERDRIVE_SCD_LOG_DISC"),
        "1",
        StringComparison.Ordinal);

    private readonly List<FileEntry> _files;
    private readonly object _lock = new();
    private readonly CdCue _cue;

    public CdCue Cue => _cue;

    private readonly struct FileEntry
    {
        public readonly string Path;
        public readonly int SectorSize;
        public readonly int SectorCount;
        public readonly int StartFrameAbs;
        public readonly FileStream? Stream;
        public readonly byte[]? Data;

        public FileEntry(string path, int sectorSize, int sectorCount, int startFrameAbs, byte[]? data)
        {
            Path = path;
            SectorSize = sectorSize;
            SectorCount = sectorCount;
            StartFrameAbs = startFrameAbs;
            Data = data;
            Stream = data == null ? File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read) : null;
        }
    }

    private CdRom(List<FileEntry> files, CdCue cue)
    {
        _cue = cue;
        _files = files;
    }

    public static CdRom? Open(string path, bool preload = false)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            if (LogDisc)
                Console.Error.WriteLine("[SCD-DISC] Open: empty path");
            return null;
        }

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

        var files = new List<FileEntry>();
        if (cue.Files.Count > 0 && cue.FileStartFrames.Count == cue.Files.Count)
        {
            for (int i = 0; i < cue.Files.Count; i++)
            {
                string filePath = cue.Files[i];
                if (!File.Exists(filePath))
                {
                    if (LogDisc)
                        Console.Error.WriteLine($"[SCD-DISC] Open: data file not found path='{filePath}' (cue='{path}')");
                    return null;
                }

                int sectorSize = GuessSectorSize(filePath);
                int sectorCount = GuessSectorCount(filePath, sectorSize);
                byte[]? data = null;
                if (preload)
                {
                    try
                    {
                        data = File.ReadAllBytes(filePath);
                    }
                    catch
                    {
                        data = null;
                    }
                }

                files.Add(new FileEntry(filePath, sectorSize, sectorCount, cue.FileStartFrames[i], data));
            }
        }
        else
        {
            if (!File.Exists(dataPath))
            {
                if (LogDisc)
                    Console.Error.WriteLine($"[SCD-DISC] Open: data file not found path='{dataPath}' (cue='{path}')");
                return null;
            }

            int sectorSize = GuessSectorSize(dataPath);
            int sectorCount = GuessSectorCount(dataPath, sectorSize);
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

            files.Add(new FileEntry(dataPath, sectorSize, sectorCount, 150, data));
        }

        var rom = new CdRom(files, cue);
        int totalSectors = 0;
        foreach (var file in files)
            totalSectors += file.SectorCount;
        var discEnd = new CdTime(0, 2, 0).AddFrames(totalSectors);
        cue.FinalizeEndTimes(discEnd);
        if (LogDisc)
        {
            Console.Error.WriteLine(
                $"[SCD-DISC] Open: cue='{path}' files={files.Count} preload={(preload ? 1 : 0)} " +
                $"tracks={cue.Tracks.Count} end={discEnd}");
        }

        return rom;
    }

    public bool ReadSector(CdTime time, Span<byte> buffer)
    {
        int absoluteFrames = time.ToFrames();
        if (absoluteFrames < 150)
        {
            if (TryWriteFakePregap(time, buffer))
                return true;
            buffer.Fill(0);
            return false;
        }

        if (!TryResolveFile(time, out FileEntry entry, out int lbaInFile))
        {
            if (TryWriteFakePregap(time, buffer))
                return true;
            buffer.Fill(0);
            return false;
        }

        long offset = (long)lbaInFile * entry.SectorSize;
        lock (_lock)
        {
            long dataLength = entry.Data?.Length ?? entry.Stream?.Length ?? 0;
            if (offset + entry.SectorSize > dataLength)
            {
                buffer.Fill(0);
                return false;
            }

            if (entry.Data != null)
            {
                int start = (int)offset;
                if (entry.SectorSize == 2352)
                {
                    entry.Data.AsSpan(start, entry.SectorSize).CopyTo(buffer);
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
                int msfFrames = absoluteFrames;
                int minutes = msfFrames / (60 * 75);
                int rem = msfFrames % (60 * 75);
                int seconds = rem / 75;
                int frames = rem % 75;

                buffer[12] = ToBcd(minutes);
                buffer[13] = ToBcd(seconds);
                buffer[14] = ToBcd(frames);
                buffer[15] = 0x01; // Mode 1

                entry.Data.AsSpan(start, 2048).CopyTo(buffer.Slice(16, 2048));
                return true;
            }

            entry.Stream!.Seek(offset, SeekOrigin.Begin);
            if (entry.SectorSize == 2352)
            {
                int read = entry.Stream.Read(buffer);
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
            int msfFrames2 = absoluteFrames;
            int minutes2 = msfFrames2 / (60 * 75);
            int rem2 = msfFrames2 % (60 * 75);
            int seconds2 = rem2 / 75;
            int frames2 = rem2 % 75;

            buffer[12] = ToBcd(minutes2);
            buffer[13] = ToBcd(seconds2);
            buffer[14] = ToBcd(frames2);
            buffer[15] = 0x01; // Mode 1

            Span<byte> data = buffer.Slice(16, 2048);
            int read2048 = entry.Stream.Read(data);
            if (read2048 != 2048)
            {
                if (read2048 > 0)
                    data.Slice(read2048).Fill(0);
                return false;
            }
            return true;
        }
    }

    private bool TryWriteFakePregap(CdTime time, Span<byte> buffer)
    {
        CdTrack? track = _cue.FindTrackByTime(time);
        if (track == null || track.TrackType != CdTrackType.Data)
            return false;

        if (time >= track.StartTime)
            return false;

        buffer.Fill(0);
        // Sync (12 bytes) - match jgenesis pregap filler.
        buffer[0] = 0x00;
        for (int i = 1; i < 11; i++)
            buffer[i] = 0x11;
        buffer[11] = 0x00;

        buffer[12] = ToBcd(time.Minutes);
        buffer[13] = ToBcd(time.Seconds);
        buffer[14] = ToBcd(time.Frames);
        buffer[15] = 0x01; // Mode 1
        return true;
    }

    public bool ReadSector(int trackNumber, CdTime relativeTime, Span<byte> buffer)
    {
        CdTrack track = _cue.Track(trackNumber);
        CdTime absolute = track.StartTime.AddFrames(relativeTime.ToFrames());
        return ReadSector(absolute, buffer);
    }

    private bool TryResolveFile(CdTime time, out FileEntry entry, out int lbaInFile)
    {
        int absoluteFrames = time.ToFrames();
        foreach (var file in _files)
        {
            int start = file.StartFrameAbs;
            int end = start + file.SectorCount;
            if (absoluteFrames >= start && absoluteFrames < end)
            {
                entry = file;
                lbaInFile = absoluteFrames - start;
                return true;
            }
        }

        entry = default;
        lbaInFile = 0;
        return false;
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
