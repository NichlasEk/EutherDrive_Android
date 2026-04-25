using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace EutherDrive.UI.Ambient;

internal readonly record struct MachineRoomMiniPlayerSnapshot(
    bool HasSelection,
    bool IsPlaying,
    bool IsPaused,
    string TrackTitle,
    string StatusText);

internal sealed class MachineRoomMiniPlayerController : IDisposable
{
    private static readonly string[] SupportedMediaExtensions =
    [
        ".mp3", ".mp4", ".m4a", ".aac", ".flac", ".ogg", ".opus", ".wav", ".webm", ".mkv", ".mov"
    ];

    private static readonly string[] SupportedPlaylistExtensions = [".m3u", ".m3u8", ".pls"];

    private readonly object _lock = new();
    private List<string> _playlist = [];
    private Process? _process;
    private int _currentIndex;
    private int _volumePercent = 60;
    private bool _isPlaying;
    private bool _isPaused;
    private bool _stopping;
    private bool _disposed;
    private string _trackTitle = "Cyberpunk Ambient";
    private string _statusText = "Ambient off.";

    public event EventHandler? StateChanged;

    public MachineRoomMiniPlayerSnapshot GetSnapshot()
    {
        lock (_lock)
        {
            return new MachineRoomMiniPlayerSnapshot(
                _playlist.Count > 0,
                _isPlaying,
                _isPaused,
                _trackTitle,
                _statusText);
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
            }
            else
            {
                _trackTitle = GetDisplayTitle(_playlist[0]);
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
        int previousPercent;
        lock (_lock)
        {
            previousPercent = _volumePercent;
            _volumePercent = Math.Clamp(percent, 0, 100);
            process = _process;
        }

        if (process == null || process.HasExited)
            return;

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

    public Task TogglePlayPauseAsync()
    {
        bool shouldStart;
        Process? process;

        lock (_lock)
        {
            shouldStart = !_isPlaying || _process == null || _process.HasExited;
            process = _process;
        }

        if (shouldStart)
            return StartCurrentAsync();

        try
        {
            process?.StandardInput.Write("p");
            process?.StandardInput.Flush();
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
        bool shouldStart;
        lock (_lock)
        {
            if (_playlist.Count == 0)
                return Task.CompletedTask;
            _currentIndex = (_currentIndex - 1 + _playlist.Count) % _playlist.Count;
            shouldStart = _isPlaying || _process != null;
        }

        return shouldStart ? StartCurrentAsync() : RefreshStoppedTrackAsync();
    }

    public Task NextAsync()
    {
        bool shouldStart;
        lock (_lock)
        {
            if (_playlist.Count == 0)
                return Task.CompletedTask;
            _currentIndex = (_currentIndex + 1) % _playlist.Count;
            shouldStart = _isPlaying || _process != null;
        }

        return shouldStart ? StartCurrentAsync() : RefreshStoppedTrackAsync();
    }

    public Task RandomFromDirectoryAsync(string? root)
    {
        bool shouldStart;
        lock (_lock)
            shouldStart = _isPlaying || _process != null;

        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            lock (_lock)
                _statusText = "Set MP3 Dir before random.";
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
            _trackTitle = GetDisplayTitle(_playlist[_currentIndex]);
            _statusText = $"Random from {Path.GetFileName(root)}.";
        }

        NotifyStateChanged();
        return shouldStart ? StartCurrentAsync() : Task.CompletedTask;
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
                _trackTitle = GetDisplayTitle(_playlist[Math.Clamp(_currentIndex, 0, _playlist.Count - 1)]);
                _statusText = "Stopped.";
            }
            else
            {
                _trackTitle = "Cyberpunk Ambient";
                _statusText = "Ambient off.";
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
        string? ffplayPath = FindFfplay();
        if (ffplayPath == null)
        {
            lock (_lock)
            {
                _isPlaying = false;
                _isPaused = false;
                _statusText = "Install ffplay to play mp4/audio here.";
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
            var startInfo = new ProcessStartInfo(ffplayPath)
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                CreateNoWindow = true
            };

            startInfo.ArgumentList.Add("-hide_banner");
            startInfo.ArgumentList.Add("-loglevel");
            startInfo.ArgumentList.Add("error");
            startInfo.ArgumentList.Add("-nostats");
            startInfo.ArgumentList.Add("-volume");
            startInfo.ArgumentList.Add(_volumePercent.ToString());
            startInfo.ArgumentList.Add("-nodisp");
            startInfo.ArgumentList.Add("-autoexit");
            startInfo.ArgumentList.Add(path);

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
                _isPlaying = true;
                _isPaused = false;
                _stopping = false;
                _trackTitle = GetDisplayTitle(path);
                _statusText = totalCount > 1
                    ? $"Playing {currentIndex + 1}/{totalCount}."
                    : "Playing.";
            }
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
            if (advance)
                _currentIndex = (_currentIndex + 1) % _playlist.Count;
            else if (!_disposed && !_stopping)
                _statusText = "Finished.";
        }

        NotifyStateChanged();
        if (advance)
            _ = StartCurrentAsync();
    }

    private void StopProcess(bool clearSelection)
    {
        Process? process;
        lock (_lock)
        {
            _stopping = true;
            process = _process;
            _process = null;
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

    private Task RefreshStoppedTrackAsync()
    {
        lock (_lock)
        {
            if (_playlist.Count > 0)
            {
                _trackTitle = GetDisplayTitle(_playlist[Math.Clamp(_currentIndex, 0, _playlist.Count - 1)]);
                _statusText = "Selected.";
            }
        }

        NotifyStateChanged();
        return Task.CompletedTask;
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

    private static string? FindFfplay()
    {
        string executableName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "ffplay.exe" : "ffplay";
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
