using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
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
using EutherDrive.Core;
using EutherDrive.UI.Audio;
using EutherDrive.Audio;

namespace EutherDrive.UI;

public partial class MainWindow : Window
{
    private IEmulatorCore? _core;

    // EN bitmap som vi alltid blitar till
    private WriteableBitmap? _wb;

    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _fpsSw = Stopwatch.StartNew();
    private readonly Stopwatch _earlyMagentaTimer = new();
    private bool _earlyMagentaReported;
    private int _frames;
    private long _lastStatusUpdateMs;
    private string _lastStatusKeys = string.Empty;
    private long _presentedFrames;
    private static readonly bool FrameBufferTraceEnabled =
        Environment.GetEnvironmentVariable("EUTHERDRIVE_FB_TRACE") == "1";
    private static readonly bool SkipUiBlitEnabled =
        Environment.GetEnvironmentVariable("EUTHERDRIVE_SKIP_UI_BLIT") == "1";
    private readonly Action _presentOnUiAction;
    private IEmulatorCore? _pendingPresentCore;

    private string? _romPath;

    // Input “håll nere”
    private readonly HashSet<Key> _keysDown = new();
    private OpenAlAudioOutput? _audioOutput;
    private AudioEngine? _audioEngine;
    private bool _audioEnabled;
    private bool _audioFormatMismatchLogged;
    private const int AudioSampleRate = 44100;
    private const int AudioChannels = 2;
    private static readonly bool AudioEnvEnabled = Environment.GetEnvironmentVariable("EUTHERDRIVE_AUDIO") == "1";
    private static readonly bool AudioTimedEnvEnabled = Environment.GetEnvironmentVariable("EUTHERDRIVE_AUDIO_TIMED") == "1";
    private static readonly bool YmEnvEnabled = Environment.GetEnvironmentVariable("EUTHERDRIVE_YM") == "1";
    private static readonly bool AudioStatsEnabled =
        Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_AUDIO_STATS") == "1";
    private static readonly bool TraceAudioLevel =
        Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_AUDLVL") == "1";
    private static readonly bool TracePerf =
        Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_PERF") == "1";
    private const int AudioMaxFramesPerTick = 4096;
    private TextWriter? _originalConsoleOut;
    private StreamWriter? _romLogWriter;
    private bool _toneTestRunning;
    private bool _psgBlipRunning;
    private bool _audioTimedEnabled;
    private long _audioLastTicks;
    private double _audioFrameAccumulator;
    private long _audioLastDropLogTicks;
    private ConsoleRegion _regionOverride = ConsoleRegion.Auto;
    private ConsoleRegion _defaultRegionOverride = ConsoleRegion.Auto;
    private ConsoleRegion _romRegionHint = ConsoleRegion.Auto;
    private readonly Dictionary<string, ConsoleRegion> _romRegionOverrides = new(StringComparer.OrdinalIgnoreCase);
    private string? _romRegionKey;
    private bool _regionOverrideUpdating;
    private const string RegionSettingsFileName = "eutherdrive_region.txt";
    private const string LastRomPathFileName = "eutherdrive_last_rom.txt";

    // UI heartbeat
    private readonly bool _heartbeatEnabled = Environment.GetEnvironmentVariable("EUTHERDRIVE_UI_HEARTBEAT") == "1";
    private DispatcherTimer? _heartbeatTimer;
    private int _heartbeatTicks;
    private int _tickTraceCount;
    private readonly bool _tickTraceEnabled = Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_TICK") == "1";
    private bool _heartbeatState;
    private Thread? _emuThread;
    private volatile bool _emuRunning;
    private double _emuTargetFps = 60.0;
    private int _padTypeRaw = (int)PadType.ThreeButton;

