using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EutherDrive.UI.Offworld;

internal sealed class InSightWeatherApiResponseDto
{
    [JsonPropertyName("sol_keys")]
    public string[]? SolKeys { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalData { get; init; }

    public string? GetLatestSolKey()
    {
        IEnumerable<string> candidates = SolKeys is { Length: > 0 }
            ? SolKeys
            : AdditionalData?.Keys.Where(IsNumericSolKey) ?? [];

        return candidates
            .Select(key => (Key: key, Value: ParseSolNumber(key)))
            .Where(entry => entry.Value.HasValue)
            .OrderByDescending(entry => entry.Value!.Value)
            .Select(entry => entry.Key)
            .FirstOrDefault();
    }

    public bool TryGetSol(string solKey, JsonSerializerOptions serializerOptions, out InSightSolDto? sol)
    {
        sol = null;
        if (AdditionalData == null
            || !AdditionalData.TryGetValue(solKey, out JsonElement element)
            || element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        sol = element.Deserialize<InSightSolDto>(serializerOptions);
        return sol != null;
    }

    private static bool IsNumericSolKey(string key)
        => key.All(char.IsDigit);

    private static int? ParseSolNumber(string key)
        => int.TryParse(key, out int value) ? value : null;
}

internal sealed class InSightSolDto
{
    [JsonPropertyName("AT")]
    public InSightSensorSummaryDto? Temperature { get; init; }

    [JsonPropertyName("PRE")]
    public InSightSensorSummaryDto? Pressure { get; init; }

    [JsonPropertyName("HWS")]
    public InSightSensorSummaryDto? HorizontalWindSpeed { get; init; }

    [JsonPropertyName("WD")]
    public InSightWindDirectionDto? WindDirection { get; init; }

    [JsonPropertyName("Season")]
    public string? Season { get; init; }

    [JsonPropertyName("First_UTC")]
    public DateTimeOffset? FirstUtc { get; init; }

    [JsonPropertyName("Last_UTC")]
    public DateTimeOffset? LastUtc { get; init; }
}

internal sealed class InSightSensorSummaryDto
{
    [JsonPropertyName("av")]
    public double? Average { get; init; }

    [JsonPropertyName("mn")]
    public double? Minimum { get; init; }

    [JsonPropertyName("mx")]
    public double? Maximum { get; init; }

    [JsonPropertyName("ct")]
    public int? Count { get; init; }
}

internal sealed class InSightWindDirectionDto
{
    [JsonPropertyName("most_common")]
    public InSightCompassPointDto? MostCommon { get; init; }
}

internal sealed class InSightCompassPointDto
{
    [JsonPropertyName("compass_point")]
    public string? CompassPoint { get; init; }

    [JsonPropertyName("compass_degrees")]
    public double? CompassDegrees { get; init; }

    [JsonPropertyName("ct")]
    public int? Count { get; init; }
}
