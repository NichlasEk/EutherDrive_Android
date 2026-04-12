using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace EutherDrive.UI.Offworld;

internal sealed class MarsWeatherService : IMarsTelemetryProvider
{
    private const string DefaultEndpoint =
        "https://api.nasa.gov/insight_weather/?api_key=DEMO_KEY&feedtype=json&ver=1.0";

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
        _refreshInterval = refreshInterval ?? TimeSpan.FromMinutes(45);

        if (_httpClient.Timeout == Timeout.InfiniteTimeSpan)
            _httpClient.Timeout = TimeSpan.FromSeconds(15);
    }

    public async Task<MarsTelemetrySnapshot> GetLatestMarsTelemetryAsync(CancellationToken cancellationToken = default)
    {
        MarsTelemetrySnapshot? cached = await TryReadCacheAsync(cancellationToken).ConfigureAwait(false);
        if (cached is not null && DateTimeOffset.UtcNow - cached.RetrievedAtUtc <= _refreshInterval)
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
                $"NASA offworld link failed. {ex.Message}");
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
        request.Headers.TryAddWithoutValidation("User-Agent", "EutherDrive/1.0 (Offworld Monitor)");
        request.Headers.TryAddWithoutValidation("Accept", "application/json");

        using HttpResponseMessage response = await _httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using Stream contentStream = await response.Content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);

        InSightWeatherApiResponseDto? apiResponse = await JsonSerializer
            .DeserializeAsync<InSightWeatherApiResponseDto>(contentStream, s_jsonOptions, cancellationToken)
            .ConfigureAwait(false);

        if (apiResponse == null)
            throw new InvalidOperationException("NASA returned an empty telemetry payload.");

        string? latestSolKey = apiResponse.GetLatestSolKey();
        if (string.IsNullOrWhiteSpace(latestSolKey))
            throw new InvalidOperationException("NASA telemetry payload did not include any usable sol keys.");

        if (!apiResponse.TryGetSol(latestSolKey, s_jsonOptions, out InSightSolDto? latestSol) || latestSol == null)
            throw new InvalidOperationException($"NASA telemetry payload did not include summary data for sol {latestSolKey}.");

        int? solNumber = int.TryParse(latestSolKey, out int parsedSol) ? parsedSol : null;
        return MapToSnapshot(solNumber, latestSol, DateTimeOffset.UtcNow);
    }

    private static MarsTelemetrySnapshot MapToSnapshot(int? solNumber, InSightSolDto sol, DateTimeOffset retrievedAtUtc)
    {
        // NASA omits whole sensor blocks when a sol does not pass validity checks,
        // so every AT/PRE/HWS access must tolerate null sensor objects.
        InSightSensorSummaryDto? temperature = sol.Temperature;
        InSightSensorSummaryDto? pressure = sol.Pressure;
        InSightSensorSummaryDto? wind = sol.HorizontalWindSpeed;

        // WD.most_common is documented to exist but can explicitly be null.
        string? windCompassPoint = sol.WindDirection?.MostCommon?.CompassPoint;

        return new MarsTelemetrySnapshot
        {
            Sol = solNumber,
            TemperatureAverageC = temperature?.Average,
            TemperatureMinimumC = temperature?.Minimum,
            TemperatureMaximumC = temperature?.Maximum,
            PressureAveragePa = pressure?.Average,
            PressureMinimumPa = pressure?.Minimum,
            PressureMaximumPa = pressure?.Maximum,
            WindSpeedAverageMs = wind?.Average,
            WindSpeedMinimumMs = wind?.Minimum,
            WindSpeedMaximumMs = wind?.Maximum,
            WindCompassPoint = string.IsNullOrWhiteSpace(windCompassPoint) ? null : windCompassPoint.Trim().ToUpperInvariant(),
            Season = string.IsNullOrWhiteSpace(sol.Season) ? null : sol.Season.Trim(),
            ObservationStartUtc = sol.FirstUtc,
            ObservationEndUtc = sol.LastUtc,
            RetrievedAtUtc = retrievedAtUtc,
            Origin = MarsTelemetryOrigin.Live,
            ProviderName = "NASA InSight",
            StatusDetail = "Live offworld telemetry received from NASA."
        };
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