    public MainWindow()
    {
        InitializeComponent();

        HookInput();

        Focusable = true;
        AttachedToVisualTree += (_, __) => Focus();

        StatusText.Text = "Idle";

        _audioEnabled = true;
        if (AudioEnabledCheck != null)
            AudioEnabledCheck.IsChecked = true;
        _audioTimedEnabled = AudioTimedEnvEnabled || _audioEnabled;
        UpdatePadTypeFromUi();
        if (SixButtonPadCheck != null)
        {
            SixButtonPadCheck.Checked += (_, _) => UpdatePadTypeFromUi();
            SixButtonPadCheck.Unchecked += (_, _) => UpdatePadTypeFromUi();
        }
        LoadLastRomPath();
        LoadRegionOverrideSetting();
        UpdateRegionOverrideCombo();
        UpdateRomRegionHintText();

        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(16.666), DispatcherPriority.Render, (_, _) => Tick());
        _presentOnUiAction = PresentPendingFrame;
    }

    public ConsoleRegion RegionOverride
    {
        get => _regionOverride;
        private set => _regionOverride = value;
    }

    public ConsoleRegion RomRegionHint
    {
        get => _romRegionHint;
        private set => _romRegionHint = value;
    }

    private void OnFrameRateChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_core is MdTracerAdapter adapter && FrameRateCombo?.SelectedItem is ComboBoxItem item)
        {
            var tag = item.Tag?.ToString();
            var mode = tag switch
            {
                "Hz50" => FrameRateMode.Hz50,
                "Hz60" => FrameRateMode.Hz60,
                _ => FrameRateMode.Auto
            };
            adapter.SetFrameRateMode(mode);
            UpdateEmuTargetFps();
        }
    }

    private void UpdateEmuTargetFps()
    {
        double target = 60.0;
        if (_core is MdTracerAdapter adapter)
            target = adapter.GetTargetFps();
        _emuTargetFps = target;
    }

    private void OnApplyCpuCycles(object? sender, RoutedEventArgs e)
    {
        if (_core is not MdTracerAdapter adapter)
        {
            StatusText.Text = "CPU cycles: no tracing core";
            return;
        }

        if (int.TryParse(CpuCyclesTextBox.Text, out var cycles) && cycles > 0)
        {
            adapter.SetCpuCyclesPerLine(cycles);
            StatusText.Text = $"Cycles/line locked at {cycles}";
        }
        else
        {
            StatusText.Text = "Invalid cycles value";
        }
    }

    private void OnRegionOverrideChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_regionOverrideUpdating)
            return;

        if (RegionOverrideCombo?.SelectedItem is not ComboBoxItem item)
            return;

        string tag = item.Tag?.ToString() ?? "Auto";
        ConsoleRegion region = tag switch
        {
            "JP" => ConsoleRegion.JP,
            "US" => ConsoleRegion.US,
            "EU" => ConsoleRegion.EU,
            _ => ConsoleRegion.Auto
        };

        SetRegionOverride(region, resetIfRunning: true, persist: true);
    }

    private void OnUseRomRegion(object? sender, RoutedEventArgs e)
    {
        if (RomRegionHint == ConsoleRegion.Auto)
        {
            StatusText.Text = "ROM region hint unavailable.";
            return;
        }

        SetRegionOverride(RomRegionHint, resetIfRunning: true, persist: true);
        StatusText.Text = $"Region override set to ROM hint ({RomRegionHint}).";
    }

    private void HookInput()
    {
        Focusable = true;

        AddHandler(KeyDownEvent, HandleKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(KeyUpEvent, HandleKeyUp, RoutingStrategies.Tunnel, handledEventsToo: true);

        // klick var som helst (utom textfält) => ta tillbaka fokus
        PointerPressed += (_, args) =>
        {
            if (args.Source is TextBox)
                return;
            Focus();
        };
    }

    private void HandleKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Source is TextBox)
            return;

        if (e.Key == Key.T && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            _ = RunToneTestAsync();
        }

        lock (_keysDown)
            _keysDown.Add(e.Key);
        e.Handled = true;
    }

    private void HandleKeyUp(object? sender, KeyEventArgs e)
    {
        if (e.Source is TextBox)
            return;

        lock (_keysDown)
            _keysDown.Remove(e.Key);
        e.Handled = true;
    }

    private async void OnOpenRom(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        IStorageFolder? startFolder = null;
        if (!string.IsNullOrWhiteSpace(_romPath))
        {
            string? folderPath = Path.GetDirectoryName(_romPath);
            if (!string.IsNullOrWhiteSpace(folderPath))
                startFolder = await StorageProvider.TryGetFolderFromPathAsync(folderPath);
        }

        var options = new FilePickerOpenOptions
        {
            Title = "Select Mega Drive ROM",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("ROMs")
                {
                    Patterns = new[] { "*.bin", "*.md", "*.gen", "*.smd", "*.iso", "*.*" }
                }
            }
        };

        if (startFolder != null)
            options.SuggestedStartLocation = startFolder;
        if (!string.IsNullOrWhiteSpace(_romPath))
            options.SuggestedFileName = Path.GetFileName(_romPath);

        var files = await StorageProvider.OpenFilePickerAsync(options);

        if (files.Count == 0)
            return;

        _romPath = files[0].TryGetLocalPath();
        RomPathText.Text = _romPath ?? files[0].Name;
        StatusText.Text = "ROM selected";
    }

    private void OnStart(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            _timer.Stop();
            StopEmuLoop();
            _frames = 0;
            _fpsSw.Restart();
            _earlyMagentaTimer.Restart();
            _earlyMagentaReported = false;
            if (SplashImage != null)
                SplashImage.IsVisible = true;

            // välj core
            if (UseDummyCoreCheck.IsChecked == true)
            {
                _core = new DummyCore();
                _core.Reset();
                StatusText.Text = "Running (Dummy)";
            }

            else
            {
            _core = new MdTracerAdapter();   // <-- Steg A core

                if (!string.IsNullOrWhiteSpace(_romPath))
                {
                    StartRomLog();
                    _timer.Stop();

                    if (_core is MdTracerAdapter m)
                    {
                        m.PowerCycleAndLoadRom(_romPath);

                        // Visa i UI direkt (snabbast att se)
                        UpdateRomInfo(m.RomInfo);

                        // OCH i terminal (om du kör från terminal)
                        Console.WriteLine(m.RomInfo.Summary);
                        SaveLastRomPath();
                    }
                    else
                    {
                        _core.LoadRom(_romPath);
                        SaveLastRomPath();
                    }
                }
                else
                {
                    StatusText.Text = "No ROM selected";
                }

                // LoadRom() kallar Reset() redan i vår adapter.
                // Så du behöver inte _core.Reset() här.
            }

            StartAudioEngineIfEnabled();
            if (_audioEngine != null)
            {
                _audioOutput?.Dispose();
                _audioOutput = null;
                StatusText.Text = "Audio engine ready";
            }
            else
            {
                _audioOutput?.Dispose();
                _audioOutput = OpenAlAudioOutput.TryCreate();
                StatusText.Text = _audioOutput is null
                    ? "Audio output unavailable"
                    : "Audio output ready";
            }


            // skapa bitmap utifrån core-storlek
            EnsureBitmapFromCore();
            if (SplashImage != null && !string.IsNullOrWhiteSpace(_romPath))
                SplashImage.IsVisible = false;
            StartHeartbeat();

            StartEmuLoop();
            _timer.Start();
            Focus();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Start failed: {ex.Message}";
            Console.WriteLine(ex.ToString());
        }
    }

    private void ApplyRegionOverrideToCore(bool resetIfRunning)
    {
        if (_core is not MdTracerAdapter adapter)
            return;

        adapter.SetRegionOverride(RegionOverride);
        UpdateEmuTargetFps();
        if (resetIfRunning && !string.IsNullOrWhiteSpace(_romPath))
        {
            adapter.Reset();
            StatusText.Text = $"Region override set to {RegionOverride}. Reset applied.";
        }
    }

    private void SetRegionOverride(ConsoleRegion region, bool resetIfRunning, bool persist)
    {
        RegionOverride = region;
        UpdateRegionOverrideCombo();

        if (persist)
        {
            if (!string.IsNullOrWhiteSpace(_romRegionKey))
            {
                if (region == ConsoleRegion.Auto)
                    _romRegionOverrides.Remove(_romRegionKey);
                else
                    _romRegionOverrides[_romRegionKey] = region;
            }
            else
            {
                _defaultRegionOverride = region;
            }

            SaveRegionOverrideSetting();
        }

        ApplyRegionOverrideToCore(resetIfRunning);
    }

    private void UpdateRomRegionHint(ConsoleRegion? hint)
    {
        RomRegionHint = hint ?? ConsoleRegion.Auto;
        UpdateRomRegionHintText();
        UpdateEmuTargetFps();
    }

    private void UpdateRomInfo(RomInfo info)
    {
        if (RomInfoText != null)
            RomInfoText.Text = info.Summary;

        UpdateRomRegionHint(info.RegionHint);
        _romRegionKey = GetRomRegionKey(info);

        ConsoleRegion target = _defaultRegionOverride;
        if (!string.IsNullOrWhiteSpace(_romRegionKey) &&
            _romRegionOverrides.TryGetValue(_romRegionKey, out var perRom))
        {
            target = perRom;
        }

        SetRegionOverride(target, resetIfRunning: false, persist: false);
    }

    private void UpdateRomRegionHintText()
    {
        if (RomRegionHintText != null)
            RomRegionHintText.Text = $"ROM suggests: {RomRegionHint}";
    }

    private static string? GetRomRegionKey(RomInfo info)
    {
        string serial = info.SerialNumber?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(serial))
            return null;
        return $"serial:{serial}";
    }

    private void UpdateRegionOverrideCombo()
    {
        if (RegionOverrideCombo == null)
            return;

        _regionOverrideUpdating = true;
        try
        {
            foreach (var item in RegionOverrideCombo.Items.Cast<ComboBoxItem>())
            {
                if (string.Equals(item.Tag?.ToString(), RegionOverride.ToString(), StringComparison.OrdinalIgnoreCase))
                {
                    RegionOverrideCombo.SelectedItem = item;
                    return;
                }
            }
        }
        finally
        {
            _regionOverrideUpdating = false;
        }
    }

    private void LoadRegionOverrideSetting()
    {
        string path = GetRegionSettingsPath();
        if (!File.Exists(path))
            return;

        _romRegionOverrides.Clear();
        _defaultRegionOverride = ConsoleRegion.Auto;

        foreach (string rawLine in File.ReadAllLines(path))
        {
            string raw = rawLine.Trim();
            if (raw.Length == 0 || raw.StartsWith("#", StringComparison.Ordinal))
                continue;

            int equals = raw.IndexOf('=');
            if (equals < 0)
            {
                if (Enum.TryParse(raw, ignoreCase: true, out ConsoleRegion region))
                    _defaultRegionOverride = region;
                continue;
            }

            string key = raw[..equals].Trim();
            string value = raw[(equals + 1)..].Trim();
            if (!Enum.TryParse(value, ignoreCase: true, out ConsoleRegion parsed))
                continue;

            if (key.Equals("default", StringComparison.OrdinalIgnoreCase))
            {
                _defaultRegionOverride = parsed;
                continue;
            }

            if (key.StartsWith("serial:", StringComparison.OrdinalIgnoreCase))
                _romRegionOverrides[key] = parsed;
        }

        RegionOverride = _defaultRegionOverride;
    }

    private void SaveRegionOverrideSetting()
    {
        string path = GetRegionSettingsPath();
        var lines = new List<string> { $"default={_defaultRegionOverride}" };
        foreach (var entry in _romRegionOverrides.OrderBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase))
            lines.Add($"{entry.Key}={entry.Value}");
        File.WriteAllLines(path, lines);
    }

    private static string GetRegionSettingsPath()
        => Path.Combine(Directory.GetCurrentDirectory(), RegionSettingsFileName);

    private void LoadLastRomPath()
    {
        string path = GetLastRomPathSettingsPath();
        if (!File.Exists(path))
            return;

        string raw = File.ReadAllText(path).Trim();
        if (string.IsNullOrWhiteSpace(raw))
            return;

        _romPath = raw;
        if (RomPathText != null)
            RomPathText.Text = _romPath;
    }

    private void SaveLastRomPath()
    {
        if (string.IsNullOrWhiteSpace(_romPath))
            return;

        File.WriteAllText(GetLastRomPathSettingsPath(), _romPath);
    }

    private static string GetLastRomPathSettingsPath()
        => Path.Combine(Directory.GetCurrentDirectory(), LastRomPathFileName);

    private void OnStop(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _timer.Stop();
        StopEmuLoop();
        StatusText.Text = "Stopped";
        StopAudioEngine();
        _audioOutput?.Dispose();
        _audioOutput = null;
        if (SplashImage != null)
            SplashImage.IsVisible = true;
        if (_heartbeatTimer != null)
        {
            _heartbeatTimer.Stop();
            _heartbeatTimer = null;
        }
        _toneTestRunning = false;
    }

    private void OnToneTestClick(object? sender, RoutedEventArgs e)
    {
        _ = RunToneTestAsync();
    }

    private void OnPsgBlipClick(object? sender, RoutedEventArgs e)
    {
        _ = RunPsgBlipAsync();
    }

    private async Task RunToneTestAsync()
    {
        if (_toneTestRunning)
            return;

        _toneTestRunning = true;
        StatusText.Text = "Tone test: playing 440 Hz...";
        Console.WriteLine("[TestToneGenerator] starting tone test.");

        try
        {
            using var sink = new PwCatAudioSink();
            sink.Start(48000, 2);
            var tone = TestToneGenerator.GenerateSine(48000, 440, 2.0, 2);
            sink.Submit(tone);
            await Task.Delay(TimeSpan.FromSeconds(2));
            sink.Stop();
            StatusText.Text = "Tone test: completed.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Tone test failed: {ex.Message}";
            Console.WriteLine($"[TestToneGenerator] error: {ex}");
        }
        finally
        {
            _toneTestRunning = false;
        }
    }

    private async Task RunPsgBlipAsync()
    {
        if (_psgBlipRunning)
            return;

        _psgBlipRunning = true;
        try
        {
            if (_core is not MdTracerAdapter adapter)
            {
                StatusText.Text = "PSG blip: core not ready";
                return;
            }

            StatusText.Text = "PSG blip: playing...";

            const int tone = 0x100;
            byte low = (byte)(tone & 0x0F);
            byte high = (byte)((tone >> 4) & 0x3F);

            if (!adapter.WritePsg((byte)(0x80 | low)))
            {
                StatusText.Text = "PSG blip: bus unavailable";
                return;
            }

            adapter.WritePsg(high);
            adapter.WritePsg(0x90);
            await Task.Delay(150);
            adapter.WritePsg(0x9F);

            StatusText.Text = "PSG blip: done";
        }
        finally
        {
            _psgBlipRunning = false;
        }
    }

    private void StartAudioEngineIfEnabled()
    {
        StopAudioEngine();
        _audioEnabled = AudioEnabledCheck?.IsChecked == true || AudioEnvEnabled;
        _audioTimedEnabled = AudioTimedEnvEnabled || (AudioEnabledCheck?.IsChecked == true);
        ApplyAudioOptionsToCore();
        if (!_audioEnabled)
        {
            ResetAudioTiming();
            return;
        }

        _audioEngine = new AudioEngine(new PwCatAudioSink(), AudioSampleRate, AudioChannels);
        _audioEngine.Start();
        _audioFormatMismatchLogged = false;
        ResetAudioTiming();
    }

    private void StopAudioEngine()
    {
        if (_audioEngine == null)
            return;

        _audioEngine.Stop();
        _audioEngine = null;
        ResetAudioTiming();
    }

    private void ProducePsgForFrame()
    {
        if (_audioEngine == null || _core == null)
            return;

        if (_audioTimedEnabled && _core is MdTracerAdapter adapter)
        {
            long now = Stopwatch.GetTimestamp();
            if (_audioLastTicks == 0)
            {
                _audioLastTicks = now;
                return;
            }

            double elapsed = (now - _audioLastTicks) / (double)Stopwatch.Frequency;
            _audioLastTicks = now;
            _audioFrameAccumulator += elapsed * AudioSampleRate;
            int frames = (int)_audioFrameAccumulator;
            if (frames <= 0)
                return;

            if (frames > AudioMaxFramesPerTick)
            {
                frames = AudioMaxFramesPerTick;
                _audioFrameAccumulator = 0;
                if (AudioStatsEnabled)
                    _audioEngine?.ReportTimedClamp();
                long logNow = now;
                if (logNow - _audioLastDropLogTicks > Stopwatch.Frequency)
                {
                    _audioLastDropLogTicks = logNow;
                    Console.WriteLine($"[AudioEngine] timed audio clamped to {AudioMaxFramesPerTick} frames.");
                }
            }
            else
            {
                _audioFrameAccumulator -= frames;
            }

            long genStart = AudioStatsEnabled ? Stopwatch.GetTimestamp() : 0;
            var timed = adapter.GetAudioBufferForFrames(frames, out int timedRate, out int timedChannels);
            long genTicks = AudioStatsEnabled ? Stopwatch.GetTimestamp() - genStart : 0;
            if (timed.IsEmpty)
            {
                if (TraceAudioLevel)
                    Console.WriteLine($"[AUDLVL] timed EMPTY framesReq={frames} rate={timedRate} ch={timedChannels}");
                return;
            }

            if (TraceAudioLevel)
            {
                int timedPeak = 0;
                for (int i = 0; i < timed.Length; i++)
                {
                    int v = timed[i];
                    if (v < 0) v = -v;
                    if (v > timedPeak) timedPeak = v;
                }
                Console.WriteLine($"[AUDLVL] timed peak={timedPeak} samples={timed.Length} rate={timedRate} ch={timedChannels} framesReq={frames}");
            }

            if (timedRate != AudioSampleRate || timedChannels != AudioChannels)
            {
                if (!_audioFormatMismatchLogged)
                {
                    _audioFormatMismatchLogged = true;
                    Console.WriteLine($"[AudioEngine] core audio format mismatch: {timedRate} Hz, {timedChannels} ch (expected {AudioSampleRate} Hz, {AudioChannels} ch)");
                }
                return;
            }

            _audioEngine.Submit(timed);
            if (AudioStatsEnabled)
            {
                int producedFrames = timed.Length / timedChannels;
                _audioEngine.ReportGenerateBatch(producedFrames, genTicks, timedMode: true);
            }
            return;
        }

        long genStartFrame = AudioStatsEnabled ? Stopwatch.GetTimestamp() : 0;
        var audio = _core.GetAudioBuffer(out int sampleRate, out int channels);
        long genTicksFrame = AudioStatsEnabled ? Stopwatch.GetTimestamp() - genStartFrame : 0;
        if (audio.IsEmpty)
        {
            if (TraceAudioLevel)
                Console.WriteLine("[AUDLVL] frame EMPTY");
            return;
        }

        if (TraceAudioLevel)
        {
            int framePeak = 0;
            for (int i = 0; i < audio.Length; i++)
            {
                int v = audio[i];
                if (v < 0) v = -v;
                if (v > framePeak) framePeak = v;
            }
            Console.WriteLine($"[AUDLVL] frame peak={framePeak} samples={audio.Length} rate={sampleRate} ch={channels}");
        }

        if (sampleRate != AudioSampleRate || channels != AudioChannels)
        {
            if (!_audioFormatMismatchLogged)
            {
                _audioFormatMismatchLogged = true;
                Console.WriteLine($"[AudioEngine] core audio format mismatch: {sampleRate} Hz, {channels} ch (expected {AudioSampleRate} Hz, {AudioChannels} ch)");
                }
                return;
            }

        _audioEngine.Submit(audio);
        if (AudioStatsEnabled)
        {
            int producedFrames = audio.Length / channels;
            _audioEngine.ReportGenerateBatch(producedFrames, genTicksFrame, timedMode: false);
        }
    }

    private void ResetAudioTiming()
    {
        _audioLastTicks = 0;
        _audioFrameAccumulator = 0;
        _audioLastDropLogTicks = 0;
    }

    private void ApplyAudioOptionsToCore()
    {
        if (_core is not MdTracerAdapter adapter)
            return;

        bool wantYm = YmEnvEnabled || (AudioEnabledCheck?.IsChecked == true);
        adapter.SetYmEnabled(wantYm);
    }

    private void OnAudioToggle(object? sender, RoutedEventArgs e)
    {
        StartAudioEngineIfEnabled();
    }

    private void Tick()
    {
        if (_core == null)
            return;
        MaybeUpdateStatusText();

        long tickStart = TracePerf ? Stopwatch.GetTimestamp() : 0;

        if (_tickTraceEnabled && (++_tickTraceCount % 60) == 0)
            Console.WriteLine("[UI] Tick " + _tickTraceCount);

        // rendera frame
        var core = _core;
        if (Dispatcher.UIThread.CheckAccess())
        {
            RenderFrame(core);
        }
        else
        {
            _pendingPresentCore = core;
            Dispatcher.UIThread.Post(_presentOnUiAction);
        }

        if (TracePerf)
            PerfHotspots.Add(PerfHotspot.UiTick, Stopwatch.GetTimestamp() - tickStart);

        // fps
        _frames++;
        if (_fpsSw.ElapsedMilliseconds >= 1000)
        {
            FpsText.Text = $"FPS: {_frames}";
            _frames = 0;
            _fpsSw.Restart();
        }
    }

    private void MaybeUpdateStatusText()
    {
        if (_core == null)
            return;

        long now = _fpsSw.ElapsedMilliseconds;
        if (now - _lastStatusUpdateMs < 250)
            return;

        string keys;
        lock (_keysDown)
        {
            keys = _keysDown.Count == 0
                ? "-"
                : string.Join(", ", _keysDown.OrderBy(k => k.ToString()));
        }

        if (keys == _lastStatusKeys && now - _lastStatusUpdateMs < 1000)
            return;

        _lastStatusUpdateMs = now;
        _lastStatusKeys = keys;
        StatusText.Text = $"Core: {_core.GetType().Name}  Keys: {keys}";
    }

    private void SubmitAudio()
    {
        if (_audioEngine != null)
            return;

        if (_core is null || _audioOutput is null)
            return;

        var audio = _core.GetAudioBuffer(out int sampleRate, out int channels);
        if (audio.IsEmpty)
        {
            if (TraceAudioLevel)
                Console.WriteLine("[AUDLVL] pcm EMPTY");
            return;
        }

        if (TraceAudioLevel)
        {
            int peak = 0;
            for (int i = 0; i < audio.Length; i++)
            {
                int v = audio[i];
                if (v < 0) v = -v;
                if (v > peak) peak = v;
            }
            Console.WriteLine($"[AUDLVL] peak={peak} samples={audio.Length} rate={sampleRate} channels={channels}");
        }

        _audioOutput.Start(sampleRate, channels);
        _audioOutput.Submit(audio);
    }

    private void ApplyInputToCore(IEmulatorCore core)
    {
        bool up;
        bool down;
        bool left;
        bool right;
        bool a;
        bool b;
        bool c;
        bool start;
        bool x;
        bool y;
        bool z;
        bool mode;
        PadType padType;
        lock (_keysDown)
        {
            up    = _keysDown.Contains(Key.Up);
            down  = _keysDown.Contains(Key.Down);
            left  = _keysDown.Contains(Key.Left);
            right = _keysDown.Contains(Key.Right);

            // Knappar: flera alternativ för att slippa layout-strul
            a = _keysDown.Contains(Key.Z);
            b = _keysDown.Contains(Key.X);
            c = _keysDown.Contains(Key.C);
            start = _keysDown.Contains(Key.Enter)
                || _keysDown.Contains(Key.Return);
            x = _keysDown.Contains(Key.A);
            y = _keysDown.Contains(Key.S);
            z = _keysDown.Contains(Key.D);
            mode = _keysDown.Contains(Key.LeftShift) || _keysDown.Contains(Key.RightShift);
            padType = (PadType)Volatile.Read(ref _padTypeRaw);
        }

        core.SetInputState(up, down, left, right, a, b, c, start, x, y, z, mode, padType);

        // StatusText uppdateras i Tick()
    }


    private void EnsureBitmapFromCore()
    {
        if (_core == null) return;

        _ = _core.GetFrameBuffer(out var w, out var h, out _);
        if (w <= 0 || h <= 0)
            throw new InvalidOperationException($"Core returned invalid size {w}x{h}.");

            if (_wb == null || _wb.PixelSize.Width != w || _wb.PixelSize.Height != h)
            {
                _wb = new WriteableBitmap(
                    new PixelSize(w, h),
                                      new Vector(96, 96),
                                      PixelFormat.Bgra8888,
                                      AlphaFormat.Unpremul);

                ScreenImage.Source = _wb;
                if (SplashImage != null && !string.IsNullOrWhiteSpace(_romPath))
                    SplashImage.IsVisible = false;
            }
        }

    private unsafe void RenderFrame(IEmulatorCore core)
    {
        EnsureBitmapFromCore();
        if (_wb == null)
            return;

        var src = core.GetFrameBuffer(out var w, out var h, out var srcStride);
        if (src.IsEmpty || srcStride <= 0 || w <= 0 || h <= 0)
            return;

        if (SkipUiBlitEnabled)
        {
            if (!_earlyMagentaReported && _earlyMagentaTimer.IsRunning)
            {
                _earlyMagentaReported = true;
                _earlyMagentaTimer.Stop();
                Console.WriteLine($"[MainWindow] Early magenta ready after {_earlyMagentaTimer.Elapsed.TotalMilliseconds:0.0} ms");
            }
            return;
        }

        long lockStart = TracePerf ? Stopwatch.GetTimestamp() : 0;
        using var fb = _wb.Lock();
        if (TracePerf)
            PerfHotspots.Add(PerfHotspot.UiLock, Stopwatch.GetTimestamp() - lockStart);
        int dstStride = fb.RowBytes;

        int copyBytesPerRow = Math.Min(w * 4, Math.Min(srcStride, dstStride));
        if (copyBytesPerRow <= 0)
            return;

        fixed (byte* pSrc0 = src)
        {
            byte* pDst0 = (byte*)fb.Address.ToPointer();
            long blitStart = TracePerf ? Stopwatch.GetTimestamp() : 0;

            if (FrameBufferTraceEnabled)
            {
                _presentedFrames++;
                Console.WriteLine($"[MainWindow] Present frame={_presentedFrames} srcPtr=0x{(nint)pSrc0:X} size={w}x{h} stride={srcStride} bytes={src.Length}");
            }

            if (copyBytesPerRow == srcStride && copyBytesPerRow == dstStride)
            {
                long totalBytes = (long)copyBytesPerRow * h;
                Buffer.MemoryCopy(pSrc0, pDst0, totalBytes, totalBytes);
            }
            else
            {
                for (int y = 0; y < h; y++)
                {
                    byte* pSrcRow = pSrc0 + (y * srcStride);
                    byte* pDstRow = pDst0 + (y * dstStride);
                    Buffer.MemoryCopy(pSrcRow, pDstRow, dstStride, copyBytesPerRow);
                }
            }

            if (TracePerf)
                PerfHotspots.Add(PerfHotspot.UiBlit, Stopwatch.GetTimestamp() - blitStart);
        }

        // VIKTIGT: tvinga repaint
        ScreenImage.InvalidateVisual();

        if (!_earlyMagentaReported && _earlyMagentaTimer.IsRunning)
        {
            _earlyMagentaReported = true;
            _earlyMagentaTimer.Stop();
            Console.WriteLine($"[MainWindow] Early magenta ready after {_earlyMagentaTimer.Elapsed.TotalMilliseconds:0.0} ms");
        }
    }

    private void PresentPendingFrame()
    {
        var core = _pendingPresentCore;
        if (core != null)
            RenderFrame(core);
    }

    private void UpdatePadTypeFromUi()
    {
        int raw = SixButtonPadCheck?.IsChecked == true
            ? (int)PadType.SixButton
            : (int)PadType.ThreeButton;
        Volatile.Write(ref _padTypeRaw, raw);
    }

    private void StartEmuLoop()
    {
        if (_core == null)
            return;
        if (_emuRunning)
            return;
        _emuRunning = true;
        _emuThread = new Thread(EmuLoop)
        {
            IsBackground = true,
            Name = "EmuLoop",
            Priority = ThreadPriority.AboveNormal
        };
        _emuThread.Start();
    }

    private void StopEmuLoop()
    {
        _emuRunning = false;
        if (_emuThread == null)
            return;
        if (!_emuThread.Join(1000))
            _emuThread.Interrupt();
        _emuThread = null;
    }

    private void EmuLoop()
    {
        double targetFps = Volatile.Read(ref _emuTargetFps);
        long ticksPerFrame = (long)(Stopwatch.Frequency / targetFps);
        long nextTick = Stopwatch.GetTimestamp();
        while (_emuRunning)
        {
            double currentTarget = Volatile.Read(ref _emuTargetFps);
            if (Math.Abs(currentTarget - targetFps) > 0.001)
            {
                targetFps = currentTarget;
                ticksPerFrame = (long)(Stopwatch.Frequency / targetFps);
            }

            long now = Stopwatch.GetTimestamp();
            if (now < nextTick)
            {
                int sleepMs = (int)((nextTick - now) * 1000 / Stopwatch.Frequency);
                if (sleepMs > 1)
                    Thread.Sleep(sleepMs - 1);
                continue;
            }

            if (now - nextTick > ticksPerFrame * 4)
                nextTick = now;
            nextTick += ticksPerFrame;

            var core = _core;
            if (core == null)
                continue;

            ApplyInputToCore(core);
            try
            {
                core.RunFrame();
            }
            catch (Exception ex)
            {
                Console.WriteLine("[EmuLoop] RunFrame exception: " + ex);
            }

            ProducePsgForFrame();
            SubmitAudio();
        }
    }

    private unsafe void StartHeartbeat()
    {
        if (!_heartbeatEnabled || _heartbeatTimer != null || _wb == null)
            return;

        _heartbeatTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Render, (_, _) =>
        {
            if (_wb == null)
                return;

            using var fb = _wb.Lock();
            int stride = fb.RowBytes;
            byte* pixel = (byte*)fb.Address.ToPointer();
            byte r = _heartbeatState ? (byte)0 : (byte)0xFF;
            byte g = _heartbeatState ? (byte)0xFF : (byte)0;
            byte b = (byte)(_heartbeatState ? 0 : 0xFF);
            pixel[0] = b;
            pixel[1] = g;
            pixel[2] = r;
            pixel[3] = 255;

            _heartbeatState = !_heartbeatState;
            _heartbeatTicks++;
            if ((_heartbeatTicks & 7) == 0)
                Console.WriteLine("[UI] Heartbeat tick " + _heartbeatTicks);

            ScreenImage.InvalidateVisual();
        });
        _heartbeatTimer.Start();
    }

    private void StartRomLog()
    {
        string path = Path.Combine(Environment.CurrentDirectory, "rom_start.log");
        _romLogWriter?.Dispose();
        _romLogWriter = new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read))
        {
            AutoFlush = true
        };

        _originalConsoleOut ??= Console.Out;
        Console.SetOut(new TeeTextWriter(_originalConsoleOut, _romLogWriter));
    }

    private sealed class TeeTextWriter : TextWriter
    {
        private readonly TextWriter _a;
        private readonly TextWriter _b;

        public TeeTextWriter(TextWriter a, TextWriter b)
        {
            _a = a;
            _b = b;
        }

        public override Encoding Encoding => _a.Encoding;

        public override void Write(char value)
        {
            _a.Write(value);
            _b.Write(value);
        }

        public override void Write(string? value)
        {
            _a.Write(value);
            _b.Write(value);
        }

        public override void WriteLine(string? value)
        {
            _a.WriteLine(value);
            _b.WriteLine(value);
        }

        public override void Flush()
        {
            _a.Flush();
            _b.Flush();
        }
    }
}
