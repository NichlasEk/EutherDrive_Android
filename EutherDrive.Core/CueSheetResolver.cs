using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace EutherDrive.Core;

public static class CueSheetResolver
{
    public sealed record CueTrackReference(
        string FilePath,
        string TrackType,
        int TrackNumber,
        int Index01Lba,
        long FileOffsetBytes,
        int SectorSize,
        int DataOffset);

    private static readonly Regex s_trackNumberRegex = new(@"(?:track|trk|disc|disk|cd)\s*0*(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static string? ResolveFirstReferencedPath(string cuePath)
    {
        foreach (string referencedFile in EnumerateReferencedFiles(cuePath))
            return ResolveReferencedPath(cuePath, referencedFile);

        return null;
    }

    public static CueTrackReference? ResolveFirstDataTrack(string cuePath)
    {
        if (string.IsNullOrWhiteSpace(cuePath) || !File.Exists(cuePath))
            return null;

        string baseDir = Path.GetDirectoryName(cuePath) ?? string.Empty;
        string? currentFilePath = null;
        string? currentTrackType = null;
        int currentTrackNumber = 0;
        int? currentFileBaseLba = null;

        foreach (string rawLine in File.ReadLines(cuePath))
        {
            string line = rawLine.Trim();
            if (line.Length == 0)
                continue;

            if (line.StartsWith("FILE", StringComparison.OrdinalIgnoreCase))
            {
                string? referencedFile = TryExtractReferencedFile(line);
                currentFilePath = string.IsNullOrWhiteSpace(referencedFile)
                    ? null
                    : ResolveReferencedPathFromDirectory(baseDir, referencedFile);
                currentTrackType = null;
                currentTrackNumber = 0;
                currentFileBaseLba = null;
                continue;
            }

            string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
                continue;

            if (parts[0].Equals("TRACK", StringComparison.OrdinalIgnoreCase))
            {
                currentTrackType = parts.Length >= 3 ? parts[2] : null;
                currentTrackNumber = parts.Length >= 2 && int.TryParse(parts[1], out int parsedTrackNumber)
                    ? parsedTrackNumber
                    : 0;
                continue;
            }

            if (!parts[0].Equals("INDEX", StringComparison.OrdinalIgnoreCase)
                || parts.Length < 3
                || !string.Equals(parts[1], "01", StringComparison.OrdinalIgnoreCase)
                || currentFilePath == null
                || currentTrackType == null
                || !TryParseMsf(parts[2], out int index01Lba))
            {
                continue;
            }

            currentFileBaseLba ??= index01Lba;
            if (!LooksLikeDataTrackType(currentTrackType))
                continue;

            int sectorSize = GetSectorSize(currentTrackType);
            long relativeLba = Math.Max(0, index01Lba - currentFileBaseLba.Value);
            return new CueTrackReference(
                currentFilePath,
                currentTrackType,
                currentTrackNumber,
                index01Lba,
                relativeLba * sectorSize,
                sectorSize,
                sectorSize == 2352 ? 16 : 0);
        }

        return null;
    }

    public static IEnumerable<string> EnumerateReferencedFiles(string cuePath)
    {
        if (string.IsNullOrWhiteSpace(cuePath) || !File.Exists(cuePath))
            yield break;

        foreach (string rawLine in File.ReadLines(cuePath))
        {
            string line = rawLine.Trim();
            if (!line.StartsWith("FILE", StringComparison.OrdinalIgnoreCase))
                continue;

            string? referenced = TryExtractReferencedFile(line);
            if (!string.IsNullOrWhiteSpace(referenced))
                yield return referenced;
        }
    }

    public static string ResolveReferencedPath(string cuePath, string referencedFile)
    {
        string baseDir = Path.GetDirectoryName(cuePath) ?? string.Empty;
        return ResolveReferencedPathFromDirectory(baseDir, referencedFile);
    }

    public static string ResolveReferencedPathFromDirectory(string baseDir, string referencedFile)
    {
        string combined = Path.GetFullPath(Path.Combine(baseDir, referencedFile));
        if (File.Exists(combined))
            return combined;

        string nameOnly = Path.GetFileName(referencedFile);
        string sibling = Path.GetFullPath(Path.Combine(baseDir, nameOnly));
        if (File.Exists(sibling))
            return sibling;

        string extension = Path.GetExtension(referencedFile);
        if (string.IsNullOrWhiteSpace(extension) || !Directory.Exists(baseDir))
            return combined;

        string[] candidates = Directory.GetFiles(baseDir, $"*{extension}");
        if (candidates.Length == 1)
            return Path.GetFullPath(candidates[0]);

        string? bestCandidate = FindBestCandidate(referencedFile, candidates);
        return bestCandidate ?? combined;
    }

    private static string? TryExtractReferencedFile(string line)
    {
        int firstQuote = line.IndexOf('"');
        if (firstQuote >= 0)
        {
            int secondQuote = line.IndexOf('"', firstQuote + 1);
            if (secondQuote > firstQuote)
                return line.Substring(firstQuote + 1, secondQuote - firstQuote - 1);
        }

        string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 ? parts[1] : null;
    }

    private static bool TryParseMsf(string value, out int lba)
    {
        lba = 0;
        string[] parts = value.Split(':', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3)
            return false;

        if (!int.TryParse(parts[0], out int minutes)
            || !int.TryParse(parts[1], out int seconds)
            || !int.TryParse(parts[2], out int frames))
        {
            return false;
        }

        lba = (minutes * 60 * 75) + (seconds * 75) + frames;
        return true;
    }

    private static bool LooksLikeDataTrackType(string trackType)
    {
        return trackType.StartsWith("MODE", StringComparison.OrdinalIgnoreCase);
    }

    private static int GetSectorSize(string trackType)
    {
        if (trackType.EndsWith("/2048", StringComparison.OrdinalIgnoreCase))
            return 2048;
        if (trackType.EndsWith("/2336", StringComparison.OrdinalIgnoreCase))
            return 2336;
        return 2352;
    }

    private static string? FindBestCandidate(string referencedFile, string[] candidates)
    {
        string referencedName = Path.GetFileName(referencedFile);
        string referencedStem = Path.GetFileNameWithoutExtension(referencedName);
        string referencedCanonical = CanonicalizeStem(referencedStem);
        int? referencedTrack = TryExtractTrackNumber(referencedStem);

        string? bestCandidate = null;
        int bestScore = int.MinValue;
        foreach (string candidate in candidates)
        {
            string candidateName = Path.GetFileName(candidate);
            string candidateStem = Path.GetFileNameWithoutExtension(candidateName);
            string candidateCanonical = CanonicalizeStem(candidateStem);
            int? candidateTrack = TryExtractTrackNumber(candidateStem);

            int score = 0;
            if (candidateName.Equals(referencedName, StringComparison.OrdinalIgnoreCase))
                score += 1000;
            if (candidateCanonical.Equals(referencedCanonical, StringComparison.Ordinal))
                score += 500;
            else if (!string.IsNullOrEmpty(referencedCanonical)
                     && !string.IsNullOrEmpty(candidateCanonical)
                     && (candidateCanonical.Contains(referencedCanonical, StringComparison.Ordinal)
                         || referencedCanonical.Contains(candidateCanonical, StringComparison.Ordinal)))
                score += 200;

            if (referencedTrack.HasValue && candidateTrack.HasValue)
            {
                if (referencedTrack.Value == candidateTrack.Value)
                    score += 400;
                else
                    score -= 250;
            }

            if (score > bestScore)
            {
                bestScore = score;
                bestCandidate = candidate;
            }
            else if (score == bestScore)
            {
                bestCandidate = null;
            }
        }

        return bestScore > 0 && bestCandidate != null ? Path.GetFullPath(bestCandidate) : null;
    }

    private static int? TryExtractTrackNumber(string stem)
    {
        Match match = s_trackNumberRegex.Match(stem);
        if (match.Success && int.TryParse(match.Groups[1].Value, out int track))
            return track;

        return null;
    }

    private static string CanonicalizeStem(string stem)
    {
        if (string.IsNullOrWhiteSpace(stem))
            return string.Empty;

        string withoutDecorations = StripDecorations(stem);
        var sb = new StringBuilder(withoutDecorations.Length);
        bool lastWasSpace = false;
        foreach (char ch in withoutDecorations)
        {
            char normalized = char.ToLowerInvariant(ch);
            if (char.IsLetterOrDigit(normalized))
            {
                sb.Append(normalized);
                lastWasSpace = false;
            }
            else if (!lastWasSpace)
            {
                sb.Append(' ');
                lastWasSpace = true;
            }
        }

        return sb.ToString().Trim();
    }

    private static string StripDecorations(string stem)
    {
        var sb = new StringBuilder(stem.Length);
        for (int i = 0; i < stem.Length; i++)
        {
            char ch = stem[i];
            if (ch is '(' or '[' or '{')
            {
                char closing = ch == '(' ? ')' : ch == '[' ? ']' : '}';
                int closingIndex = stem.IndexOf(closing, i + 1);
                if (closingIndex > i)
                {
                    string content = stem.Substring(i + 1, closingIndex - i - 1);
                    if (s_trackNumberRegex.IsMatch(content))
                    {
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
}
