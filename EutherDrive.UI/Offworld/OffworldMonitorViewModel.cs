using System;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;

namespace EutherDrive.UI.Offworld;

internal sealed class OffworldMonitorViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly IMarsTelemetryProvider _provider;
    private readonly DispatcherTimer _refreshTimer;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly TimeSpan _staleThreshold;
    private bool _initialized;
    private string _statusText = "OFFWORLD LINK: STANDBY";
    private string _solLabel = "SOL N/A";
    private string _temperatureLabel = "N/A";
    private string _pressureLabel = "N/A";
    private string _windLabel = "N/A";
    private string _seasonLabel = "N/A";
    private string _lastTelemetryLabel = "Awaiting telemetry.";
    private string _flavorLine = "Awaiting offworld handshake.";
    private string _sourceLabel = "NO LINK";
    private bool _isDataStale;
    private bool _disposed;

    public OffworldMonitorViewModel(IMarsTelemetryProvider provider, TimeSpan? refreshInterval = null)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        TimeSpan interval = refreshInterval ?? TimeSpan.FromMinutes(45);
        _staleThreshold = interval + interval;
        _refreshTimer = new DispatcherTimer { Interval = interval };
        _refreshTimer.Tick += OnRefreshTimerTick;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string SolLabel
    {
        get => _solLabel;
        private set => SetProperty(ref _solLabel, value);
    }

    public string TemperatureLabel
    {
        get => _temperatureLabel;
        private set => SetProperty(ref _temperatureLabel, value);
    }

    public string PressureLabel
    {
        get => _pressureLabel;
        private set => SetProperty(ref _pressureLabel, value);
    }

    public string WindLabel
    {
        get => _windLabel;
        private set => SetProperty(ref _windLabel, value);
    }

    public string SeasonLabel
    {
        get => _seasonLabel;
        private set => SetProperty(ref _seasonLabel, value);
    }

    public string LastTelemetryLabel
    {
        get => _lastTelemetryLabel;
        private set => SetProperty(ref _lastTelemetryLabel, value);
    }

    public string FlavorLine
    {
        get => _flavorLine;
        private set => SetProperty(ref _flavorLine, value);
    }

    public string SourceLabel
    {
        get => _sourceLabel;
        private set => SetProperty(ref _sourceLabel, value);
    }

    public bool IsDataStale
    {
        get => _isDataStale;
        private set => SetProperty(ref _isDataStale, value);
    }

    public async Task InitializeAsync()
    {
        if (_initialized || _disposed)
            return;

        _initialized = true;
        _refreshTimer.Start();
        await RefreshAsync();
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed)
            return;

        if (!await _refreshGate.WaitAsync(0, cancellationToken))
            return;

        try
        {
            MarsTelemetrySnapshot telemetry = await _provider.GetLatestMarsTelemetryAsync(cancellationToken);

            ApplySnapshot(telemetry);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            ApplySnapshot(MarsTelemetrySnapshot.Unavailable(DateTimeOffset.UtcNow, ex.Message));
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _refreshTimer.Stop();
        _refreshGate.Dispose();
    }

    public static OffworldMonitorViewModel CreatePreview()
    {
        MarsTelemetrySnapshot previewSnapshot = new()
        {
            Sol = 1434,
            TemperatureAverageC = -62.3,
            TemperatureMinimumC = -97.8,
            TemperatureMaximumC = -14.1,
            PressureAveragePa = 742.7,
            PressureMinimumPa = 728.2,
            PressureMaximumPa = 759.3,
            WindSpeedAverageMs = 4.3,
            WindSpeedMinimumMs = 0.6,
            WindSpeedMaximumMs = 16.4,
            WindCompassPoint = "SSW",
            Season = "winter",
            ObservationStartUtc = new DateTimeOffset(2022, 12, 10, 0, 48, 0, TimeSpan.Zero),
            ObservationEndUtc = new DateTimeOffset(2022, 12, 11, 1, 17, 0, TimeSpan.Zero),
            RetrievedAtUtc = DateTimeOffset.UtcNow,
            Origin = MarsTelemetryOrigin.Live,
            ProviderName = "NASA InSight",
            StatusDetail = "Preview offworld telemetry."
        };

        var viewModel = new OffworldMonitorViewModel(new StaticMarsTelemetryProvider(previewSnapshot), TimeSpan.FromHours(12));
        viewModel.ApplySnapshot(previewSnapshot);
        return viewModel;
    }

    private async void OnRefreshTimerTick(object? sender, EventArgs e)
    {
        try
        {
            await RefreshAsync();
        }
        catch
        {
            // Keep the panel quiet on background refresh faults; the last snapshot remains visible.
        }
    }

    private void ApplySnapshot(MarsTelemetrySnapshot snapshot)
    {
        bool observationArchive = snapshot.ObservationEndUtc.HasValue
            && DateTimeOffset.UtcNow - snapshot.ObservationEndUtc.Value > TimeSpan.FromDays(90);
        bool stale = snapshot.Origin == MarsTelemetryOrigin.Cache
            && DateTimeOffset.UtcNow - snapshot.RetrievedAtUtc > _staleThreshold;

        IsDataStale = stale;
        StatusText = BuildStatusText(snapshot, stale);
        SourceLabel = BuildSourceLabel(snapshot, stale);
        SolLabel = snapshot.Sol.HasValue ? $"SOL {snapshot.Sol.Value}" : "SOL N/A";
        TemperatureLabel = FormatMetric(snapshot.TemperatureAverageC, snapshot.TemperatureMinimumC, snapshot.TemperatureMaximumC, "°C");
        PressureLabel = FormatMetric(snapshot.PressureAveragePa, snapshot.PressureMinimumPa, snapshot.PressureMaximumPa, "Pa");
        WindLabel = FormatWind(snapshot.WindSpeedAverageMs, snapshot.WindSpeedMinimumMs, snapshot.WindSpeedMaximumMs, snapshot.WindCompassPoint);
        SeasonLabel = string.IsNullOrWhiteSpace(snapshot.Season)
            ? "N/A"
            : snapshot.Season.ToUpperInvariant();
        LastTelemetryLabel = BuildLastTelemetryLabel(snapshot);
        FlavorLine = BuildFlavorLine(snapshot, stale, observationArchive);
    }

    private static string BuildStatusText(MarsTelemetrySnapshot snapshot, bool stale)
    {
        if (!snapshot.HasAnyTelemetry)
            return "OFFWORLD LINK: DARK";

        return snapshot.Origin switch
        {
            MarsTelemetryOrigin.Live => "OFFWORLD LINK: ACTIVE",
            MarsTelemetryOrigin.Cache when stale => "TELEMETRY STALE",
            MarsTelemetryOrigin.Cache => "OFFWORLD LINK: CACHE RELAY",
            _ => "OFFWORLD LINK: DARK"
        };
    }

    private static string BuildSourceLabel(MarsTelemetrySnapshot snapshot, bool stale)
    {
        return snapshot.Origin switch
        {
            MarsTelemetryOrigin.Live => "LIVE FEED",
            MarsTelemetryOrigin.Cache when stale => "STALE CACHE",
            MarsTelemetryOrigin.Cache => "CACHE RELAY",
            _ => "NO LINK"
        };
    }

    private static string BuildFlavorLine(MarsTelemetrySnapshot snapshot, bool stale, bool observationArchive)
    {
        if (!snapshot.HasAnyTelemetry)
            return "Awaiting offworld handshake.";

        if (stale)
            return "TELEMETRY STALE";

        if (observationArchive)
            return "LAST SOL RECEIVED";

        return snapshot.Origin == MarsTelemetryOrigin.Live
            ? "OFFWORLD LINK: ACTIVE"
            : "CACHE RELAY ENGAGED";
    }

    private static string BuildLastTelemetryLabel(MarsTelemetrySnapshot snapshot)
    {
        string observed = snapshot.ObservationEndUtc.HasValue
            ? snapshot.ObservationEndUtc.Value.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture)
            : "N/A";
        string fetched = snapshot.RetrievedAtUtc.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);

        return $"Last telemetry {observed} • sync {fetched}";
    }

    private static string FormatMetric(double? average, double? minimum, double? maximum, string unit)
    {
        if (!average.HasValue && !minimum.HasValue && !maximum.HasValue)
            return "N/A";

        string averageText = average.HasValue
            ? $"{average.Value:0.0} {unit}"
            : "N/A";

        string? rangeText = FormatRange(minimum, maximum);
        return string.IsNullOrWhiteSpace(rangeText)
            ? averageText
            : $"{averageText} / {rangeText}";
    }

    private static string FormatWind(double? average, double? minimum, double? maximum, string? compassPoint)
    {
        if (!average.HasValue && !minimum.HasValue && !maximum.HasValue)
            return "N/A";

        string direction = string.IsNullOrWhiteSpace(compassPoint) ? string.Empty : $" {compassPoint}";
        string averageText = average.HasValue
            ? $"{average.Value:0.0} m/s{direction}"
            : "N/A";

        string? rangeText = FormatRange(minimum, maximum);
        return string.IsNullOrWhiteSpace(rangeText)
            ? averageText
            : $"{averageText} / {rangeText}";
    }

    private static string FormatNullableNumber(double? value)
        => value.HasValue ? value.Value.ToString("0.0", CultureInfo.InvariantCulture) : "N/A";

    private static string? FormatRange(double? minimum, double? maximum)
    {
        if (minimum.HasValue && maximum.HasValue)
            return $"{FormatNullableNumber(minimum)}..{FormatNullableNumber(maximum)}";
        if (minimum.HasValue)
            return $"lo {FormatNullableNumber(minimum)}";
        if (maximum.HasValue)
            return $"hi {FormatNullableNumber(maximum)}";
        return null;
    }

    private void SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (Equals(field, value))
            return;

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private sealed class StaticMarsTelemetryProvider : IMarsTelemetryProvider
    {
        private readonly MarsTelemetrySnapshot _snapshot;

        public StaticMarsTelemetryProvider(MarsTelemetrySnapshot snapshot)
        {
            _snapshot = snapshot;
        }

        public Task<MarsTelemetrySnapshot> GetLatestMarsTelemetryAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(_snapshot);
    }
}
