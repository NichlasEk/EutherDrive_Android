using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using ProjectPSX.IO;

namespace EutherDrive.Core.SegaCd;

internal readonly struct CdTime : IComparable<CdTime>
{
    public static readonly CdTime Zero = new(0, 0, 0);
    public static readonly CdTime TwoSeconds = new(0, 2, 0);

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

    public CdTime Add(CdTime other) => FromFrames(ToFrames() + other.ToFrames());

    public CdTime SaturatingSub(CdTime other)
    {
        int diff = ToFrames() - other.ToFrames();
        return diff <= 0 ? Zero : FromFrames(diff);
    }

    public CdTime Sub(CdTime other) => SaturatingSub(other);

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
    public CdTime PregapLen { get; set; }
    public CdTime PauseLen { get; set; }
    public CdTime PostgapLen { get; set; }
    public CdTime FileTime { get; set; }
    public int FileIndex { get; set; } = -1;

    public CdTime EffectiveStartTime() => StartTime.Add(PregapLen).Add(PauseLen);
}

internal sealed class CdCue
{
    private readonly List<CdTrack> _tracks = new();
    private readonly List<string> _files = new();

    public IReadOnlyList<CdTrack> Tracks => _tracks;
    public IReadOnlyList<string> Files => _files;

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
        if (time < first.StartTime)
            return first;

