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
using Key = Avalonia.Input.Key;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using EutherDrive.Core;
using EutherDrive.Core.MdTracerCore;
using EutherDrive.UI.Audio;
using EutherDrive.Audio;
using EutherDrive.Core.Savestates;
using EutherDrive.UI.Savestates;
using Tomlyn;
using SdlApi = Silk.NET.SDL.Sdl;
using GameControllerAxis = Silk.NET.SDL.GameControllerAxis;
using GameControllerButton = Silk.NET.SDL.GameControllerButton;

namespace EutherDrive.UI;

public partial class MainWindow : Window
{
    private IEmulatorCore? _core;
    private readonly object _coreAudioLock = new();
    private readonly SavestateService _savestateService;
    private readonly SavestateViewModel _savestateViewModel;

    // EN bitmap som vi alltid blitar till
    private WriteableBitmap? _wb;

    private SdlApi? _sdl;
    private IntPtr _activeGamepad1 = IntPtr.Zero;
    private IntPtr _activeGamepad2 = IntPtr.Zero;
    private bool _sdlInputInitialized;
    private long _lastGamepadScanTicks;
    private readonly HashSet<GamepadButton> _gamepad1ButtonsDown = new();
    private readonly HashSet<GamepadButton> _gamepad1ButtonsDownPrev = new();
    private readonly HashSet<GamepadButton> _gamepad2ButtonsDown = new();
    private readonly HashSet<GamepadButton> _gamepad2ButtonsDownPrev = new();
    private int _lastGamepad1ButtonPressed = (int)GamepadButton.None;
    private int _lastGamepad2ButtonPressed = (int)GamepadButton.None;
    private readonly object _gamepadStateLock = new();

    private static readonly GamepadButton[] s_gamepadButtonsToPoll =
    {
        GamepadButton.A,
        GamepadButton.B,
        GamepadButton.X,
        GamepadButton.Y,
        GamepadButton.LeftShoulder,
        GamepadButton.RightShoulder,
        GamepadButton.LeftTrigger,
        GamepadButton.RightTrigger,
        GamepadButton.Back,
        GamepadButton.Start,
        GamepadButton.LeftThumb,
        GamepadButton.RightThumb,
        GamepadButton.DPadUp,
        GamepadButton.DPadDown,
        GamepadButton.DPadLeft,
        GamepadButton.DPadRight
    };

    private static readonly Dictionary<GamepadButton, GameControllerButton> sdlButtonMap = new()
    {
        [GamepadButton.A] = GameControllerButton.A,
        [GamepadButton.B] = GameControllerButton.B,
        [GamepadButton.X] = GameControllerButton.X,
        [GamepadButton.Y] = GameControllerButton.Y,
        [GamepadButton.LeftShoulder] = GameControllerButton.Leftshoulder,
        [GamepadButton.RightShoulder] = GameControllerButton.Rightshoulder,
        [GamepadButton.Back] = GameControllerButton.Back,
        [GamepadButton.Start] = GameControllerButton.Start,
        [GamepadButton.LeftThumb] = GameControllerButton.Leftstick,
        [GamepadButton.RightThumb] = GameControllerButton.Rightstick,
        [GamepadButton.DPadUp] = GameControllerButton.DpadUp,
        [GamepadButton.DPadDown] = GameControllerButton.DpadDown,
        [GamepadButton.DPadLeft] = GameControllerButton.DpadLeft,
        [GamepadButton.DPadRight] = GameControllerButton.DpadRight
    };

    private readonly DispatcherTimer _timer;
    private DispatcherTimer? _audioDebugTimer;
    private bool _audioDebugEnabled;
    private bool _pad2MirrorEnabled;
    private bool _inputTraceEnabled;
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
    private readonly List<string> _recentRomPaths = new();
    private bool _recentRomUpdating;

    // Input “håll nere”
    private readonly HashSet<Key> _keysDown = new();
    private InputMappingSettings _inputMappings = new InputMappingSettings();
    private IAudioSink? _audioOutput;
    private AudioEngine? _audioEngine;
    private bool _audioEnabled = true;
    private bool _audioFormatMismatchLogged;
    private const int AudioSampleRate = 44100;
    private const int AudioChannels = 2;
    private const int AudioBufferChunkFrames = 256;
    private static readonly double AudioCyclesScale = GetAudioCyclesScale();
    private static readonly double SystemCyclesScale = GetSystemCyclesScale();
    private static readonly bool TraceAudioCycles =
        Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_AUDIO_CYCLES") == "1";
    private static readonly bool AudioEnvEnabled = Environment.GetEnvironmentVariable("EUTHERDRIVE_AUDIO") == "1";
    private static readonly bool AudioTimedEnvEnabled = Environment.GetEnvironmentVariable("EUTHERDRIVE_AUDIO_TIMED") != "0";
    private static readonly bool YmEnvEnabled = Environment.GetEnvironmentVariable("EUTHERDRIVE_YM") == "1";
    private static readonly bool AudioStatsEnabled =
        Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_AUDIO_STATS") == "1";
    private static readonly bool AudioRawTiming =
        Environment.GetEnvironmentVariable("EUTHERDRIVE_AUDIO_RAW_TIMING") == "1";
    private static readonly bool TraceConsoleEnabled =
        Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_CONSOLE") != "0" && !AudioRawTiming;
    private static readonly bool TraceAudioLevel =
        Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_AUDLVL") == "1";
    private static readonly bool TracePerf =
        Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_PERF") == "1";
    private static readonly bool TraceAudioQueue =
        Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_AUDIO_QUEUE") == "1";
    private static readonly bool AudioCatchupEnabled =
        Environment.GetEnvironmentVariable("EUTHERDRIVE_AUDIO_CATCHUP") != "0" && !AudioRawTiming;
    private static readonly bool AudioPullEnabled =
        Environment.GetEnvironmentVariable("EUTHERDRIVE_AUDIO_PULL") == "1" && !AudioRawTiming;
    private static readonly bool AudioClockFrame =
        !string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_AUDIO_CLOCK"), "cycles", StringComparison.OrdinalIgnoreCase);
    private static readonly bool TraceAudioPull =
        Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_AUDIO_PULL") == "1";
    private static readonly bool AudioPllEnabled =
        Environment.GetEnvironmentVariable("EUTHERDRIVE_AUDIO_PLL") == "1" && !AudioRawTiming;
    private const int AudioMaxFramesPerTick = 4096;
    private static readonly int AudioTargetBufferedFrames = GetAudioTargetBufferedFrames();
    private static readonly int AudioMaxCatchupFramesPerTick = GetAudioMaxCatchupFramesPerTick();
    private static readonly double AudioPllMax = GetAudioPllMax();
    private static readonly int AudioEngineBufferFrames = GetAudioEngineBufferFrames();
    private static readonly int AudioEngineBatchFrames = GetAudioEngineBatchFrames();
    private static readonly int AudioPullMaxFrames = GetAudioPullMaxFrames();
    private static readonly int AudioTimedMaxFrames = GetAudioTimedMaxFrames();
    private static readonly bool TraceUiFrame = IsEnvEnabled("EUTHERDRIVE_TRACE_UI_FRAME");
    private static readonly bool TraceUiRender = IsEnvEnabled("EUTHERDRIVE_TRACE_UI_RENDER");
    private static readonly bool TraceUiPresent = IsEnvEnabled("EUTHERDRIVE_TRACE_UI_PRESENT");
    private static readonly bool TraceUiAudio = IsEnvEnabled("EUTHERDRIVE_TRACE_UI_AUDIO");
    private static readonly bool SkipDuplicateFrames = !IsEnvEnabled("EUTHERDRIVE_DISABLE_SKIP_DUP_FRAMES");
    private static readonly bool TraceSysCycles = IsEnvEnabled("EUTHERDRIVE_TRACE_SYS_CYCLES");
    private static readonly bool Pad2MirrorDefault =
        Environment.GetEnvironmentVariable("EUTHERDRIVE_PAD2_MIRROR") == "1";
    private static readonly bool TracePadUiDefault =
        Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_PAD_UI") == "1";
    private static readonly bool TracePadMapping =
        Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_PAD_MAPPING") == "1";
    private static readonly bool TracePadRaw =
        Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_PAD_RAW") == "1";
    private static readonly bool AudioOutPllEnabledEnv = Environment.GetEnvironmentVariable("EUTHERDRIVE_AUDIO_OUT_PLL") == "1";
    private static readonly bool AudioTimedDrainEnabledEnv = Environment.GetEnvironmentVariable("EUTHERDRIVE_AUDIO_TIMED_DRAIN") == "1";
    private TextWriter? _originalConsoleOut;
    private StreamWriter? _romLogWriter;
    private bool _toneTestRunning;
    private bool _psgBlipRunning;
    private bool _audioTimedEnabled;
    private bool _ymResampleLinear;
    private double _z80CyclesMult = 1.0;
    private bool _speedLockEnabled = true;
    private bool _renderSkipEnabled;
    private int _renderSkipCounter;
    private bool _smsOverscanEnabled;
    private double _speedScale = 1.0;
    private long _emuFpsLastTicks;
    private int _emuFpsFrames;
    private double _emuActualFps;
    private long _sysCycleLastLogTicks;
    private long _sysCycleLastValue;
    private long _speedLockLastTicks;
    private static readonly bool TraceSpeedLock =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_SPEEDLOCK"), "1", StringComparison.Ordinal);
    private long _audioDebugLastTicks;
    private double _audioDebugLastRatio;
    private long _audioDebugLastDeltaCycles;
    private string _audioDebugLastText = string.Empty;
    private long _audioDebugCycleLastTicks;
    private long _audioDebugStatsLastTicks;
    private long _audioDebugLastProduced;
    private long _audioDebugLastConsumed;
    private long _audioDebugLastDropped;
    private long _audioDebugLastUnderrunEvents;
    private long _audioDebugLastUnderrunFrames;
    private const int AutoFireRateMin = 5;
    private const int AutoFireRateMax = 30;
    private const int AutoFireRateDefault = 12;
    private int _autoFireRateHz = AutoFireRateDefault;
    private int _autoFireMask;
    private long _audioLastTicks;
    private double _audioFrameAccumulator;
    private double _audioDrivenAccumulator;
    private long _audioLastSystemCycles;
    private long _audioCycleLogLastTicks;
    private long _audioLastDropLogTicks;
    private long _audioQueueLogLastTicks;
    private bool _audioPullMode;
    private short[] _audioPullBuffer = Array.Empty<short>();
    private short[] _audioPullOutBuffer = Array.Empty<short>();
    private int _audioPullBufferedSamples;
    private long _audioPullLogLastTicks;
    private volatile bool _audioPullReady;
    private long _audioPullLastFrameCounter = -1;
    private long _audioPullLastFrameCounterTicks;
    private int _masterVolumePercent = DefaultMasterVolumePercent;
    private int _psgMixPercent = DefaultPsgMixPercent;
    private int _ymMixPercent = DefaultYmMixPercent;
    private int _noiseMixPercent = DefaultNoiseMixPercent;
    private ConsoleRegion _regionOverride = ConsoleRegion.Auto;
    private ConsoleRegion _defaultRegionOverride = ConsoleRegion.Auto;
    private ConsoleRegion _romRegionHint = ConsoleRegion.Auto;
    private FrameRateMode _frameRateMode = FrameRateMode.Auto;
    private readonly Dictionary<string, ConsoleRegion> _romRegionOverrides = new(StringComparer.OrdinalIgnoreCase);
    private string? _romRegionKey;
    private bool _regionOverrideUpdating;
    private bool _frameRateUpdating;
    private const string SettingsFileName = "eutherdrive_settings.toml";
    private const string LegacyJsonSettingsFileName = "eutherdrive_settings.json";
    private const string LegacyRegionSettingsFileName = "eutherdrive_region.txt";
    private const string LegacyLastRomPathFileName = "eutherdrive_last_rom.txt";
    private const int DefaultMasterVolumePercent = 50;
    private const int DefaultPsgMixPercent = 100;
    private const int DefaultYmMixPercent = 100;
    private const int DefaultNoiseMixPercent = 100;
    private const int DefaultCpuCyclesPerLine = 488;

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

