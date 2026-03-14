using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace EutherDrive.Core;

public static class CueSheetResolver
{
    private static readonly Regex s_trackNumberRegex = new(@"(?:track|trk|disc|disk|cd)\s*0*(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static string? ResolveFirstReferencedPath(string cuePath)
    {
        foreach (string referencedFile in EnumerateReferencedFiles(cuePath))
            return ResolveReferencedPath(cuePath, referencedFile);

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
