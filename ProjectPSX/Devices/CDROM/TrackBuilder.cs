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
            public bool isAudio { get; private set; }

            public Track(String file, long size, byte number, int lba, int lbaStart, int lbaEnd, bool isAudio) {
                this.file = file;
                this.size = size;
                this.number = number;
                this.lba = lba;
                this.lbaStart = lbaStart;
                this.lbaEnd = lbaEnd;
                this.isAudio = isAudio;
            }
        }

        public static List<Track> fromCue(String cue) {
            Console.WriteLine($"[CD Track Builder] Generating CD Tracks from: {cue}");
            List<Track> tracks = new List<Track>();
            String dir = Path.GetDirectoryName(cue);
            String line;
            int lbaCounter = 0;
            byte number = 0;
            using StreamReader cueFile = new StreamReader(cue);
            while ((line = cueFile.ReadLine()) != null) {
                if (line.StartsWith("FILE")) {
                    String[] splittedSring = line.Split("\"");

                    String file = ResolveCueFilePath(cue, splittedSring[1]);
                    long size = new FileInfo(file).Length;
                    int lba = (int)(size / BytesPerSectorRaw);
                    int lbaStart = lbaCounter + 150;
                    number++;
                    //hardcoding :P
                    if (tracks.Count > 0) {
                        lbaStart += 150;
                    }

                    int lbaEnd = lbaCounter + lba;

                    lbaCounter += lba;

                    string trackTypeLine = cueFile.ReadLine();
                    bool isAudio = trackTypeLine.Contains("AUDIO");

                    tracks.Add(new Track(file, size, number, lba, lbaStart, lbaEnd, isAudio));

                    Console.WriteLine($"File: {file} Size: {size} Number: {number} LbaStart: {lbaStart} LbaEnd: {lbaEnd} isAudio {isAudio}");
                }
            }


            return tracks;
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
            int lba = (int)(size / BytesPerSectorRaw);
            int lbaStart = 150; // 150 frames (2 seconds) offset from track 1
            int lbaEnd = lba;
            byte number = 1;
            bool isAudio = false;

            tracks.Add(new Track(file, size, number, lba, lbaStart, lbaEnd, isAudio));

            Console.WriteLine($"File: {file} Size: {size} Number: {number} LbaStart: {lbaStart} LbaEnd: {lbaEnd} isAudio {isAudio}");

            return tracks;
        }
    }
}
