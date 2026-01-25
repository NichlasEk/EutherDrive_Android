using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using EutherDrive.Core.MdTracerCore;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using EutherDrive.Core;
using EutherDrive.UI.Audio;
using EutherDrive.Audio;
using EutherDrive.Core.Savestates;
using EutherDrive.UI.Savestates;

namespace EutherDrive.UI;

public partial class MainWindow : Window
{
    private IEmulatorCore? _core;
    private readonly SavestateService _savestateService;
    private readonly SavestateViewModel _savestateViewModel;

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
    private bool _audioEnabled = true;
    private bool _audioFormatMismatchLogged;
    private const int AudioSampleRate = 44100;
    private const int AudioChannels = 2;
    private static readonly bool AudioEnvEnabled = Environment.GetEnvironmentVariable("EUTHERDRIVE_AUDIO") == "1";
    private static readonly bool AudioTimedEnvEnabled = Environment.GetEnvironmentVariable("EUTHERDRIVE_AUDIO_TIMED") != "0";
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
    private const int AutoFireRateMin = 5;
    private const int AutoFireRateMax = 30;
    private const int AutoFireRateDefault = 12;
    private int _autoFireRateHz = AutoFireRateDefault;
    private int _autoFireMask;
    private long _audioLastTicks;
    private double _audioFrameAccumulator;
    private long _audioLastDropLogTicks;
    private int _masterVolumePercent = DefaultMasterVolumePercent;
    private ConsoleRegion _regionOverride = ConsoleRegion.Auto;
    private ConsoleRegion _defaultRegionOverride = ConsoleRegion.Auto;
    private ConsoleRegion _romRegionHint = ConsoleRegion.Auto;
    private FrameRateMode _frameRateMode = FrameRateMode.Auto;
    private readonly Dictionary<string, ConsoleRegion> _romRegionOverrides = new(StringComparer.OrdinalIgnoreCase);
    private string? _romRegionKey;
    private bool _regionOverrideUpdating;
    private bool _frameRateUpdating;
    private const string SettingsFileName = "eutherdrive_settings.json";
    private const string LegacyRegionSettingsFileName = "eutherdrive_region.txt";
    private const string LegacyLastRomPathFileName = "eutherdrive_last_rom.txt";
    private const int DefaultMasterVolumePercent = 50;

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
    private WindowState _prevWindowState = WindowState.Normal;
    private DispatcherTimer? _cursorHideTimer;
    private bool _cursorHidden;
    private Point _lastMousePosition;

    public MainWindow(string? romPath = null)
    {
        InitializeComponent();

        HookInput();
        HookMouseMovement();

        _savestateService = new SavestateService();
        _savestateViewModel = new SavestateViewModel(
            _savestateService,
            () => _core as ISavestateCapable,
            PauseEmulation,
            ResumeEmulation,
            SetStatus);
        if (SavestatePanel != null)
            SavestatePanel.DataContext = _savestateViewModel;

        LoadSettings();
        if (MasterVolumeSlider != null)
            MasterVolumeSlider.Value = _masterVolumePercent;
        UpdateMasterVolumeText();
        if (AudioEnabledCheck != null)
            AudioEnabledCheck.IsChecked = _audioEnabled || AudioEnvEnabled;

        Focusable = true;
        AttachedToVisualTree += (_, __) => Focus();
        PropertyChanged += OnWindowPropertyChanged;

        StatusText.Text = "Idle";

        _audioTimedEnabled = AudioTimedEnvEnabled;
        UpdatePadTypeFromUi();
        UpdateAutoFireMask();
        UpdateAutoFireRateText(_autoFireRateHz);
        if (SixButtonPadCheck != null)
        {
            SixButtonPadCheck.Checked += (_, _) => UpdatePadTypeFromUi();
            SixButtonPadCheck.Unchecked += (_, _) => UpdatePadTypeFromUi();
        }
        UpdateRegionOverrideCombo();
        UpdateFrameRateCombo();
        UpdateRomRegionHintText();

        // Initialize timer
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(16.666), DispatcherPriority.Render, (_, _) => Tick());

