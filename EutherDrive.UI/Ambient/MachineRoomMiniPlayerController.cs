using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Globalization;
using System.Linq;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace EutherDrive.UI.Ambient;

internal readonly record struct MachineRoomMiniPlayerSnapshot(
    bool HasSelection,
    bool IsPlaying,
    bool IsPaused,
    bool IsVideo,
    bool CanSeek,
    double PositionSeconds,
    double DurationSeconds,
    string TrackTitle,
    string StatusText,
    string? CoverPath);

internal sealed class MachineRoomVideoFrameEventArgs : EventArgs
{
    public MachineRoomVideoFrameEventArgs(byte[] pixels, int width, int height, int stride)
    {
        Pixels = pixels;
        Width = width;
        Height = height;
        Stride = stride;
    }

    public byte[] Pixels { get; }
    public int Width { get; }
    public int Height { get; }
    public int Stride { get; }
}

internal sealed class MachineRoomMiniPlayerController : IDisposable
{
    private static readonly string[] SupportedMediaExtensions =
    [
        ".mp3", ".mp4", ".m4a", ".aac", ".flac", ".ogg", ".opus", ".wav", ".webm", ".mkv", ".mov",
        ".avi", ".mpeg", ".mpg", ".wmv"
    ];

    private static readonly string[] SupportedPlaylistExtensions = [".m3u", ".m3u8", ".pls"];
    private static readonly string[] VideoExtensions = [".mp4", ".webm", ".mkv", ".mov", ".avi", ".mpeg", ".mpg", ".wmv"];

    private readonly object _lock = new();
    private readonly string _coverCacheRoot;
    private List<string> _playlist = [];
    private Process? _process;
    private Process? _videoProcess;
    private CancellationTokenSource? _videoDecodeCts;
    private PlayerKind _playerKind = PlayerKind.None;
    private string? _mpvSocketPath;
    private int _currentIndex;
    private int _volumePercent = 60;
    private bool _isPlaying;
    private bool _isPaused;
    private bool _stopping;
    private bool _disposed;
    private string _trackTitle = "Cyberpunk Ambient";
    private string _statusText = "Ambient off.";
    private string? _coverPath;
    private bool _isCurrentVideo;
    private double _positionSeconds;
    private double _durationSeconds;

    public MachineRoomMiniPlayerController(string coverCacheRoot)
    {
        _coverCacheRoot = coverCacheRoot ?? throw new ArgumentNullException(nameof(coverCacheRoot));
    }

    public event EventHandler? StateChanged;
    public event EventHandler<MachineRoomVideoFrameEventArgs>? VideoFrameReady;

    public MachineRoomMiniPlayerSnapshot GetSnapshot()
    {
        lock (_lock)
        {
            return new MachineRoomMiniPlayerSnapshot(
                _playlist.Count > 0,
                _isPlaying,
                _isPaused,
                _isCurrentVideo,
                _durationSeconds > 0,
                _positionSeconds,
                _durationSeconds,
                _trackTitle,
                _statusText,
                _coverPath);
        }
    }

    public void LoadFiles(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        List<string> playlist = ExpandSelection(paths).ToList();

        StopProcess(clearSelection: false);
        lock (_lock)
        {
            _playlist = playlist;
            _currentIndex = 0;
            _isPlaying = false;
            _isPaused = false;

            if (_playlist.Count == 0)
            {
                _trackTitle = "Mini Winamp";
                _statusText = "No playable media selected.";
                _coverPath = null;
                _isCurrentVideo = false;
                _positionSeconds = 0;
                _durationSeconds = 0;
            }
            else
            {
                UpdateCurrentTrackMetadataLocked();
                _statusText = _playlist.Count == 1
                    ? "Loaded 1 track."
                    : $"Loaded {_playlist.Count} tracks.";
            }
        }

        NotifyStateChanged();
    }

    public void SetVolumePercent(int percent)
    {
        Process? process;
        PlayerKind playerKind;
        string? mpvSocketPath;
        int volumePercent;
        int previousPercent;
        lock (_lock)
        {
            previousPercent = _volumePercent;
            _volumePercent = Math.Clamp(percent, 0, 100);
            volumePercent = _volumePercent;
            process = _process;
            playerKind = _playerKind;
            mpvSocketPath = _mpvSocketPath;
        }

        if (process == null || process.HasExited)
            return;

        if (playerKind == PlayerKind.Mpv && !string.IsNullOrWhiteSpace(mpvSocketPath))
        {
            SendMpvCommand(mpvSocketPath, $"{{\"command\":[\"set_property\",\"volume\",{volumePercent}]}}");
            return;
        }

        int steps = (int)Math.Round((_volumePercent - previousPercent) / 10.0);
        if (steps == 0)
            return;

        char command = steps > 0 ? '9' : '0';
        try
        {
            for (int i = 0; i < Math.Abs(steps); i++)
                process.StandardInput.Write(command);
            process.StandardInput.Flush();
        }
        catch
        {
            // ffplay volume hotkeys are best effort; the next track starts with the exact requested volume.
        }
    }

