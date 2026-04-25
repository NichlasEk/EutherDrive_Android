using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace EutherDrive.UI.Offworld;

internal sealed class MarsWeatherService : IMarsTelemetryProvider
{
    private const string DefaultEndpoint =
        "https://spaceinformer.com/mars-temperature-live/";
    private const string ProviderName = "SpaceInformer Mars Weather";

    private static readonly Regex s_scriptBlockRegex = new("<(script|style)[^>]*>.*?</\\1>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex s_tagRegex = new("<[^>]+>", RegexOptions.Singleline);
    private static readonly Regex s_whitespaceRegex = new("\\s+", RegexOptions.Compiled);
    private static readonly Regex s_numberRegex = new("-?\\d+(?:\\.\\d+)?", RegexOptions.Compiled);

    private static readonly JsonSerializerOptions s_jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly HttpClient _httpClient;
    private readonly string _cachePath;
    private readonly TimeSpan _refreshInterval;

    public MarsWeatherService(HttpClient httpClient, string? cachePath = null, TimeSpan? refreshInterval = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _cachePath = string.IsNullOrWhiteSpace(cachePath) ? GetDefaultCachePath() : cachePath;
        _refreshInterval = refreshInterval ?? TimeSpan.FromMinutes(20);

        if (_httpClient.Timeout == Timeout.InfiniteTimeSpan)
            _httpClient.Timeout = TimeSpan.FromSeconds(15);
    }

    public async Task<MarsTelemetrySnapshot> GetLatestMarsTelemetryAsync(CancellationToken cancellationToken = default)
    {
        MarsTelemetrySnapshot? cached = await TryReadCacheAsync(cancellationToken).ConfigureAwait(false);
        if (cached is not null
            && string.Equals(cached.ProviderName, ProviderName, StringComparison.Ordinal)
            && DateTimeOffset.UtcNow - cached.RetrievedAtUtc <= _refreshInterval)
        {
            return cached with
            {
                Origin = MarsTelemetryOrigin.Cache,
                StatusDetail = "Cached telemetry replayed inside refresh window."
            };
        }

        try
        {
            MarsTelemetrySnapshot liveSnapshot = await FetchLiveAsync(cancellationToken).ConfigureAwait(false);
            await WriteCacheAsync(liveSnapshot, cancellationToken).ConfigureAwait(false);
            return liveSnapshot;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (cached is not null)
            {
                return cached with
                {
                    Origin = MarsTelemetryOrigin.Cache,
                    StatusDetail = $"Live offworld link failed; replaying cache. {ex.Message}"
                };
            }

            return MarsTelemetrySnapshot.Unavailable(
                DateTimeOffset.UtcNow,
                $"SpaceInformer offworld link failed. {ex.Message}");
        }
    }

    public static string GetDefaultCachePath()
    {
        string root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(root))
            root = Directory.GetCurrentDirectory();

        return Path.Combine(root, "EutherDrive", "Offworld", "mars-telemetry-cache.json");
    }