        foreach (var track in _tracks)
        {
            CdTime start = track.StartTime;
            if (time >= start && time < track.EndTime)
                return track;
        }
        return null;
    }

    public static CdCue FromIsoLength(int sectorCount)
    {
        var cue = new CdCue();
        var dataLen = CdTime.FromFrames(sectorCount);
        var end = CdTime.Zero.Add(CdTime.TwoSeconds).Add(dataLen).Add(CdTime.TwoSeconds);
        cue._tracks.Add(new CdTrack
        {
            Number = 1,
            TrackType = CdTrackType.Data,
            StartTime = CdTime.Zero,
            EndTime = end,
            PregapLen = CdTime.TwoSeconds,
            PauseLen = CdTime.Zero,
            PostgapLen = CdTime.TwoSeconds,
            FileTime = CdTime.Zero,
            FileIndex = 0
        });
        return cue;
    }

    public static CdCue Parse(string cuePath)
    {
        var cue = new CdCue();
        if (!VirtualFileSystem.Exists(cuePath))
            return cue;

        var parsedFiles = new List<ParsedFile>();
        ParsedFile? currentFile = null;
        ParsedTrack? currentTrack = null;
        foreach (var rawLine in ReadLines(cuePath))
        {
            string line = rawLine.Trim();
            if (line.Length == 0)
                continue;

            if (line.StartsWith("FILE", StringComparison.OrdinalIgnoreCase))
            {
                if (currentTrack != null)
                {
                    currentFile ??= new ParsedFile(string.Empty);
                    currentFile.Tracks.Add(currentTrack);
                    currentTrack = null;
                }
                if (currentFile != null)
                {
                    parsedFiles.Add(currentFile);
                    currentFile = null;
                }
                int firstQuote = line.IndexOf('"');
                if (firstQuote >= 0)
                {
                    int secondQuote = line.IndexOf('"', firstQuote + 1);
                    if (secondQuote > firstQuote)
                    {
                        string fileName = line.Substring(firstQuote + 1, secondQuote - firstQuote - 1);
                        string fullPath = ResolveCueFilePath(cuePath, fileName);
                        cue._files.Add(fullPath);
                        currentFile = new ParsedFile(fullPath);
                    }
                }
            }
            else if (line.StartsWith("TRACK", StringComparison.OrdinalIgnoreCase))
            {
                if (currentTrack != null)
                {
                    currentFile ??= new ParsedFile(string.Empty);
                    currentFile.Tracks.Add(currentTrack);
                }
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 3 && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int num))
                {
                    currentTrack = new ParsedTrack
                    {
                        Number = num,
                        TrackType = parts[2].StartsWith("AUDIO", StringComparison.OrdinalIgnoreCase) ? CdTrackType.Audio : CdTrackType.Data
                    };
                }
            }
            else if (line.StartsWith("INDEX 01", StringComparison.OrdinalIgnoreCase) && currentTrack != null)
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
                        currentTrack.TrackStart = new CdTime(mm, ss, ff);
                    }
                }
            }
            else if (line.StartsWith("INDEX 00", StringComparison.OrdinalIgnoreCase) && currentTrack != null)
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
                        currentTrack.PauseStart = new CdTime(mm, ss, ff);
                    }
                }
            }
            else if (line.StartsWith("PREGAP", StringComparison.OrdinalIgnoreCase) && currentTrack != null)
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2)
                {
                    var timeParts = parts[1].Split(':');
                    if (timeParts.Length == 3
                        && int.TryParse(timeParts[0], out int mm)
                        && int.TryParse(timeParts[1], out int ss)
                        && int.TryParse(timeParts[2], out int ff))
                    {
                        currentTrack.PregapLen = new CdTime(mm, ss, ff);
                    }
                }
            }
        }

        if (currentTrack != null)
        {
            currentFile ??= new ParsedFile(string.Empty);
            currentFile.Tracks.Add(currentTrack);
        }
        if (currentFile != null)
            parsedFiles.Add(currentFile);

        BuildTracksFromParsedFiles(cue, parsedFiles);

        return cue;
    }

    private static string ResolveCueFilePath(string cuePath, string fileName)
    {
        return CueSheetResolver.ResolveReferencedPath(cuePath, fileName);
    }

    private static IEnumerable<string> ReadLines(string path)
    {
        using var reader = new StreamReader(VirtualFileSystem.OpenRead(path));
        while (reader.ReadLine() is { } line)
            yield return line;
    }

    private static int GuessSectorSize(string path)
    {
        long length = VirtualFileSystem.GetLength(path);
        if (length % 2352 == 0)
            return 2352;
        return 2048;
    }

    private static int GuessSectorCount(string path, int? sectorSizeOverride = null)
    {
        long length = VirtualFileSystem.GetLength(path);
        int sectorSize = sectorSizeOverride ?? GuessSectorSize(path);
        if (sectorSize <= 0)
            return 0;
        return (int)(length / sectorSize);
    }

    private static void BuildTracksFromParsedFiles(CdCue cue, List<ParsedFile> files)
    {
        if (files.Count == 0)
            return;

        int absoluteStartFrames = 0;
        for (int fileIndex = 0; fileIndex < files.Count; fileIndex++)
        {
            ParsedFile file = files[fileIndex];
            if (string.IsNullOrWhiteSpace(file.Path))
                continue;
            if (!VirtualFileSystem.Exists(file.Path))
                continue;

            int sectorSize = GuessSectorSize(file.Path);
            int sectorCount = GuessSectorCount(file.Path, sectorSize);
            CdTime fileLen = CdTime.FromFrames(sectorCount);

            for (int i = 0; i < file.Tracks.Count; i++)
            {
                ParsedTrack track = file.Tracks[i];
                CdTime trackStart = track.TrackStart ?? CdTime.Zero;
                CdTime pauseStart = track.PauseStart ?? trackStart;
                CdTime pauseLen = trackStart.Sub(pauseStart);
                // Cue-backed discs still need the implicit 00:02:00 lead-in before the first
                // data track, but later track timing should come from INDEX 00/01 / PREGAP
                // rather than a synthetic extra data postgap.
                CdTime pregapLen =
                    track.TrackType == CdTrackType.Data && track.Number == 1
                        ? (track.PregapLen ?? CdTime.TwoSeconds)
                        : (track.PregapLen ?? CdTime.Zero);

                CdTime dataEndTime;
                if (i + 1 < file.Tracks.Count)
                {
                    ParsedTrack next = file.Tracks[i + 1];
                    dataEndTime = next.PauseStart ?? next.TrackStart ?? trackStart;
                }
                else
                {
                    dataEndTime = fileLen;
                }
                if (dataEndTime < trackStart)
                    dataEndTime = trackStart;

                CdTime dataLen = dataEndTime.Sub(trackStart);
                CdTime postgapLen = CdTime.Zero;
                CdTime paddedLen = pregapLen.Add(pauseLen).Add(dataLen).Add(postgapLen);

                CdTime startTime = CdTime.FromFrames(absoluteStartFrames);
                CdTime endTime = startTime.Add(paddedLen);

                cue._tracks.Add(new CdTrack
                {
                    Number = track.Number,
                    TrackType = track.TrackType,
                    StartTime = startTime,
                    EndTime = endTime,
                    PregapLen = pregapLen,
                    PauseLen = pauseLen,
                    PostgapLen = postgapLen,
                    FileTime = pauseStart,
                    FileIndex = fileIndex
                });

                absoluteStartFrames = endTime.ToFrames();
            }
        }

        if (cue._tracks.Count == 0)
            return;

        CdTrack last = cue._tracks[^1];
        if (last.PostgapLen.ToFrames() == 0)
        {
            last.PostgapLen = CdTime.TwoSeconds;
            last.EndTime = last.EndTime.Add(CdTime.TwoSeconds);
            cue._tracks[^1] = last;
        }
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
        public readonly Stream? Stream;
        public readonly byte[]? Data;

        public FileEntry(string path, int sectorSize, int sectorCount, int startFrameAbs, byte[]? data)
        {
            Path = path;
            SectorSize = sectorSize;
            SectorCount = sectorCount;
            StartFrameAbs = startFrameAbs;
            Data = data;
            Stream = data == null ? VirtualFileSystem.OpenRead(path) : null;
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
        if (cue.Files.Count > 0)
        {
            for (int i = 0; i < cue.Files.Count; i++)
            {
                string filePath = cue.Files[i];
                if (!VirtualFileSystem.Exists(filePath))
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
                        data = VirtualFileSystem.ReadAllBytes(filePath);
                    }
                    catch
                    {
                        data = null;
                    }
                }

                files.Add(new FileEntry(filePath, sectorSize, sectorCount, 0, data));
            }
        }
        else
        {
            if (!VirtualFileSystem.Exists(dataPath))
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
                    data = VirtualFileSystem.ReadAllBytes(dataPath);
                }
                catch
                {
                    data = null;
                }
            }

            files.Add(new FileEntry(dataPath, sectorSize, sectorCount, 0, data));
        }

        var rom = new CdRom(files, cue);
        int totalSectors = 0;
        foreach (var file in files)
            totalSectors += file.SectorCount;
        var discEnd = CdTime.TwoSeconds.AddFrames(totalSectors);
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
        CdTrack? track = _cue.FindTrackByTime(time);
        if (track == null)
        {
            buffer.Fill(0);
            return false;
        }

        CdTime relative = time.Sub(track.StartTime);
        return ReadSector(track.Number, relative, buffer);
    }

    public bool ReadSector(int trackNumber, CdTime relativeTime, Span<byte> buffer)
    {
        CdTrack track = _cue.Track(trackNumber);
        CdTime trackLen = track.EndTime.Sub(track.StartTime);
        CdTime dataEnd = trackLen.Sub(track.PostgapLen);

        if (relativeTime < track.PregapLen || relativeTime >= dataEnd)
        {
            if (track.TrackType == CdTrackType.Data)
                WriteFakeDataPregap(relativeTime, buffer);
            else
                buffer.Fill(0);
            return true;
        }

        int relativeFrames = relativeTime.Sub(track.PregapLen).ToFrames();
        int sectorNumber = relativeFrames;

        if (track.FileIndex < 0 || track.FileIndex >= _files.Count)
        {
            buffer.Fill(0);
            return false;
        }

        FileEntry entry = _files[track.FileIndex];
        int fileStartSector = track.FileTime.ToFrames();
        int lbaInFile = fileStartSector + sectorNumber;
        int absoluteFrames = track.StartTime.AddFrames(relativeTime.ToFrames()).ToFrames();
        return ReadSectorFromFile(entry, lbaInFile, buffer, absoluteFrames);
    }

    private bool ReadSectorFromFile(FileEntry entry, int lbaInFile, Span<byte> buffer, int absoluteFrames)
    {
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
                int minutes = absoluteFrames / (60 * 75);
                int rem = absoluteFrames % (60 * 75);
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
            int minutes2 = absoluteFrames / (60 * 75);
            int rem2 = absoluteFrames % (60 * 75);
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

    private static void WriteFakeDataPregap(CdTime time, Span<byte> buffer)
    {
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
    }

    private static int GuessSectorSize(string path)
    {
        long length = VirtualFileSystem.GetLength(path);
        if (length % 2352 == 0)
            return 2352;
        return 2048;
    }

    private static int GuessSectorCount(string path, int? sectorSizeOverride = null)
    {
        long length = VirtualFileSystem.GetLength(path);
        int sectorSize = sectorSizeOverride ?? GuessSectorSize(path);
        if (sectorSize <= 0)
            return 0;
        return (int)(length / sectorSize);
    }

    private static string? ResolveCueDataPath(string cuePath)
    {
        return CueSheetResolver.ResolveFirstReferencedPath(cuePath);
    }

    private static byte ToBcd(int value)
    {
        if (value < 0) value = 0;
        int tens = value / 10;
        int ones = value % 10;
        return (byte)((tens << 4) | ones);
    }
}

internal sealed class ParsedTrack
{
    public int Number { get; set; }
    public CdTrackType TrackType { get; set; }
    public CdTime? TrackStart { get; set; }
    public CdTime? PauseStart { get; set; }
    public CdTime? PregapLen { get; set; }
}

internal sealed class ParsedFile
{
    public string Path { get; }
    public List<ParsedTrack> Tracks { get; } = new();

    public ParsedFile(string path)
    {
        Path = path;
    }
}