    public Task RefreshPositionAsync()
    {
        return Task.Run(() =>
        {
            Process? process;
            PlayerKind playerKind;
            string? mpvSocketPath;
            lock (_lock)
            {
                process = _process;
                playerKind = _playerKind;
                mpvSocketPath = _mpvSocketPath;
            }

            if (process == null || process.HasExited || playerKind != PlayerKind.Mpv || string.IsNullOrWhiteSpace(mpvSocketPath))
                return;

            double? position = SendMpvNumberRequest(mpvSocketPath, "{\"command\":[\"get_property\",\"time-pos\"]}");
            double? duration = SendMpvNumberRequest(mpvSocketPath, "{\"command\":[\"get_property\",\"duration\"]}");
            lock (_lock)
            {
                if (position.HasValue)
                    _positionSeconds = Math.Max(0, position.Value);
                if (duration.HasValue)
                    _durationSeconds = Math.Max(0, duration.Value);
            }
        });
    }

    public Task SeekAsync(double positionSeconds)
    {
        string? path;
        Process? process;
        PlayerKind playerKind;
        string? mpvSocketPath;
        bool isVideo;
        double seekSeconds;
        lock (_lock)
        {
            seekSeconds = Math.Max(0, positionSeconds);
            if (_durationSeconds > 0)
                seekSeconds = Math.Min(seekSeconds, _durationSeconds);
            _positionSeconds = seekSeconds;
            path = _playlist.Count == 0 ? null : _playlist[Math.Clamp(_currentIndex, 0, _playlist.Count - 1)];
            process = _process;
            playerKind = _playerKind;
            mpvSocketPath = _mpvSocketPath;
            isVideo = _isCurrentVideo;
        }

        if (process == null || process.HasExited || string.IsNullOrWhiteSpace(path))
            return Task.CompletedTask;

        if (playerKind == PlayerKind.Mpv && !string.IsNullOrWhiteSpace(mpvSocketPath))
        {
            string seconds = seekSeconds.ToString("0.###", CultureInfo.InvariantCulture);
            SendMpvCommand(mpvSocketPath, $"{{\"command\":[\"seek\",{seconds},\"absolute\"]}}");
        }
        else
        {
            lock (_lock)
                _statusText = "Seek needs mpv.";
            NotifyStateChanged();
            return Task.CompletedTask;
        }

        if (isVideo)
            StartVideoDecoder(path, seekSeconds);

        NotifyStateChanged();
        return Task.CompletedTask;
    }

    public Task TogglePlayPauseAsync()
    {
        bool shouldStart;
        Process? process;
        PlayerKind playerKind;
        string? mpvSocketPath;

        lock (_lock)
        {
            shouldStart = !_isPlaying || _process == null || _process.HasExited;
            process = _process;
            playerKind = _playerKind;
            mpvSocketPath = _mpvSocketPath;
        }

        if (shouldStart)
            return StartCurrentAsync();

        try
        {
            if (playerKind == PlayerKind.Mpv && !string.IsNullOrWhiteSpace(mpvSocketPath))
            {
                SendMpvCommand(mpvSocketPath, "{\"command\":[\"cycle\",\"pause\"]}");
            }
            else
            {
                process?.StandardInput.Write("p");
                process?.StandardInput.Flush();
            }

            lock (_lock)
            {
                _isPaused = !_isPaused;
                _statusText = _isPaused ? "Paused." : "Playing.";
            }
        }
        catch (Exception ex)
        {
            lock (_lock)
                _statusText = $"Pause failed: {ex.Message}";
        }

        NotifyStateChanged();
        return Task.CompletedTask;
    }

    public Task PreviousAsync()
    {
        lock (_lock)
        {
            if (!MoveWithinCurrentDirectoryLocked(-1))
                return Task.CompletedTask;
        }

        return StartCurrentAsync();
    }

    public Task NextAsync()
    {
        lock (_lock)
        {
            if (!MoveWithinCurrentDirectoryLocked(1))
                return Task.CompletedTask;
        }

        return StartCurrentAsync();
    }