    private async Task<MarsTelemetrySnapshot> FetchLiveAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, DefaultEndpoint);
        request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) EutherDrive/1.0 Safari/537.36");
        request.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml");
        request.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
        request.Headers.TryAddWithoutValidation("Cache-Control", "no-cache");

        using HttpResponseMessage response = await _httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        string html = await response.Content
            .ReadAsStringAsync(cancellationToken)
            .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(html))
            throw new InvalidOperationException("SpaceInformer returned an empty telemetry payload.");
        if (html.Contains("Security Incident Detected", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("SpaceInformer blocked the offworld telemetry request.");

        return ParseSpaceInformerSnapshot(html, DateTimeOffset.UtcNow);
    }

    private static MarsTelemetrySnapshot ParseSpaceInformerSnapshot(string html, DateTimeOffset retrievedAtUtc)
    {
        IReadOnlyList<string> lines = ExtractTextLines(html);
        int surfaceTempIndex = IndexOfLine(lines, "Surface Temp");
        int pressureIndex = IndexOfLine(lines, "Atmospheric Pressure");
        int seasonIndex = IndexOfLine(lines, "Martian Season");

        string? location = FindLineValue(lines, "LOCATION:");
        string? solText = FindSolText(lines);
        string? pressureStatus = pressureIndex >= 0 ? NextUsefulLine(lines, pressureIndex + 2) : null;
        string? season = seasonIndex >= 0 ? NextUsefulLine(lines, seasonIndex + 1) : null;
        string? observerNote = FindLineValue(lines, "Observer Note:");

        return new MarsTelemetrySnapshot
        {
            Sol = ParseInt(solText),
            Location = NormalizeOptional(location),
            TemperatureAverageC = ParseDouble(surfaceTempIndex >= 0 ? NextUsefulLine(lines, surfaceTempIndex + 1) : null),
            TemperatureMinimumC = ParseTaggedDouble(lines, "MIN:"),
            TemperatureMaximumC = ParseTaggedDouble(lines, "MAX:"),
            PressureAveragePa = ParseDouble(pressureIndex >= 0 ? NextUsefulLine(lines, pressureIndex + 1) : null),
            PressureStatus = NormalizeOptional(pressureStatus),
            Season = NormalizeOptional(season),
            SolarLongitudeDeg = ParseTaggedDouble(lines, "Angle:"),
            RetrievedAtUtc = retrievedAtUtc,
            Origin = MarsTelemetryOrigin.Live,
            ProviderName = ProviderName,
            StatusDetail = NormalizeOptional(observerNote) ?? "Live offworld telemetry received from SpaceInformer."
        };
    }

    private static IReadOnlyList<string> ExtractTextLines(string html)
    {
        string withoutScripts = s_scriptBlockRegex.Replace(html, "\n");
        string text = s_tagRegex.Replace(withoutScripts, "\n");
        text = WebUtility.HtmlDecode(text);

        var lines = new List<string>();
        foreach (string rawLine in text.Split('\n'))
        {
            string line = s_whitespaceRegex.Replace(rawLine, " ").Trim();
            if (line.Length > 0)
                lines.Add(line);
        }

        return lines;
    }

    private static int IndexOfLine(IReadOnlyList<string> lines, string expected)
    {
        for (int i = 0; i < lines.Count; i++)
        {
            if (string.Equals(lines[i], expected, StringComparison.OrdinalIgnoreCase))
                return i;
        }

        return -1;
    }

    private static string? FindLineValue(IReadOnlyList<string> lines, string prefix)
    {
        foreach (string line in lines)
        {
            if (line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return line[prefix.Length..].Trim();
        }

        return null;
    }

    private static string? FindSolText(IReadOnlyList<string> lines)
    {
        foreach (string line in lines)
        {
            if (line.StartsWith("SOL ", StringComparison.OrdinalIgnoreCase))
                return line[4..].Trim();
        }

        return null;
    }

    private static string? NextUsefulLine(IReadOnlyList<string> lines, int startIndex)
    {
        for (int i = startIndex; i < lines.Count; i++)
        {
            string line = lines[i];
            if (!line.Equals("Loading...", StringComparison.OrdinalIgnoreCase)
                && !line.Equals("Loading...", StringComparison.Ordinal)
                && !line.Equals("Refresh Link", StringComparison.OrdinalIgnoreCase))
            {
                return line;
            }
        }

        return null;
    }

    private static double? ParseTaggedDouble(IReadOnlyList<string> lines, string tag)
    {
        foreach (string line in lines)
        {
            int tagIndex = line.IndexOf(tag, StringComparison.OrdinalIgnoreCase);
            if (tagIndex < 0)
                continue;

            return ParseDouble(line[(tagIndex + tag.Length)..]);
        }

        return null;
    }

    private static int? ParseInt(string? text)
    {
        double? value = ParseDouble(text);
        return value.HasValue ? (int)Math.Round(value.Value) : null;
    }

    private static double? ParseDouble(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        Match match = s_numberRegex.Match(text);
        if (!match.Success)
            return null;

        return double.TryParse(match.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            ? value
            : null;
    }

    private static string? NormalizeOptional(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        string trimmed = value.Trim();
        return trimmed is "—" or "-"
            ? null
            : trimmed;
    }

    private async Task<MarsTelemetrySnapshot?> TryReadCacheAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_cachePath))
            return null;

        try
        {
            await using FileStream stream = File.OpenRead(_cachePath);
            return await JsonSerializer
                .DeserializeAsync<MarsTelemetrySnapshot>(stream, s_jsonOptions, cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    private async Task WriteCacheAsync(MarsTelemetrySnapshot snapshot, CancellationToken cancellationToken)
    {
        string? directory = Path.GetDirectoryName(_cachePath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        string tempPath = _cachePath + ".tmp";
        await using (FileStream stream = File.Create(tempPath))
        {
            await JsonSerializer.SerializeAsync(stream, snapshot, s_jsonOptions, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        File.Move(tempPath, _cachePath, overwrite: true);
    }
}