        // Load ROM from command line if provided
        if (!string.IsNullOrEmpty(romPath) && File.Exists(romPath))
        {
            StatusText.Text = $"Loading from CLI: {romPath}";
            _romPath = romPath;
            _core = new MdTracerAdapter();
            ApplyMasterVolumeToCore();
            SaveSettings();
            if (_core is MdTracerAdapter m)
            {
                m.PowerCycleAndLoadRom(_romPath);

                // Auto-load savestate slot 1 if flag is set
                if (Environment.GetEnvironmentVariable("EUTHERDRIVE_LOAD_SLOT1_ON_BOOT") == "1")
                {
                    try
                    {
                        _savestateService.Load(m, 1);
                        StatusText.Text = "Loaded savestate slot 1";
                    }
                    catch (Exception ex)
                    {
                        StatusText.Text = $"Savestate load failed: {ex.Message}";
                    }
                }

                UpdateRomInfo(m.RomInfo);
                ApplyFrameRateModeToCore(resetIfRunning: false);
                Console.WriteLine(m.RomInfo.Summary);

                // Sync UI checkbox with VDP class default (preserve framebuffer on display off)
                if (PreserveFbCheck != null)
                {
                    md_vdp.PreserveFramebufferOnDisplayOff = PreserveFbCheck.IsChecked == true;
                }
            }
            else
            {
                _core.LoadRom(_romPath);
            }
            _savestateViewModel.Refresh();
        }
        _presentOnUiAction = PresentPendingFrame;
        ApplyFullScreenLayout(WindowState == WindowState.FullScreen);

