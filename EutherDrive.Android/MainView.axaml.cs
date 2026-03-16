using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using EutherDrive.Audio;
using EutherDrive.Core;
using ProjectPSX.IO;

namespace EutherDrive.Android;

public partial class MainView : UserControl
{
    private const int InputLatchFrames = 6;
    private const int AndroidAudioBufferFrames = 16384;
    private const int AndroidAudioBatchFrames = 256;
    private const double TargetFrameRate = 60.0;

    private readonly MainViewModel _viewModel = new();
    private readonly object _inputSync = new();
    private readonly object _frameSync = new();
    private readonly HashSet<string> _pressedDirections = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _pressedActions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _directionLatchFrames = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _actionLatchFrames = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _virtualSystemPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _frameTimer;
    private readonly string _appDataDir;
    private readonly string _settingsPath;
    private readonly Stopwatch _perfStopwatch = Stopwatch.StartNew();
    private IEmulatorCore? _core;
    private Thread? _emulationThread;
    private volatile bool _emulationThreadRunning;
    private AudioEngine? _audioEngine;
    private WriteableBitmap? _bitmap;
    private byte[] _latestFrameBuffer = Array.Empty<byte>();
    private string? _selectedRomPath;
    private string? _selectedRomDisplayName;
    private volatile string _latestPerfSummary = "Perf idle";
    private int _presentedFrames;
    private long _latestFrameSerial;
    private long _presentedFrameSerial;
    private long _perfWindowStartTicks;
    private int _perfWindowFrames;
    private double _perfAccumulatedEmuMs;
    private double _perfAccumulatedAudioMs;
    private double _perfAccumulatedBlitMs;
    private long _emulatedFrames;
    private int _lastFrameWidth;
    private int _lastFrameHeight;