    public Task RandomFromDirectoryAsync(string? root)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            lock (_lock)
                _statusText = "Set Media Dir before random.";
            NotifyStateChanged();
            return Task.CompletedTask;
        }

        List<string> tracks;
        try
        {
            tracks = Directory
                .EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Where(IsSupportedMediaPath)
                .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex)
        {
            lock (_lock)
                _statusText = $"Random failed: {ex.Message}";
            NotifyStateChanged();
            return Task.CompletedTask;
        }

        if (tracks.Count == 0)
        {
            lock (_lock)
                _statusText = "No media found under this folder.";
            NotifyStateChanged();
            return Task.CompletedTask;
        }

        int randomIndex = Random.Shared.Next(tracks.Count);

        StopProcess(clearSelection: false);
        lock (_lock)
        {
            _playlist = tracks;
            _currentIndex = randomIndex;
            UpdateCurrentTrackMetadataLocked();
            _statusText = $"Random from {Path.GetFileName(root)}.";
        }

        NotifyStateChanged();
        return StartCurrentAsync();
    }

    public void Stop()
    {
        StopProcess(clearSelection: false);
        lock (_lock)
        {
            _isPlaying = false;
            _isPaused = false;
            if (_playlist.Count > 0)
            {
                UpdateCurrentTrackMetadataLocked();
                _statusText = "Stopped.";
            }
            else
            {
                _trackTitle = "Cyberpunk Ambient";
                _statusText = "Ambient off.";
                _coverPath = null;
                _positionSeconds = 0;
                _durationSeconds = 0;
            }
        }

        NotifyStateChanged();
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        StopProcess(clearSelection: true);
    }

    private Task StartCurrentAsync()
    {
        PlayerLaunch player = FindPlayer();
        if (player.Kind == PlayerKind.None)
        {
            lock (_lock)
            {
                _isPlaying = false;
                _isPaused = false;
                _statusText = "Install mpv or ffplay to play media here.";
            }

            NotifyStateChanged();
            return Task.CompletedTask;
        }

        string? path;
        int currentIndex;
        int totalCount;
        lock (_lock)
        {
            if (_playlist.Count == 0)
                return Task.CompletedTask;

            currentIndex = Math.Clamp(_currentIndex, 0, _playlist.Count - 1);
            _currentIndex = currentIndex;
            path = _playlist[currentIndex];
            totalCount = _playlist.Count;
        }

        StopProcess(clearSelection: false);

        try
        {
            string? mpvSocketPath = null;
            var startInfo = new ProcessStartInfo(player.Path)
            {
                UseShellExecute = false,
                RedirectStandardInput = player.Kind == PlayerKind.Ffplay,
                CreateNoWindow = true
            };

            if (player.Kind == PlayerKind.Mpv)
            {
                mpvSocketPath = GetMpvSocketPath();
                startInfo.ArgumentList.Add("--no-video");
                startInfo.ArgumentList.Add("--really-quiet");
                startInfo.ArgumentList.Add("--force-window=no");
                startInfo.ArgumentList.Add("--input-terminal=no");
                startInfo.ArgumentList.Add($"--input-ipc-server={mpvSocketPath}");
                startInfo.ArgumentList.Add($"--volume={_volumePercent}");
                startInfo.ArgumentList.Add(path);
            }
            else
            {
                startInfo.ArgumentList.Add("-hide_banner");
                startInfo.ArgumentList.Add("-loglevel");
                startInfo.ArgumentList.Add("error");
                startInfo.ArgumentList.Add("-nostats");
                startInfo.ArgumentList.Add("-volume");
                startInfo.ArgumentList.Add(_volumePercent.ToString());
                startInfo.ArgumentList.Add("-nodisp");
                startInfo.ArgumentList.Add("-autoexit");
                startInfo.ArgumentList.Add(path);
            }

            var process = new Process
            {
                StartInfo = startInfo,
                EnableRaisingEvents = true
            };
            process.Exited += OnProcessExited;
            process.Start();

            lock (_lock)
            {
                _process = process;
                _playerKind = player.Kind;
                _mpvSocketPath = mpvSocketPath;
                _positionSeconds = 0;
                _durationSeconds = 0;
                _isPlaying = true;
                _isPaused = false;
                _stopping = false;
                UpdateCurrentTrackMetadataLocked();
                _statusText = totalCount > 1
                    ? $"Playing {currentIndex + 1}/{totalCount}."
                    : "Playing.";
            }

            if (IsVideoPath(path))
                StartVideoDecoder(path, 0);
        }
        catch (Exception ex)
        {
            lock (_lock)
            {
                _isPlaying = false;
                _isPaused = false;
                _statusText = $"Player failed: {ex.Message}";
            }
        }

        NotifyStateChanged();
        return Task.CompletedTask;
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        bool advance;
        lock (_lock)
        {
            if (sender is Process process && !ReferenceEquals(process, _process))
                return;

            advance = !_disposed && !_stopping && _isPlaying && _playlist.Count > 1;
            _isPlaying = false;
            _isPaused = false;
            if (!advance)
                _positionSeconds = 0;
            if (advance)
                _currentIndex = (_currentIndex + 1) % _playlist.Count;
            else if (!_disposed && !_stopping)
                _statusText = "Finished.";
        }

        if (!advance)
            StopVideoDecoder();

        NotifyStateChanged();
        if (advance)
            _ = StartCurrentAsync();
    }

    private void StopProcess(bool clearSelection)
    {
        StopVideoDecoder();

        Process? process;
        string? mpvSocketPath;
        lock (_lock)
        {
            _stopping = true;
            process = _process;
            mpvSocketPath = _mpvSocketPath;
            _process = null;
            _playerKind = PlayerKind.None;
            _mpvSocketPath = null;
            _isPlaying = false;
            _isPaused = false;
            if (clearSelection)
                _playlist.Clear();
        }

        if (process == null)
            return;

        try
        {
            process.Exited -= OnProcessExited;
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Best effort shutdown; the UI state is already detached from this process.
        }
        finally
        {
            process.Dispose();
            if (!string.IsNullOrWhiteSpace(mpvSocketPath))
                TryDeleteFile(mpvSocketPath);
        }
    }

    private static IEnumerable<string> ExpandSelection(IEnumerable<string> paths)
    {
        foreach (string path in paths.Where(static p => !string.IsNullOrWhiteSpace(p)))
        {
            string extension = Path.GetExtension(path);
            if (SupportedPlaylistExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            {
                foreach (string playlistPath in ReadPlaylist(path))
                    yield return playlistPath;
            }
            else if (IsSupportedMediaPath(path) && File.Exists(path))
            {
                yield return path;
            }
        }
    }

    private void StartVideoDecoder(string path, double startSeconds)
    {
        StopVideoDecoder();

        var cts = new CancellationTokenSource();
        lock (_lock)
        {
            _videoDecodeCts = cts;
        }

        _ = Task.Run(() => DecodeVideoFramesAsync(path, Math.Max(0, startSeconds), cts.Token));
    }

    private async Task DecodeVideoFramesAsync(string path, double startSeconds, CancellationToken cancellationToken)
    {
        string? ffmpegPath = FindExecutable(RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "ffmpeg.exe" : "ffmpeg");
        string? ffprobePath = FindExecutable(RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "ffprobe.exe" : "ffprobe");
        if (ffmpegPath == null || ffprobePath == null)
            return;

        if (!TryProbeVideo(ffprobePath, path, out int width, out int height, out double fps))
            return;

        long frameBytes64 = (long)width * height * 4;
        if (frameBytes64 <= 0 || frameBytes64 > 256L * 1024 * 1024)
            return;

        var startInfo = new ProcessStartInfo(ffmpegPath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        startInfo.ArgumentList.Add("-hide_banner");
        startInfo.ArgumentList.Add("-loglevel");
        startInfo.ArgumentList.Add("error");
        if (startSeconds > 0)
        {
            startInfo.ArgumentList.Add("-ss");
            startInfo.ArgumentList.Add(startSeconds.ToString("0.###", CultureInfo.InvariantCulture));
        }
        startInfo.ArgumentList.Add("-i");
        startInfo.ArgumentList.Add(path);
        startInfo.ArgumentList.Add("-map");
        startInfo.ArgumentList.Add("0:v:0");
        startInfo.ArgumentList.Add("-an");
        startInfo.ArgumentList.Add("-sn");
        startInfo.ArgumentList.Add("-pix_fmt");
        startInfo.ArgumentList.Add("bgra");
        startInfo.ArgumentList.Add("-f");
        startInfo.ArgumentList.Add("rawvideo");
        startInfo.ArgumentList.Add("-");

        Process? process = null;
        try
        {
            process = new Process
            {
                StartInfo = startInfo,
                EnableRaisingEvents = false
            };
            process.Start();
            _ = process.StandardError.ReadToEndAsync(cancellationToken);

            lock (_lock)
                _videoProcess = process;

            int frameBytes = (int)frameBytes64;
            int stride = width * 4;
            TimeSpan frameDelay = TimeSpan.FromMilliseconds(1000.0 / Math.Clamp(fps, 1.0, 60.0));
            Stream output = process.StandardOutput.BaseStream;

            while (!cancellationToken.IsCancellationRequested)
            {
                byte[] frame = new byte[frameBytes];
                if (!await ReadExactlyAsync(output, frame, cancellationToken).ConfigureAwait(false))
                    break;

                while (IsVideoPaused() && !cancellationToken.IsCancellationRequested)
                    await Task.Delay(50, cancellationToken).ConfigureAwait(false);

                VideoFrameReady?.Invoke(this, new MachineRoomVideoFrameEventArgs(frame, width, height, stride));
                if (frameDelay > TimeSpan.Zero)
                    await Task.Delay(frameDelay, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            // Video preview is best effort; audio playback continues even if decoding fails.
        }
        finally
        {
            if (process != null)
            {
                try
                {
                    if (!process.HasExited)
                        process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // Best effort shutdown.
                }

                process.Dispose();
            }

            lock (_lock)
            {
                if (ReferenceEquals(_videoProcess, process))
                    _videoProcess = null;
            }
        }
    }

    private bool IsVideoPaused()
    {
        lock (_lock)
        {
            return _isPaused;
        }
    }

    private void StopVideoDecoder()
    {
        Process? process;
        CancellationTokenSource? cts;
        lock (_lock)
        {
            process = _videoProcess;
            cts = _videoDecodeCts;
            _videoProcess = null;
            _videoDecodeCts = null;
        }

        try
        {
            cts?.Cancel();
        }
        catch
        {
            // Best effort cancellation.
        }

        try
        {
            if (process is { HasExited: false })
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Best effort shutdown.
        }
        finally
        {
            cts?.Dispose();
        }
    }

    private static async Task<bool> ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
                return false;
            offset += read;
        }

        return true;
    }

    private static bool TryProbeVideo(string ffprobePath, string path, out int width, out int height, out double fps)
    {
        width = 0;
        height = 0;
        fps = 24.0;

        try
        {
            var startInfo = new ProcessStartInfo(ffprobePath)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("-v");
            startInfo.ArgumentList.Add("error");
            startInfo.ArgumentList.Add("-select_streams");
            startInfo.ArgumentList.Add("v:0");
            startInfo.ArgumentList.Add("-show_entries");
            startInfo.ArgumentList.Add("stream=width,height,r_frame_rate");
            startInfo.ArgumentList.Add("-of");
            startInfo.ArgumentList.Add("default=noprint_wrappers=1:nokey=1");
            startInfo.ArgumentList.Add(path);

            using Process? process = Process.Start(startInfo);
            if (process == null)
                return false;

            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(2500);
            if (!process.HasExited || process.ExitCode != 0)
            {
                try
                {
                    if (!process.HasExited)
                        process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // Best effort cleanup.
                }

                return false;
            }

            string[] lines = output
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (lines.Length < 2
                || !int.TryParse(lines[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out width)
                || !int.TryParse(lines[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out height)
                || width <= 0
                || height <= 0)
            {
                return false;
            }

            if (lines.Length >= 3 && TryParseFrameRate(lines[2], out double parsedFps))
                fps = parsedFps;

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryParseFrameRate(string raw, out double fps)
    {
        fps = 24.0;
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        string[] parts = raw.Split(new[] { '/' }, StringSplitOptions.TrimEntries);
        if (parts.Length == 2
            && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double numerator)
            && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double denominator)
            && numerator > 0
            && denominator > 0)
        {
            fps = numerator / denominator;
            return fps > 0;
        }

        return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out fps) && fps > 0;
    }

    private Task RefreshStoppedTrackAsync()
    {
        lock (_lock)
        {
            if (_playlist.Count > 0)
            {
                UpdateCurrentTrackMetadataLocked();
                _statusText = "Selected.";
            }
        }

        NotifyStateChanged();
        return Task.CompletedTask;
    }

    private bool MoveWithinCurrentDirectoryLocked(int direction)
    {
        if (_playlist.Count == 0)
            return false;

        string currentPath = _playlist[Math.Clamp(_currentIndex, 0, _playlist.Count - 1)];
        List<string> directoryPlaylist = GetDirectoryPlaylist(currentPath);
        if (directoryPlaylist.Count > 0)
        {
            _playlist = directoryPlaylist;
            string fullCurrentPath = Path.GetFullPath(currentPath);
            int directoryIndex = _playlist.FindIndex(path =>
                string.Equals(Path.GetFullPath(path), fullCurrentPath, StringComparison.Ordinal));
            _currentIndex = directoryIndex >= 0 ? directoryIndex : 0;
        }

        _currentIndex = (_currentIndex + direction + _playlist.Count) % _playlist.Count;
        return true;
    }

    private static List<string> GetDirectoryPlaylist(string mediaPath)
    {
        string? directory = Path.GetDirectoryName(mediaPath);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            return [];

        try
        {
            return Directory
                .EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
                .Where(IsSupportedMediaPath)
                .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    private static IEnumerable<string> ReadPlaylist(string playlistPath)
    {
        if (!File.Exists(playlistPath))
            yield break;

        string? baseDirectory = Path.GetDirectoryName(playlistPath);
        IEnumerable<string> lines;
        try
        {
            lines = File.ReadLines(playlistPath);
        }
        catch
        {
            yield break;
        }

        foreach (string rawLine in lines)
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
                continue;

            if (line.StartsWith("File", StringComparison.OrdinalIgnoreCase))
            {
                int equals = line.IndexOf('=');
                if (equals >= 0)
                    line = line[(equals + 1)..].Trim();
            }

            if (!Path.IsPathRooted(line) && !string.IsNullOrWhiteSpace(baseDirectory))
                line = Path.GetFullPath(Path.Combine(baseDirectory, line));

            string extension = Path.GetExtension(line);
            if (SupportedMediaExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase) && File.Exists(line))
                yield return line;
        }
    }

    private static string GetDisplayTitle(string path)
    {
        string title = Path.GetFileNameWithoutExtension(path);
        return string.IsNullOrWhiteSpace(title) ? Path.GetFileName(path) : title;
    }

    private static bool IsSupportedMediaPath(string path)
        => SupportedMediaExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    private static bool IsVideoPath(string path)
        => VideoExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    private void UpdateCurrentTrackMetadataLocked()
    {
        if (_playlist.Count == 0)
        {
            _trackTitle = "Mini Winamp";
            _coverPath = null;
            _isCurrentVideo = false;
            return;
        }

        string path = _playlist[Math.Clamp(_currentIndex, 0, _playlist.Count - 1)];
        _trackTitle = GetDisplayTitle(path);
        _isCurrentVideo = IsVideoPath(path);
        _coverPath = TryGetEmbeddedCoverPath(path);
    }

    private string? TryGetEmbeddedCoverPath(string mediaPath)
    {
        if (!File.Exists(mediaPath))
            return null;

        try
        {
            if (string.Equals(Path.GetExtension(mediaPath), ".mp3", StringComparison.OrdinalIgnoreCase))
            {
                byte[]? imageBytes = TryReadId3ApicImage(mediaPath, out string? mimeType);
                if (imageBytes is { Length: > 0 })
                {
                    Directory.CreateDirectory(_coverCacheRoot);
                    string extension = GetImageExtension(mimeType, imageBytes);
                    string cacheName = GetStableCacheStem(mediaPath) + extension;
                    string cachePath = Path.Combine(_coverCacheRoot, cacheName);
                    if (!File.Exists(cachePath))
                        File.WriteAllBytes(cachePath, imageBytes);
                    return cachePath;
                }
            }

            return TryExtractCoverWithFfmpeg(mediaPath)
                ?? TryExtractCompanionCoverWithFfmpeg(mediaPath);
        }
        catch
        {
            return null;
        }
    }

    private static byte[]? TryReadId3ApicImage(string path, out string? mimeType)
    {
        mimeType = null;
        using var stream = File.OpenRead(path);
        Span<byte> header = stackalloc byte[10];
        if (stream.Read(header) != header.Length
            || header[0] != (byte)'I'
            || header[1] != (byte)'D'
            || header[2] != (byte)'3')
        {
            return null;
        }

        int majorVersion = header[3];
        int tagSize = DecodeSynchsafeInt(header[6], header[7], header[8], header[9]);
        if (tagSize <= 0)
            return null;

        long tagEnd = Math.Min(stream.Length, 10L + tagSize);
        Span<byte> frameHeader = stackalloc byte[6];
        Span<byte> id3FrameHeader = stackalloc byte[10];
        while (stream.Position + (majorVersion == 2 ? 6 : 10) <= tagEnd)
        {
            if (majorVersion == 2)
            {
                if (stream.Read(frameHeader) != frameHeader.Length)
                    return null;

                string frameId = System.Text.Encoding.ASCII.GetString(frameHeader[..3]);
                int frameSize = (frameHeader[3] << 16) | (frameHeader[4] << 8) | frameHeader[5];
                if (frameSize <= 0 || stream.Position + frameSize > tagEnd)
                    return null;

                byte[] frame = new byte[frameSize];
                if (stream.Read(frame) != frame.Length)
                    return null;
                if (frameId == "PIC")
                    return ParsePicFrame(frame, out mimeType);
                continue;
            }

            if (stream.Read(id3FrameHeader) != id3FrameHeader.Length)
                return null;

            if (id3FrameHeader[..4].IndexOf((byte)0) >= 0)
                return null;

            string id3FrameId = System.Text.Encoding.ASCII.GetString(id3FrameHeader[..4]);
            int id3FrameSize = majorVersion == 4
                ? DecodeSynchsafeInt(id3FrameHeader[4], id3FrameHeader[5], id3FrameHeader[6], id3FrameHeader[7])
                : BinaryPrimitives.ReadInt32BigEndian(id3FrameHeader[4..8]);
            if (id3FrameSize <= 0 || stream.Position + id3FrameSize > tagEnd)
                return null;

            byte[] id3Frame = new byte[id3FrameSize];
            if (stream.Read(id3Frame) != id3Frame.Length)
                return null;
            if (id3FrameId == "APIC")
                return ParseApicFrame(id3Frame, out mimeType);
        }

        return null;
    }

    private static byte[]? ParseApicFrame(byte[] frame, out string? mimeType)
    {
        mimeType = null;
        if (frame.Length < 4)
            return null;

        int index = 1;
        int mimeEnd = Array.IndexOf(frame, (byte)0, index);
        if (mimeEnd < 0 || mimeEnd + 2 >= frame.Length)
            return null;

        mimeType = System.Text.Encoding.ASCII.GetString(frame, index, mimeEnd - index);
        index = mimeEnd + 2;
        index = SkipEncodedText(frame, index, frame[0]);
        return index >= 0 && index < frame.Length ? frame[index..] : null;
    }

    private static byte[]? ParsePicFrame(byte[] frame, out string? mimeType)
    {
        mimeType = null;
        if (frame.Length < 6)
            return null;

        string format = System.Text.Encoding.ASCII.GetString(frame, 1, 3).Trim();
        mimeType = format.Equals("PNG", StringComparison.OrdinalIgnoreCase) ? "image/png" : "image/jpeg";
        int index = SkipEncodedText(frame, 5, frame[0]);
        return index >= 0 && index < frame.Length ? frame[index..] : null;
    }

    private static int SkipEncodedText(byte[] data, int start, byte encoding)
    {
        if (start >= data.Length)
            return -1;

        if (encoding is 1 or 2)
        {
            for (int i = start; i + 1 < data.Length; i += 2)
            {
                if (data[i] == 0 && data[i + 1] == 0)
                    return i + 2;
            }

            return -1;
        }

        int end = Array.IndexOf(data, (byte)0, start);
        return end < 0 ? -1 : end + 1;
    }

    private static int DecodeSynchsafeInt(byte b0, byte b1, byte b2, byte b3)
        => (b0 << 21) | (b1 << 14) | (b2 << 7) | b3;

    private static string GetImageExtension(string? mimeType, byte[] imageBytes)
    {
        if (string.Equals(mimeType, "image/png", StringComparison.OrdinalIgnoreCase)
            || imageBytes is [0x89, 0x50, 0x4E, 0x47, ..])
        {
            return ".png";
        }

        return ".jpg";
    }

    private string? TryExtractCompanionCoverWithFfmpeg(string mediaPath)
    {
        if (!string.Equals(Path.GetExtension(mediaPath), ".mp3", StringComparison.OrdinalIgnoreCase))
            return null;

        string? directory = Path.GetDirectoryName(mediaPath);
        string? parent = string.IsNullOrWhiteSpace(directory) ? null : Directory.GetParent(directory)?.FullName;
        if (string.IsNullOrWhiteSpace(parent))
            return null;

        string companion = Path.Combine(parent, Path.GetFileNameWithoutExtension(mediaPath) + ".m4a");
        return File.Exists(companion) ? TryExtractCoverWithFfmpeg(companion, mediaPath) : null;
    }

    private string? TryExtractCoverWithFfmpeg(string mediaPath, string? cacheKeyPath = null)
    {
        string? ffmpegPath = FindExecutable(RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "ffmpeg.exe" : "ffmpeg");
        if (ffmpegPath == null)
            return null;

        Directory.CreateDirectory(_coverCacheRoot);
        string cachePath = Path.Combine(_coverCacheRoot, GetStableCacheStem(cacheKeyPath ?? mediaPath) + ".jpg");
        if (File.Exists(cachePath))
            return cachePath;

        string tempPath = Path.Combine(
            _coverCacheRoot,
            $"{Path.GetFileNameWithoutExtension(cachePath)}.{Guid.NewGuid():N}.tmp.jpg");
        try
        {
            var startInfo = new ProcessStartInfo(ffmpegPath)
            {
                UseShellExecute = false,
                CreateNoWindow = true
            };

            startInfo.ArgumentList.Add("-hide_banner");
            startInfo.ArgumentList.Add("-loglevel");
            startInfo.ArgumentList.Add("error");
            startInfo.ArgumentList.Add("-y");
            startInfo.ArgumentList.Add("-i");
            startInfo.ArgumentList.Add(mediaPath);
            startInfo.ArgumentList.Add("-map");
            startInfo.ArgumentList.Add("0:v:0");
            startInfo.ArgumentList.Add("-frames:v");
            startInfo.ArgumentList.Add("1");
            startInfo.ArgumentList.Add(tempPath);

            using var process = Process.Start(startInfo);
            process?.WaitForExit(2500);
            if (process == null
                || !process.HasExited
                || process.ExitCode != 0
                || !File.Exists(tempPath)
                || new FileInfo(tempPath).Length == 0)
            {
                try
                {
                    if (process is { HasExited: false })
                        process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // Best effort cleanup.
                }

                return null;
            }

            File.Move(tempPath, cachePath, overwrite: true);
            return cachePath;
        }
        catch
        {
            return null;
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
            catch
            {
                // Ignore cache cleanup failures.
            }
        }
    }

    private static string GetStableCacheStem(string path)
        => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(path))).ToLowerInvariant();

    private static PlayerLaunch FindPlayer()
    {
        string? mpv = FindExecutable(RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "mpv.exe" : "mpv");
        if (mpv != null)
            return new PlayerLaunch(PlayerKind.Mpv, mpv);

        string? ffplay = FindExecutable(RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "ffplay.exe" : "ffplay");
        return ffplay == null ? default : new PlayerLaunch(PlayerKind.Ffplay, ffplay);
    }

    private static string GetMpvSocketPath()
    {
        string name = $"eutherdrive-machine-room-{Environment.ProcessId}-{Guid.NewGuid():N}";
        return RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? $@"\\.\pipe\{name}"
            : Path.Combine(Path.GetTempPath(), $"{name}.sock");
    }

    private static void SendMpvCommand(string socketPath, string json)
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                using var pipe = OpenMpvNamedPipe(socketPath);
                byte[] pipeBytes = Encoding.UTF8.GetBytes(json + "\n");
                pipe.Write(pipeBytes, 0, pipeBytes.Length);
                pipe.Flush();
                return;
            }

            using var socket = OpenMpvUnixSocket(socketPath);
            byte[] bytes = Encoding.UTF8.GetBytes(json + "\n");
            socket.Send(bytes);
        }
        catch
        {
            // Runtime player control is best effort; the next track starts with the saved setting.
        }
    }

    private static double? SendMpvNumberRequest(string socketPath, string json)
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                using var pipe = OpenMpvNamedPipe(socketPath);
                return SendMpvNumberRequest(pipe, json);
            }

            using var socket = OpenMpvUnixSocket(socketPath);
            using var stream = new NetworkStream(socket, ownsSocket: false);
            return SendMpvNumberRequest(stream, json);
        }
        catch
        {
            return null;
        }
    }

    private static Socket OpenMpvUnixSocket(string socketPath)
    {
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        socket.Connect(new UnixDomainSocketEndPoint(socketPath));
        return socket;
    }

    private static NamedPipeClientStream OpenMpvNamedPipe(string pipePath)
    {
        var pipe = new NamedPipeClientStream(
            ".",
            GetWindowsPipeName(pipePath),
            PipeDirection.InOut,
            PipeOptions.None);
        pipe.Connect(250);
        return pipe;
    }

    private static double? SendMpvNumberRequest(Stream stream, string json)
    {
        if (stream.CanTimeout)
        {
            stream.ReadTimeout = 250;
            stream.WriteTimeout = 250;
        }

        byte[] bytes = Encoding.UTF8.GetBytes(json + "\n");
        stream.Write(bytes, 0, bytes.Length);
        stream.Flush();

        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        string? response = reader.ReadLine();
        if (string.IsNullOrWhiteSpace(response))
            return null;

        using JsonDocument document = JsonDocument.Parse(response);
        if (!document.RootElement.TryGetProperty("data", out JsonElement data)
            || data.ValueKind != JsonValueKind.Number)
        {
            return null;
        }

        return data.TryGetDouble(out double value) ? value : null;
    }

    private static string GetWindowsPipeName(string pipePath)
    {
        const string win32Prefix = @"\\.\pipe\";
        const string ntPrefix = @"\\?\pipe\";
        if (pipePath.StartsWith(win32Prefix, StringComparison.OrdinalIgnoreCase))
            return pipePath[win32Prefix.Length..];
        if (pipePath.StartsWith(ntPrefix, StringComparison.OrdinalIgnoreCase))
            return pipePath[ntPrefix.Length..];
        return pipePath;
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Ignore temporary IPC socket cleanup failures.
        }
    }

    private readonly record struct PlayerLaunch(PlayerKind Kind, string Path);

    private enum PlayerKind
    {
        None,
        Mpv,
        Ffplay
    }

    private static string? FindExecutable(string executableName)
    {
        string? pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathEnv))
            return null;

        foreach (string directory in pathEnv.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(directory))
                continue;

            string candidate = Path.Combine(directory, executableName);
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    private void NotifyStateChanged()
        => StateChanged?.Invoke(this, EventArgs.Empty);
}