        ApplyConsoleSilence();
        HookInput();
        HookMouseMovement();

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex)
                LogException(ex, "UnhandledException");
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            LogException(e.Exception, "UnobservedTaskException");
            e.SetObserved();
        };

        _savestateService = new SavestateService();
        _savestateViewModel = new SavestateViewModel(
            _savestateService,
            () => _core as ISavestateCapable,
            PauseEmulation,
            ResumeEmulation,
            SetStatus);
        if (SavestatePanel != null)
            SavestatePanel.DataContext = _savestateViewModel;

        _ymResampleLinear = IsEnvEnabled("EUTHERDRIVE_YM_RESAMPLE_LINEAR")
            || string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_YM_RESAMPLE"), "linear", StringComparison.OrdinalIgnoreCase);
        LoadSettings();
        UpdateRecentRomCombo();
        if (MasterVolumeSlider != null)
            MasterVolumeSlider.Value = _masterVolumePercent;
        UpdateMasterVolumeText();
        if (PsgMixSlider != null)
            PsgMixSlider.Value = _psgMixPercent;
        UpdatePsgMixText();
        if (YmMixSlider != null)
            YmMixSlider.Value = _ymMixPercent;
        UpdateYmMixText();
        if (NoiseMixSlider != null)
            NoiseMixSlider.Value = _noiseMixPercent;
        UpdateNoiseMixText();
        if (AudioEnabledCheck != null)
            AudioEnabledCheck.IsChecked = _audioEnabled || AudioEnvEnabled;
        UpdateYmResampleUi();
        UpdateZ80CyclesMultUi();

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
        UpdateSpeedLockUi();
        UpdateRenderSkipUi();
        UpdateSpeedUi();
        UpdateSmsOverscanUi();

        // Initialize timer
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(16.666), DispatcherPriority.Render, (_, _) => Tick());
        _audioDebugEnabled = TraceUiAudio;
        if (AudioDebugCheck != null)
        {
            AudioDebugCheck.IsChecked = _audioDebugEnabled;
            AudioDebugCheck.Checked += OnAudioDebugToggle;
            AudioDebugCheck.Unchecked += OnAudioDebugToggle;
        }
        _pad2MirrorEnabled = Pad2MirrorDefault;
        if (Pad2MirrorCheck != null)
        {
            Pad2MirrorCheck.IsChecked = _pad2MirrorEnabled;
            Pad2MirrorCheck.Checked += OnPad2MirrorToggle;
            Pad2MirrorCheck.Unchecked += OnPad2MirrorToggle;
        }
        _inputTraceEnabled = TracePadUiDefault;
        if (InputTraceCheck != null)
        {
            InputTraceCheck.IsChecked = _inputTraceEnabled;
            InputTraceCheck.Checked += OnInputTraceToggle;
            InputTraceCheck.Unchecked += OnInputTraceToggle;
        }
        UpdateAudioDebugTimer();

        // Load ROM from command line if provided
        if (!string.IsNullOrEmpty(romPath) && File.Exists(romPath))
        {
            StatusText.Text = $"Loading from CLI: {romPath}";
            _romPath = romPath;
            AddRecentRom(_romPath);
            _core = new MdTracerAdapter();
            ApplyMasterVolumeToCore();
            ApplyAudioMixToCore();
            ApplyDefaultCpuCyclesPerLine();
            if (_core is MdTracerAdapter smsAdapter)
                smsAdapter.SetShowSmsOverscan(_smsOverscanEnabled);
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

    private void UpdateSpeedLockUi()
    {
        if (SpeedLockCheck != null)
            SpeedLockCheck.IsChecked = _speedLockEnabled;
    }

    private void OnSpeedLockToggle(object? sender, RoutedEventArgs e)
    {
        _speedLockEnabled = SpeedLockCheck?.IsChecked == true;
        SaveSettings();
    }

    private void UpdateRenderSkipUi()
    {
        if (RenderSkipCheck != null)
            RenderSkipCheck.IsChecked = _renderSkipEnabled;
    }

    private void OnRenderSkipToggle(object? sender, RoutedEventArgs e)
    {
        _renderSkipEnabled = RenderSkipCheck?.IsChecked == true;
        _renderSkipCounter = 0;
        SaveSettings();
    }

    private void UpdateSpeedUi()
    {
        if (SpeedSlider != null)
            SpeedSlider.Value = _speedScale;
        if (SpeedValueText != null)
            SpeedValueText.Text = $"{_speedScale * 100:0}%";
    }

    private void UpdateSmsOverscanUi()
    {
        if (SmsOverscanCheck != null)
            SmsOverscanCheck.IsChecked = _smsOverscanEnabled;
    }

    private void OnSmsOverscanToggle(object? sender, RoutedEventArgs e)
    {
        _smsOverscanEnabled = SmsOverscanCheck?.IsChecked == true;
        if (_core is MdTracerAdapter adapter)
            adapter.SetShowSmsOverscan(_smsOverscanEnabled);
        SaveSettings();
    }

    private void OnSpeedSliderChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        _speedScale = e.NewValue;
        if (SpeedValueText != null)
            SpeedValueText.Text = $"{_speedScale * 100:0}%";
        SaveSettings();
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

        InitGamepadInput();
    }

    private void InitGamepadInput()
    {
        try
        {
            _sdl = SdlApi.GetApi();
            int rc = _sdl.Init(SdlApi.InitGamecontroller | SdlApi.InitJoystick | SdlApi.InitEvents);
            if (rc != 0)
            {
                Console.WriteLine("[Gamepad] SDL init failed: " + _sdl.GetErrorS());
                _sdlInputInitialized = false;
                return;
            }

            _sdlInputInitialized = true;
            TryOpenGamepads();
            Console.WriteLine("[Gamepad] SDL gamepad input initialized");
        }
        catch (Exception ex)
        {
            Console.WriteLine("[Gamepad] SDL init exception: " + ex.Message);
            _sdlInputInitialized = false;
        }
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

        if (e.Key == Key.F9 && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            if (_core is MdTracerAdapter adapter)
                adapter.TriggerSmsDump();
            e.Handled = true;
            return;
        }
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
        AddRecentRom(_romPath);
    }

    private void OnStart(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            Console.WriteLine($"[UI] Start clicked. romPath='{_romPath}' exists={(!string.IsNullOrWhiteSpace(_romPath) && File.Exists(_romPath))}");
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
                Console.WriteLine("[UI] Core created (MdTracerAdapter).");
                ApplyMasterVolumeToCore();
                ApplyAudioMixToCore();
                ApplyDefaultCpuCyclesPerLine();
                if (_core is MdTracerAdapter smsAdapter)
                    smsAdapter.SetShowSmsOverscan(_smsOverscanEnabled);

                if (!string.IsNullOrWhiteSpace(_romPath))
                {
                    Console.WriteLine($"[UI] Loading ROM: {_romPath}");
                    StartRomLog();
                    _timer.Stop();

                    if (_core is MdTracerAdapter m)
                    {
                        m.PowerCycleAndLoadRom(_romPath);
                        Console.WriteLine("[UI] ROM loaded into core.");
                        _audioPullReady = true;
                        PrimePullAudio();

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
                        AddRecentRom(_romPath);
                    }
                    else
                    {
                        _core.LoadRom(_romPath);
                        _audioPullReady = true;
                        PrimePullAudio();
                        AddRecentRom(_romPath);
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
                
                // Test: Use NullAudioSink to debug audio sync issues
                bool useNullAudio = Environment.GetEnvironmentVariable("EUTHERDRIVE_NULL_AUDIO") == "1";
                if (useNullAudio)
                {
                    _audioOutput = new NullAudioSink();
                    StatusText.Text = "Null audio (debug)";
                }
                else
                {
                    _audioOutput = OpenAlAudioOutput.TryCreate();
                    StatusText.Text = _audioOutput is null
                        ? "Audio output unavailable"
                        : "Audio output ready";
                }
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
            LogException(ex, "Start");
            _audioPullReady = false;
        }
    }

    private static void LogException(Exception ex, string context)
    {
        Console.WriteLine($"[UI-ERROR] {context}: {ex.Message}");
        Console.WriteLine(ex.ToString());
        if (ex.InnerException != null)
        {
            Console.WriteLine($"[UI-ERROR] {context} inner: {ex.InnerException.Message}");
            Console.WriteLine(ex.InnerException.ToString());
        }
    }

    private void AddRecentRom(string? path, bool save = true, bool updateCombo = true)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        string fullPath = path;
        _recentRomPaths.RemoveAll(p => string.Equals(p, fullPath, StringComparison.OrdinalIgnoreCase));
        _recentRomPaths.Insert(0, fullPath);
        if (_recentRomPaths.Count > 5)
            _recentRomPaths.RemoveRange(5, _recentRomPaths.Count - 5);

        if (save)
            SaveSettings();

        if (updateCombo)
        {
            Dispatcher.UIThread.Post(UpdateRecentRomCombo, DispatcherPriority.Background);
        }
    }

    private void UpdateRecentRomCombo()
    {
        if (RecentRomCombo == null)
            return;

        _recentRomUpdating = true;
        try
        {
            RecentRomCombo.Items.Clear();
            ComboBoxItem? selected = null;
            foreach (var path in _recentRomPaths)
            {
                string name = Path.GetFileName(path);
                var item = new ComboBoxItem { Content = name, Tag = path };
                ToolTip.SetTip(item, path);
                RecentRomCombo.Items.Add(item);
                if (!string.IsNullOrWhiteSpace(_romPath)
                    && string.Equals(path, _romPath, StringComparison.OrdinalIgnoreCase))
                {
                    selected = item;
                }
            }
            RecentRomCombo.SelectedItem = selected;
        }
        finally
        {
            _recentRomUpdating = false;
        }
    }

    private void OnRecentRomSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (_recentRomUpdating || RecentRomCombo?.SelectedItem is not ComboBoxItem item)
            return;

        if (item.Tag is string path && !string.IsNullOrWhiteSpace(path))
        {
            _romPath = path;
            if (RomPathText != null)
                RomPathText.Text = _romPath;
            StatusText.Text = "ROM selected (recent)";
            AddRecentRom(_romPath);
        }
    }

    private async void OnAbout(object? sender, RoutedEventArgs e)
    {
        var text = new TextBlock
        {
            Text = "Made by Nichlas and AI",
            Margin = new Thickness(16),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        };

        var dialog = new Window
        {
            Title = "About",
            Width = 260,
            Height = 120,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = text
        };

        await dialog.ShowDialog(this);
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

    private void ApplyDefaultCpuCyclesPerLine()
    {
        if (_core is not MdTracerAdapter adapter)
            return;
        adapter.SetCpuCyclesPerLine(DefaultCpuCyclesPerLine);
        if (CpuCyclesTextBox != null)
            CpuCyclesTextBox.Text = DefaultCpuCyclesPerLine.ToString(System.Globalization.CultureInfo.InvariantCulture);
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

    private void ApplyAudioMixToCore()
    {
        if (_core is MdTracerAdapter adapter)
        {
            adapter.SetPsgMixPercent(_psgMixPercent);
            adapter.SetYmMixPercent(_ymMixPercent);
            adapter.SetPsgNoiseMixPercent(_noiseMixPercent);
        }
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

    private void UpdatePsgMixText()
    {
        if (PsgMixValueText != null)
            PsgMixValueText.Text = $"{_psgMixPercent}%";
    }

    private void UpdateYmMixText()
    {
        if (YmMixValueText != null)
            YmMixValueText.Text = $"{_ymMixPercent}%";
    }

    private void UpdateNoiseMixText()
    {
        if (NoiseMixValueText != null)
            NoiseMixValueText.Text = $"{_noiseMixPercent}%";
    }

    private void OnPsgMixChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        int percent = ClampMixPercent((int)Math.Round(e.NewValue));
        if (percent == _psgMixPercent)
            return;
        _psgMixPercent = percent;
        UpdatePsgMixText();
        ApplyAudioMixToCore();
        SaveSettings();
    }

    private void OnYmMixChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        int percent = ClampMixPercent((int)Math.Round(e.NewValue));
        if (percent == _ymMixPercent)
            return;
        _ymMixPercent = percent;
        UpdateYmMixText();
        ApplyAudioMixToCore();
        SaveSettings();
    }

    private void OnNoiseMixChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        int percent = ClampMixPercent((int)Math.Round(e.NewValue));
        if (percent == _noiseMixPercent)
            return;
        _noiseMixPercent = percent;
        UpdateNoiseMixText();
        ApplyAudioMixToCore();
        SaveSettings();
    }

    private static int ClampMixPercent(int value)
    {
        if (value < 0) return 0;
        if (value > 200) return 200;
        return value;
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
            RomInfoText.Text = string.IsNullOrWhiteSpace(info.ExtraInfo)
                ? info.Summary
                : $"{info.Summary}\n{info.ExtraInfo}";

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

    private void UpdateYmResampleUi()
    {
        if (YmResampleCombo == null)
            return;
        foreach (var item in YmResampleCombo.Items)
        {
            if (item is ComboBoxItem combo && combo.Tag is string tag)
            {
                bool isLinear = string.Equals(tag, "linear", StringComparison.OrdinalIgnoreCase);
                if (isLinear == _ymResampleLinear)
                {
                    YmResampleCombo.SelectedItem = item;
                    return;
                }
            }
        }
    }

    private void UpdateZ80CyclesMultUi()
    {
        if (Z80CyclesMultTextBox == null)
            return;
        Z80CyclesMultTextBox.Text = _z80CyclesMult.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
    }

    public enum GamepadButton
    {
        None = 0,
        A = 1,
        B = 2,
        X = 3,
        Y = 4,
        LeftShoulder = 5,
        RightShoulder = 6,
        LeftTrigger = 7,
        RightTrigger = 8,
        Back = 9,
        Start = 10,
        LeftThumb = 11,
        RightThumb = 12,
        DPadUp = 13,
        DPadDown = 14,
        DPadLeft = 15,
        DPadRight = 16,
    }

    public sealed class InputMappingSettings
    {
        public Dictionary<string, Key> KeyboardMappings { get; set; } = new();
        public Dictionary<string, GamepadButton> GamepadMappings { get; set; } = new();
        public Dictionary<string, GamepadButton> Gamepad2Mappings { get; set; } = new();

        public InputMappingSettings()
        {
            // Set default keyboard mappings (matching current hardcoded mapping)
            KeyboardMappings["Up"] = Key.Up;
            KeyboardMappings["Down"] = Key.Down;
            KeyboardMappings["Left"] = Key.Left;
            KeyboardMappings["Right"] = Key.Right;
            KeyboardMappings["A"] = Key.Z;
            KeyboardMappings["B"] = Key.X;
            KeyboardMappings["C"] = Key.C;
            KeyboardMappings["Start"] = Key.Enter;
            KeyboardMappings["Pause"] = Key.Enter;
            KeyboardMappings["X"] = Key.A;
            KeyboardMappings["Y"] = Key.S;
            KeyboardMappings["Z"] = Key.D;
            KeyboardMappings["Mode"] = Key.LeftShift;

            // Default gamepad mappings (optional, can be None)
            GamepadMappings["Up"] = GamepadButton.DPadUp;
            GamepadMappings["Down"] = GamepadButton.DPadDown;
            GamepadMappings["Left"] = GamepadButton.DPadLeft;
            GamepadMappings["Right"] = GamepadButton.DPadRight;
            GamepadMappings["A"] = GamepadButton.A;
            GamepadMappings["B"] = GamepadButton.B;
            GamepadMappings["C"] = GamepadButton.X;
            GamepadMappings["Start"] = GamepadButton.Start;
            GamepadMappings["Pause"] = GamepadButton.Start;
            GamepadMappings["X"] = GamepadButton.Y;
            GamepadMappings["Y"] = GamepadButton.LeftShoulder;
            GamepadMappings["Z"] = GamepadButton.RightShoulder;
            GamepadMappings["Mode"] = GamepadButton.Back;

            // Gamepad 2 mappings start empty to avoid accidental P2 input.
        }
    }

    private sealed class MappingItem
    {
        public string Action { get; set; } = "";
        public Key KeyboardKey { get; set; }
        public GamepadButton GamepadButton1 { get; set; }
        public GamepadButton GamepadButton2 { get; set; }
        public Button? KeyboardButton { get; set; }
        public Button? Gamepad1ButtonControl { get; set; }
        public Button? Gamepad2ButtonControl { get; set; }
    }

    private sealed class InputMappingDialog : Window
    {
        private enum RecordingDevice
        {
            Keyboard,
            Gamepad1,
            Gamepad2
        }

        public InputMappingSettings Mappings { get; }
        private readonly List<MappingItem> _items = new();
        private MappingItem? _currentlyRecording;
        private TextBlock? _recordingHint;
        private readonly Func<GamepadButton?>? _gamepad1ButtonProvider;
        private readonly Func<GamepadButton?>? _gamepad2ButtonProvider;
        private DispatcherTimer? _gamepadPollTimer;
        private RecordingDevice _recordingDevice = RecordingDevice.Keyboard;

        public InputMappingDialog(InputMappingSettings currentMappings, Func<GamepadButton?>? gamepad1ButtonProvider = null, Func<GamepadButton?>? gamepad2ButtonProvider = null)
        {
            Mappings = currentMappings;
            _gamepad1ButtonProvider = gamepad1ButtonProvider;
            _gamepad2ButtonProvider = gamepad2ButtonProvider;
            Title = "Input Settings";
            Width = 860;
            Height = 600;
            Background = new SolidColorBrush(Color.Parse("#0F1216"));
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            // Convert mappings to list items
            var actions = new[] { "Up", "Down", "Left", "Right", "A", "B", "C", "Start", "Pause", "X", "Y", "Z", "Mode" };
            foreach (var action in actions)
            {
                Mappings.KeyboardMappings.TryGetValue(action, out Key key);
                Mappings.GamepadMappings.TryGetValue(action, out GamepadButton gp1);
                Mappings.Gamepad2Mappings.TryGetValue(action, out GamepadButton gp2);
                _items.Add(new MappingItem { Action = action, KeyboardKey = key, GamepadButton1 = gp1, GamepadButton2 = gp2 });
            }

            BuildUi();
        }

        private void BuildUi()
        {
            var stack = new StackPanel { Spacing = 10, Margin = new Thickness(16) };
            stack.Children.Add(new TextBlock
            {
                Text = "Input Mappings",
                FontSize = 18,
                FontWeight = Avalonia.Media.FontWeight.Bold
            });

            stack.Children.Add(new TextBlock
            {
                Text = "Click on a key binding to change it. Press any key or gamepad button while recording.",
                TextWrapping = TextWrapping.Wrap
            });

            _recordingHint = new TextBlock
            {
                Text = "",
                Foreground = new SolidColorBrush(Colors.Yellow),
                IsVisible = false
            };
            stack.Children.Add(_recordingHint);

            // Create a grid for the mappings
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // Headers
            AddTextBlock(grid, 0, 0, "Action", true);
            AddTextBlock(grid, 0, 1, "Keyboard Key", true);
            AddTextBlock(grid, 0, 2, "Gamepad 1", true);
            AddTextBlock(grid, 0, 3, "Gamepad 2", true);

            // Rows
            for (int i = 0; i < _items.Count; i++)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var item = _items[i];
                int row = i + 1;

                AddTextBlock(grid, row, 0, item.Action, false);

                // Keyboard key button
                var keyButton = new Button
                {
                    Content = item.KeyboardKey.ToString(),
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                    Background = new SolidColorBrush(Color.Parse("#2A313B")),
                    Foreground = new SolidColorBrush(Colors.White)
                };
                keyButton.Click += (s, e) => StartRecording(item, RecordingDevice.Keyboard);
                Grid.SetRow(keyButton, row);
                Grid.SetColumn(keyButton, 1);
                grid.Children.Add(keyButton);
                item.KeyboardButton = keyButton;

                bool gamepad1Enabled = _gamepad1ButtonProvider != null;
                var gp1Button = new Button
                {
                    Content = item.GamepadButton1.ToString(),
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                    Background = new SolidColorBrush(Color.Parse(gamepad1Enabled ? "#2A313B" : "#1A1F26")),
                    Foreground = new SolidColorBrush(gamepad1Enabled ? Colors.White : Colors.Gray),
                    IsEnabled = gamepad1Enabled
                };
                if (gamepad1Enabled)
                    gp1Button.Click += (s, e) => StartRecording(item, RecordingDevice.Gamepad1);
                Grid.SetRow(gp1Button, row);
                Grid.SetColumn(gp1Button, 2);
                grid.Children.Add(gp1Button);
                item.Gamepad1ButtonControl = gp1Button;

                bool gamepad2Enabled = _gamepad2ButtonProvider != null;
                var gp2Button = new Button
                {
                    Content = item.GamepadButton2.ToString(),
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                    Background = new SolidColorBrush(Color.Parse(gamepad2Enabled ? "#2A313B" : "#1A1F26")),
                    Foreground = new SolidColorBrush(gamepad2Enabled ? Colors.White : Colors.Gray),
                    IsEnabled = gamepad2Enabled
                };
                if (gamepad2Enabled)
                    gp2Button.Click += (s, e) => StartRecording(item, RecordingDevice.Gamepad2);
                Grid.SetRow(gp2Button, row);
                Grid.SetColumn(gp2Button, 3);
                grid.Children.Add(gp2Button);
                item.Gamepad2ButtonControl = gp2Button;
            }

            stack.Children.Add(grid);

            var buttonPanel = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 10 };
            var okButton = new Button { Content = "OK", Width = 80 };
            okButton.Click += (s, e) => OnOk();
            var cancelButton = new Button { Content = "Cancel", Width = 80 };
            cancelButton.Click += (s, e) => Close(false);
            buttonPanel.Children.Add(okButton);
            buttonPanel.Children.Add(cancelButton);
            stack.Children.Add(buttonPanel);

            Content = new ScrollViewer { Content = stack };

            // Hook global key events
            KeyDown += OnDialogKeyDown;

            if (_gamepad1ButtonProvider != null || _gamepad2ButtonProvider != null)
            {
                _gamepadPollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
                _gamepadPollTimer.Tick += (_, _) => PollGamepadRecording();
                _gamepadPollTimer.Start();
            }

            Closed += (_, _) => _gamepadPollTimer?.Stop();
        }

        private static void AddTextBlock(Grid grid, int row, int col, string text, bool bold)
        {
            var tb = new TextBlock
            {
                Text = text,
                Margin = new Thickness(4),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                FontWeight = bold ? Avalonia.Media.FontWeight.Bold : Avalonia.Media.FontWeight.Normal
            };
            Grid.SetRow(tb, row);
            Grid.SetColumn(tb, col);
            grid.Children.Add(tb);
        }

        private void StartRecording(MappingItem item, RecordingDevice device)
        {
            _currentlyRecording = item;
            _recordingDevice = device;
            if (_recordingHint != null)
            {
                string deviceText = device switch
                {
                    RecordingDevice.Keyboard => "Keyboard",
                    RecordingDevice.Gamepad1 => "Gamepad 1",
                    RecordingDevice.Gamepad2 => "Gamepad 2",
                    _ => "Gamepad"
                };
                string keyText = device == RecordingDevice.Keyboard ? "key" : "button";
                _recordingHint.Text = $"Recording for {item.Action} ({deviceText}). Press a {keyText} or Escape to cancel...";
                _recordingHint.IsVisible = true;
            }
            // Focus the window to capture keys
            Focus();
        }

        private void OnDialogKeyDown(object? sender, KeyEventArgs e)
        {
            if (_currentlyRecording == null)
                return;

            // Escape cancels recording
            if (e.Key == Key.Escape)
            {
                _currentlyRecording = null;
                if (_recordingHint != null)
                    _recordingHint.IsVisible = false;
                e.Handled = true;
                return;
            }

            if (_recordingDevice != RecordingDevice.Keyboard)
                return;

            // Update keyboard mapping
            _currentlyRecording.KeyboardKey = e.Key;
            if (_currentlyRecording.KeyboardButton != null)
                _currentlyRecording.KeyboardButton.Content = e.Key.ToString();

            // Stop recording
            _currentlyRecording = null;
            if (_recordingHint != null)
                _recordingHint.IsVisible = false;
            e.Handled = true;
        }

        private void PollGamepadRecording()
        {
            if (_currentlyRecording == null || _recordingDevice == RecordingDevice.Keyboard)
                return;

            Func<GamepadButton?>? provider = _recordingDevice == RecordingDevice.Gamepad1
                ? _gamepad1ButtonProvider
                : _gamepad2ButtonProvider;
            if (provider == null)
                return;
            var button = provider();
            if (!button.HasValue)
                return;

            if (_recordingDevice == RecordingDevice.Gamepad1)
            {
                _currentlyRecording.GamepadButton1 = button.Value;
                if (_currentlyRecording.Gamepad1ButtonControl != null)
                    _currentlyRecording.Gamepad1ButtonControl.Content = button.Value.ToString();
            }
            else if (_recordingDevice == RecordingDevice.Gamepad2)
            {
                _currentlyRecording.GamepadButton2 = button.Value;
                if (_currentlyRecording.Gamepad2ButtonControl != null)
                    _currentlyRecording.Gamepad2ButtonControl.Content = button.Value.ToString();
            }

            _currentlyRecording = null;
            if (_recordingHint != null)
                _recordingHint.IsVisible = false;
        }



        private void OnOk()
        {
            // Update mappings from items
            foreach (var item in _items)
            {
                Mappings.KeyboardMappings[item.Action] = item.KeyboardKey;
                Mappings.GamepadMappings[item.Action] = item.GamepadButton1;
                Mappings.Gamepad2Mappings[item.Action] = item.GamepadButton2;
            }
            Close(true);
        }
    }

    private sealed class UiSettings
    {
        public string? LastRomPath { get; set; }
        public List<string>? RecentRomPaths { get; set; }
        public int MasterVolumePercent { get; set; } = DefaultMasterVolumePercent;
        public int PsgMixPercent { get; set; } = DefaultPsgMixPercent;
        public int YmMixPercent { get; set; } = DefaultYmMixPercent;
        public int NoiseMixPercent { get; set; } = DefaultNoiseMixPercent;
        public bool AudioEnabled { get; set; } = true;
        public bool YmResampleLinear { get; set; } = false;
        public double Z80CyclesMult { get; set; } = 1.0;
        public bool SpeedLockEnabled { get; set; } = true;
        public bool RenderSkipEnabled { get; set; } = false;
        public double SpeedScale { get; set; } = 1.0;
        public bool SmsOverscanEnabled { get; set; } = false;
        public ConsoleRegion DefaultRegionOverride { get; set; } = ConsoleRegion.Auto;
        public Dictionary<string, ConsoleRegion>? RomRegionOverrides { get; set; }
        public FrameRateMode FrameRateMode { get; set; } = FrameRateMode.Auto;
        public InputMappingSettings InputMappings { get; set; } = new();
    }

    private void LoadSettings()
    {
        string path = GetSettingsPath();
        if (File.Exists(path))
        {
            try
            {
                var settings = TryLoadTomlSettings(path);
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

        bool migrated = TryMigrateJsonSettings();
        if (migrated)
            return;

        bool legacyMigrated = LoadLegacySettings();
        if (legacyMigrated)
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

        _recentRomPaths.Clear();
        if (settings.RecentRomPaths != null)
        {
            foreach (var path in settings.RecentRomPaths)
            {
                if (!string.IsNullOrWhiteSpace(path))
                    AddRecentRom(path, save: false, updateCombo: false);
            }
        }
        else if (!string.IsNullOrWhiteSpace(settings.LastRomPath))
        {
            AddRecentRom(settings.LastRomPath, save: false, updateCombo: false);
        }
        UpdateRecentRomCombo();

        _masterVolumePercent = ClampPercent(settings.MasterVolumePercent);
        _psgMixPercent = ClampMixPercent(settings.PsgMixPercent);
        _ymMixPercent = ClampMixPercent(settings.YmMixPercent);
        _noiseMixPercent = ClampMixPercent(settings.NoiseMixPercent);
        _audioEnabled = settings.AudioEnabled;
        _ymResampleLinear = settings.YmResampleLinear;
        _z80CyclesMult = settings.Z80CyclesMult > 0 ? settings.Z80CyclesMult : 1.0;
        _speedLockEnabled = settings.SpeedLockEnabled;
        _renderSkipEnabled = settings.RenderSkipEnabled;
        _speedScale = settings.SpeedScale > 0 ? settings.SpeedScale : 1.0;
        _smsOverscanEnabled = settings.SmsOverscanEnabled;

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

        // Input mappings
        if (settings.InputMappings != null)
        {
            _inputMappings = settings.InputMappings;
            if (_inputMappings.Gamepad2Mappings == null || _inputMappings.Gamepad2Mappings.Count == 0)
            {
                _inputMappings.Gamepad2Mappings = new Dictionary<string, GamepadButton>();
            }
            if (!_inputMappings.KeyboardMappings.ContainsKey("Pause"))
                _inputMappings.KeyboardMappings["Pause"] = Key.Enter;
            if (!_inputMappings.GamepadMappings.ContainsKey("Pause"))
                _inputMappings.GamepadMappings["Pause"] = GamepadButton.Start;
            if (_inputMappings.Gamepad2Mappings != null && !_inputMappings.Gamepad2Mappings.ContainsKey("Pause"))
                _inputMappings.Gamepad2Mappings["Pause"] = GamepadButton.Start;
            _inputMappings.KeyboardMappings.Remove("Reset");
            _inputMappings.GamepadMappings.Remove("Reset");
            _inputMappings.Gamepad2Mappings?.Remove("Reset");
        }
        UpdateYmResampleUi();
        UpdateZ80CyclesMultUi();
        UpdateSpeedLockUi();
        UpdateRenderSkipUi();
        UpdateSpeedUi();
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
            RecentRomPaths = _recentRomPaths.ToList(),
            MasterVolumePercent = _masterVolumePercent,
            PsgMixPercent = _psgMixPercent,
            YmMixPercent = _ymMixPercent,
            NoiseMixPercent = _noiseMixPercent,
            AudioEnabled = _audioEnabled,
            YmResampleLinear = _ymResampleLinear,
            Z80CyclesMult = _z80CyclesMult,
            SpeedLockEnabled = _speedLockEnabled,
            RenderSkipEnabled = _renderSkipEnabled,
            SpeedScale = _speedScale,
            SmsOverscanEnabled = _smsOverscanEnabled,
            DefaultRegionOverride = _defaultRegionOverride,
            RomRegionOverrides = new Dictionary<string, ConsoleRegion>(_romRegionOverrides, StringComparer.OrdinalIgnoreCase),
            FrameRateMode = _frameRateMode,
            InputMappings = _inputMappings
        };
        WriteTomlSettings(GetSettingsPath(), settings);
    }

    private static string GetSettingsPath()
        => Path.Combine(Directory.GetCurrentDirectory(), SettingsFileName);

    private static string GetLegacyJsonSettingsPath()
        => Path.Combine(Directory.GetCurrentDirectory(), LegacyJsonSettingsFileName);

    private static UiSettings? TryLoadTomlSettings(string path)
    {
        try
        {
            string toml = File.ReadAllText(path);
            return Toml.ToModel<UiSettings>(toml);
        }
        catch
        {
            return null;
        }
    }

    private bool TryMigrateJsonSettings()
    {
        string legacyPath = GetLegacyJsonSettingsPath();
        if (!File.Exists(legacyPath))
            return false;

        try
        {
            string json = File.ReadAllText(legacyPath);
            var settings = JsonSerializer.Deserialize<UiSettings>(json);
            if (settings == null)
                return false;

            ApplySettings(settings);
            WriteTomlSettings(GetSettingsPath(), settings);
            File.Move(legacyPath, legacyPath + ".bak", overwrite: true);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void WriteTomlSettings(string path, UiSettings settings)
    {
        string toml = Toml.FromModel(settings);
        File.WriteAllText(path, toml);
    }

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
            "F1: Fullscreen",
            "Ctrl+F9: SMS VRAM/NT dump"));

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

    private async void OnInputSettings(object? sender, RoutedEventArgs e)
    {
        // Create and show input mapping configuration dialog
        Func<GamepadButton?>? gamepadProvider1 = _sdlInputInitialized
            ? () =>
            {
                UpdateGamepadState();
                return TryConsumeLastGamepad1ButtonPressed();
            }
            : null;
        Func<GamepadButton?>? gamepadProvider2 = _sdlInputInitialized && _activeGamepad2 != IntPtr.Zero
            ? () =>
            {
                UpdateGamepadState();
                return TryConsumeLastGamepad2ButtonPressed();
            }
            : null;
        var dialog = new InputMappingDialog(_inputMappings, gamepadProvider1, gamepadProvider2);
        if (await dialog.ShowDialog<bool>(this))
        {
            // Save updated mappings
            _inputMappings = dialog.Mappings;
            SaveSettings();
        }
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
        _audioTimedEnabled = _audioEnabled && AudioTimedEnvEnabled && !AudioRawTiming;
        ApplyAudioOptionsToCore();
        if (!_audioEnabled)
        {
            ResetAudioTiming();
            return;
        }

        _audioEngine = new AudioEngine(new PwCatAudioSink(), AudioSampleRate, AudioChannels, framesPerBatch: AudioEngineBatchFrames, bufferFrames: AudioEngineBufferFrames);
        _audioEngine.SetTargetBufferedFrames(AudioTargetBufferedFrames);
        _audioEngine.Start();
        if (AudioPullEnabled)
        {
            _audioPullMode = true;
            _audioPullReady = false;
            _audioEngine.EnablePullMode(AudioPullProducer, targetBufferedFrames: AudioTargetBufferedFrames, maxFramesPerPull: AudioPullMaxFrames);
            PrimePullAudio();
        }
        else
        {
            _audioPullMode = false;
            _audioPullReady = false;
        }
        _audioFormatMismatchLogged = false;
        ResetAudioTiming();
        if (_core is MdTracerAdapter adapter)
            PrefillAudioEngineBuffer(adapter);
    }

    private void StopAudioEngine()
    {
        if (_audioEngine == null)
            return;

        _audioEngine.Stop();
        _audioEngine = null;
        _audioPullMode = false;
        _audioPullReady = false;
        ResetAudioTiming();
    }

    private void ProducePsgForFrame()
    {
        if (_audioEngine == null || _core == null)
            return;
        // Audio is filled in the emu loop when the ring buffer is low.
    }

    private void ResetAudioTiming()
    {
        _audioLastTicks = 0;
        _audioFrameAccumulator = 0;
        _audioDrivenAccumulator = 0;
        _audioLastDropLogTicks = 0;
        _audioLastSystemCycles = 0;
        _audioPullBufferedSamples = 0;
        _audioPullReady = false;
        _audioPullLastFrameCounter = -1;
        _audioPullLastFrameCounterTicks = 0;
    }

    private void PrefillAudioEngineBuffer(MdTracerAdapter adapter)
    {
        if (_audioEngine == null)
            return;
        int target = AudioTargetBufferedFrames;
        if (target <= 0)
            return;
        int safety = 0;
        while (_audioEngine.BufferedFrames < target && safety < 128)
        {
            int need = target - _audioEngine.BufferedFrames;
            int chunk = need < AudioBufferChunkFrames ? need : AudioBufferChunkFrames;
            if (chunk <= 0)
                break;
            var audio = adapter.GetAudioBufferForFrames(chunk, out int rate, out int channels);
            if (audio.IsEmpty || rate != AudioSampleRate || channels != AudioChannels)
                break;
            _audioEngine.Submit(audio);
            safety++;
        }
    }

    private void ApplyAudioOptionsToCore()
    {
        if (_core is not MdTracerAdapter adapter)
            return;

        bool wantYm = YmEnvEnabled || (AudioEnabledCheck?.IsChecked == true);
        adapter.SetYmEnabled(wantYm);
        adapter.SetYmResampleLinear(_ymResampleLinear);
        adapter.SetZ80CycleMultiplier(_z80CyclesMult);
    }

    private void OnYmResampleChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (YmResampleCombo?.SelectedItem is ComboBoxItem item && item.Tag is string tag)
        {
            _ymResampleLinear = string.Equals(tag, "linear", StringComparison.OrdinalIgnoreCase);
            ApplyAudioOptionsToCore();
            SaveSettings();
        }
    }

    private void OnApplyZ80CyclesMult(object? sender, RoutedEventArgs e)
    {
        if (Z80CyclesMultTextBox == null)
            return;
        string raw = Z80CyclesMultTextBox.Text?.Trim() ?? string.Empty;
        if (!double.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double value))
            return;
        if (value <= 0.0)
            return;
        _z80CyclesMult = value;
        ApplyAudioOptionsToCore();
        SaveSettings();
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
        if (_renderSkipEnabled)
        {
            double emuFps = Volatile.Read(ref _emuActualFps);
            double targetFps = GetLiveTargetFps();
            if (emuFps > (targetFps * 1.02))
            {
                _renderSkipCounter++;
                if ((_renderSkipCounter & 1) == 1)
                    return;
            }
            else
            {
                _renderSkipCounter = 0;
            }
        }
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
            double emuFps = Volatile.Read(ref _emuActualFps);
            EmuFpsText.Text = $"Emu FPS: {emuFps:0.0}";
            _frames = 0;
            _fpsSw.Restart();
        }

        if (_core is EutherDrive.Core.Savestates.ISavestateCapable savestateCore)
        {
            long frame = savestateCore.FrameCounter ?? -1;
            FrameText.Text = $"Frame: {frame}";
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

    private void UpdateAudioDebugText()
    {
        if (!_audioDebugEnabled || AudioDebugText == null)
            return;

        long now = Stopwatch.GetTimestamp();
        if (now - _audioDebugLastTicks < Stopwatch.Frequency)
            return;
        _audioDebugLastTicks = now;

        if (_core is not MdTracerAdapter)
        {
            SetAudioDebugText("Audio dbg: no tracing core");
            return;
        }

        if (!_audioEnabled || _audioEngine == null)
        {
            SetAudioDebugText("Audio dbg: disabled");
            return;
        }

        int buffered = _audioEngine.BufferedFrames;
        int target = AudioTargetBufferedFrames;
        double fpsTarget = GetLiveTargetFps();
        double fpsEmu = Volatile.Read(ref _emuActualFps);
        double acc = _audioFrameAccumulator;
        string ratioText = _audioDebugLastRatio > 0 ? _audioDebugLastRatio.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) : "NA";
        string deltaText = _audioDebugLastDeltaCycles > 0 ? _audioDebugLastDeltaCycles.ToString(System.Globalization.CultureInfo.InvariantCulture) : "NA";
        string rateText = "rate=NA";
        string underrunText = "und=NA/NA";

        long statsNow = Stopwatch.GetTimestamp();
        long produced = _audioEngine.ProducedFramesTotal;
        long consumed = _audioEngine.ConsumedFramesTotal;
        long dropped = _audioEngine.DroppedFramesTotal;
        long underrunEvents = _audioEngine.UnderrunEventsTotal;
        long underrunFrames = _audioEngine.UnderrunFramesTotal;
        if (_audioDebugStatsLastTicks != 0)
        {
            double elapsedSec = (statsNow - _audioDebugStatsLastTicks) / (double)Stopwatch.Frequency;
            if (elapsedSec > 0)
            {
                long prodDelta = produced - _audioDebugLastProduced;
                long consDelta = consumed - _audioDebugLastConsumed;
                long dropDelta = dropped - _audioDebugLastDropped;
                long undEventsDelta = underrunEvents - _audioDebugLastUnderrunEvents;
                long undFramesDelta = underrunFrames - _audioDebugLastUnderrunFrames;
                double prodFps = prodDelta / elapsedSec;
                double consFps = consDelta / elapsedSec;
                double dropFps = dropDelta / elapsedSec;
                double undEventsPerSec = undEventsDelta / elapsedSec;
                double undFramesPerSec = undFramesDelta / elapsedSec;
                rateText = $"rate={prodFps:0}/{consFps:0} drop={dropFps:0}";
                underrunText = $"und={undEventsPerSec:0}/{undFramesPerSec:0}";
            }
        }
        _audioDebugStatsLastTicks = statsNow;
        _audioDebugLastProduced = produced;
        _audioDebugLastConsumed = consumed;
        _audioDebugLastDropped = dropped;
        _audioDebugLastUnderrunEvents = underrunEvents;
        _audioDebugLastUnderrunFrames = underrunFrames;

        string mode = AudioClockFrame ? "frame" : (_audioTimedEnabled ? "timed" : "cycles");
        string line1 =
            $"Audio dbg: buf={buffered}/{target} acc={acc:0.##} mode={mode} fps={fpsTarget:0.##} emu={fpsEmu:0.##}";
        string line2 =
            $"Rate: {rateText} {underrunText}";
        string line3 =
            $"Cycles: ratio={ratioText} dCyc={deltaText} pll={(AudioPllEnabled ? 1 : 0)} outpll={(AudioOutPllEnabledEnv ? 1 : 0)} drain={(AudioTimedDrainEnabledEnv ? 1 : 0)}";
        string text = line1 + Environment.NewLine + line2 + Environment.NewLine + line3;
        SetAudioDebugText(text);
        UpdateInputDebugText();
    }

    private void UpdateInputDebugText()
    {
        if (InputDebugText == null)
            return;
        MdTracerAdapter.SetPadUiTrace(_inputTraceEnabled);
        InputDebugText.Text = MdTracerAdapter.GetPadUiText() ?? string.Empty;
    }

    private void OnPad2MirrorToggle(object? sender, RoutedEventArgs e)
    {
        _pad2MirrorEnabled = Pad2MirrorCheck?.IsChecked == true;
    }

    private void OnInputTraceToggle(object? sender, RoutedEventArgs e)
    {
        _inputTraceEnabled = InputTraceCheck?.IsChecked == true;
        MdTracerAdapter.SetPadUiTrace(_inputTraceEnabled);
        if (!_inputTraceEnabled && InputDebugText != null)
            InputDebugText.Text = string.Empty;
    }

    private void SetAudioDebugText(string text)
    {
        if (AudioDebugText == null)
            return;
        if (string.Equals(_audioDebugLastText, text, StringComparison.Ordinal))
            return;
        _audioDebugLastText = text;
        AudioDebugText.Text = text;
    }

    private void OnAudioDebugToggle(object? sender, RoutedEventArgs e)
    {
        _audioDebugEnabled = AudioDebugCheck?.IsChecked == true;
        UpdateAudioDebugTimer();
        if (!_audioDebugEnabled && AudioDebugText != null)
            AudioDebugText.Text = string.Empty;
    }

    private void UpdateAudioDebugTimer()
    {
        if (_audioDebugEnabled)
        {
            if (_audioDebugTimer == null)
                _audioDebugTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => UpdateAudioDebugText());
            _audioDebugTimer.Start();
        }
        else
        {
            _audioDebugTimer?.Stop();
        }
    }

    private void ApplyConsoleSilence()
    {
        if (TraceConsoleEnabled)
            return;
        _originalConsoleOut ??= Console.Out;
        Console.SetOut(TextWriter.Null);
        Console.SetError(TextWriter.Null);
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
        UpdateGamepadState();

        bool up;
        bool down;
        bool left;
        bool right;
        bool a;
        bool b;
        bool c;
        bool start;
        bool pause;
        bool x;
        bool y;
        bool z;
        bool mode;
        bool up2 = false;
        bool down2 = false;
        bool left2 = false;
        bool right2 = false;
        bool a2 = false;
        bool b2 = false;
        bool c2 = false;
        bool start2 = false;
        bool x2 = false;
        bool y2 = false;
        bool z2 = false;
        bool mode2 = false;
        PadType padType;
        int autoMask;
        int autoRate;
        lock (_keysDown)
        {
            // Use configured keyboard mappings
            up    = _inputMappings.KeyboardMappings.TryGetValue("Up", out Key upKey) && _keysDown.Contains(upKey);
            down  = _inputMappings.KeyboardMappings.TryGetValue("Down", out Key downKey) && _keysDown.Contains(downKey);
            left  = _inputMappings.KeyboardMappings.TryGetValue("Left", out Key leftKey) && _keysDown.Contains(leftKey);
            right = _inputMappings.KeyboardMappings.TryGetValue("Right", out Key rightKey) && _keysDown.Contains(rightKey);
            a     = _inputMappings.KeyboardMappings.TryGetValue("A", out Key aKey) && _keysDown.Contains(aKey);
            b     = _inputMappings.KeyboardMappings.TryGetValue("B", out Key bKey) && _keysDown.Contains(bKey);
            c     = _inputMappings.KeyboardMappings.TryGetValue("C", out Key cKey) && _keysDown.Contains(cKey);
            start = _inputMappings.KeyboardMappings.TryGetValue("Start", out Key startKey) && _keysDown.Contains(startKey);
            pause = _inputMappings.KeyboardMappings.TryGetValue("Pause", out Key pauseKey) && _keysDown.Contains(pauseKey);
            x     = _inputMappings.KeyboardMappings.TryGetValue("X", out Key xKey) && _keysDown.Contains(xKey);
            y     = _inputMappings.KeyboardMappings.TryGetValue("Y", out Key yKey) && _keysDown.Contains(yKey);
            z     = _inputMappings.KeyboardMappings.TryGetValue("Z", out Key zKey) && _keysDown.Contains(zKey);
            mode  = _inputMappings.KeyboardMappings.TryGetValue("Mode", out Key modeKey) && _keysDown.Contains(modeKey);
            padType = (PadType)Volatile.Read(ref _padTypeRaw);
        }

        // Combine with gamepad inputs if mapped
        if (_inputMappings.GamepadMappings.TryGetValue("Up", out GamepadButton gpUp) && gpUp != GamepadButton.None)
            up |= IsGamepadButtonPressed(gpUp);
        if (_inputMappings.GamepadMappings.TryGetValue("Down", out GamepadButton gpDown) && gpDown != GamepadButton.None)
            down |= IsGamepadButtonPressed(gpDown);
        if (_inputMappings.GamepadMappings.TryGetValue("Left", out GamepadButton gpLeft) && gpLeft != GamepadButton.None)
            left |= IsGamepadButtonPressed(gpLeft);
        if (_inputMappings.GamepadMappings.TryGetValue("Right", out GamepadButton gpRight) && gpRight != GamepadButton.None)
            right |= IsGamepadButtonPressed(gpRight);
        if (_inputMappings.GamepadMappings.TryGetValue("A", out GamepadButton gpA) && gpA != GamepadButton.None)
            a |= IsGamepadButtonPressed(gpA);
        if (_inputMappings.GamepadMappings.TryGetValue("B", out GamepadButton gpB) && gpB != GamepadButton.None)
            b |= IsGamepadButtonPressed(gpB);
        if (_inputMappings.GamepadMappings.TryGetValue("C", out GamepadButton gpC) && gpC != GamepadButton.None)
            c |= IsGamepadButtonPressed(gpC);
        if (_inputMappings.GamepadMappings.TryGetValue("Start", out GamepadButton gpStart) && gpStart != GamepadButton.None)
            start |= IsGamepadButtonPressed(gpStart);
        if (_inputMappings.GamepadMappings.TryGetValue("Pause", out GamepadButton gpPause) && gpPause != GamepadButton.None)
            pause |= IsGamepadButtonPressed(gpPause);
        if (_inputMappings.GamepadMappings.TryGetValue("X", out GamepadButton gpX) && gpX != GamepadButton.None)
            x |= IsGamepadButtonPressed(gpX);
        if (_inputMappings.GamepadMappings.TryGetValue("Y", out GamepadButton gpY) && gpY != GamepadButton.None)
            y |= IsGamepadButtonPressed(gpY);
        if (_inputMappings.GamepadMappings.TryGetValue("Z", out GamepadButton gpZ) && gpZ != GamepadButton.None)
            z |= IsGamepadButtonPressed(gpZ);
        if (_inputMappings.GamepadMappings.TryGetValue("Mode", out GamepadButton gpMode) && gpMode != GamepadButton.None)
            mode |= IsGamepadButtonPressed(gpMode);

        // Gamepad 2 mappings (no keyboard for P2)
        if (_activeGamepad2 != IntPtr.Zero && _inputMappings.Gamepad2Mappings.Count > 0)
        {
            if (_inputMappings.Gamepad2Mappings.TryGetValue("Up", out GamepadButton gp2Up) && gp2Up != GamepadButton.None)
                up2 |= IsGamepad2ButtonPressed(gp2Up);
            if (_inputMappings.Gamepad2Mappings.TryGetValue("Down", out GamepadButton gp2Down) && gp2Down != GamepadButton.None)
                down2 |= IsGamepad2ButtonPressed(gp2Down);
            if (_inputMappings.Gamepad2Mappings.TryGetValue("Left", out GamepadButton gp2Left) && gp2Left != GamepadButton.None)
                left2 |= IsGamepad2ButtonPressed(gp2Left);
            if (_inputMappings.Gamepad2Mappings.TryGetValue("Right", out GamepadButton gp2Right) && gp2Right != GamepadButton.None)
                right2 |= IsGamepad2ButtonPressed(gp2Right);
            if (_inputMappings.Gamepad2Mappings.TryGetValue("A", out GamepadButton gp2A) && gp2A != GamepadButton.None)
                a2 |= IsGamepad2ButtonPressed(gp2A);
            if (_inputMappings.Gamepad2Mappings.TryGetValue("B", out GamepadButton gp2B) && gp2B != GamepadButton.None)
                b2 |= IsGamepad2ButtonPressed(gp2B);
            if (_inputMappings.Gamepad2Mappings.TryGetValue("C", out GamepadButton gp2C) && gp2C != GamepadButton.None)
                c2 |= IsGamepad2ButtonPressed(gp2C);
            if (_inputMappings.Gamepad2Mappings.TryGetValue("Start", out GamepadButton gp2Start) && gp2Start != GamepadButton.None)
                start2 |= IsGamepad2ButtonPressed(gp2Start);
            if (_inputMappings.Gamepad2Mappings.TryGetValue("X", out GamepadButton gp2X) && gp2X != GamepadButton.None)
                x2 |= IsGamepad2ButtonPressed(gp2X);
            if (_inputMappings.Gamepad2Mappings.TryGetValue("Y", out GamepadButton gp2Y) && gp2Y != GamepadButton.None)
                y2 |= IsGamepad2ButtonPressed(gp2Y);
            if (_inputMappings.Gamepad2Mappings.TryGetValue("Z", out GamepadButton gp2Z) && gp2Z != GamepadButton.None)
                z2 |= IsGamepad2ButtonPressed(gp2Z);
            if (_inputMappings.Gamepad2Mappings.TryGetValue("Mode", out GamepadButton gp2Mode) && gp2Mode != GamepadButton.None)
                mode2 |= IsGamepad2ButtonPressed(gp2Mode);
        }

        if (core is MdTracerAdapter smsAdapter && smsAdapter.IsMasterSystemMode)
        {
            // SMS Pause uses dedicated Pause mapping (separate from MD Start).
            start = pause;

            // SMS pads only have 2 buttons; ignore MD-only buttons to avoid accidental triggers.
            x = false;
            y = false;
            z = false;
            mode = false;
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

        MdTracerAdapter.SetPad2Mirror(_pad2MirrorEnabled);
        core.SetInputState(up, down, left, right, a, b, c, start, x, y, z, mode, padType);
        if (core is MdTracerAdapter adapter)
            adapter.SetPad2InputState(up2, down2, left2, right2, a2, b2, c2, start2, x2, y2, z2, mode2, padType);
        if (TracePadMapping)
        {
            string gp1Down;
            string gp2DownStr;
            lock (_gamepadStateLock)
            {
                gp1Down = _gamepad1ButtonsDown.Count == 0 ? "-" : string.Join(",", _gamepad1ButtonsDown);
                gp2DownStr = _gamepad2ButtonsDown.Count == 0 ? "-" : string.Join(",", _gamepad2ButtonsDown);
            }
            Console.WriteLine($"[PADMAP] gp1down={gp1Down} gp2down={gp2DownStr} | a={a} b={b} c={c} start={start} pause={pause} x={x} y={y} z={z} mode={mode}");
        }

        // StatusText uppdateras i Tick()
    }

    private bool IsGamepadButtonPressed(GamepadButton button)
    {
        if (!_sdlInputInitialized || _sdl == null || _activeGamepad1 == IntPtr.Zero)
            return false;
        lock (_gamepadStateLock)
        {
            return _gamepad1ButtonsDown.Contains(button);
        }
    }

    private bool IsGamepad2ButtonPressed(GamepadButton button)
    {
        if (!_sdlInputInitialized || _sdl == null || _activeGamepad2 == IntPtr.Zero)
            return false;
        lock (_gamepadStateLock)
        {
            return _gamepad2ButtonsDown.Contains(button);
        }
    }

    private void UpdateGamepadState()
    {
        if (!_sdlInputInitialized || _sdl == null)
            return;

        lock (_gamepadStateLock)
        {
        EnsureGamepadsConnected();
        if (_activeGamepad1 == IntPtr.Zero && _activeGamepad2 == IntPtr.Zero)
            return;

        _sdl.PumpEvents();

        if (_activeGamepad1 != IntPtr.Zero)
            UpdateGamepadStateFor(_activeGamepad1, _gamepad1ButtonsDown, _gamepad1ButtonsDownPrev, ref _lastGamepad1ButtonPressed);
        if (_activeGamepad2 != IntPtr.Zero)
            UpdateGamepadStateFor(_activeGamepad2, _gamepad2ButtonsDown, _gamepad2ButtonsDownPrev, ref _lastGamepad2ButtonPressed);
        }
    }

    private void UpdateGamepadStateFor(
        IntPtr controllerPtr,
        HashSet<GamepadButton> down,
        HashSet<GamepadButton> prev,
        ref int lastPressed)
    {
        prev.Clear();
        prev.UnionWith(down);
        down.Clear();

        foreach (var button in s_gamepadButtonsToPoll)
        {
            if (GetGamepadButtonState(controllerPtr, button))
                down.Add(button);
        }

        if (TracePadRaw && !down.SetEquals(prev))
        {
            string raw = down.Count == 0 ? "-" : string.Join(",", down);
            Console.WriteLine($"[PADRAW] down={raw}");
        }

        foreach (var button in down)
        {
            if (!prev.Contains(button))
                Interlocked.Exchange(ref lastPressed, (int)button);
        }
    }

    private void EnsureGamepadsConnected()
    {
        if (_activeGamepad1 != IntPtr.Zero)
        {
            unsafe
            {
                var controller = (Silk.NET.SDL.GameController*)_activeGamepad1;
                if (_sdl!.GameControllerGetAttached(controller) == 0)
                    CloseGamepad(ref _activeGamepad1, _gamepad1ButtonsDown, _gamepad1ButtonsDownPrev);
            }
        }

        if (_activeGamepad2 != IntPtr.Zero)
        {
            unsafe
            {
                var controller = (Silk.NET.SDL.GameController*)_activeGamepad2;
                if (_sdl!.GameControllerGetAttached(controller) == 0)
                    CloseGamepad(ref _activeGamepad2, _gamepad2ButtonsDown, _gamepad2ButtonsDownPrev);
            }
        }

        if (_activeGamepad1 == IntPtr.Zero || _activeGamepad2 == IntPtr.Zero)
        {
            long now = Stopwatch.GetTimestamp();
            if (now - _lastGamepadScanTicks < Stopwatch.Frequency * 2)
                return;

            _lastGamepadScanTicks = now;
            TryOpenGamepads();
        }
    }

    private bool GetGamepadButtonState(IntPtr controllerPtr, GamepadButton button)
    {
        if (controllerPtr == IntPtr.Zero || _sdl == null)
            return false;

        if (button == GamepadButton.LeftTrigger)
            return GetTriggerState(controllerPtr, GameControllerAxis.Triggerleft);
        if (button == GamepadButton.RightTrigger)
            return GetTriggerState(controllerPtr, GameControllerAxis.Triggerright);

        if (!sdlButtonMap.TryGetValue(button, out var sdlButton))
            return false;

        unsafe
        {
            var controller = (Silk.NET.SDL.GameController*)controllerPtr;
            return _sdl.GameControllerGetButton(controller, sdlButton) != 0;
        }
    }

    private bool GetTriggerState(IntPtr controllerPtr, GameControllerAxis axis)
    {
        unsafe
        {
            var controller = (Silk.NET.SDL.GameController*)controllerPtr;
            short value = _sdl!.GameControllerGetAxis(controller, axis);
            return value > 16000;
        }
    }

    private void TryOpenGamepads()
    {
        if (_sdl == null)
            return;

        int count = _sdl.NumJoysticks();
        for (int i = 0; i < count; i++)
        {
            if (_sdl.IsGameController(i) == 0)
                continue;

            unsafe
            {
                var controller = _sdl.GameControllerOpen(i);
                if (controller != null)
                {
                    if (_activeGamepad1 == IntPtr.Zero)
                    {
                        _activeGamepad1 = (IntPtr)controller;
                        _gamepad1ButtonsDown.Clear();
                        _gamepad1ButtonsDownPrev.Clear();
                        Console.WriteLine($"[Gamepad] Connected gamepad 1 index {i}");
                    }
                    else if (_activeGamepad2 == IntPtr.Zero)
                    {
                        _activeGamepad2 = (IntPtr)controller;
                        _gamepad2ButtonsDown.Clear();
                        _gamepad2ButtonsDownPrev.Clear();
                        Console.WriteLine($"[Gamepad] Connected gamepad 2 index {i}");
                    }
                    else
                    {
                        _sdl.GameControllerClose(controller);
                    }
                    if (_activeGamepad1 != IntPtr.Zero && _activeGamepad2 != IntPtr.Zero)
                        return;
                }
            }
        }
    }

    private void CloseGamepad(ref IntPtr controllerPtr, HashSet<GamepadButton> down, HashSet<GamepadButton> prev)
    {
        if (controllerPtr == IntPtr.Zero || _sdl == null)
            return;

        unsafe
        {
            var controller = (Silk.NET.SDL.GameController*)controllerPtr;
            _sdl.GameControllerClose(controller);
        }
        controllerPtr = IntPtr.Zero;
        down.Clear();
        prev.Clear();
    }

    private GamepadButton? TryConsumeLastGamepad1ButtonPressed()
    {
        int button = Interlocked.Exchange(ref _lastGamepad1ButtonPressed, (int)GamepadButton.None);
        if (button == (int)GamepadButton.None)
            return null;
        return (GamepadButton)button;
    }

    private GamepadButton? TryConsumeLastGamepad2ButtonPressed()
    {
        int button = Interlocked.Exchange(ref _lastGamepad2ButtonPressed, (int)GamepadButton.None);
        if (button == (int)GamepadButton.None)
            return null;
        return (GamepadButton)button;
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
        EnsureBitmapFromCore(w, h);
    }

    private void EnsureBitmapFromCore(int w, int h)
    {
        if (w <= 0 || h <= 0)
            throw new InvalidOperationException($"Core returned invalid size {w}x{h}.");

        // Only recreate bitmap when size actually changes
        if (_wb == null || _wb.PixelSize.Width != w || _wb.PixelSize.Height != h)
        {
            if (TraceUiRender)
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

    private long _lastCoreFrameId = -1;
    private int _presentTickCounter;
    private int _presentLogInterval = 60;

    private unsafe void RenderFrame(IEmulatorCore core)
    {
        var src = core.GetFrameBuffer(out var w, out var h, out var srcStride);
        if (src.IsEmpty || srcStride <= 0 || w <= 0 || h <= 0)
        {
            if (TraceUiPresent)
                Console.WriteLine($"[MainWindow] Present tick={_presentTickCounter}: EMPTY");
            _presentTickCounter++;
            return;
        }

        EnsureBitmapFromCore(w, h);
        if (_wb == null)
            return;

        // Check if this is actually a new frame
        long currentFrameId = _presentTickCounter;
        if (core is EutherDrive.Core.Savestates.ISavestateCapable sc && sc.FrameCounter.HasValue)
            currentFrameId = sc.FrameCounter.Value;
        else if (core is InterlaceTestCore itc)
            currentFrameId = itc.GetFrameId();

        bool isNewFrame = currentFrameId != _lastCoreFrameId;
        _lastCoreFrameId = currentFrameId;

        // Only log when frame changes (for debugging flicker)
        if (isNewFrame)
        {
            if (TraceUiPresent)
                Console.WriteLine($"[Present] tick={_presentTickCounter} NEW_FRAME frameId={currentFrameId}");
        }
        _presentTickCounter++;

        if (!isNewFrame && SkipDuplicateFrames)
            return;

        if (SkipUiBlitEnabled)
        {
            if (!_earlyMagentaReported && _earlyMagentaTimer.IsRunning)
            {
                _earlyMagentaReported = true;
                _earlyMagentaTimer.Stop();
                if (TraceUiPresent)
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
        if (TraceUiPresent)
            Console.WriteLine($"[MainWindow] Present WxH={w}x{h} stride={srcStride} forceOpaque={forceOpaque}");

        if (!_earlyMagentaReported && _earlyMagentaTimer.IsRunning)
        {
            _earlyMagentaReported = true;
            _earlyMagentaTimer.Stop();
            if (TraceUiPresent)
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
        double ticksPerFrame = Stopwatch.Frequency / targetFps;
        double nextTick = Stopwatch.GetTimestamp();
        while (_emuRunning)
        {
            double currentTarget = GetLiveTargetFps();
            if (Math.Abs(currentTarget - targetFps) > 0.001)
            {
                targetFps = currentTarget;
                ticksPerFrame = Stopwatch.Frequency / targetFps;
            }

            double now = Stopwatch.GetTimestamp();
            var core = _core;
            if (core == null)
                continue;
            if (_speedLockEnabled && now < nextTick)
            {
                int sleepMs = (int)((nextTick - now) * 1000 / Stopwatch.Frequency);
                if (sleepMs > 2)
                {
                    Thread.Sleep(sleepMs - 2);
                    // Spin-wait for last ~2ms for better accuracy
                    while (Stopwatch.GetTimestamp() < nextTick)
                    {
                        Thread.Sleep(0); // Yield
                    }
                }
                else
                {
                    // Spin-wait for short intervals
                    while (Stopwatch.GetTimestamp() < nextTick)
                    {
                        Thread.Sleep(0); // Yield
                    }
                }
                continue;
            }

            if (_speedLockEnabled)
            {
                if (now - nextTick > ticksPerFrame * 4)
                    nextTick = now;
                nextTick += ticksPerFrame;
            }

            try
            {
                lock (_coreAudioLock)
                {
                    ApplyInputToCore(core);
                    core.RunFrame();
                    GenerateAudioFromSystemCycles(core);
                }

                if (AudioCatchupEnabled && _audioEngine != null && !_audioPullMode)
                    CatchUpAudio(core);

                // Framebuffer analyzer for debugging
                if (core is MdTracerAdapter adapter && adapter.FbAnalyzer.Enabled)
                {
                    adapter.FbAnalyzer.AnalyzeFrame();
                    Console.Error.Flush(); // Ensure output is visible immediately
                }
            }
            catch (Exception ex)
            {
                if (TraceUiRender)
                    Console.WriteLine("[EmuLoop] RunFrame exception: " + ex);
            }

            ProducePsgForFrame();
            SubmitAudio();

            // Track actual emu FPS
            _emuFpsFrames++;
            long emuFpsNow = Stopwatch.GetTimestamp();
            if (_emuFpsLastTicks == 0)
                _emuFpsLastTicks = emuFpsNow;
            long emuFpsDelta = emuFpsNow - _emuFpsLastTicks;
            if (emuFpsDelta >= Stopwatch.Frequency)
            {
                double elapsed = emuFpsDelta / (double)Stopwatch.Frequency;
                double fps = _emuFpsFrames / elapsed;
                Volatile.Write(ref _emuActualFps, fps);
                _emuFpsFrames = 0;
                _emuFpsLastTicks = emuFpsNow;
                if (TraceSpeedLock)
                {
                    double liveTarget = GetLiveTargetFps();
                    Console.WriteLine(
                        $"[SPEEDLOCK] target={liveTarget:0.###} emu={fps:0.###} ticksPerFrame={ticksPerFrame:0.###} lock={(_speedLockEnabled ? 1 : 0)} speed={_speedScale * 100:0.#}");
                }
            }

            if (TraceSysCycles && core is MdTracerAdapter adapterCycles)
            {
                long nowTicks = Stopwatch.GetTimestamp();
                if (_sysCycleLastLogTicks == 0)
                {
                    _sysCycleLastLogTicks = nowTicks;
                    _sysCycleLastValue = adapterCycles.GetSystemCycles();
                }
                else if (nowTicks - _sysCycleLastLogTicks >= Stopwatch.Frequency)
                {
                    long current = adapterCycles.GetSystemCycles();
                    long delta = current - _sysCycleLastValue;
                    double seconds = (nowTicks - _sysCycleLastLogTicks) / (double)Stopwatch.Frequency;
                    double cyclesPerSecond = seconds > 0 ? (delta / seconds) : 0;
                    double expected = adapterCycles.GetM68kClockHz();
                    double ratio = expected > 0 ? cyclesPerSecond / expected : 0;
                    Console.WriteLine($"[SYS-CYCLES] cps={cyclesPerSecond:0} expected={expected:0} ratio={ratio:0.000}");
                    _sysCycleLastLogTicks = nowTicks;
                    _sysCycleLastValue = current;
                }
            }

            if (!_speedLockEnabled && (_emuFpsFrames % 60) == 0)
                Thread.Sleep(0);
        }
    }

    private void CatchUpAudio(IEmulatorCore core)
    {
        if (_audioEngine == null || core is not MdTracerAdapter adapter)
            return;
        if (AudioClockFrame)
            return;

        int buffered = _audioEngine.BufferedFrames;
        if (buffered >= AudioTargetBufferedFrames)
            return;

        int framesToCatch = Math.Min(AudioMaxCatchupFramesPerTick, AudioTargetBufferedFrames - buffered);
        if (framesToCatch <= 0)
            return;

        int safety = 0;
        while (framesToCatch > 0 && safety < AudioMaxCatchupFramesPerTick)
        {
            ApplyInputToCore(adapter);
            adapter.RunFrame();
            GenerateAudioFromSystemCycles(adapter);
            framesToCatch -= (int)(AudioSampleRate / GetLiveTargetFps());
            safety++;
        }
    }


    private void PrimePullAudio()
    {
        if (!_audioPullMode || _core is not MdTracerAdapter adapter)
            return;
        lock (_coreAudioLock)
        {
            if (!_audioPullReady)
                return;
            for (int i = 0; i < 3; i++)
            {
                ApplyInputToCore(adapter);
                adapter.RunFrame();
                GenerateAudioFromSystemCycles(adapter);
            }
        }
    }

    private ReadOnlySpan<short> AudioPullProducer(int frames)
    {
        if (_core is not MdTracerAdapter adapter)
            return ReadOnlySpan<short>.Empty;
        return adapter.GetAudioBufferForFrames(frames, out _, out _);
    }

    private void AppendPullAudio(ReadOnlySpan<short> audio)
    {
        if (audio.IsEmpty)
            return;

        int newSamples = _audioPullBufferedSamples + audio.Length;
        if (_audioPullBuffer.Length < newSamples)
        {
            int newSize = _audioPullBuffer.Length == 0 ? 4096 : _audioPullBuffer.Length;
            while (newSize < newSamples)
                newSize *= 2;
            var next = new short[newSize];
            if (_audioPullBufferedSamples > 0)
                Array.Copy(_audioPullBuffer, 0, next, 0, _audioPullBufferedSamples);
            _audioPullBuffer = next;
        }

        audio.CopyTo(_audioPullBuffer.AsSpan(_audioPullBufferedSamples));
        _audioPullBufferedSamples = newSamples;
    }

    private void GenerateAudioFromSystemCycles(IEmulatorCore core)
    {
        if (_audioEngine == null || core is not MdTracerAdapter adapter)
            return;
        if (AudioClockFrame)
        {
            GenerateAudioForEmuFrame(adapter, GetLiveTargetFps());
            return;
        }
        if (_audioPullMode)
            return;

        if (_audioTimedEnabled)
        {
            if (TraceUiAudio)
            {
                _audioDebugLastRatio = 0;
                _audioDebugLastDeltaCycles = 0;
            }
            GenerateAudioFromWallClock(adapter);
            return;
        }

        long currentCycles = adapter.GetSystemCycles();
        if (_audioLastSystemCycles == 0)
        {
            _audioLastSystemCycles = currentCycles;
            return;
        }

        long deltaCycles = currentCycles - _audioLastSystemCycles;
        if (deltaCycles <= 0)
            return;

        _audioLastSystemCycles = currentCycles;
        double cyclesPerSecond = adapter.GetM68kClockHz();
        if (cyclesPerSecond <= 0)
            return;
        double rateScale = 1.0;
        if (!_audioPullMode && AudioPllEnabled && _audioEngine != null && AudioTargetBufferedFrames > 0)
        {
            int buffered = _audioEngine.BufferedFrames;
            double error = (AudioTargetBufferedFrames - buffered) / (double)AudioTargetBufferedFrames;
            if (error > 1.0) error = 1.0;
            else if (error < -1.0) error = -1.0;
            rateScale = 1.0 + (error * AudioPllMax);
        }
        _audioFrameAccumulator += (deltaCycles * SystemCyclesScale) * AudioCyclesScale * rateScale * (AudioSampleRate / cyclesPerSecond);
        if (TraceUiAudio)
        {
            long now = Stopwatch.GetTimestamp();
            if (now - _audioDebugCycleLastTicks >= Stopwatch.Frequency)
            {
                _audioDebugCycleLastTicks = now;
                double expectedPerFrame = cyclesPerSecond / adapter.GetTargetFps();
                _audioDebugLastDeltaCycles = deltaCycles;
                _audioDebugLastRatio = expectedPerFrame > 0 ? (deltaCycles / expectedPerFrame) : 0;
            }
        }

        if (TraceAudioCycles && Stopwatch.GetTimestamp() - _audioCycleLogLastTicks > Stopwatch.Frequency)
        {
            _audioCycleLogLastTicks = Stopwatch.GetTimestamp();
            double expectedPerFrame = cyclesPerSecond / adapter.GetTargetFps();
            Console.WriteLine($"[AUDIO-CYCLES] deltaCycles={deltaCycles} expectedPerFrame={expectedPerFrame:F1} ratio={(deltaCycles / expectedPerFrame):F3}");
        }
        int frames = (int)_audioFrameAccumulator;
        if (frames <= 0)
            return;

        _audioFrameAccumulator -= frames;
        if (frames > AudioMaxFramesPerTick)
            frames = AudioMaxFramesPerTick;

        while (frames > 0)
        {
            int chunk = frames < AudioBufferChunkFrames ? frames : AudioBufferChunkFrames;
            var audio = adapter.GetAudioBufferForFrames(chunk, out int rate, out int channels);
            if (!audio.IsEmpty && rate == AudioSampleRate && channels == AudioChannels)
            {
                // Always submit to the ring buffer so audio is produced even in pull mode.
                _audioEngine.Submit(audio);
                frames -= chunk;
            }
            else
            {
                break;
            }
        }

        if (TraceAudioQueue && _audioEngine != null && Stopwatch.GetTimestamp() - _audioQueueLogLastTicks > Stopwatch.Frequency)
        {
            _audioQueueLogLastTicks = Stopwatch.GetTimestamp();
            Console.WriteLine(
                $"[AUDIO-QUEUE] buffered={_audioEngine.BufferedFrames} target={AudioTargetBufferedFrames} " +
                $"rateScale={rateScale:F4} acc={_audioFrameAccumulator:F2}");
        }
    }

    private void GenerateAudioFromWallClock(MdTracerAdapter adapter)
    {
        if (_audioEngine == null)
            return;
        if (_audioPullMode)
            return;

        long now = Stopwatch.GetTimestamp();
        if (_audioLastTicks == 0)
        {
            _audioLastTicks = now;
            return;
        }

        double elapsed = (now - _audioLastTicks) / (double)Stopwatch.Frequency;
        _audioLastTicks = now;
        if (elapsed <= 0)
            return;

        // Clamp long gaps to avoid massive burst generation.
        const double maxElapsed = 0.25;
        if (elapsed > maxElapsed)
            elapsed = maxElapsed;

        int buffered = _audioEngine.BufferedFrames;
        if (buffered >= AudioTargetBufferedFrames)
            return;

        _audioFrameAccumulator += elapsed * AudioSampleRate;
        int frames = (int)_audioFrameAccumulator;
        if (frames <= 0)
            return;

        int needFrames = AudioTargetBufferedFrames - buffered;
        if (needFrames <= 0)
            return;
        if (frames > needFrames)
            frames = needFrames;

        _audioFrameAccumulator -= frames;
        if (frames > AudioMaxFramesPerTick)
            frames = AudioMaxFramesPerTick;
        if (frames > AudioTimedMaxFrames)
            frames = AudioTimedMaxFrames;

        while (frames > 0)
        {
            int chunk = frames < AudioBufferChunkFrames ? frames : AudioBufferChunkFrames;
            var audio = adapter.GetAudioBufferForFrames(chunk, out int rate, out int channels);
            if (!audio.IsEmpty && rate == AudioSampleRate && channels == AudioChannels)
            {
                _audioEngine.Submit(audio);
                frames -= chunk;
            }
            else
            {
                break;
            }
        }

        if (TraceAudioQueue && _audioEngine != null && Stopwatch.GetTimestamp() - _audioQueueLogLastTicks > Stopwatch.Frequency)
        {
            _audioQueueLogLastTicks = Stopwatch.GetTimestamp();
            Console.WriteLine(
                $"[AUDIO-QUEUE] buffered={_audioEngine.BufferedFrames} target={AudioTargetBufferedFrames} " +
                $"timed=1 acc={_audioFrameAccumulator:F2}");
        }
    }

    private void GenerateAudioForEmuFrame(MdTracerAdapter adapter, double targetFps)
    {
        if (_audioEngine == null || targetFps <= 0)
            return;

        double rateScale = 1.0;
        if (AudioPllEnabled && _audioEngine != null && AudioTargetBufferedFrames > 0)
        {
            int buffered = _audioEngine.BufferedFrames;
            double error = (AudioTargetBufferedFrames - buffered) / (double)AudioTargetBufferedFrames;
            if (error > 1.0) error = 1.0;
            else if (error < -1.0) error = -1.0;
            const double deadZone = 0.05;
            if (Math.Abs(error) >= deadZone)
                rateScale = 1.0 + (error * AudioPllMax);
        }

        double framesPerEmuFrame = (AudioSampleRate / targetFps) * rateScale;
        _audioDrivenAccumulator += framesPerEmuFrame;
        int frames = (int)_audioDrivenAccumulator;
        if (frames <= 0)
            return;

        _audioDrivenAccumulator -= frames;
        if (frames > AudioMaxFramesPerTick)
            frames = AudioMaxFramesPerTick;

        while (frames > 0)
        {
            int chunk = frames < AudioBufferChunkFrames ? frames : AudioBufferChunkFrames;
            var audio = adapter.GetAudioBufferForFrames(chunk, out int rate, out int channels);
            if (!audio.IsEmpty && rate == AudioSampleRate && channels == AudioChannels)
            {
                _audioEngine.Submit(audio);
                frames -= chunk;
            }
            else
            {
                break;
            }
        }
    }

    private static double GetAudioCyclesScale()
    {
        string? raw = Environment.GetEnvironmentVariable("EUTHERDRIVE_AUDIO_CYCLES_SCALE");
        if (!string.IsNullOrWhiteSpace(raw)
            && double.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double value)
            && value > 0)
        {
            return value;
        }
        return 1.0;
    }

    private static double GetSystemCyclesScale()
    {
        string? raw = Environment.GetEnvironmentVariable("EUTHERDRIVE_SYSTEM_CYCLES_SCALE");
        if (!string.IsNullOrWhiteSpace(raw)
            && double.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double value)
            && value > 0)
        {
            return value;
        }
        // Default: no scaling unless explicitly overridden.
        return 1.0;
    }

    private static int GetAudioTargetBufferedFrames()
    {
        string? raw = Environment.GetEnvironmentVariable("EUTHERDRIVE_AUDIO_TARGET_MS");
        if (!string.IsNullOrWhiteSpace(raw)
            && int.TryParse(raw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int ms)
            && ms > 0)
        {
            return (int)(AudioSampleRate * (ms / 1000.0));
        }
        // Default ~200ms target buffer for steadier audio under load.
        return (int)(AudioSampleRate * 0.20);
    }

    private static int GetAudioMaxCatchupFramesPerTick()
    {
        string? raw = Environment.GetEnvironmentVariable("EUTHERDRIVE_AUDIO_CATCHUP_FRAMES");
        if (!string.IsNullOrWhiteSpace(raw)
            && int.TryParse(raw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int value)
            && value > 0)
        {
            return value;
        }
        // Default cap to avoid runaway catch-up.
        return 32;
    }

    private static double GetAudioPllMax()
    {
        string? raw = Environment.GetEnvironmentVariable("EUTHERDRIVE_AUDIO_PLL_MAX");
        if (!string.IsNullOrWhiteSpace(raw)
            && double.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double value)
            && value > 0)
        {
            return value;
        }
        // Default ±0.3% rate correction.
        return 0.003;
    }

    private static int GetAudioEngineBufferFrames()
    {
        string? raw = Environment.GetEnvironmentVariable("EUTHERDRIVE_AUDIO_BUFFER_FRAMES");
        if (!string.IsNullOrWhiteSpace(raw)
            && int.TryParse(raw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int value)
            && value > 0)
        {
            return value;
        }
        return 16384;
    }

    private static int GetAudioEngineBatchFrames()
    {
        string? raw = Environment.GetEnvironmentVariable("EUTHERDRIVE_AUDIO_BATCH_FRAMES");
        if (!string.IsNullOrWhiteSpace(raw)
            && int.TryParse(raw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int value)
            && value > 0)
        {
            return value;
        }
        return 256;
    }

    private static int GetAudioPullMaxFrames()
    {
        string? raw = Environment.GetEnvironmentVariable("EUTHERDRIVE_AUDIO_PULL_MAX_FRAMES");
        if (!string.IsNullOrWhiteSpace(raw)
            && int.TryParse(raw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int value)
            && value > 0)
        {
            return value;
        }
        return 2048;
    }

    private static int GetAudioTimedMaxFrames()
    {
        string? raw = Environment.GetEnvironmentVariable("EUTHERDRIVE_AUDIO_TIMED_MAX_FRAMES");
        if (!string.IsNullOrWhiteSpace(raw)
            && int.TryParse(raw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int value)
            && value > 0)
        {
            return value;
        }
        return 1024;
    }

    // reserved

    private double GetLiveTargetFps()
    {
        if (_core is MdTracerAdapter adapter)
            return adapter.GetTargetFps() * _speedScale;
        return Volatile.Read(ref _emuTargetFps) * _speedScale;
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
        if (!IsEnvEnabled("EUTHERDRIVE_TRACE_ROM_START") && !IsEnvEnabled("EUTHERDRIVE_TRACE_ALL"))
            return;

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

    private static bool IsEnvEnabled(string key)
    {
        var value = Environment.GetEnvironmentVariable(key);
        if (string.IsNullOrWhiteSpace(value))
            return false;

        value = value.Trim();
        return value == "1"
            || value.Equals("true", StringComparison.OrdinalIgnoreCase)
            || value.Equals("yes", StringComparison.OrdinalIgnoreCase)
            || value.Equals("on", StringComparison.OrdinalIgnoreCase);
    }
}