    public MainView()
    {
        InitializeComponent();
        _appDataDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _settingsPath = Path.Combine(_appDataDir, "android-settings.json");
        DataContext = _viewModel;
        _frameTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(16.666), DispatcherPriority.Render, (_, _) => PresentLatestFrame());
        LoadSettings();
        ApplySettings();
        _viewModel.SettingsHint = "Small BIOS/chip files are imported into app storage. Large disc images are intentionally not cached here.";
    }

    private async void OnPickRom(object? sender, RoutedEventArgs e)
    {
        StopSession(clearSelection: false, footerStatus: "Selecting ROM...");

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider is not { } storageProvider)
        {
            _viewModel.FooterStatus = "ROM picker is unavailable on this surface.";
            return;
        }

        var files = await storageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose ROM",
            AllowMultiple = false,
            FileTypeFilter = new List<FilePickerFileType>
            {
                new("ROM files")
                {
                    Patterns = new[]
                    {
                        "*.bin", "*.md", "*.smd", "*.gen", "*.sms", "*.gg",
                        "*.nes", "*.smc", "*.sfc", "*.pce", "*.cue", "*.iso", "*.chd", "*.z64", "*.n64", "*.v64"
                    }
                },
                FilePickerFileTypes.All
            }
        });

        if (files.Count == 0)
        {
            return;
        }

        _viewModel.FooterStatus = "Importing ROM into app storage...";

        try
        {
            ReleaseSelectedVirtualRom();
            string importedPath = await ImportRomAsync(files[0], isSystemFile: false);
            _selectedRomPath = importedPath;
            _selectedRomDisplayName = files[0].Name;
            _viewModel.SetSelectedRom(importedPath, files[0].Name);
        }
        catch (Exception ex)
        {
            _selectedRomPath = null;
            _selectedRomDisplayName = null;
            _viewModel.FooterStatus = ex.Message;
            _viewModel.ScreenOverlayVisible = true;
            _viewModel.ScreenTitle = "ROM policy";
            _viewModel.ScreenDescription = ex.Message;
        }
    }

    private void OnStartClicked(object? sender, RoutedEventArgs e)
    {
        if (!_viewModel.HasRomLoaded)
        {
            _viewModel.FooterStatus = "Pick a ROM before starting.";
            return;
        }

        try
        {
            StartCore();
            _viewModel.IsRunning = true;
            _viewModel.FooterStatus = "ROM started in Android host.";
        }
        catch (Exception ex)
        {
            _viewModel.IsRunning = false;
            string details = FormatExceptionForUi(ex);
            _viewModel.FooterStatus = $"Start failed: {details}";
            _viewModel.ScreenOverlayVisible = true;
            _viewModel.ScreenTitle = "Boot failed";
            _viewModel.ScreenDescription = details;
        }
    }

    private void OnStopClicked(object? sender, RoutedEventArgs e)
    {
        StopSession(
            clearSelection: false,
            footerStatus: _viewModel.HasRomLoaded
                ? "Session paused. ROM remains selected."
                : "Idle.");
    }

    private void OnMenuTapped(object? sender, RoutedEventArgs e)
    {
        _viewModel.SettingsVisible = true;
    }

    private void OnCloseSettings(object? sender, RoutedEventArgs e)
    {
        _viewModel.SettingsVisible = false;
    }

    private void OnOverlayPress(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Border border && border.Tag is string tag)
        {
            lock (_inputSync)
            {
                border.Classes.Add("padPressed");
                _pressedDirections.Add(tag);
                _directionLatchFrames[tag] = InputLatchFrames;
            }
            _viewModel.SetLastPressed(tag);
            UpdateOverlaySummary();
        }
    }

    private void OnOverlayRelease(object? sender, PointerReleasedEventArgs e)
    {
        if (sender is Border border && border.Tag is string tag)
        {
            lock (_inputSync)
            {
                border.Classes.Remove("padPressed");
                _pressedDirections.Remove(tag);
                _directionLatchFrames[tag] = InputLatchFrames;
            }
            UpdateOverlaySummary();
        }
    }

    private void OnOverlayCaptureLost(object? sender, RoutedEventArgs e)
    {
        if (sender is Border border && border.Tag is string tag)
        {
            lock (_inputSync)
            {
                border.Classes.Remove("padPressed");
                _pressedDirections.Remove(tag);
                _directionLatchFrames[tag] = InputLatchFrames;
            }
            UpdateOverlaySummary();
        }
    }

    private void OnOverlayButtonPress(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Avalonia.Controls.Button button && button.Tag is string tag)
        {
            lock (_inputSync)
            {
                _pressedActions.Add(tag);
                _actionLatchFrames[tag] = InputLatchFrames;
            }
            _viewModel.SetLastPressed(tag);
            UpdateOverlaySummary();
        }
    }

    private void OnOverlayButtonRelease(object? sender, PointerReleasedEventArgs e)
    {
        if (sender is Avalonia.Controls.Button button && button.Tag is string tag)
        {
            lock (_inputSync)
            {
                _pressedActions.Remove(tag);
                _actionLatchFrames[tag] = InputLatchFrames;
            }
            UpdateOverlaySummary();
        }
    }

    private void OnOverlayButtonCaptureLost(object? sender, RoutedEventArgs e)
    {
        if (sender is Avalonia.Controls.Button button && button.Tag is string tag)
        {
            lock (_inputSync)
            {
                _pressedActions.Remove(tag);
                _actionLatchFrames[tag] = InputLatchFrames;
            }
            UpdateOverlaySummary();
        }
    }

    private void UpdateOverlaySummary()
    {
        string directions;
        string actions;
        lock (_inputSync)
        {
            directions = FormatActiveDirectionsLocked();
            actions = FormatActiveActionsLocked();
        }

        _viewModel.OverlaySummary = $"D:{directions}  A:{actions}";
    }

    private void StartCore()
    {
        if (string.IsNullOrWhiteSpace(_selectedRomPath))
        {
            throw new InvalidOperationException("No ROM selected.");
        }

        StopSession(clearSelection: false, footerStatus: null);
        _presentedFrames = 0;
        ResetPerfCounters();
        _core = CreateCoreForRom(_selectedRomPath);

        if (_core is MdTracerAdapter md)
        {
            md.PowerCycleAndLoadRom(_selectedRomPath);
            md.SetMasterVolumePercent(100);
        }
        else
        {
            _core.LoadRom(_selectedRomPath);

            switch (_core)
            {
                case SnesAdapter snes:
                    snes.SetMasterVolumePercent(100);
                    break;
                case PceCdAdapter pce:
                    pce.SetMasterVolumePercent(100);
                    break;
                case NesAdapter nes:
                    nes.SetMasterVolumePercent(100);
                    break;
                case PsxAdapter psx:
                    psx.SetMasterVolumePercent(100);
                    break;
                case N64Adapter n64:
                    n64.SetMasterVolumePercent(100);
                    break;
            }
        }

        _viewModel.ScreenOverlayVisible = true;
        _viewModel.FooterStatus = $"Booting {_selectedRomDisplayName ?? Path.GetFileName(_selectedRomPath)}...";
        InitializeAudio(core: _core);
        StartEmulationLoop(_core);
        _frameTimer.Start();
    }

    private void StartEmulationLoop(IEmulatorCore? core)
    {
        if (core == null)
        {
            return;
        }

        _emulationThreadRunning = true;
        _emulationThread = new Thread(() => EmulationLoop(core))
        {
            IsBackground = true,
            Name = "EutherDrive.Android.Emulation"
        };
        _emulationThread.Start();
    }

    private void EmulationLoop(IEmulatorCore core)
    {
        long frameTicks = (long)(Stopwatch.Frequency / TargetFrameRate);
        long nextFrameTicks = Stopwatch.GetTimestamp();

        while (_emulationThreadRunning && ReferenceEquals(core, _core))
        {
            ApplyOverlayInput(core);

            try
            {
                long emuStart = _perfStopwatch.ElapsedTicks;
                core.RunFrame();
                long audioStart = _perfStopwatch.ElapsedTicks;
                SubmitAudio(core);
                long blitStart = _perfStopwatch.ElapsedTicks;
                CaptureLatestFrame(core);
                long frameEnd = _perfStopwatch.ElapsedTicks;
                UpdatePerfStats(
                    audioStart - emuStart,
                    blitStart - audioStart,
                    frameEnd - blitStart);
            }
            catch (Exception ex)
            {
                _emulationThreadRunning = false;
                string details = FormatExceptionForUi(ex);
                Dispatcher.UIThread.Post(() => HandleEmulationFailure(details));
                return;
            }

            nextFrameTicks += frameTicks;
            long nowTicks = Stopwatch.GetTimestamp();
            if (nextFrameTicks < nowTicks - frameTicks)
            {
                nextFrameTicks = nowTicks;
            }

            SleepUntil(nextFrameTicks);
        }
    }

    private unsafe void PresentLatestFrame()
    {
        UpdateOverlaySummary();
        if (_viewModel.PerfSummary != _latestPerfSummary)
        {
            _viewModel.PerfSummary = _latestPerfSummary;
        }

        long serial;
        int width;
        int height;
        int srcStride;
        byte[] frameBuffer;

        lock (_frameSync)
        {
            serial = _latestFrameSerial;
            if (serial == 0 || serial == _presentedFrameSerial)
            {
                return;
            }

            width = _lastFrameWidth;
            height = _lastFrameHeight;
            srcStride = width * 4;
            frameBuffer = _latestFrameBuffer;

            EnsureBitmap(width, height);
            if (_bitmap == null)
            {
                return;
            }

            using var fb = _bitmap.Lock();
            int dstStride = fb.RowBytes;
            int rowBytes = Math.Min(srcStride, dstStride);
            int pixelCount = rowBytes / 4;
            if (rowBytes <= 0)
            {
                return;
            }

            fixed (byte* srcPtr = frameBuffer)
            {
                byte* dstPtr = (byte*)fb.Address.ToPointer();
                for (int y = 0; y < height; y++)
                {
                    uint* srcRow = (uint*)(srcPtr + (y * srcStride));
                    uint* dstRow = (uint*)(dstPtr + (y * dstStride));
                    for (int x = 0; x < pixelCount; x++)
                    {
                        dstRow[x] = srcRow[x];
                    }
                }
            }

            _presentedFrameSerial = serial;
        }

        ScreenImage.InvalidateVisual();
        _presentedFrames++;
        if (_presentedFrames == 1)
        {
            _viewModel.ScreenOverlayVisible = false;
            _viewModel.FooterStatus = $"Rendering {_selectedRomDisplayName ?? "ROM"}";
        }
    }

    private void HandleEmulationFailure(string details)
    {
        StopSession(clearSelection: false, footerStatus: null);
        _viewModel.ScreenOverlayVisible = true;
        _viewModel.ScreenTitle = "Runtime error";
        _viewModel.ScreenDescription = details;
        _viewModel.FooterStatus = $"Runtime error: {details}";
    }

    private static string FormatExceptionForUi(Exception ex)
    {
        string typeName = ex.GetType().Name;
        string message = string.IsNullOrWhiteSpace(ex.Message) ? "(no message)" : ex.Message;
        string? firstFrame = ex.StackTrace?
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();

        return string.IsNullOrWhiteSpace(firstFrame)
            ? $"{typeName}: {message}"
            : $"{typeName}: {message}\n{firstFrame.Trim()}";
    }

    private void ResetPerfCounters()
    {
        _perfWindowStartTicks = _perfStopwatch.ElapsedTicks;
        _perfWindowFrames = 0;
        _perfAccumulatedEmuMs = 0;
        _perfAccumulatedAudioMs = 0;
        _perfAccumulatedBlitMs = 0;
        _emulatedFrames = 0;
        _presentedFrameSerial = 0;
        _latestFrameSerial = 0;
        _lastFrameWidth = 0;
        _lastFrameHeight = 0;
        _latestPerfSummary = "Perf idle";
        _viewModel.PerfSummary = "Perf idle";
    }

    private void UpdatePerfStats(long emuTicks, long audioTicks, long blitTicks)
    {
        _perfWindowFrames++;
        _perfAccumulatedEmuMs += StopwatchTicksToMs(emuTicks);
        _perfAccumulatedAudioMs += StopwatchTicksToMs(audioTicks);
        _perfAccumulatedBlitMs += StopwatchTicksToMs(blitTicks);

        long nowTicks = _perfStopwatch.ElapsedTicks;
        double windowMs = StopwatchTicksToMs(nowTicks - _perfWindowStartTicks);
        if (windowMs < 250)
            return;

        double avgFrameMs = windowMs / _perfWindowFrames;
        double fps = avgFrameMs > 0 ? 1000.0 / avgFrameMs : 0;
        _latestPerfSummary =
            $"Perf  FPS:{fps:0}  Emu:{_perfAccumulatedEmuMs / _perfWindowFrames:0.0}ms  Audio:{_perfAccumulatedAudioMs / _perfWindowFrames:0.0}ms  Blit:{_perfAccumulatedBlitMs / _perfWindowFrames:0.0}ms  Frame:{_emulatedFrames}  Res:{_lastFrameWidth}x{_lastFrameHeight}";

        _perfWindowStartTicks = nowTicks;
        _perfWindowFrames = 0;
        _perfAccumulatedEmuMs = 0;
        _perfAccumulatedAudioMs = 0;
        _perfAccumulatedBlitMs = 0;
    }

    private static double StopwatchTicksToMs(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

    private static void SleepUntil(long targetTicks)
    {
        while (true)
        {
            long remainingTicks = targetTicks - Stopwatch.GetTimestamp();
            if (remainingTicks <= 0)
            {
                return;
            }

            double remainingMs = StopwatchTicksToMs(remainingTicks);
            if (remainingMs > 1.5)
            {
                Thread.Sleep(Math.Max(0, (int)remainingMs - 1));
            }
            else
            {
                Thread.SpinWait(64);
            }
        }
    }

    private void StopEmulationLoop()
    {
        _emulationThreadRunning = false;
        Thread? thread = _emulationThread;
        _emulationThread = null;
        if (thread != null && thread != Thread.CurrentThread)
        {
            thread.Join();
        }
    }

    private void StopSession(bool clearSelection, string? footerStatus)
    {
        _frameTimer.Stop();
        StopEmulationLoop();
        IEmulatorCore? core = _core;
        _core = null;
        _audioEngine?.Dispose();
        _audioEngine = null;
        _bitmap = null;
        _presentedFrames = 0;
        ResetPerfCounters();
        lock (_frameSync)
        {
            _latestFrameBuffer = Array.Empty<byte>();
        }
        ScreenImage.Source = null;
        _viewModel.IsRunning = false;
        _viewModel.ScreenOverlayVisible = true;

        if (core is IDisposable disposableCore)
        {
            disposableCore.Dispose();
        }

        if (clearSelection)
        {
            ReleaseSelectedVirtualRom();
            _selectedRomPath = null;
            _selectedRomDisplayName = null;
        }

        if (footerStatus != null)
        {
            _viewModel.FooterStatus = footerStatus;
        }
    }

    private void InitializeAudio(IEmulatorCore? core)
    {
        _audioEngine?.Dispose();
        _audioEngine = null;

        if (core == null)
        {
            return;
        }

        ReadOnlySpan<short> initialAudio = core.GetAudioBuffer(out int sampleRate, out int channels);
        if (sampleRate <= 0 || channels <= 0)
        {
            return;
        }

        try
        {
            _audioEngine = new AudioEngine(
                new AndroidAudioSink(),
                sampleRate,
                channels,
                framesPerBatch: AndroidAudioBatchFrames,
                bufferFrames: AndroidAudioBufferFrames);
            _audioEngine.SetTargetBufferedFrames((int)(sampleRate * 0.20));
            _audioEngine.Start();

            if (!initialAudio.IsEmpty)
            {
                _audioEngine.Submit(initialAudio);
            }
        }
        catch (Exception ex)
        {
            _audioEngine?.Dispose();
            _audioEngine = null;
            _viewModel.FooterStatus = $"Audio init failed: {ex.Message}";
        }
    }

    private void SubmitAudio(IEmulatorCore core)
    {
        AudioEngine? audioEngine = _audioEngine;
        if (audioEngine == null)
        {
            return;
        }

        ReadOnlySpan<short> audio = core.GetAudioBuffer(out int sampleRate, out int channels);
        if (audio.IsEmpty || sampleRate <= 0 || channels <= 0)
        {
            return;
        }

        audioEngine.Submit(audio);
    }

    private async Task<string> ImportRomAsync(IStorageFile file, bool isSystemFile)
    {
        string extension = Path.GetExtension(file.Name);
        string? localPath = file.TryGetLocalPath();
        if (!isSystemFile
            && !string.IsNullOrWhiteSpace(localPath)
            && File.Exists(localPath))
        {
            return localPath;
        }

        if (!isSystemFile)
        {
            string ext = extension.ToLowerInvariant();
            if (ext is ".cue" or ".iso" or ".img" or ".chd" or ".pbp")
            {
                throw new InvalidOperationException("Large/disc-based ROMs are not cached on Android yet. A streaming backend is still needed for PS1/CD images.");
            }
        }

        string baseDir = Path.Combine(_appDataDir, isSystemFile ? "system-files" : "rom-cache");
        Directory.CreateDirectory(baseDir);

        string safeName = string.IsNullOrWhiteSpace(Path.GetFileNameWithoutExtension(file.Name))
            ? "rom"
            : Path.GetFileNameWithoutExtension(file.Name);
        string targetPath = Path.Combine(baseDir, $"{safeName}-{Guid.NewGuid():N}{extension}");

        Stream? source = await file.OpenReadAsync();
        bool transferredToVirtualFile = false;
        try
        {
            if (!isSystemFile && TryRegisterVirtualDiscSource(file, source, out string? virtualPath))
            {
                transferredToVirtualFile = true;
                return virtualPath!;
            }

            if (!isSystemFile && source.CanSeek && source.Length > 128L * 1024 * 1024)
            {
                throw new InvalidOperationException("This ROM is too large for temporary Android caching. Disc images need direct streaming support instead.");
            }

            await using FileStream destination = File.Create(targetPath);
            await source.CopyToAsync(destination);
            await destination.FlushAsync();
            return targetPath;
        }
        finally
        {
            if (!transferredToVirtualFile)
            {
                await source.DisposeAsync();
            }
        }
    }

    private static bool TryRegisterVirtualDiscSource(IStorageFile file, Stream source, out string? virtualPath)
    {
        virtualPath = null;

        if (!source.CanSeek || source.Length <= 128L * 1024 * 1024)
        {
            return false;
        }

        string ext = Path.GetExtension(file.Name).ToLowerInvariant();
        if (ext != ".bin")
        {
            return false;
        }

        string candidatePath = VirtualFileSystem.RegisterSharedStream(file.Name, source, ownsStream: true);

        OpticalDiscKind discKind = OpticalDiscDetector.Detect(candidatePath);
        if (discKind != OpticalDiscKind.Psx)
        {
            VirtualFileSystem.Unregister(candidatePath);
            throw new InvalidOperationException("Large direct-stream discs are currently only enabled for single-file PSX .bin images on Android.");
        }

        virtualPath = candidatePath;
        return true;
    }

    private void ReleaseSelectedVirtualRom()
    {
        if (!string.IsNullOrWhiteSpace(_selectedRomPath) && VirtualFileSystem.IsVirtualPath(_selectedRomPath))
        {
            VirtualFileSystem.Unregister(_selectedRomPath);
        }
    }

    private async Task PickSystemFileAsync(string key, string title, string[] patterns)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider is not { } storageProvider)
        {
            _viewModel.FooterStatus = "System file picker is unavailable on this surface.";
            return;
        }

        var files = await storageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType(title)
                {
                    Patterns = patterns
                }
            }
        });

        if (files.Count == 0)
        {
            return;
        }

        try
        {
            string importedPath = await ImportRomAsync(files[0], isSystemFile: true);
            _viewModel.SetSystemFile(key, importedPath, files[0].Name);
            SaveSettings();
            ApplySettings();
            _viewModel.FooterStatus = $"{key} selected";
        }
        catch (Exception ex)
        {
            _viewModel.FooterStatus = $"{key} import failed: {ex.Message}";
        }
    }

    private void ClearSystemFile(string key)
    {
        _viewModel.SetSystemFile(key, null, null);
        SaveSettings();
        ApplySettings();
        _viewModel.FooterStatus = $"{key} cleared";
    }

    private async void OnPickPceBios(object? sender, RoutedEventArgs e) => await PickSystemFileAsync("PCE BIOS", "Select PCE BIOS", new[] { "*.pce", "*.bin", "*.*" });
    private void OnClearPceBios(object? sender, RoutedEventArgs e) => ClearSystemFile("PCE BIOS");
    private async void OnPickPsxBios(object? sender, RoutedEventArgs e) => await PickSystemFileAsync("PSX BIOS", "Select PSX BIOS", new[] { "*.bin", "*.*" });
    private void OnClearPsxBios(object? sender, RoutedEventArgs e) => ClearSystemFile("PSX BIOS");
    private async void OnPickDsp1(object? sender, RoutedEventArgs e) => await PickSystemFileAsync("DSP1", "Select DSP1 ROM", new[] { "*.bin", "*.*" });
    private void OnClearDsp1(object? sender, RoutedEventArgs e) => ClearSystemFile("DSP1");
    private async void OnPickDsp2(object? sender, RoutedEventArgs e) => await PickSystemFileAsync("DSP2", "Select DSP2 ROM", new[] { "*.bin", "*.*" });
    private void OnClearDsp2(object? sender, RoutedEventArgs e) => ClearSystemFile("DSP2");
    private async void OnPickDsp3(object? sender, RoutedEventArgs e) => await PickSystemFileAsync("DSP3", "Select DSP3 ROM", new[] { "*.bin", "*.*" });
    private void OnClearDsp3(object? sender, RoutedEventArgs e) => ClearSystemFile("DSP3");
    private async void OnPickDsp4(object? sender, RoutedEventArgs e) => await PickSystemFileAsync("DSP4", "Select DSP4 ROM", new[] { "*.bin", "*.*" });
    private void OnClearDsp4(object? sender, RoutedEventArgs e) => ClearSystemFile("DSP4");
    private async void OnPickSt010(object? sender, RoutedEventArgs e) => await PickSystemFileAsync("ST010", "Select ST010 ROM", new[] { "*.bin", "*.*" });
    private void OnClearSt010(object? sender, RoutedEventArgs e) => ClearSystemFile("ST010");
    private async void OnPickSt011(object? sender, RoutedEventArgs e) => await PickSystemFileAsync("ST011", "Select ST011 ROM", new[] { "*.bin", "*.rom", "*.*" });
    private void OnClearSt011(object? sender, RoutedEventArgs e) => ClearSystemFile("ST011");
    private async void OnPickSt018(object? sender, RoutedEventArgs e) => await PickSystemFileAsync("ST018", "Select ST018 ROM", new[] { "*.bin", "*.rom", "*.*" });
    private void OnClearSt018(object? sender, RoutedEventArgs e) => ClearSystemFile("ST018");

    private void ApplySettings()
    {
        PceCdAdapter.BiosPath = _viewModel.PceBiosPath;
        PsxAdapter.BiosPath = RegisterSystemFileVirtualPath("PSX BIOS", _viewModel.PsxBiosPath, _viewModel.PsxBiosDisplay);

        SetEnv("EUTHERDRIVE_DSP1_ROM", _viewModel.Dsp1Path);
        SetEnv("EUTHERDRIVE_DSP2_ROM", _viewModel.Dsp2Path);
        SetEnv("EUTHERDRIVE_DSP3_ROM", _viewModel.Dsp3Path);
        SetEnv("EUTHERDRIVE_DSP4_ROM", _viewModel.Dsp4Path);
        SetEnv("EUTHERDRIVE_ST010_ROM", _viewModel.St010Path);
        SetEnv("EUTHERDRIVE_ST011_ROM", _viewModel.St011Path);
        SetEnv("EUTHERDRIVE_ST018_ROM", _viewModel.St018Path);
    }

    private string? RegisterSystemFileVirtualPath(string key, string? physicalPath, string? displayName)
    {
        if (_virtualSystemPaths.TryGetValue(key, out string existingPath))
        {
            VirtualFileSystem.Unregister(existingPath);
            _virtualSystemPaths.Remove(key);
        }

        if (string.IsNullOrWhiteSpace(physicalPath) || !File.Exists(physicalPath))
        {
            return null;
        }

        byte[] data = File.ReadAllBytes(physicalPath);
        string virtualPath = VirtualFileSystem.RegisterBytes(displayName ?? Path.GetFileName(physicalPath), data);
        _virtualSystemPaths[key] = virtualPath;
        return virtualPath;
    }

    private static void SetEnv(string key, string? value)
    {
        Environment.SetEnvironmentVariable(key, string.IsNullOrWhiteSpace(value) ? null : value);
    }

    private void SaveSettings()
    {
        var settings = new AndroidSettings
        {
            PceBiosPath = _viewModel.PceBiosPath,
            PceBiosDisplay = _viewModel.PceBiosDisplay,
            PsxBiosPath = _viewModel.PsxBiosPath,
            PsxBiosDisplay = _viewModel.PsxBiosDisplay,
            Dsp1Path = _viewModel.Dsp1Path,
            Dsp1Display = _viewModel.Dsp1Display,
            Dsp2Path = _viewModel.Dsp2Path,
            Dsp2Display = _viewModel.Dsp2Display,
            Dsp3Path = _viewModel.Dsp3Path,
            Dsp3Display = _viewModel.Dsp3Display,
            Dsp4Path = _viewModel.Dsp4Path,
            Dsp4Display = _viewModel.Dsp4Display,
            St010Path = _viewModel.St010Path,
            St010Display = _viewModel.St010Display,
            St011Path = _viewModel.St011Path,
            St011Display = _viewModel.St011Display,
            St018Path = _viewModel.St018Path,
            St018Display = _viewModel.St018Display
        };

        Directory.CreateDirectory(_appDataDir);
        File.WriteAllText(_settingsPath, JsonSerializer.Serialize(settings));
    }

    private void LoadSettings()
    {
        if (!File.Exists(_settingsPath))
        {
            return;
        }

        try
        {
            AndroidSettings? settings = JsonSerializer.Deserialize<AndroidSettings>(File.ReadAllText(_settingsPath));
            if (settings == null)
            {
                return;
            }

            _viewModel.PceBiosPath = settings.PceBiosPath;
            _viewModel.PceBiosDisplay = settings.PceBiosDisplay ?? "(auto)";
            _viewModel.PsxBiosPath = settings.PsxBiosPath;
            _viewModel.PsxBiosDisplay = settings.PsxBiosDisplay ?? "(none)";
            _viewModel.Dsp1Path = settings.Dsp1Path;
            _viewModel.Dsp1Display = settings.Dsp1Display ?? "(none)";
            _viewModel.Dsp2Path = settings.Dsp2Path;
            _viewModel.Dsp2Display = settings.Dsp2Display ?? "(none)";
            _viewModel.Dsp3Path = settings.Dsp3Path;
            _viewModel.Dsp3Display = settings.Dsp3Display ?? "(none)";
            _viewModel.Dsp4Path = settings.Dsp4Path;
            _viewModel.Dsp4Display = settings.Dsp4Display ?? "(none)";
            _viewModel.St010Path = settings.St010Path;
            _viewModel.St010Display = settings.St010Display ?? "(none)";
            _viewModel.St011Path = settings.St011Path;
            _viewModel.St011Display = settings.St011Display ?? "(none)";
            _viewModel.St018Path = settings.St018Path;
            _viewModel.St018Display = settings.St018Display ?? "(none)";
        }
        catch
        {
            _viewModel.SettingsHint = "Settings file was unreadable and will be recreated on next save.";
        }
    }

    private void ApplyOverlayInput(IEmulatorCore core)
    {
        lock (_inputSync)
        {
            bool up = IsDirectionActiveLocked("Up");
            bool down = IsDirectionActiveLocked("Down");
            bool left = IsDirectionActiveLocked("Left");
            bool right = IsDirectionActiveLocked("Right");
            bool a = IsActionActiveLocked("A");
            bool b = IsActionActiveLocked("B");
            bool c = IsActionActiveLocked("C") || IsActionActiveLocked("Y");
            bool start = IsActionActiveLocked("Start");
            bool x = IsActionActiveLocked("X");
            bool y = IsActionActiveLocked("Y");
            bool z = IsActionActiveLocked("Z");
            bool mode = IsActionActiveLocked("Select") || IsActionActiveLocked("Menu");

            core.SetInputState(up, down, left, right, a, b, c, start, x, y, z, mode, PadType.SixButton);
            AdvanceLatchFrames(_directionLatchFrames, _pressedDirections);
            AdvanceLatchFrames(_actionLatchFrames, _pressedActions);
        }
    }

    private static void AdvanceLatchFrames(Dictionary<string, int> latches, HashSet<string> pressed)
    {
        if (latches.Count == 0)
        {
            return;
        }

        List<string>? expired = null;
        foreach (string key in latches.Keys.ToArray())
        {
            int frames = latches[key];
            if (pressed.Contains(key))
            {
                continue;
            }

            if (frames <= 1)
            {
                expired ??= new List<string>();
                expired.Add(key);
            }
            else
            {
                latches[key] = frames - 1;
            }
        }

        if (expired == null)
        {
            return;
        }

        foreach (string key in expired)
        {
            latches.Remove(key);
        }
    }

    private bool IsDirectionActiveLocked(string tag)
    {
        return _pressedDirections.Contains(tag)
            || (_directionLatchFrames.TryGetValue(tag, out int frames) && frames > 0);
    }

    private bool IsActionActiveLocked(string tag)
    {
        return _pressedActions.Contains(tag)
            || (_actionLatchFrames.TryGetValue(tag, out int frames) && frames > 0);
    }

    private string FormatActiveDirectionsLocked()
    {
        var active = new List<string>(4);
        foreach (string tag in new[] { "Up", "Down", "Left", "Right" })
        {
            if (IsDirectionActiveLocked(tag))
            {
                active.Add(tag);
            }
        }

        return active.Count == 0 ? "-" : string.Join(" ", active);
    }

    private string FormatActiveActionsLocked()
    {
        var active = new List<string>(8);
        foreach (string tag in new[] { "A", "B", "X", "Y", "Start", "Select", "Menu" })
        {
            if (IsActionActiveLocked(tag))
            {
                active.Add(tag);
            }
        }

        return active.Count == 0 ? "-" : string.Join(" ", active);
    }

    private unsafe void CaptureLatestFrame(IEmulatorCore core)
    {
        var src = core.GetFrameBuffer(out int width, out int height, out int srcStride);
        if (src.IsEmpty || width <= 0 || height <= 0 || srcStride <= 0)
        {
            return;
        }

        int dstStride = width * 4;
        int rowBytes = Math.Min(width * 4, srcStride);
        int pixelCount = rowBytes / 4;
        if (rowBytes <= 0)
        {
            return;
        }

        lock (_frameSync)
        {
            _lastFrameWidth = width;
            _lastFrameHeight = height;
            int requiredBytes = dstStride * height;
            if (_latestFrameBuffer.Length < requiredBytes)
            {
                _latestFrameBuffer = new byte[requiredBytes];
            }

            fixed (byte* srcPtr = src)
            fixed (byte* dstPtr = _latestFrameBuffer)
            {
                for (int y = 0; y < height; y++)
                {
                    uint* srcRow = (uint*)(srcPtr + (y * srcStride));
                    uint* dstRow = (uint*)(dstPtr + (y * dstStride));

                    for (int x = 0; x < pixelCount; x++)
                    {
                        dstRow[x] = srcRow[x] | 0xFF000000u;
                    }
                }
            }

            _emulatedFrames++;
            _latestFrameSerial++;
        }
    }

    private void EnsureBitmap(int width, int height)
    {
        if (_bitmap != null && _bitmap.PixelSize.Width == width && _bitmap.PixelSize.Height == height)
        {
            return;
        }

        _bitmap = new WriteableBitmap(
            new PixelSize(width, height),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Unpremul);

        ScreenImage.Source = _bitmap;
    }

    private static IEmulatorCore CreateCoreForRom(string path)
    {
        OpticalDiscKind discKind = OpticalDiscDetector.Detect(path);
        switch (discKind)
        {
            case OpticalDiscKind.SegaCd:
                return new EutherDrive.Core.SegaCd.SegaCdAdapter();
            case OpticalDiscKind.Psx:
                return new PsxAdapter();
            case OpticalDiscKind.PceCd:
                return new PceCdAdapter();
        }

        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext == ".pce" || ext == ".cue")
        {
            return new PceCdAdapter();
        }

        if (ext is ".z64" or ".n64" or ".v64")
        {
            return new N64Adapter();
        }

        if (ext is ".smc" or ".sfc")
        {
            return new SnesAdapter();
        }

        if (ext == ".nes")
        {
            return new NesAdapter();
        }

        return new MdTracerAdapter();
    }

    private sealed class MainViewModel : INotifyPropertyChanged
    {
        private string _headerSubtitle = "Big screen first. ROM picker and touch controls live around it.";
        private string _statusPill = "Idle";
        private string _selectedConsoleLabel = "No ROM";
        private string _screenTitle = "Main screen comes first";
        private string _screenDescription = "This Android fork now has a focused play surface instead of carrying the full desktop menu stack.";
        private string _currentRomDisplay = "No ROM selected.";
        private string _lastPressedDisplay = "-";
        private string _footerStatus = "Ready for ROM selection.";
        private string _perfSummary = "Perf idle";
        private string _overlaySummary = "D:-  A:-";
        private bool _settingsVisible;
        private string _settingsHint = "Pick BIOS and chip ROMs here.";
        private string _pceBiosDisplay = "(auto)";
        private string _psxBiosDisplay = "(none)";
        private string _dsp1Display = "(none)";
        private string _dsp2Display = "(none)";
        private string _dsp3Display = "(none)";
        private string _dsp4Display = "(none)";
        private string _st010Display = "(none)";
        private string _st011Display = "(none)";
        private string _st018Display = "(none)";
        private bool _screenOverlayVisible = true;
        private bool _isRunning;
        private string? _selectedRomPath;
        private string? _pceBiosPath;
        private string? _psxBiosPath;
        private string? _dsp1Path;
        private string? _dsp2Path;
        private string? _dsp3Path;
        private string? _dsp4Path;
        private string? _st010Path;
        private string? _st011Path;
        private string? _st018Path;

        public event PropertyChangedEventHandler? PropertyChanged;

        public string HeaderSubtitle
        {
            get => _headerSubtitle;
            set => SetField(ref _headerSubtitle, value);
        }

        public string StatusPill
        {
            get => _statusPill;
            set => SetField(ref _statusPill, value);
        }

        public string SelectedConsoleLabel
        {
            get => _selectedConsoleLabel;
            set => SetField(ref _selectedConsoleLabel, value);
        }

        public string ScreenTitle
        {
            get => _screenTitle;
            set => SetField(ref _screenTitle, value);
        }

        public string ScreenDescription
        {
            get => _screenDescription;
            set => SetField(ref _screenDescription, value);
        }

        public string CurrentRomDisplay
        {
            get => _currentRomDisplay;
            set => SetField(ref _currentRomDisplay, value);
        }

        public string LastPressedDisplay
        {
            get => _lastPressedDisplay;
            set => SetField(ref _lastPressedDisplay, value);
        }

        public string FooterStatus
        {
            get => _footerStatus;
            set => SetField(ref _footerStatus, value);
        }

        public string PerfSummary
        {
            get => _perfSummary;
            set => SetField(ref _perfSummary, value);
        }

        public string OverlaySummary
        {
            get => _overlaySummary;
            set => SetField(ref _overlaySummary, value);
        }

        public bool SettingsVisible
        {
            get => _settingsVisible;
            set => SetField(ref _settingsVisible, value);
        }

        public string SettingsHint
        {
            get => _settingsHint;
            set => SetField(ref _settingsHint, value);
        }

        public string PceBiosDisplay { get => _pceBiosDisplay; set => SetField(ref _pceBiosDisplay, value); }
        public string PsxBiosDisplay { get => _psxBiosDisplay; set => SetField(ref _psxBiosDisplay, value); }
        public string Dsp1Display { get => _dsp1Display; set => SetField(ref _dsp1Display, value); }
        public string Dsp2Display { get => _dsp2Display; set => SetField(ref _dsp2Display, value); }
        public string Dsp3Display { get => _dsp3Display; set => SetField(ref _dsp3Display, value); }
        public string Dsp4Display { get => _dsp4Display; set => SetField(ref _dsp4Display, value); }
        public string St010Display { get => _st010Display; set => SetField(ref _st010Display, value); }
        public string St011Display { get => _st011Display; set => SetField(ref _st011Display, value); }
        public string St018Display { get => _st018Display; set => SetField(ref _st018Display, value); }

        public bool ScreenOverlayVisible
        {
            get => _screenOverlayVisible;
            set => SetField(ref _screenOverlayVisible, value);
        }

        public string? PceBiosPath { get => _pceBiosPath; set => SetField(ref _pceBiosPath, value); }
        public string? PsxBiosPath { get => _psxBiosPath; set => SetField(ref _psxBiosPath, value); }
        public string? Dsp1Path { get => _dsp1Path; set => SetField(ref _dsp1Path, value); }
        public string? Dsp2Path { get => _dsp2Path; set => SetField(ref _dsp2Path, value); }
        public string? Dsp3Path { get => _dsp3Path; set => SetField(ref _dsp3Path, value); }
        public string? Dsp4Path { get => _dsp4Path; set => SetField(ref _dsp4Path, value); }
        public string? St010Path { get => _st010Path; set => SetField(ref _st010Path, value); }
        public string? St011Path { get => _st011Path; set => SetField(ref _st011Path, value); }
        public string? St018Path { get => _st018Path; set => SetField(ref _st018Path, value); }

        public bool HasRomLoaded => !string.IsNullOrWhiteSpace(_selectedRomPath);

        public bool IsRunning
        {
            get => _isRunning;
            set
            {
                if (!SetField(ref _isRunning, value))
                {
                    return;
                }

                StatusPill = value ? "Running shell" : (HasRomLoaded ? "ROM loaded" : "Idle");
                ScreenTitle = value ? "Touch overlay armed" : (HasRomLoaded ? "ROM ready to boot" : "Main screen comes first");
            }
        }

        public void SetSelectedRom(string romPath, string? displayName = null)
        {
            _selectedRomPath = romPath;
            CurrentRomDisplay = displayName ?? romPath;
            SelectedConsoleLabel = GuessConsole(displayName ?? romPath);
            FooterStatus = "ROM imported. Press Start to boot.";
            ScreenOverlayVisible = true;
            HeaderSubtitle = "Focused Android layout with quick ROM access and on-screen controls.";
            StatusPill = "ROM loaded";
            ScreenTitle = "ROM selected";
            ScreenDescription = displayName ?? Path.GetFileName(romPath);
            OnPropertyChanged(nameof(HasRomLoaded));
        }

        public void SetSystemFile(string key, string? path, string? displayName)
        {
            string label = path == null ? "(none)" : (displayName ?? Path.GetFileName(path));
            switch (key)
            {
                case "PCE BIOS":
                    PceBiosPath = path;
                    PceBiosDisplay = path == null ? "(auto)" : label;
                    break;
                case "PSX BIOS":
                    PsxBiosPath = path;
                    PsxBiosDisplay = label;
                    break;
                case "DSP1":
                    Dsp1Path = path;
                    Dsp1Display = label;
                    break;
                case "DSP2":
                    Dsp2Path = path;
                    Dsp2Display = label;
                    break;
                case "DSP3":
                    Dsp3Path = path;
                    Dsp3Display = label;
                    break;
                case "DSP4":
                    Dsp4Path = path;
                    Dsp4Display = label;
                    break;
                case "ST010":
                    St010Path = path;
                    St010Display = label;
                    break;
                case "ST011":
                    St011Path = path;
                    St011Display = label;
                    break;
                case "ST018":
                    St018Path = path;
                    St018Display = label;
                    break;
            }
        }

        public void SetLastPressed(string value)
        {
            LastPressedDisplay = value;
        }

        private static string GuessConsole(string romPath)
        {
            string ext = Path.GetExtension(romPath).ToLowerInvariant();
            return ext switch
            {
                ".bin" or ".md" or ".smd" or ".gen" => "Mega Drive",
                ".sms" or ".gg" => "Master System",
                ".smc" or ".sfc" => "SNES",
                ".nes" => "NES",
                ".pce" => "PC Engine",
                ".cue" or ".iso" or ".chd" => "Disc image",
                ".z64" or ".n64" or ".v64" => "Nintendo 64",
                _ => "ROM"
            };
        }

        private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
            {
                return false;
            }

            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    private sealed class AndroidSettings
    {
        public string? PceBiosPath { get; set; }
        public string? PceBiosDisplay { get; set; }
        public string? PsxBiosPath { get; set; }
        public string? PsxBiosDisplay { get; set; }
        public string? Dsp1Path { get; set; }
        public string? Dsp1Display { get; set; }
        public string? Dsp2Path { get; set; }
        public string? Dsp2Display { get; set; }
        public string? Dsp3Path { get; set; }
        public string? Dsp3Display { get; set; }
        public string? Dsp4Path { get; set; }
        public string? Dsp4Display { get; set; }
        public string? St010Path { get; set; }
        public string? St010Display { get; set; }
        public string? St011Path { get; set; }
        public string? St011Display { get; set; }
        public string? St018Path { get; set; }
        public string? St018Display { get; set; }
    }
}
