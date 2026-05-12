using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using EutherDrive.Audio;
using EutherDrive.UI.Audio;
using NLayer;

namespace EutherDrive.UI.Ambient;

internal readonly record struct AmbientMusicSnapshot(
    bool IsActive,
    bool IsBusy,
    string TrackTitle,
    string StatusText,
    string? CoverPath);

internal sealed class AmbientMusicController : IDisposable
{
    private sealed record CachedAmbientTrack(AmbientTrackInfo Track, string AudioPath, string CoverPath);

    private static readonly HttpClient s_httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(45)
    };

    private const int PlaylistTrackCount = 5;
    private const int OutputChannels = 2;
    private const int OutputSampleRate = 44100;
    private const int FramesPerBatch = 1024;
    private const int BufferFrames = 8192;
    private const int TargetBufferedFrames = 4096;

    private static readonly string? AudioSinkEnv =
        Environment.GetEnvironmentVariable("EUTHERDRIVE_AUDIO_SINK");

    private readonly object _lock = new();
    private readonly string _cacheRoot;
    private readonly HashSet<string> _lastSelection = new(StringComparer.OrdinalIgnoreCase);
    private List<CachedAmbientTrack> _playlist = [];
    private CancellationTokenSource? _playbackCts;
    private Task? _playbackTask;
    private AudioEngine? _audioEngine;
    private IAudioSink? _audioSink;
    private short[]? _decodedTrackPcm;
    private int _decodedTrackIndex = -1;
    private int _currentTrackIndex;
    private int _currentSampleIndex;
    private bool _isActive;
    private bool _isBusy;
    private bool _audioEnabled = true;
    private bool _romActive;
    private int _masterVolumePercent = 50;
    private string _trackTitle = "Cyberpunk Ambient";
    private string _statusText = "Ambient off.";
    private string? _coverPath;
    private bool _disposed;

    public AmbientMusicController(string cacheRoot)
    {
        _cacheRoot = cacheRoot ?? throw new ArgumentNullException(nameof(cacheRoot));
    }

    public event EventHandler? StateChanged;

    public AmbientMusicSnapshot GetSnapshot()
    {
        lock (_lock)
        {
            return new AmbientMusicSnapshot(
                _isActive,
                _isBusy,
                _trackTitle,
                _statusText,
                _coverPath);
        }
    }

    public void SetMasterVolumePercent(int percent)
    {
        lock (_lock)
            _masterVolumePercent = Math.Clamp(percent, 0, 200);
    }

    public void SetAudioEnabled(bool audioEnabled)
    {
        bool changed;
        lock (_lock)
        {
            changed = _audioEnabled != audioEnabled;
            _audioEnabled = audioEnabled;
        }

        if (changed)
            _ = UpdatePlaybackStateAsync();
    }

    public Task SetRomActiveAsync(bool romActive)
    {
        bool changed;
        lock (_lock)
        {
            changed = _romActive != romActive;
            _romActive = romActive;
        }

        return changed ? UpdatePlaybackStateAsync() : Task.CompletedTask;
    }

    public async Task ToggleAsync()
    {
        bool shouldDeactivate;
        lock (_lock)
            shouldDeactivate = _isActive && !_isBusy;

        if (shouldDeactivate)
        {
            await DeactivateAsync().ConfigureAwait(false);
            return;
        }

        await ActivateAsync().ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        try
        {
            StopPlaybackAsync(clearTrackState: true).GetAwaiter().GetResult();
        }
        catch
        {
            // Ignore shutdown failures.
        }
    }

    private async Task ActivateAsync()
    {
        lock (_lock)
        {
            if (_disposed || _isBusy)
                return;

            _isBusy = true;
            _statusText = "Downloading 5 ambient tracks...";
            _trackTitle = "Cyberpunk Ambient";
            _coverPath = null;
        }

        NotifyStateChanged();
        await StopPlaybackAsync(clearTrackState: true).ConfigureAwait(false);

        try
        {
            AmbientTrackInfo[] selection = await SelectRandomTracksAsync().ConfigureAwait(false);
            PrepareCacheRoot();

            var playlist = new List<CachedAmbientTrack>(selection.Length);
            for (int i = 0; i < selection.Length; i++)
            {
                AmbientTrackInfo track = selection[i];
                string title = FormatTrackTitle(track.Title);
                lock (_lock)
                {
                    _statusText = $"Downloading {i + 1}/{selection.Length}: {title}";
                    _trackTitle = title;
                    _coverPath = null;
                }

                NotifyStateChanged();

                string baseName = $"{i + 1:00}-{GetSafeFileStem(track.Title)}";
                string audioPath = Path.Combine(_cacheRoot, baseName + ".mp3");
                string coverPath = Path.Combine(_cacheRoot, baseName + ".jpg");

                await DownloadFileAsync(track.Mp3Url, audioPath).ConfigureAwait(false);
                await DownloadFileAsync(track.CoverUrl, coverPath).ConfigureAwait(false);

                playlist.Add(new CachedAmbientTrack(track, audioPath, coverPath));
            }

            lock (_lock)
            {
                _playlist = playlist;
                _lastSelection.Clear();
                foreach (CachedAmbientTrack track in playlist)
                    _lastSelection.Add(track.Track.DownloadPath);

                _isActive = true;
                _currentTrackIndex = 0;
                _currentSampleIndex = 0;
                _decodedTrackPcm = null;
                _decodedTrackIndex = -1;
                UpdateSnapshotForCurrentTrackLocked();
            }
        }
        catch (Exception ex)
        {
            lock (_lock)
            {
                _playlist.Clear();
                _isActive = false;
                _trackTitle = "Cyberpunk Ambient";
                _coverPath = null;
                _statusText = $"Ambient failed: {ex.Message}";
            }
        }
        finally
        {
            lock (_lock)
            {
                _isBusy = false;
                UpdateSnapshotForCurrentTrackLocked();
            }

            NotifyStateChanged();
        }

        await UpdatePlaybackStateAsync().ConfigureAwait(false);
    }

    private async Task DeactivateAsync()
    {
        lock (_lock)
        {
            if (_disposed)
                return;

            _isActive = false;
            _isBusy = false;
        }

        await StopPlaybackAsync(clearTrackState: true).ConfigureAwait(false);

        lock (_lock)
        {
            _playlist.Clear();
            _trackTitle = "Cyberpunk Ambient";
            _coverPath = null;
            _statusText = "Ambient off.";
        }

        NotifyStateChanged();
    }

    private async Task UpdatePlaybackStateAsync()
    {
        bool shouldPlay;
        lock (_lock)
        {
            shouldPlay = _isActive
                && !_isBusy
                && _audioEnabled
                && _playlist.Count > 0;
        }

        if (!shouldPlay)
        {
            await StopPlaybackAsync(clearTrackState: false).ConfigureAwait(false);
            lock (_lock)
                UpdateSnapshotForCurrentTrackLocked();
            NotifyStateChanged();
            return;
        }

        await EnsurePlaybackRunningAsync().ConfigureAwait(false);
    }

    private async Task EnsurePlaybackRunningAsync()
    {
        Task? runningTask;
        lock (_lock)
            runningTask = _playbackTask;

        if (runningTask != null && !runningTask.IsCompleted)
        {
            lock (_lock)
                UpdateSnapshotForCurrentTrackLocked();
            NotifyStateChanged();
            return;
        }

        await StopPlaybackAsync(clearTrackState: false).ConfigureAwait(false);

        var cts = new CancellationTokenSource();
        IAudioSink sink = CreateAudioSink(AudioSinkEnv);
        var engine = new AudioEngine(sink, OutputSampleRate, OutputChannels, framesPerBatch: FramesPerBatch, bufferFrames: BufferFrames);
        engine.SetTargetBufferedFrames(TargetBufferedFrames);
        engine.Start();

        lock (_lock)
        {
            if (_disposed || !_isActive || _playlist.Count == 0 || _audioEnabled == false)
            {
                engine.Stop();
                sink.Dispose();
                cts.Dispose();
                UpdateSnapshotForCurrentTrackLocked();
                NotifyStateChanged();
                return;
            }

            _playbackCts = cts;
            _audioSink = sink;
            _audioEngine = engine;
            _playbackTask = Task.Run(() => PlaybackLoopAsync(cts.Token), cts.Token);
            UpdateSnapshotForCurrentTrackLocked();
        }

        NotifyStateChanged();
    }

    private async Task StopPlaybackAsync(bool clearTrackState)
    {
        CancellationTokenSource? cts;
        Task? playbackTask;
        AudioEngine? engine;
        IAudioSink? sink;

        lock (_lock)
        {
            cts = _playbackCts;
            playbackTask = _playbackTask;
            engine = _audioEngine;
            sink = _audioSink;
            _playbackCts = null;
            _playbackTask = null;
            _audioEngine = null;
            _audioSink = null;
        }

        cts?.Cancel();
        engine?.Stop();
        sink?.Dispose();
        cts?.Dispose();

        if (playbackTask != null)
        {
            try
            {
                await playbackTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch
            {
            }
        }

        lock (_lock)
        {
            if (clearTrackState)
            {
                _decodedTrackPcm = null;
                _decodedTrackIndex = -1;
                _currentTrackIndex = 0;
                _currentSampleIndex = 0;
            }
        }
    }

    private async Task PlaybackLoopAsync(CancellationToken cancellationToken)
    {
        short[] mixBuffer = new short[FramesPerBatch * OutputChannels];

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                CachedAmbientTrack currentTrack;
                int currentTrackIndex;
                int sampleIndex;
                short[]? decodedTrack;
                AudioEngine? engine;

                lock (_lock)
                {
                    if (_playlist.Count == 0)
                        return;

                    currentTrackIndex = Math.Clamp(_currentTrackIndex, 0, _playlist.Count - 1);
                    currentTrack = _playlist[currentTrackIndex];
                    sampleIndex = _currentSampleIndex;
                    decodedTrack = _decodedTrackIndex == currentTrackIndex ? _decodedTrackPcm : null;
                    engine = _audioEngine;
                }

                if (engine == null)
                    return;

                if (decodedTrack == null)
                {
                    await DecodeTrackAsync(currentTrackIndex, currentTrack, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (sampleIndex >= decodedTrack.Length)
                {
                    AdvanceTrack();
                    continue;
                }

                if (engine.BufferedFrames >= TargetBufferedFrames)
                {
                    await Task.Delay(12, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                int samplesToCopy = Math.Min(mixBuffer.Length, decodedTrack.Length - sampleIndex);
                ApplyVolume(decodedTrack.AsSpan(sampleIndex, samplesToCopy), mixBuffer.AsSpan(0, samplesToCopy));
                engine.Submit(mixBuffer.AsSpan(0, samplesToCopy));

                bool trackEnded;
                lock (_lock)
                {
                    if (_decodedTrackIndex == currentTrackIndex && _currentSampleIndex == sampleIndex)
                        _currentSampleIndex += samplesToCopy;

                    trackEnded = _decodedTrackIndex == currentTrackIndex
                        && _decodedTrackPcm != null
                        && _currentSampleIndex >= _decodedTrackPcm.Length;
                }

                if (trackEnded)
                    AdvanceTrack();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            lock (_lock)
                _statusText = $"Ambient error: {ex.Message}";
            NotifyStateChanged();
        }
    }

    private async Task DecodeTrackAsync(int trackIndex, CachedAmbientTrack track, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _trackTitle = FormatTrackTitle(track.Track.Title);
            _coverPath = track.CoverPath;
            _statusText = $"Decoding {_trackTitle}...";
        }

        NotifyStateChanged();

        short[] pcm = await Task.Run(() => DecodeMp3ToStereoPcm(track.AudioPath, OutputSampleRate), cancellationToken).ConfigureAwait(false);
        if (pcm.Length == 0)
            throw new InvalidOperationException($"Track decode returned no samples for {track.Track.Title}.");

        lock (_lock)
        {
            if (_disposed || !_isActive || trackIndex != _currentTrackIndex || _playlist.Count == 0)
                return;

            _decodedTrackPcm = pcm;
            _decodedTrackIndex = trackIndex;
            UpdateSnapshotForCurrentTrackLocked();
        }

        NotifyStateChanged();
    }

    private void AdvanceTrack()
    {
        lock (_lock)
        {
            if (_playlist.Count == 0)
                return;

            _currentTrackIndex = (_currentTrackIndex + 1) % _playlist.Count;
            _currentSampleIndex = 0;
            _decodedTrackIndex = -1;
            _decodedTrackPcm = null;
            UpdateSnapshotForCurrentTrackLocked();
        }

        NotifyStateChanged();
    }

    private void UpdateSnapshotForCurrentTrackLocked()
    {
        if (!_isActive)
        {
            _trackTitle = "Cyberpunk Ambient";
            _coverPath = null;
            _statusText = "Ambient off.";
            return;
        }

        if (_playlist.Count > 0)
        {
            CachedAmbientTrack current = _playlist[Math.Clamp(_currentTrackIndex, 0, _playlist.Count - 1)];
            _trackTitle = FormatTrackTitle(current.Track.Title);
            _coverPath = current.CoverPath;
        }
        else if (!_isBusy)
        {
            _trackTitle = "Cyberpunk Ambient";
            _coverPath = null;
        }

        if (_isBusy)
            return;

        if (!_audioEnabled)
        {
            _statusText = "Ambient armed, audio off.";
            return;
        }

        if (_playbackTask is { IsCompleted: false })
        {
            _statusText = "Playing ambient.";
            return;
        }

        _statusText = _playlist.Count > 0 ? "Ambient ready." : "Ambient off.";
    }

    private async Task<AmbientTrackInfo[]> SelectRandomTracksAsync()
    {
        lock (_lock)
        {
            _statusText = "Crawling StockTune...";
            _trackTitle = "Cyberpunk Ambient";
            _coverPath = null;
        }

        NotifyStateChanged();

        IReadOnlyList<AmbientTrackInfo> catalog = await StockTuneCrawler.CrawlCyberpunkAmbientAsync(
            status =>
            {
                lock (_lock)
                    _statusText = status;
                NotifyStateChanged();
            },
            CancellationToken.None).ConfigureAwait(false);

        AmbientTrackInfo[] candidates;
        lock (_lock)
        {
            candidates = catalog
                .Where(track => !_lastSelection.Contains(track.DownloadPath))
                .ToArray();
        }

        if (candidates.Length < PlaylistTrackCount)
            candidates = [.. catalog];

        Random.Shared.Shuffle(candidates);
        return candidates.Take(Math.Min(PlaylistTrackCount, candidates.Length)).ToArray();
    }

    private void PrepareCacheRoot()
    {
        Directory.CreateDirectory(_cacheRoot);

        foreach (string file in Directory.EnumerateFiles(_cacheRoot))
        {
            try
            {
                File.Delete(file);
            }
            catch
            {
                // Ignore stale file cleanup failures.
            }
        }
    }

    private static async Task DownloadFileAsync(string url, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        string tempPath = path + ".tmp";

        using HttpResponseMessage response = await s_httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using (Stream input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
        await using (var output = File.Create(tempPath))
        {
            await input.CopyToAsync(output).ConfigureAwait(false);
        }

        File.Move(tempPath, path, overwrite: true);
    }

    private static short[] DecodeMp3ToStereoPcm(string path, int targetSampleRate)
    {
        using var stream = File.OpenRead(path);
        using var mpegFile = new MpegFile(stream);

        int sourceChannels = Math.Max(1, mpegFile.Channels);
        int sourceSampleRate = Math.Max(1, mpegFile.SampleRate);
        var samples = new List<float>(131072);
        float[] readBuffer = new float[8192];

        while (true)
        {
            int read = mpegFile.ReadSamples(readBuffer, 0, readBuffer.Length);
            if (read <= 0)
                break;

            for (int i = 0; i < read; i++)
                samples.Add(readBuffer[i]);
        }

        int sourceFrameCount = samples.Count / sourceChannels;
        if (sourceFrameCount == 0)
            return Array.Empty<short>();

        if (targetSampleRate <= 0)
            targetSampleRate = sourceSampleRate;

        int targetFrameCount = sourceSampleRate == targetSampleRate
            ? sourceFrameCount
            : (int)Math.Ceiling(sourceFrameCount * (double)targetSampleRate / sourceSampleRate);

        var pcm = new short[targetFrameCount * OutputChannels];
        for (int frame = 0; frame < targetFrameCount; frame++)
        {
            double sourcePosition = sourceSampleRate == targetSampleRate
                ? frame
                : frame * (double)sourceSampleRate / targetSampleRate;
            int baseFrame = Math.Min(sourceFrameCount - 1, (int)sourcePosition);
            int nextFrame = Math.Min(sourceFrameCount - 1, baseFrame + 1);
            double fraction = sourcePosition - baseFrame;

            GetStereoSample(samples, sourceChannels, baseFrame, out float left0, out float right0);
            GetStereoSample(samples, sourceChannels, nextFrame, out float left1, out float right1);

            float left = (float)(left0 + ((left1 - left0) * fraction));
            float right = (float)(right0 + ((right1 - right0) * fraction));

            int sampleIndex = frame * OutputChannels;
            pcm[sampleIndex] = FloatToPcm16(left);
            pcm[sampleIndex + 1] = FloatToPcm16(right);
        }

        return pcm;
    }

    private static void GetStereoSample(List<float> samples, int sourceChannels, int frame, out float left, out float right)
    {
        int index = frame * sourceChannels;
        left = samples[index];
        right = sourceChannels > 1 ? samples[index + 1] : left;
    }

    private void ApplyVolume(ReadOnlySpan<short> source, Span<short> destination)
    {
        float scale;
        lock (_lock)
            scale = _masterVolumePercent / 100f;

        for (int i = 0; i < source.Length; i++)
        {
            int scaled = (int)Math.Round(source[i] * scale);
            destination[i] = (short)Math.Clamp(scaled, short.MinValue, short.MaxValue);
        }
    }

    private static short FloatToPcm16(float sample)
    {
        sample = Math.Clamp(sample, -1f, 1f);
        int scaled = (int)Math.Round(sample * short.MaxValue);
        return (short)Math.Clamp(scaled, short.MinValue, short.MaxValue);
    }

    private static string FormatTrackTitle(string title)
    {
        string normalized = title.Replace('-', ' ').Trim();
        return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(normalized);
    }

    private static string GetSafeFileStem(string title)
    {
        var chars = title
            .Trim()
            .ToLowerInvariant()
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : '-')
            .ToArray();
        string normalized = new(chars);
        while (normalized.Contains("--", StringComparison.Ordinal))
            normalized = normalized.Replace("--", "-", StringComparison.Ordinal);
        return normalized.Trim('-');
    }

    private static IAudioSink CreateAudioSink(string? sinkPrefRaw)
    {
        string? sinkPref = sinkPrefRaw?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(sinkPref) || sinkPref == "sdl2")
        {
            IAudioSink? sdlSink = Sdl2AudioSink.TryCreate();
            if (sdlSink != null)
                return sdlSink;

            IAudioSink? openAlSink = OpenAlAudioOutput.TryCreate();
            if (openAlSink != null)
                return openAlSink;

            return CreatePlatformFallbackAudioSink();
        }

        if (sinkPref == "openal")
        {
            IAudioSink? openAlSink = OpenAlAudioOutput.TryCreate();
            return openAlSink ?? CreatePlatformFallbackAudioSink();
        }

        if (sinkPref == "pwcat")
            return CreatePwCatOrFallbackAudioSink();

        return CreatePlatformFallbackAudioSink();
    }

    private static IAudioSink CreatePlatformFallbackAudioSink()
    {
        if (OperatingSystem.IsLinux())
            return new PwCatAudioSink();

        Console.WriteLine("[AmbientMusic] No native audio sink available; falling back to NullAudioSink.");
        return new NullAudioSink();
    }

    private static IAudioSink CreatePwCatOrFallbackAudioSink()
    {
        if (OperatingSystem.IsLinux())
            return new PwCatAudioSink();

        Console.WriteLine("[AmbientMusic] pw-cat requested on a non-Linux platform; falling back to NullAudioSink.");
        return new NullAudioSink();
    }

    private void NotifyStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);
}
