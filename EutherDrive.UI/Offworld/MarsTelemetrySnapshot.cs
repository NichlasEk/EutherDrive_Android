using System;

namespace EutherDrive.UI.Offworld;

internal enum MarsTelemetryOrigin
{
    Live,
    Cache,
    Unavailable
}

internal sealed record MarsTelemetrySnapshot
{
    public int? Sol { get; init; }
    public string? Location { get; init; }
    public double? TemperatureAverageC { get; init; }
    public double? TemperatureMinimumC { get; init; }
    public double? TemperatureMaximumC { get; init; }
    public double? PressureAveragePa { get; init; }
    public double? PressureMinimumPa { get; init; }
    public double? PressureMaximumPa { get; init; }
    public string? PressureStatus { get; init; }
    public double? WindSpeedAverageMs { get; init; }
    public double? WindSpeedMinimumMs { get; init; }
    public double? WindSpeedMaximumMs { get; init; }
    public string? WindCompassPoint { get; init; }
    public string? Season { get; init; }
    public double? SolarLongitudeDeg { get; init; }
    public DateTimeOffset? ObservationStartUtc { get; init; }
    public DateTimeOffset? ObservationEndUtc { get; init; }
    public DateTimeOffset RetrievedAtUtc { get; init; }
    public MarsTelemetryOrigin Origin { get; init; }
    public string ProviderName { get; init; } = "SpaceInformer Mars Weather";
    public string? StatusDetail { get; init; }

    public bool HasAnyTelemetry =>
        Sol.HasValue
        || TemperatureAverageC.HasValue
        || PressureAveragePa.HasValue
        || WindSpeedAverageMs.HasValue
        || !string.IsNullOrWhiteSpace(Season)
        || SolarLongitudeDeg.HasValue
        || ObservationEndUtc.HasValue;

    public static MarsTelemetrySnapshot Unavailable(DateTimeOffset retrievedAtUtc, string? statusDetail = null)
        => new()
        {
            RetrievedAtUtc = retrievedAtUtc,
            Origin = MarsTelemetryOrigin.Unavailable,
            StatusDetail = statusDetail ?? "No offworld telemetry received."
        };
}