        // Auto-start if ROM was loaded from CLI
        if (!string.IsNullOrEmpty(_romPath))
        {
            OnStart(null, null);
        }
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
        if (_frameRateUpdating)
            return;
        if (FrameRateCombo?.SelectedItem is not ComboBoxItem item)
            return;
        var tag = item.Tag?.ToString();
        _frameRateMode = tag switch
        {
            "Hz50" => FrameRateMode.Hz50,
            "Hz60" => FrameRateMode.Hz60,
            _ => FrameRateMode.Auto
        };
        ApplyFrameRateModeToCore(resetIfRunning: false);
        SaveSettings();
    }

    private void UpdateEmuTargetFps()
    {
        double target = 60.0;
        if (_core is MdTracerAdapter adapter)
            target = adapter.GetTargetFps();
        Volatile.Write(ref _emuTargetFps, target);
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
            if (WindowState == WindowState.FullScreen && _cursorHidden)
            {
                ShowCursor();
                ResetCursorHideTimer();
            }
        };
    }

    private void HookMouseMovement()
    {
        PointerMoved += (_, args) =>
        {
            var position = args.GetPosition(this);
            HandleMouseMovement(position);
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
        else if (e.Key == Key.F1)
        {
            ToggleFullScreen();
        }
        else if (HandleSavestateHotkey(e.Key))
        {
            e.Handled = true;
            return;
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

    private bool HandleSavestateHotkey(Key key)
    {
        return key switch
        {
            Key.F5 => TryInvokeCommand(_savestateViewModel.SaveSlot1Command),
            Key.F6 => TryInvokeCommand(_savestateViewModel.SaveSlot2Command),
            Key.F7 => TryInvokeCommand(_savestateViewModel.SaveSlot3Command),
            Key.F8 => TryInvokeCommand(_savestateViewModel.LoadSlot1Command),
            Key.F9 => TryInvokeCommand(_savestateViewModel.LoadSlot2Command),
            Key.F10 => TryInvokeCommand(_savestateViewModel.LoadSlot3Command),
            _ => false
        };
    }

    private static bool TryInvokeCommand(ICommand command)
    {
        if (command == null)
            return false;
        if (!command.CanExecute(null))
            return false;
        command.Execute(null);
        return true;
    }

    private void OnAutoFireToggle(object? sender, RoutedEventArgs e)
    {
        UpdateAutoFireMask();
    }

    private void UpdateAutoFireMask()
    {
        int mask = 0;
        if (AutoFireAButton?.IsChecked == true)
            mask |= 1;
        if (AutoFireBButton?.IsChecked == true)
            mask |= 2;
        if (AutoFireCButton?.IsChecked == true)
            mask |= 4;
        Volatile.Write(ref _autoFireMask, mask);
    }

    private void OnAutoFireRateChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        int rate = (int)Math.Round(e.NewValue);
        rate = Math.Clamp(rate, AutoFireRateMin, AutoFireRateMax);
        Volatile.Write(ref _autoFireRateHz, rate);
        UpdateAutoFireRateText(rate);
    }

    private void UpdateAutoFireRateText(int rate)
    {
        if (AutoFireRateText != null)
            AutoFireRateText.Text = $"{rate}Hz";
    }

    private void HandleMouseMovement(Point position)
    {
        if (WindowState == WindowState.FullScreen && _cursorHidden)
        {
            ShowCursor();
            ResetCursorHideTimer();
        }
        _lastMousePosition = position;
    }

    private void ToggleFullScreen()
    {
        if (WindowState == WindowState.FullScreen)
        {
            WindowState = _prevWindowState == WindowState.FullScreen
                ? WindowState.Normal
                : _prevWindowState;
            ApplyFullScreenLayout(false);
            StopCursorHideTimer();
            ShowCursor();
            return;
        }

        _prevWindowState = WindowState;
        WindowState = WindowState.FullScreen;
        ApplyFullScreenLayout(true);
        StartCursorHideTimer();
    }

    private void StartCursorHideTimer()
    {
        StopCursorHideTimer();
        _cursorHideTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Normal, (_, _) =>
        {
            if (WindowState == WindowState.FullScreen && !_cursorHidden)
            {
                HideCursor();
            }
        });
        _cursorHideTimer.Start();
    }

    private void StopCursorHideTimer()
    {
        _cursorHideTimer?.Stop();
        _cursorHideTimer = null;
    }

    private void ResetCursorHideTimer()
    {
        if (WindowState == WindowState.FullScreen && _cursorHideTimer != null)
        {
            _cursorHideTimer.Stop();
            _cursorHideTimer.Start();
        }
    }

    private void HideCursor()
    {
        if (!_cursorHidden)
        {
            Cursor = new Cursor(StandardCursorType.None);
            _cursorHidden = true;
        }
    }

    private void ShowCursor()
    {
        if (_cursorHidden)
        {
            Cursor = Cursor.Default;
            _cursorHidden = false;
        }
    }

    private void OnWindowPropertyChanged(object? sender, Avalonia.AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Window.WindowStateProperty)
        {
            if (WindowState != WindowState.FullScreen)
            {
                StopCursorHideTimer();
                ShowCursor();
            }
            else
            {
                StartCursorHideTimer();
            }
        }
    }

    private void ApplyFullScreenLayout(bool fullScreen)
    {
        if (RootGrid == null || ScreenBorder == null || TopControlsPanel == null || InfoPanel == null)
            return;

        TopControlsPanel.IsVisible = !fullScreen;
        InfoPanel.IsVisible = !fullScreen;

        RootGrid.Margin = fullScreen ? new Thickness(0) : new Thickness(12);
        RootGrid.RowSpacing = fullScreen ? 0 : 12;
        RootGrid.ColumnSpacing = fullScreen ? 0 : 12;

        ScreenBorder.Padding = fullScreen ? new Thickness(0) : new Thickness(10);
        ScreenBorder.CornerRadius = fullScreen ? new CornerRadius(0) : new CornerRadius(8);
        Grid.SetRow(ScreenBorder, fullScreen ? 0 : 1);
        Grid.SetColumn(ScreenBorder, fullScreen ? 0 : 1);
        Grid.SetRowSpan(ScreenBorder, fullScreen ? 2 : 1);
        Grid.SetColumnSpan(ScreenBorder, fullScreen ? 2 : 1);
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
                    Patterns = new[] { "*.bin", "*.md", "*.gen", "*.smd", "*.sms", "*.sg", "*.gg", "*.zip", "*.7z", "*.iso", "*.*" }
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
        SaveSettings();
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
                ApplyMasterVolumeToCore();

                if (!string.IsNullOrWhiteSpace(_romPath))
                {
                    StartRomLog();
                    _timer.Stop();

                    if (_core is MdTracerAdapter m)
                    {
                        m.PowerCycleAndLoadRom(_romPath);

                        // Auto-load savestate slot 1 if flag is set
                        if (Environment.GetEnvironmentVariable("EUTHERDRIVE_LOAD_SLOT1_ON_BOOT") == "1")
                        {
                            try
                            {
                                _savestateService.Load(m, 1);
                                StatusText.Text = "Loaded savestate slot 1";
                            }
                            catch (Exception ex)
                            {
                                StatusText.Text = $"Savestate load failed: {ex.Message}";
                            }
                        }

                        // Visa i UI direkt (snabbast att se)
                        UpdateRomInfo(m.RomInfo);
                        ApplyFrameRateModeToCore(resetIfRunning: false);

                        // OCH i terminal (om du kör från terminal)
                        Console.WriteLine(m.RomInfo.Summary);
                        SaveSettings();
                    }
                    else
                    {
                        _core.LoadRom(_romPath);
                        SaveSettings();
                    }
                }
                else
                {
                    StatusText.Text = "No ROM selected";
                }

                // LoadRom() kallar Reset() redan i vår adapter.
                // Så du behöver inte _core.Reset() här.
            }

            _savestateViewModel.Refresh();
            ApplyFrameRateModeToCore(resetIfRunning: false);
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

    private void ApplyFrameRateModeToCore(bool resetIfRunning)
    {
        if (_core is not MdTracerAdapter adapter)
            return;

        adapter.SetFrameRateMode(_frameRateMode);
        UpdateEmuTargetFps();
        if (resetIfRunning && !string.IsNullOrWhiteSpace(_romPath))
        {
            adapter.Reset();
            StatusText.Text = $"Frame rate set to {_frameRateMode}. Reset applied.";
        }
    }

    private void SetStatus(string message)
    {
        StatusText.Text = message;
    }

    private bool PauseEmulation()
    {
        bool wasRunning = _emuRunning;
        if (wasRunning)
        {
            _timer.Stop();
            StopEmuLoop();
        }
        return wasRunning;
    }

    private void ResumeEmulation(bool wasRunning)
    {
        if (!wasRunning)
            return;
        StartEmuLoop();
        _timer.Start();
    }

    private void ApplyMasterVolumeToCore()
    {
        if (_core is MdTracerAdapter adapter)
            adapter.SetMasterVolumePercent(_masterVolumePercent);
    }

    private void UpdateMasterVolumeText()
    {
        if (MasterVolumeValueText != null)
            MasterVolumeValueText.Text = $"{_masterVolumePercent}%";
    }

    private void OnMasterVolumeChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        int percent = (int)Math.Round(e.NewValue);
        if (percent < 0) percent = 0;
        else if (percent > 100) percent = 100;
        if (percent == _masterVolumePercent)
            return;

        _masterVolumePercent = percent;
        UpdateMasterVolumeText();
        ApplyMasterVolumeToCore();
        SaveSettings();
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

            SaveSettings();
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

    private void UpdateFrameRateCombo()
    {
        if (FrameRateCombo == null)
            return;

        _frameRateUpdating = true;
        try
        {
            foreach (var item in FrameRateCombo.Items.Cast<ComboBoxItem>())
            {
                if (string.Equals(item.Tag?.ToString(), _frameRateMode.ToString(), StringComparison.OrdinalIgnoreCase))
                {
                    FrameRateCombo.SelectedItem = item;
                    return;
                }
            }
        }
        finally
        {
            _frameRateUpdating = false;
        }
    }

    private sealed class UiSettings
    {
        public string? LastRomPath { get; set; }
        public int MasterVolumePercent { get; set; } = DefaultMasterVolumePercent;
        public bool AudioEnabled { get; set; } = true;
        public ConsoleRegion DefaultRegionOverride { get; set; } = ConsoleRegion.Auto;
        public Dictionary<string, ConsoleRegion>? RomRegionOverrides { get; set; }
        public FrameRateMode FrameRateMode { get; set; } = FrameRateMode.Auto;
    }

    private void LoadSettings()
    {
        string path = GetSettingsPath();
        if (File.Exists(path))
        {
            try
            {
                string json = File.ReadAllText(path);
                var settings = JsonSerializer.Deserialize<UiSettings>(json);
                if (settings != null)
                {
                    ApplySettings(settings);
                    return;
                }
            }
            catch
            {
                // Ignore settings parse errors and fall back to legacy files.
            }
        }

        bool migrated = LoadLegacySettings();
        if (migrated)
            SaveSettings();
    }

    private void ApplySettings(UiSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.LastRomPath))
        {
            _romPath = settings.LastRomPath;
            if (RomPathText != null)
                RomPathText.Text = _romPath;
        }

        _masterVolumePercent = ClampPercent(settings.MasterVolumePercent);
        _audioEnabled = settings.AudioEnabled;

        _defaultRegionOverride = settings.DefaultRegionOverride;
        _romRegionOverrides.Clear();
        if (settings.RomRegionOverrides != null)
        {
            foreach (var entry in settings.RomRegionOverrides)
                _romRegionOverrides[entry.Key] = entry.Value;
        }

        RegionOverride = _defaultRegionOverride;
        _frameRateMode = settings.FrameRateMode;
        UpdateFrameRateCombo();
    }

    private static int ClampPercent(int value)
    {
        if (value < 0) return 0;
        if (value > 100) return 100;
        return value;
    }

    private bool LoadLegacySettings()
    {
        bool loaded = false;
        if (File.Exists(GetLegacyLastRomPathSettingsPath()))
        {
            LoadLegacyLastRomPath();
            loaded = true;
        }

        if (File.Exists(GetLegacyRegionSettingsPath()))
        {
            LoadLegacyRegionOverrideSetting();
            loaded = true;
        }

        return loaded;
    }

    private void SaveSettings()
    {
        var settings = new UiSettings
        {
            LastRomPath = _romPath,
            MasterVolumePercent = _masterVolumePercent,
            AudioEnabled = _audioEnabled,
            DefaultRegionOverride = _defaultRegionOverride,
            RomRegionOverrides = new Dictionary<string, ConsoleRegion>(_romRegionOverrides, StringComparer.OrdinalIgnoreCase),
            FrameRateMode = _frameRateMode
        };
        string json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(GetSettingsPath(), json);
    }

    private static string GetSettingsPath()
        => Path.Combine(Directory.GetCurrentDirectory(), SettingsFileName);

    private void LoadLegacyRegionOverrideSetting()
    {
        string path = GetLegacyRegionSettingsPath();
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

    private static string GetLegacyRegionSettingsPath()
        => Path.Combine(Directory.GetCurrentDirectory(), LegacyRegionSettingsFileName);

    private void LoadLegacyLastRomPath()
    {
        string path = GetLegacyLastRomPathSettingsPath();
        if (!File.Exists(path))
            return;

        string raw = File.ReadAllText(path).Trim();
        if (string.IsNullOrWhiteSpace(raw))
            return;

        _romPath = raw;
        if (RomPathText != null)
            RomPathText.Text = _romPath;
    }

    private static string GetLegacyLastRomPathSettingsPath()
        => Path.Combine(Directory.GetCurrentDirectory(), LegacyLastRomPathFileName);

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
        _psgBlipRunning = false;
    }

    private void OnTestInterlace2(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_core is MdTracerAdapter adapter)
        {
            adapter.RunInterlaceMode2Test();
            StatusText.Text = "Interlace mode 2 test run - check console output";
        }
        else
        {
            StatusText.Text = "No tracing core available";
        }
    }

    private void OnToneTestClick(object? sender, RoutedEventArgs e)
    {
        _ = RunToneTestAsync();
    }

    private async void OnShowControls(object? sender, RoutedEventArgs e)
    {
        var root = new StackPanel { Spacing = 12, Margin = new Thickness(16) };
        root.Children.Add(new TextBlock
        {
            Text = "Controls",
            FontSize = 18,
            FontWeight = Avalonia.Media.FontWeight.Bold
        });

        root.Children.Add(BuildControlsSection("Gameplay",
            "Arrow keys: D-pad",
            "Z/X/C: A/B/C",
            "Enter: Start",
            "Right Shift: Mode (optional)",
            "A/S/D: X/Y/Z (6-button mode)"));

        root.Children.Add(BuildControlsSection("Savestates",
            "F5: Save Slot 1",
            "F6: Save Slot 2",
            "F7: Save Slot 3",
            "F8: Load Slot 1",
            "F9: Load Slot 2",
            "F10: Load Slot 3"));

        root.Children.Add(BuildControlsSection("UI",
            "F1: Fullscreen"));

        var dialog = new Window
        {
            Title = "Controls",
            Width = 420,
            Height = 380,
            Background = new SolidColorBrush(Color.Parse("#0F1216")),
            Content = new ScrollViewer
            {
                Content = root
            },
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };

        await dialog.ShowDialog(this);
    }

    private static Control BuildControlsSection(string title, params string[] lines)
    {
        var section = new StackPanel { Spacing = 6 };
        section.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 14,
            FontWeight = Avalonia.Media.FontWeight.Bold
        });

        foreach (string line in lines)
        {
            section.Children.Add(new TextBlock
            {
                Text = line,
                FontSize = 12
            });
        }

        return section;
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
        _audioTimedEnabled = AudioTimedEnvEnabled && _audioEnabled;
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
        SaveSettings();
    }

    private void OnInterlaceTestToggle(object? sender, RoutedEventArgs e)
    {
        bool enabled = InterlaceTestCheck?.IsChecked == true;

        if (InterlaceTestInfo != null)
        {
            if (enabled)
            {
                InterlaceTestInfo.Text = "Interlace Test Mode: 320x448 | Field toggles every frame | Scanlines every 16 lines";
                Console.WriteLine("[UI] Interlace Test Mode ENABLED - 320x448 framebuffer");
            }
            else
            {
                InterlaceTestInfo.Text = "";
            }
        }

        if (enabled)
        {
            // Enable test mode - stop any running core and start test core (no ROM needed)
            _timer.Stop();
            StopEmuLoop();
            _frames = 0;
            _fpsSw.Restart();

            _core = new InterlaceTestCore();
            _core.Reset();

            // Enable and sync static mode checkbox
            if (InterlaceStaticCheck != null)
            {
                InterlaceStaticCheck.IsEnabled = true;
                InterlaceStaticCheck.IsChecked = true; // Default to static for easier verification
            }

            EnsureBitmapFromCore();
            if (SplashImage != null)
                SplashImage.IsVisible = false;

            StartEmuLoop();
            _timer.Start();
            StatusText.Text = "Interlace Test Mode (320x448) - NO ROM";
            Focus();
        }
        else
        {
            // Disable test mode - stop test core
            _timer.Stop();
            StopEmuLoop();
            _core = null;

            // Disable static mode checkbox
            if (InterlaceStaticCheck != null)
            {
                InterlaceStaticCheck.IsEnabled = false;
                InterlaceStaticCheck.IsChecked = false;
            }

            // If a ROM was previously loaded, restart it
            if (!string.IsNullOrWhiteSpace(_romPath))
            {
                OnStart(null, null);
            }
            else if (SplashImage != null)
            {
                SplashImage.IsVisible = true;
            }
            StatusText.Text = "Stopped";
        }
    }

    private void OnInterlaceStaticToggle(object? sender, RoutedEventArgs e)
    {
        if (_core is InterlaceTestCore testCore)
        {
            testCore.StaticMode = InterlaceStaticCheck?.IsChecked == true;
            Console.WriteLine($"[UI] Interlace Static Mode: {testCore.StaticMode}");
        }
    }

    private void OnAsciiStreamToggle(object? sender, RoutedEventArgs e)
    {
        if (_core is MdTracerAdapter adapter)
        {
            bool enabled = AsciiStreamCheck?.IsChecked == true;
            adapter.SetAsciiStreamEnabled(enabled);
            Console.WriteLine($"[UI] ASCII Stream: {(enabled ? "ENABLED" : "DISABLED")}");
            StatusText.Text = enabled ? "ASCII Stream ON" : "ASCII Stream OFF";
        }
        else
        {
            Console.WriteLine("[UI] ASCII Stream: No MdTracerAdapter core");
            StatusText.Text = "ASCII Stream: N/A";
        }
    }

    private void OnPreserveFbToggle(object? sender, RoutedEventArgs e)
    {
        bool enabled = PreserveFbCheck?.IsChecked == true;
        md_vdp.PreserveFramebufferOnDisplayOff = enabled;
        Console.WriteLine($"[UI] Preserve FB on Display Off: {(enabled ? "ENABLED" : "DISABLED")}");
        StatusText.Text = enabled ? "Preserve FB ON" : "Preserve FB OFF";
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
        int autoMask;
        int autoRate;
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

        autoMask = Volatile.Read(ref _autoFireMask);
        autoRate = Volatile.Read(ref _autoFireRateHz);
        long nowTicks = Stopwatch.GetTimestamp();
        if ((autoMask & 1) != 0 && a)
            a = IsAutoFireActive(nowTicks, autoRate);
        if ((autoMask & 2) != 0 && b)
            b = IsAutoFireActive(nowTicks, autoRate);
        if ((autoMask & 4) != 0 && c)
            c = IsAutoFireActive(nowTicks, autoRate);

        core.SetInputState(up, down, left, right, a, b, c, start, x, y, z, mode, padType);

        // StatusText uppdateras i Tick()
    }

    private static bool IsAutoFireActive(long nowTicks, int rateHz)
    {
        if (rateHz <= 0)
            return true;

        long periodTicks = (long)(Stopwatch.Frequency / (double)rateHz);
        if (periodTicks <= 1)
            return true;

        long halfPeriod = Math.Max(1, periodTicks / 2);
        return (nowTicks % periodTicks) < halfPeriod;
    }


    private int _lastPresentedWidth;
    private int _lastPresentedHeight;

    private void EnsureBitmapFromCore()
    {
        if (_core == null) return;

        _ = _core.GetFrameBuffer(out var w, out var h, out _);
        if (w <= 0 || h <= 0)
            throw new InvalidOperationException($"Core returned invalid size {w}x{h}.");

        // Only recreate bitmap when size actually changes
        if (_wb == null || _wb.PixelSize.Width != w || _wb.PixelSize.Height != h)
        {
            Console.WriteLine($"[MainWindow] Recreating bitmap: {_lastPresentedWidth}x{_lastPresentedHeight} -> {w}x{h}");
            _wb = new WriteableBitmap(
                new PixelSize(w, h),
                new Vector(96, 96),
                PixelFormat.Bgra8888,
                AlphaFormat.Unpremul);

            ScreenImage.Source = _wb;
            if (ScreenGrid != null)
            {
                ScreenGrid.Width = w;
                ScreenGrid.Height = h;
            }
            ScreenImage.Width = w;
            ScreenImage.Height = h;
            if (SplashImage != null && !string.IsNullOrWhiteSpace(_romPath))
                SplashImage.IsVisible = false;

            _lastPresentedWidth = w;
            _lastPresentedHeight = h;
        }
    }

    private int _lastCoreFrameId;
    private int _presentTickCounter;
    private int _presentLogInterval = 60;

    private unsafe void RenderFrame(IEmulatorCore core)
    {
        EnsureBitmapFromCore();
        if (_wb == null)
            return;

        var src = core.GetFrameBuffer(out var w, out var h, out var srcStride);
        if (src.IsEmpty || srcStride <= 0 || w <= 0 || h <= 0)
        {
            Console.WriteLine($"[MainWindow] Present tick={_presentTickCounter}: EMPTY");
            _presentTickCounter++;
            return;
        }

        // Check if this is actually a new frame
        int currentFrameId = core is InterlaceTestCore itc ? itc.GetFrameId() : _presentTickCounter;
        bool isNewFrame = currentFrameId != _lastCoreFrameId;
        _lastCoreFrameId = currentFrameId;

        // Only log when frame changes (for debugging flicker)
        if (isNewFrame)
        {
            Console.WriteLine($"[Present] tick={_presentTickCounter} NEW_FRAME frameId={currentFrameId}");
        }
        _presentTickCounter++;

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

        bool forceOpaque = ForceOpaqueCheck?.IsChecked == true;

        fixed (byte* pSrc0 = src)
        {
            byte* pDst0 = (byte*)fb.Address.ToPointer();
            long blitStart = TracePerf ? Stopwatch.GetTimestamp() : 0;

            if (FrameBufferTraceEnabled)
            {
                _presentedFrames++;
                Console.WriteLine($"[MainWindow] Present frame={_presentedFrames} srcPtr=0x{(nint)pSrc0:X} size={w}x{h} stride={srcStride} bytes={src.Length}");
            }

            if (copyBytesPerRow == srcStride && copyBytesPerRow == dstStride && !forceOpaque)
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

                    if (forceOpaque)
                    {
                        // Force alpha = 0xFF on every pixel
                        for (int x = 0; x < copyBytesPerRow; x += 4)
                        {
                            pDstRow[x + 0] = pSrcRow[x + 0]; // B
                            pDstRow[x + 1] = pSrcRow[x + 1]; // G
                            pDstRow[x + 2] = pSrcRow[x + 2]; // R
                            pDstRow[x + 3] = 0xFF;          // Force opaque alpha
                        }
                    }
                    else
                    {
                        Buffer.MemoryCopy(pSrcRow, pDstRow, dstStride, copyBytesPerRow);
                    }
                }
            }

            if (TracePerf)
                PerfHotspots.Add(PerfHotspot.UiBlit, Stopwatch.GetTimestamp() - blitStart);
        }

        // VIKTIGT: tvinga repaint
        ScreenImage.InvalidateVisual();

        // Log presentation info
        Console.WriteLine($"[MainWindow] Present WxH={w}x{h} stride={srcStride} forceOpaque={forceOpaque}");

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
        double targetFps = GetLiveTargetFps();
        long ticksPerFrame = (long)(Stopwatch.Frequency / targetFps);
        long nextTick = Stopwatch.GetTimestamp();
        while (_emuRunning)
        {
            double currentTarget = GetLiveTargetFps();
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

                // Framebuffer analyzer for debugging
                if (core is MdTracerAdapter adapter && adapter.FbAnalyzer.Enabled)
                {
                    adapter.FbAnalyzer.AnalyzeFrame();
                    Console.Error.Flush(); // Ensure output is visible immediately
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("[EmuLoop] RunFrame exception: " + ex);
            }

            ProducePsgForFrame();
            SubmitAudio();
        }
    }

    private double GetLiveTargetFps()
    {
        if (_core is MdTracerAdapter adapter)
            return adapter.GetTargetFps();
        return Volatile.Read(ref _emuTargetFps);
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
