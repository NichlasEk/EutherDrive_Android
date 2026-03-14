using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace ProjectPSX.Devices.CdRom {
    public class TrackBuilder {

        private const int BytesPerSectorRaw = 2352;
        private static readonly Regex TrackNumberRegex = new(@"(?:track|trk|disc|disk|cd)\s*0*(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public class Track {

            public String file { get; private set; }
            public long size { get; private set; }
            public byte number { get; private set; }
            public int lba { get; private set; }
            public int lbaStart { get; private set; }
            public int lbaEnd { get; private set; }
            public int fileStartSector { get; private set; }
            public bool isAudio { get; private set; }

            public Track(String file, long size, byte number, int lba, int lbaStart, int lbaEnd, int fileStartSector, bool isAudio) {
                this.file = file;
                this.size = size;
                this.number = number;
                this.lba = lba;
                this.lbaStart = lbaStart;
                this.lbaEnd = lbaEnd;
                this.fileStartSector = fileStartSector;
                this.isAudio = isAudio;
            }
        }

        private sealed class CueTrackEntry {
            public string File { get; set; } = string.Empty;
            public long FileSize { get; set; }
            public byte Number { get; set; }
            public bool IsAudio { get; set; }
            public int Pregap { get; set; }
            public int Index00 { get; set; } = -1;
            public int Index01 { get; set; }
        }

        public static List<Track> fromCue(String cue) {
            Console.WriteLine($"[CD Track Builder] Generating CD Tracks from: {cue}");
            var cueTracks = new List<CueTrackEntry>();
            string? currentFile = null;
            long currentFileSize = 0;
            CueTrackEntry? currentTrack = null;

            using StreamReader cueFile = new StreamReader(cue);
            string? line;
            while ((line = cueFile.ReadLine()) != null) {
                line = line.Trim();
                if (string.IsNullOrWhiteSpace(line)) {
                    continue;
                }

                if (line.StartsWith("FILE", StringComparison.OrdinalIgnoreCase)) {
                    string referencedFile = ParseCueFile(line);
                    currentFile = ResolveCueFilePath(cue, referencedFile);
                    EnsureSupportedTrackFile(currentFile);
                    currentFileSize = new FileInfo(currentFile).Length;
                    currentTrack = null;
                    continue;
                }

                if (line.StartsWith("TRACK", StringComparison.OrdinalIgnoreCase)) {
                    if (currentFile == null) {
                        throw new InvalidDataException($"TRACK entry without FILE in cue: {cue}");
                    }

                    string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 3 || !byte.TryParse(parts[1], out byte trackNumber)) {
                        throw new InvalidDataException($"Invalid TRACK line in cue: {line}");
                    }

                    currentTrack = new CueTrackEntry {
                        File = currentFile,
                        FileSize = currentFileSize,
                        Number = trackNumber,
                        IsAudio = line.Contains("AUDIO", StringComparison.OrdinalIgnoreCase),
                        Pregap = 0,
                        Index01 = 0
                    };
                    cueTracks.Add(currentTrack);
                    continue;
                }

                if (currentTrack == null) {
                    continue;
                }

                if (line.StartsWith("PREGAP", StringComparison.OrdinalIgnoreCase)) {
                    currentTrack.Pregap = ParseMsf(line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1]);
                    continue;
                }

                if (line.StartsWith("INDEX 00", StringComparison.OrdinalIgnoreCase)) {
                    currentTrack.Index00 = ParseMsf(line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[2]);
                    continue;
                }

                if (line.StartsWith("INDEX 01", StringComparison.OrdinalIgnoreCase)) {
                    currentTrack.Index01 = ParseMsf(line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[2]);
                }
            }

            var tracks = new List<Track>(cueTracks.Count);
            int discCursor = 150;
            for (int i = 0; i < cueTracks.Count; i++) {
                CueTrackEntry cueTrack = cueTracks[i];
                CueTrackEntry? nextTrack = i + 1 < cueTracks.Count ? cueTracks[i + 1] : null;

                ValidateSectorAlignedFile(cueTrack.File, cueTrack.FileSize);
                int fileSectorCount = checked((int)(cueTrack.FileSize / BytesPerSectorRaw));

                bool hasIndex00 = cueTrack.Index00 >= 0 && cueTrack.Index01 >= cueTrack.Index00;
                int pregapSectors = hasIndex00
                    ? cueTrack.Index01 - cueTrack.Index00
                    : cueTrack.Pregap;
                int filePregapStartSector = hasIndex00
                    ? cueTrack.Index00
                    : Math.Max(0, cueTrack.Index01 - cueTrack.Pregap);

                int nextFileBoundary = fileSectorCount;
                if (nextTrack is not null && nextTrack.File.Equals(cueTrack.File, StringComparison.OrdinalIgnoreCase)) {
                    nextFileBoundary = nextTrack.Index00 >= 0 ? nextTrack.Index00 : nextTrack.Index01;
                }

                int discSpanLba = Math.Max(0, nextFileBoundary - filePregapStartSector);
                int lbaStart = discCursor + pregapSectors;
                int lbaEnd = discSpanLba > 0 ? discCursor + discSpanLba - 1 : discCursor - 1;

                tracks.Add(new Track(
                    cueTrack.File,
                    cueTrack.FileSize,
                    cueTrack.Number,
                    discSpanLba,
                    lbaStart,
                    lbaEnd,
                    cueTrack.Index01,
                    cueTrack.IsAudio));

                Console.WriteLine($"File: {cueTrack.File} Size: {cueTrack.FileSize} Number: {cueTrack.Number} LbaStart: {lbaStart} LbaEnd: {lbaEnd} fileStartSector: {cueTrack.Index01} isAudio {cueTrack.IsAudio}");

                discCursor += discSpanLba;
            }

            return tracks;
        }

        private static string ParseCueFile(string line) {
            int firstQuote = line.IndexOf('"');
            if (firstQuote >= 0) {
                int secondQuote = line.IndexOf('"', firstQuote + 1);
                if (secondQuote > firstQuote) {
                    return line.Substring(firstQuote + 1, secondQuote - firstQuote - 1);
                }
            }

            string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2) {
                return parts[1];
            }

            throw new InvalidDataException($"Invalid FILE line in cue: {line}");
        }

        private static int ParseMsf(string msf) {
            string[] parts = msf.Split(':', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 3
                || !int.TryParse(parts[0], out int minutes)
                || !int.TryParse(parts[1], out int seconds)
                || !int.TryParse(parts[2], out int frames)) {
                throw new InvalidDataException($"Invalid MSF value in cue: {msf}");
            }

            return (minutes * 60 * 75) + (seconds * 75) + frames;
        }

        private static void EnsureSupportedTrackFile(string file) {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            Span<byte> header = stackalloc byte[4];
            int read = stream.Read(header);
            if (read == 4 && header[0] == (byte)'E' && header[1] == (byte)'C' && header[2] == (byte)'M' && header[3] == 0x00) {
                throw new InvalidDataException($"ECM-compressed CD image is not supported directly: {file}");
            }
        }

        private static void ValidateSectorAlignedFile(string file, long size) {
            if (size % BytesPerSectorRaw != 0) {
                throw new InvalidDataException($"CD track file is not sector-aligned raw 2352 data: {file}");
            }
        }

        private static string ResolveCueFilePath(string cuePath, string referencedFile) {
            string dir = Path.GetDirectoryName(cuePath) ?? string.Empty;
            string combined = Path.GetFullPath(Path.Combine(dir, referencedFile));
            if (File.Exists(combined)) {
                return combined;
            }

            string nameOnly = Path.GetFileName(referencedFile);
            string sibling = Path.GetFullPath(Path.Combine(dir, nameOnly));
            if (File.Exists(sibling)) {
                return sibling;
            }

            string extension = Path.GetExtension(referencedFile);
            if (string.IsNullOrWhiteSpace(extension) || !Directory.Exists(dir)) {
                return combined;
            }

            string[] candidates = Directory.GetFiles(dir, $"*{extension}");
            if (candidates.Length == 1) {
                return Path.GetFullPath(candidates[0]);
            }

            string? bestCandidate = FindBestCandidate(referencedFile, candidates);
            if (bestCandidate != null) {
                return bestCandidate;
            }

            return combined;
        }

        private static string? FindBestCandidate(string referencedFile, string[] candidates) {
            string referencedName = Path.GetFileName(referencedFile);
            string referencedStem = Path.GetFileNameWithoutExtension(referencedName);
            string referencedCanonical = CanonicalizeStem(referencedStem);
            int? referencedTrack = TryExtractTrackNumber(referencedStem);

            string? bestCandidate = null;
            int bestScore = int.MinValue;
            foreach (string candidate in candidates) {
                string candidateName = Path.GetFileName(candidate);
                string candidateStem = Path.GetFileNameWithoutExtension(candidateName);
                string candidateCanonical = CanonicalizeStem(candidateStem);
                int? candidateTrack = TryExtractTrackNumber(candidateStem);

                int score = 0;
                if (candidateName.Equals(referencedName, StringComparison.OrdinalIgnoreCase)) {
                    score += 1000;
                }
                if (candidateCanonical.Equals(referencedCanonical, StringComparison.Ordinal)) {
                    score += 500;
                } else if (!string.IsNullOrEmpty(referencedCanonical)
                           && !string.IsNullOrEmpty(candidateCanonical)
                           && (candidateCanonical.Contains(referencedCanonical, StringComparison.Ordinal)
                               || referencedCanonical.Contains(candidateCanonical, StringComparison.Ordinal))) {
                    score += 200;
                }

                if (referencedTrack.HasValue && candidateTrack.HasValue) {
                    if (referencedTrack.Value == candidateTrack.Value) {
                        score += 400;
                    } else {
                        score -= 250;
                    }
                }

                if (score > bestScore) {
                    bestScore = score;
                    bestCandidate = candidate;
                } else if (score == bestScore) {
                    bestCandidate = null;
                }
            }

            return bestScore > 0 && bestCandidate != null ? Path.GetFullPath(bestCandidate) : null;
        }

        private static int? TryExtractTrackNumber(string stem) {
            Match match = TrackNumberRegex.Match(stem);
            if (match.Success && int.TryParse(match.Groups[1].Value, out int track)) {
                return track;
            }

            return null;
        }

        private static string CanonicalizeStem(string stem) {
            if (string.IsNullOrWhiteSpace(stem)) {
                return string.Empty;
            }

            string withoutDecorations = StripDecorations(stem);
            var sb = new StringBuilder(withoutDecorations.Length);
            bool lastWasSpace = false;
            foreach (char ch in withoutDecorations) {
                char normalized = char.ToLowerInvariant(ch);
                if (char.IsLetterOrDigit(normalized)) {
                    sb.Append(normalized);
                    lastWasSpace = false;
                } else if (!lastWasSpace) {
                    sb.Append(' ');
                    lastWasSpace = true;
                }
            }

            return sb.ToString().Trim();
        }

        private static string StripDecorations(string stem) {
            var sb = new StringBuilder(stem.Length);
            for (int i = 0; i < stem.Length; i++) {
                char ch = stem[i];
                if (ch == '(' || ch == '[' || ch == '{') {
                    char closing = ch == '(' ? ')' : ch == '[' ? ']' : '}';
                    int closingIndex = stem.IndexOf(closing, i + 1);
                    if (closingIndex > i) {
                        string content = stem.Substring(i + 1, closingIndex - i - 1);
                        if (TrackNumberRegex.IsMatch(content)) {
                            sb.Append(' ');
                            sb.Append(content);
                            sb.Append(' ');
                        }
                        i = closingIndex;
                        continue;
                    }
                }

                sb.Append(ch);
            }

            return sb.ToString();
        }

        public static List<Track> fromBin(string file) {
            Console.WriteLine($"[CD Track Builder] Generating CD Track from: {file}");
            List<Track> tracks = new List<Track>();

            long size = new FileInfo(file).Length;
            ValidateSectorAlignedFile(file, size);
            int lba = (int)(size / BytesPerSectorRaw);
            int lbaStart = 150; // 150 frames (2 seconds) offset from track 1
            int lbaEnd = lbaStart + lba - 1;
            byte number = 1;
            bool isAudio = false;

            tracks.Add(new Track(file, size, number, lba, lbaStart, lbaEnd, 0, isAudio));

            Console.WriteLine($"File: {file} Size: {size} Number: {number} LbaStart: {lbaStart} LbaEnd: {lbaEnd} isAudio {isAudio}");

            return tracks;
        }
    }
}
