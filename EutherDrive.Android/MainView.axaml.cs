using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
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
using EutherDrive.Audio;
using EutherDrive.Core;
using EutherDrive.Core.Savestates;
using EutherDrive.Rendering;
using ProjectPSX.IO;
using Tomlyn;

namespace EutherDrive.Android;

public partial class MainView : UserControl
{
    private const int InputLatchFrames = 6;
    private const int AndroidAudioBufferFrames = 16384;
    private const int AndroidAudioBatchFrames = 256;
    private const int AndroidAudioPullMaxFrames = 2048;
    private const double DefaultTargetFrameRate = 60.0;
    private const double JoystickDeadZoneRatio = 0.20;
    private const double JoystickAxisEngageRatio = 0.28;
    private const double JoystickDiagonalRatio = 0.56;
    private const double JoystickPadding = 10.0;
    private const double LandscapeIntegerSnapThreshold = 0.08;
    private const int DefaultScanlineStrengthPercent = 0;
    private const string SettingsFileName = "android-settings.toml";
    private const string LegacyJsonSettingsFileName = "android-settings.json";

    private readonly MainViewModel _viewModel = new();
    private readonly object _inputSync = new();
    private readonly object _frameSync = new();
    private readonly object _snesAudioLock = new();
    private readonly HashSet<string> _pressedDirections = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _pressedActions = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _nativePressedDirections = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _nativePressedActions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<IPointer, HashSet<string>> _dpadPointerDirections = new();
    private readonly Dictionary<IPointer, double> _dpadPointerIntensity = new();
    private readonly Dictionary<IPointer, string> _directionPointers = new();
    private readonly Dictionary<IPointer, string> _actionPointers = new();
    private readonly Dictionary<string, int> _directionPressCounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _actionPressCounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _joystickDirectionPulseFrames = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<IPointer> _facePointers = new();
    private readonly HashSet<string> _selectedVirtualPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _directionLatchFrames = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _actionLatchFrames = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _virtualSystemPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _appDataDir;
    private readonly string _settingsPath;
    private readonly string _legacyJsonSettingsPath;
    private readonly SavestateService _savestateService;
    private readonly Stopwatch _perfStopwatch = Stopwatch.StartNew();
    private readonly PsxInterlaceReconstructor _psxInterlaceReconstructor = new();
    private IEmulatorCore? _core;
    private Thread? _emulationThread;
    private volatile bool _emulationThreadRunning;
    private AudioEngine? _audioEngine;
    private bool _audioPullMode;
    private short[] _snesAudioRing = Array.Empty<short>();
    private short[] _snesAudioTemp = Array.Empty<short>();
    private int _snesAudioRead;
    private int _snesAudioWrite;
    private int _snesAudioCount;
    private int _snesAudioChannels = 2;
    private IGameRenderSurface? _renderSurface;
    private byte[] _captureFrameBuffer = Array.Empty<byte>();
    private byte[] _latestFrameBuffer = Array.Empty<byte>();
    private byte[] _presentFrameBuffer = Array.Empty<byte>();
    private string? _selectedRomPath;
    private string? _selectedRomDisplayName;
    private volatile string _latestPerfSummary = "Perf idle";
    private volatile string _latestPerfHeadline = "FPS --  MAX --";
    private int _presentedFrames;
    private long _latestFrameSerial;
    private long _presentedFrameSerial;
    private long _perfWindowStartTicks;
    private int _perfWindowFrames;
    private double _perfAccumulatedEmuMs;
    private double _perfAccumulatedAudioMs;
    private double _perfAccumulatedBlitMs;
    private long _perfAccumulatedPresentTicks;
    private long _perfPresentedWindowFrames;
    private long _perfDroppedWindowFrames;
    private long _emulatedFrames;
    private int _lastFrameWidth;
    private int _lastFrameHeight;
    private int _lastFrameStride;
    private bool _lastFrameIsPsx;
    private bool _lastPsxInterlaceBlend;
    private int _lastPsxInterlaceFieldParity = -1;
    private PsxAdapter.PresentationFrameInfo _lastPsxFrameInfo;
    private string _lastRenderSurfaceDebugSummary = string.Empty;
    private string _lastRenderSurfaceFallbackReason = string.Empty;
    private double _lastPresentationWidth;
    private double _lastPresentationHeight;
    private double _appliedPresentationWidth = double.NaN;
    private double _appliedPresentationHeight = double.NaN;
    private bool? _appliedPresentationLandscape;
    private int _bootRequestSerial;
    private bool _bootInProgress;
    private int _presentLatestFrameQueued;
    private int _androidPsxTransientBlankBurst;
    private bool _androidPsxHasStableVisibleFrame;

    public MainView()
    {
        InitializeComponent();
        _appDataDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _settingsPath = Path.Combine(_appDataDir, SettingsFileName);
        _legacyJsonSettingsPath = Path.Combine(_appDataDir, LegacyJsonSettingsFileName);
        _savestateService = new SavestateService(Path.Combine(_appDataDir, "savestates"));
        DataContext = _viewModel;
        LoadSettings();
        ApplySettings();
        UpdateAndroidRenderBackendStatus();
        _viewModel.SettingsHint = "Small BIOS/chip files are imported into app storage. Large disc images are intentionally not cached here.";
        SettingsAboutZuulView.SetActive(false);
        SizeChanged += OnViewSizeChanged;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void OnViewSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        Size size = e.NewSize;
        _viewModel.IsLandscapeMode = size.Width > size.Height && size.Height > 0;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        bool landscapeChanged = string.Equals(e.PropertyName, nameof(MainViewModel.IsLandscapeMode), StringComparison.Ordinal);
        bool debugChanged = string.Equals(e.PropertyName, nameof(MainViewModel.DebugVisible), StringComparison.Ordinal)
            || string.Equals(e.PropertyName, nameof(MainViewModel.DebugPageIndex), StringComparison.Ordinal);
        bool shellVisibilityChanged = string.Equals(e.PropertyName, nameof(MainViewModel.MainShellVisible), StringComparison.Ordinal)
            || string.Equals(e.PropertyName, nameof(MainViewModel.SettingsVisible), StringComparison.Ordinal);
        if (!landscapeChanged && !debugChanged && !shellVisibilityChanged)
            return;

        UpdateNativeRenderSurfaceVisibility();
        AttachRenderSurfaceToActiveHost();
        UpdateLandscapeNativeOverlayVisibility();
        if (_lastFrameWidth > 0 && _lastFrameHeight > 0)
            ApplyPresentationSizeForCore(_core, _lastFrameWidth, _lastFrameHeight);
        InvalidateScreenImages();
    }

    private async void OnPickRom(object? sender, RoutedEventArgs e)
    {
        CancelPendingBoot();
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
            string importedPath = await ImportRomAsync(storageProvider, files[0], isSystemFile: false);
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

    private async void OnStartClicked(object? sender, RoutedEventArgs e)
    {
        if (!_viewModel.HasRomLoaded)
        {
            _viewModel.FooterStatus = "Pick a ROM before starting.";
            return;
        }

        if (_bootInProgress)
        {
            _viewModel.FooterStatus = "Boot already in progress.";
            return;
        }

        try
        {
            await StartCoreAsync();
        }
        catch (Exception ex)
        {
            _viewModel.IsRunning = false;
            _viewModel.StatusPill = _viewModel.HasRomLoaded ? "ROM loaded" : "Idle";
            string details = FormatExceptionForUi(ex);
            _viewModel.FooterStatus = $"Start failed: {details}";
            _viewModel.ScreenOverlayVisible = true;
            _viewModel.ScreenTitle = "Boot failed";
            _viewModel.ScreenDescription = details;
        }
    }

    private void OnStopClicked(object? sender, RoutedEventArgs e)
    {
        CancelPendingBoot();
        StopSession(
            clearSelection: false,
            footerStatus: _viewModel.HasRomLoaded
                ? "Session paused. ROM remains selected."
                : "Idle.");
    }

    private void OnMenuTapped(object? sender, RoutedEventArgs e)
    {
        _viewModel.DebugVisible = false;
        _viewModel.SettingsVisible = true;
        SettingsAboutZuulView.SetActive(true);
    }

    private void OnCloseSettings(object? sender, RoutedEventArgs e)
    {
        _viewModel.SettingsVisible = false;
        SettingsAboutZuulView.SetActive(false);
    }

    private void OnOpenDebug(object? sender, RoutedEventArgs e)
    {
        _viewModel.SettingsVisible = false;
        _viewModel.DebugPageIndex = 1;
        _viewModel.DebugVisible = true;
        SettingsAboutZuulView.SetActive(false);
    }

    private void OnShowDebugGame(object? sender, RoutedEventArgs e)
    {
        _viewModel.DebugPageIndex = 0;
        _viewModel.DebugVisible = true;
    }

    private void OnShowDebugTrace(object? sender, RoutedEventArgs e)
    {
        _viewModel.DebugPageIndex = 1;
        _viewModel.DebugVisible = true;
    }

    private void OnCloseDebug(object? sender, RoutedEventArgs e)
    {
        _viewModel.DebugVisible = false;
        _viewModel.DebugPageIndex = 0;
    }

    private void OnToggleScreenFocus(object? sender, PointerPressedEventArgs e)
    {
        ToggleScreenFocusMode();
    }

    private void OnNativeScreenTapped()
    {
        Dispatcher.UIThread.Post(ToggleScreenFocusMode, DispatcherPriority.Input);
    }

    private void ToggleScreenFocusMode()
    {
        if (_viewModel.SettingsVisible || _viewModel.DebugVisible)
            return;

        _viewModel.IsFocusMode = !_viewModel.IsFocusMode;
    }

    private void OnSaveSlot1(object? sender, RoutedEventArgs e) => RunSavestateAction(slotIndex: 1, isLoad: false);

    private void OnLoadSlot1(object? sender, RoutedEventArgs e) => RunSavestateAction(slotIndex: 1, isLoad: true);

    private void OnSaveSlot2(object? sender, RoutedEventArgs e) => RunSavestateAction(slotIndex: 2, isLoad: false);

    private void OnLoadSlot2(object? sender, RoutedEventArgs e) => RunSavestateAction(slotIndex: 2, isLoad: true);

    private void OnOverlayPress(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Border border && border.Tag is string tag)
        {
            lock (_inputSync)
            {
                border.Classes.Add("padPressed");
                RegisterPointerPressLocked(_directionPointers, _directionPressCounts, _pressedDirections, e.Pointer, tag);
                _directionLatchFrames[tag] = InputLatchFrames;
            }
            e.Pointer.Capture(border);
            e.Handled = true;
            _viewModel.SetLastPressed(tag);
            UpdateOverlaySummary();
        }
    }

    private void OnDPadZonePress(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control control || control.Tag is not string tag)
        {
            return;
        }

        lock (_inputSync)
        {
            RegisterDPadDirectionsLocked(e.Pointer, ParseDPadDirections(tag));
        }

        e.Pointer.Capture(control);
        e.Handled = true;
        UpdateOverlaySummary();
    }

    private void OnDPadZoneRelease(object? sender, PointerReleasedEventArgs e)
    {
        if (sender is not Control control)
        {
            return;
        }

        lock (_inputSync)
        {
            ReleaseDPadPointerLocked(e.Pointer);
            UpdateDPadVisualsLocked();
        }

        e.Pointer.Capture(null);
        e.Handled = true;
        UpdateOverlaySummary();
    }

    private void OnDPadZoneCaptureLost(object? sender, RoutedEventArgs e)
    {
        lock (_inputSync)
        {
            foreach (IPointer pointer in _dpadPointerDirections.Keys.ToArray())
            {
                ReleaseDPadPointerLocked(pointer);
            }

            UpdateDPadVisualsLocked();
        }

        UpdateOverlaySummary();
    }

    private void OnDPadPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control control)
        {
            return;
        }

        lock (_inputSync)
        {
            UpdateDPadPointerLocked(control, e.Pointer, e.GetPosition(control));
        }

        e.Pointer.Capture(control);
        e.Handled = true;
        UpdateOverlaySummary();
    }

    private void OnDPadPointerMoved(object? sender, PointerEventArgs e)
    {
        if (sender is not Control control || e.Pointer.Captured != control)
        {
            return;
        }

        lock (_inputSync)
        {
            UpdateDPadPointerLocked(control, e.Pointer, e.GetPosition(control));
        }

        e.Handled = true;
        UpdateOverlaySummary();
    }

    private void OnDPadPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (sender is not Control control)
        {
            return;
        }

        lock (_inputSync)
        {
            ReleaseDPadPointerLocked(e.Pointer);
            ResetDPadThumbLocked(control);
        }

        e.Pointer.Capture(null);
        e.Handled = true;
        UpdateOverlaySummary();
    }

    private void OnDPadPointerCaptureLost(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control)
        {
            return;
        }

        lock (_inputSync)
        {
            foreach (IPointer pointer in _dpadPointerDirections.Keys.ToArray())
            {
                ReleaseDPadPointerLocked(pointer);
            }

            ResetDPadThumbLocked();
        }

        UpdateOverlaySummary();
    }

    private void OnOverlayRelease(object? sender, PointerReleasedEventArgs e)
    {
        if (sender is Border border && border.Tag is string tag)
        {
            lock (_inputSync)
            {
                border.Classes.Remove("padPressed");
                ReleasePointerLocked(_directionPointers, _directionPressCounts, _pressedDirections, e.Pointer, tag);
                _directionLatchFrames[tag] = InputLatchFrames;
            }
            e.Pointer.Capture(null);
            e.Handled = true;
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
                ReleaseAllPointersForTagLocked(_directionPointers, _directionPressCounts, _pressedDirections, tag);
                _directionLatchFrames[tag] = InputLatchFrames;
            }
            UpdateOverlaySummary();
        }
    }

    private void OnOverlayButtonPress(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control control && control.Tag is string tag)
        {
            lock (_inputSync)
            {
                RegisterPointerPressLocked(_actionPointers, _actionPressCounts, _pressedActions, e.Pointer, tag);
                _actionLatchFrames[tag] = InputLatchFrames;
                SetOverlayButtonVisualLocked(control, pressed: true);
                UpdateFaceVisualsLocked();
            }
            e.Pointer.Capture(control);
            e.Handled = true;
            _viewModel.SetLastPressed(tag);
            UpdateOverlaySummary();
        }
    }

    private void OnOverlayButtonRelease(object? sender, PointerReleasedEventArgs e)
    {
        if (sender is Control control && control.Tag is string tag)
        {
            lock (_inputSync)
            {
                ReleasePointerLocked(_actionPointers, _actionPressCounts, _pressedActions, e.Pointer, tag);
                _actionLatchFrames[tag] = InputLatchFrames;
                SetOverlayButtonVisualLocked(control, pressed: false);
                UpdateFaceVisualsLocked();
            }
            e.Pointer.Capture(null);
            e.Handled = true;
            UpdateOverlaySummary();
        }
    }

    private void OnOverlayButtonCaptureLost(object? sender, RoutedEventArgs e)
    {
        if (sender is Control control && control.Tag is string tag)
        {
            lock (_inputSync)
            {
                ReleaseAllPointersForTagLocked(_actionPointers, _actionPressCounts, _pressedActions, tag);
                _actionLatchFrames[tag] = InputLatchFrames;
                SetOverlayButtonVisualLocked(control, pressed: false);
                UpdateFaceVisualsLocked();
            }
            UpdateOverlaySummary();
        }
    }

    private static void SetOverlayButtonVisualLocked(Control control, bool pressed)
    {
        if (pressed)
        {
            control.Classes.Add("padPressed");
        }
        else
        {
            control.Classes.Remove("padPressed");
        }
    }

    private void OnFacePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control control)
        {
            return;
        }

        lock (_inputSync)
        {
            _facePointers.Add(e.Pointer);
            UpdateFacePointerLocked(control, e.Pointer, e.GetPosition(control));
        }

        e.Pointer.Capture(control);
        e.Handled = true;
        UpdateOverlaySummary();
    }

    private void OnFacePointerMoved(object? sender, PointerEventArgs e)
    {
        if (sender is not Control control || e.Pointer.Captured != control)
        {
            return;
        }

        lock (_inputSync)
        {
            UpdateFacePointerLocked(control, e.Pointer, e.GetPosition(control));
        }

        e.Handled = true;
        UpdateOverlaySummary();
    }

    private void OnFacePointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (sender is not Control control)
        {
            return;
        }

        lock (_inputSync)
        {
            _facePointers.Remove(e.Pointer);
            ReleaseFacePointerLocked(e.Pointer);
            HideFaceGlowLocked(control);
        }

        e.Pointer.Capture(null);
        e.Handled = true;
        UpdateOverlaySummary();
    }

    private void OnFacePointerCaptureLost(object? sender, RoutedEventArgs e)
    {
        lock (_inputSync)
        {
            foreach (IPointer pointer in _facePointers.ToArray())
            {
                ReleaseFacePointerLocked(pointer);
            }

            _facePointers.Clear();
            HideFaceGlowLocked();
        }

        UpdateOverlaySummary();
    }

    private static void RegisterPointerPressLocked(
        Dictionary<IPointer, string> pointerMap,
        Dictionary<string, int> pressCounts,
        HashSet<string> pressedTags,
        IPointer pointer,
        string tag)
    {
        if (pointerMap.TryGetValue(pointer, out string? existingTag) && existingTag is not null)
        {
            if (string.Equals(existingTag, tag, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            DecrementPressCountLocked(pressCounts, pressedTags, existingTag);
        }

        pointerMap[pointer] = tag;
        if (pressCounts.TryGetValue(tag, out int count))
        {
            pressCounts[tag] = count + 1;
        }
        else
        {
            pressCounts[tag] = 1;
        }

        pressedTags.Add(tag);
    }

    private void RegisterDPadDirectionsLocked(IPointer pointer, IEnumerable<string> directions)
    {
        ReleaseDPadPointerLocked(pointer);

        var directionSet = new HashSet<string>(directions, StringComparer.OrdinalIgnoreCase);
        if (directionSet.Count == 0)
        {
            UpdateDPadVisualsLocked();
            return;
        }

        _dpadPointerDirections[pointer] = directionSet;
        foreach (string direction in directionSet)
        {
            if (_directionPressCounts.TryGetValue(direction, out int count))
                _directionPressCounts[direction] = count + 1;
            else
                _directionPressCounts[direction] = 1;

            _pressedDirections.Add(direction);
            _directionLatchFrames[direction] = InputLatchFrames;
        }

        _viewModel.SetLastPressed(string.Join(" ", directionSet.OrderBy(static value => value)));
        UpdateDPadVisualsLocked();
    }

    private void UpdateFacePointerLocked(Control control, IPointer pointer, Point point)
    {
        string? tag = DetermineFaceAction(control, point);
        if (string.IsNullOrEmpty(tag))
        {
            ReleaseFacePointerLocked(pointer);
            HideFaceGlowLocked(control);
            return;
        }

        UpdateFaceGlowLocked(control, point);
        RegisterPointerPressLocked(_actionPointers, _actionPressCounts, _pressedActions, pointer, tag);
        _actionLatchFrames[tag] = InputLatchFrames;
        _viewModel.SetLastPressed(tag);
        UpdateFaceVisualsLocked();
    }

    private void ReleaseFacePointerLocked(IPointer pointer)
    {
        if (_actionPointers.TryGetValue(pointer, out string? currentTag)
            && (currentTag is "A" or "B" or "X" or "Y"))
        {
            ReleasePointerLocked(_actionPointers, _actionPressCounts, _pressedActions, pointer, currentTag);
            _actionLatchFrames[currentTag] = InputLatchFrames;
        }

        UpdateFaceVisualsLocked();
    }

    private static string? DetermineFaceAction(Control control, Point point)
    {
        double width = control.Bounds.Width;
        double height = control.Bounds.Height;
        if (width <= 0 || height <= 0)
        {
            return null;
        }

        double centerX = width * 0.5;
        double centerY = height * 0.5;
        double dx = point.X - centerX;
        double dy = point.Y - centerY;
        double deadZone = Math.Min(width, height) * 0.09;

        if (Math.Abs(dx) < deadZone && Math.Abs(dy) < deadZone)
        {
            return null;
        }

        if (Math.Abs(dx) >= Math.Abs(dy))
        {
            return dx >= 0 ? "A" : "Y";
        }

        return dy >= 0 ? "B" : "X";
    }

    private static IEnumerable<string> ParseDPadDirections(string tag)
    {
        return tag
            .Split(new[] { ' ', '+', '/' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static part => part is "Up" or "Down" or "Left" or "Right");
    }

    private void UpdateDPadPointerLocked(Control control, IPointer pointer, Point point)
    {
        ReleaseDPadPointerLocked(pointer);
        var directions = ResolveJoystickDirections(control, point, out double thumbOffsetX, out double thumbOffsetY, out double normalizedDistance);
        _dpadPointerIntensity[pointer] = normalizedDistance;
        UpdateDPadThumbLocked(control, thumbOffsetX, thumbOffsetY, active: true);

        if (directions.Count == 0)
        {
            UpdateDPadVisualsLocked();
            return;
        }

        _dpadPointerDirections[pointer] = directions;
        foreach (string direction in directions)
        {
            if (_directionPressCounts.TryGetValue(direction, out int count))
                _directionPressCounts[direction] = count + 1;
            else
                _directionPressCounts[direction] = 1;

            _pressedDirections.Add(direction);
            _directionLatchFrames[direction] = InputLatchFrames;
        }

        _viewModel.SetLastPressed(string.Join(" ", directions.OrderBy(static value => value)));
        UpdateDPadVisualsLocked();
    }

    private void ReleaseDPadPointerLocked(IPointer pointer)
    {
        if (!_dpadPointerDirections.TryGetValue(pointer, out HashSet<string>? directions))
        {
            _dpadPointerIntensity.Remove(pointer);
            return;
        }

        _dpadPointerDirections.Remove(pointer);
        _dpadPointerIntensity.Remove(pointer);
        foreach (string direction in directions)
        {
            DecrementPressCountLocked(_directionPressCounts, _pressedDirections, direction);
            _directionLatchFrames[direction] = InputLatchFrames;
        }

        UpdateDPadVisualsLocked();
    }

    private static void ReleasePointerLocked(
        Dictionary<IPointer, string> pointerMap,
        Dictionary<string, int> pressCounts,
        HashSet<string> pressedTags,
        IPointer pointer,
        string fallbackTag)
    {
        if (pointerMap.TryGetValue(pointer, out string? actualTag) && actualTag != null)
        {
            pointerMap.Remove(pointer);
            DecrementPressCountLocked(pressCounts, pressedTags, actualTag);
            return;
        }

        DecrementPressCountLocked(pressCounts, pressedTags, fallbackTag);
    }

    private static void ReleaseAllPointersForTagLocked(
        Dictionary<IPointer, string> pointerMap,
        Dictionary<string, int> pressCounts,
        HashSet<string> pressedTags,
        string tag)
    {
        IPointer[] pointers = pointerMap
            .Where(kvp => string.Equals(kvp.Value, tag, StringComparison.OrdinalIgnoreCase))
            .Select(kvp => kvp.Key)
            .ToArray();

        foreach (IPointer pointer in pointers)
        {
            pointerMap.Remove(pointer);
            DecrementPressCountLocked(pressCounts, pressedTags, tag);
        }
    }

    private static void DecrementPressCountLocked(
        Dictionary<string, int> pressCounts,
        HashSet<string> pressedTags,
        string tag)
    {
        if (!pressCounts.TryGetValue(tag, out int count) || count <= 1)
        {
            pressCounts.Remove(tag);
            pressedTags.Remove(tag);
            return;
        }

        pressCounts[tag] = count - 1;
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

    private void UpdateDPadVisualsLocked()
    {
        bool up = _pressedDirections.Contains("Up") || _nativePressedDirections.Contains("Up");
        bool down = _pressedDirections.Contains("Down") || _nativePressedDirections.Contains("Down");
        bool left = _pressedDirections.Contains("Left") || _nativePressedDirections.Contains("Left");
        bool right = _pressedDirections.Contains("Right") || _nativePressedDirections.Contains("Right");

        SetPadVisualState(PortraitPadUp, up);
        SetPadVisualState(PortraitPadDown, down);
        SetPadVisualState(PortraitPadLeft, left);
        SetPadVisualState(PortraitPadRight, right);
        SetPadVisualState(LandscapePadUp, up);
        SetPadVisualState(LandscapePadDown, down);
        SetPadVisualState(LandscapePadLeft, left);
        SetPadVisualState(LandscapePadRight, right);
    }

    private void UpdateFaceVisualsLocked()
    {
        bool a = _pressedActions.Contains("A") || _nativePressedActions.Contains("A");
        bool b = _pressedActions.Contains("B") || _nativePressedActions.Contains("B");
        bool x = _pressedActions.Contains("X") || _nativePressedActions.Contains("X");
        bool y = _pressedActions.Contains("Y") || _nativePressedActions.Contains("Y");

        SetFaceVisualState(PortraitFaceA, a);
        SetFaceVisualState(PortraitFaceB, b);
        SetFaceVisualState(PortraitFaceX, x);
        SetFaceVisualState(PortraitFaceY, y);
        SetFaceVisualState(LandscapeFaceA, a);
        SetFaceVisualState(LandscapeFaceB, b);
        SetFaceVisualState(LandscapeFaceX, x);
        SetFaceVisualState(LandscapeFaceY, y);
    }

    private HashSet<string> ResolveJoystickDirections(Control control, Point point, out double thumbOffsetX, out double thumbOffsetY, out double normalizedDistance)
    {
        thumbOffsetX = 0;
        thumbOffsetY = 0;
        normalizedDistance = 0;

        double width = control.Bounds.Width;
        double height = control.Bounds.Height;
        if (width <= 0 || height <= 0)
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        double centerX = width * 0.5;
        double centerY = height * 0.5;
        double dx = point.X - centerX;
        double dy = point.Y - centerY;
        double distance = Math.Sqrt((dx * dx) + (dy * dy));
        double travelRadius = GetJoystickTravelRadius(control);
        if (travelRadius <= 0)
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        if (distance > travelRadius)
        {
            double scale = travelRadius / distance;
            dx *= scale;
            dy *= scale;
            distance = travelRadius;
        }

        thumbOffsetX = dx;
        thumbOffsetY = dy;
        normalizedDistance = distance / travelRadius;

        if (normalizedDistance < JoystickDeadZoneRatio)
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        return DeterminePrecisionDirections(dx / travelRadius, dy / travelRadius);
    }

    private static HashSet<string> DeterminePrecisionDirections(double normalizedX, double normalizedY)
    {
        double absX = Math.Abs(normalizedX);
        double absY = Math.Abs(normalizedY);
        double major = Math.Max(absX, absY);
        double minor = Math.Min(absX, absY);

        var directions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (major < JoystickAxisEngageRatio)
        {
            return directions;
        }

        bool horizontalDominant = absX >= absY;
        if (horizontalDominant)
        {
            directions.Add(normalizedX >= 0 ? "Right" : "Left");
        }
        else
        {
            directions.Add(normalizedY >= 0 ? "Down" : "Up");
        }

        bool diagonalIntent = minor >= JoystickAxisEngageRatio
            && (minor / major) >= JoystickDiagonalRatio;

        if (!diagonalIntent)
        {
            return directions;
        }

        if (horizontalDominant)
        {
            directions.Add(normalizedY >= 0 ? "Down" : "Up");
        }
        else
        {
            directions.Add(normalizedX >= 0 ? "Right" : "Left");
        }

        return directions;
    }

    private double GetJoystickTravelRadius(Control control)
    {
        Border? thumb = GetDPadThumbForControl(control);
        if (thumb == null)
        {
            return 0;
        }

        double width = control.Bounds.Width;
        double height = control.Bounds.Height;
        double thumbWidth = thumb.Bounds.Width > 0 ? thumb.Bounds.Width : thumb.Width;
        double thumbHeight = thumb.Bounds.Height > 0 ? thumb.Bounds.Height : thumb.Height;
        double thumbRadius = Math.Max(thumbWidth, thumbHeight) * 0.5;
        double controlRadius = Math.Min(width, height) * 0.5;
        return Math.Max(0, controlRadius - thumbRadius - JoystickPadding);
    }

    private void UpdateDPadThumbLocked(Control control, double offsetX, double offsetY, bool active)
    {
        Border? thumb = GetDPadThumbForControl(control);
        if (thumb == null)
        {
            return;
        }

        TranslateTransform transform = EnsureTranslateTransform(thumb);
        transform.X = offsetX;
        transform.Y = offsetY;

        if (active)
        {
            if (!thumb.Classes.Contains("thumbActive"))
            {
                thumb.Classes.Add("thumbActive");
            }
        }
        else
        {
            thumb.Classes.Remove("thumbActive");
        }
    }

    private void ResetDPadThumbLocked(Control? activeControl = null)
    {
        if (activeControl == null || ReferenceEquals(activeControl, PortraitDPadSurface))
        {
            ResetDPadThumbTransform(PortraitPadThumb);
        }

        if (activeControl == null || ReferenceEquals(activeControl, LandscapeDPadSurface))
        {
            ResetDPadThumbTransform(LandscapePadThumb);
        }
    }

    private static void ResetDPadThumbTransform(Border? thumb)
    {
        if (thumb == null)
        {
            return;
        }

        TranslateTransform transform = EnsureTranslateTransform(thumb);
        transform.X = 0;
        transform.Y = 0;
        thumb.Classes.Remove("thumbActive");
    }

    private Border? GetDPadThumbForControl(Control control)
    {
        if (ReferenceEquals(control, PortraitDPadSurface))
        {
            return PortraitPadThumb;
        }

        if (ReferenceEquals(control, LandscapeDPadSurface))
        {
            return LandscapePadThumb;
        }

        return null;
    }

    private static TranslateTransform EnsureTranslateTransform(Control control)
    {
        if (control.RenderTransform is TranslateTransform transform)
        {
            return transform;
        }

        transform = new TranslateTransform();
        control.RenderTransform = transform;
        return transform;
    }

    private void UpdateFaceGlowLocked(Control control, Point point)
    {
        Border? glow = GetFaceGlowForControl(control);
        if (glow == null)
        {
            return;
        }

        double controlWidth = control.Bounds.Width;
        double controlHeight = control.Bounds.Height;
        if (controlWidth <= 0 || controlHeight <= 0)
        {
            return;
        }

        double glowWidth = double.IsNaN(glow.Width) || glow.Width <= 0 ? 54 : glow.Width;
        double glowHeight = double.IsNaN(glow.Height) || glow.Height <= 0 ? 54 : glow.Height;
        double left = Math.Clamp(point.X - (glowWidth * 0.5), 0, Math.Max(0, controlWidth - glowWidth));
        double top = Math.Clamp(point.Y - (glowHeight * 0.5), 0, Math.Max(0, controlHeight - glowHeight));

        Canvas.SetLeft(glow, left);
        Canvas.SetTop(glow, top);
        glow.IsVisible = true;
    }

    private void HideFaceGlowLocked(Control? activeControl = null)
    {
        if (activeControl == null || ReferenceEquals(activeControl, PortraitFaceSurface))
        {
            PortraitFaceGlow.IsVisible = false;
        }

        if (activeControl == null || ReferenceEquals(activeControl, LandscapeFaceSurface))
        {
            LandscapeFaceGlow.IsVisible = false;
        }
    }

    private Border? GetFaceGlowForControl(Control control)
    {
        if (ReferenceEquals(control, PortraitFaceSurface))
        {
            return PortraitFaceGlow;
        }

        if (ReferenceEquals(control, LandscapeFaceSurface))
        {
            return LandscapeFaceGlow;
        }

        return null;
    }

    private static void SetPadVisualState(Border? border, bool active)
    {
        if (border == null)
        {
            return;
        }

        if (active)
        {
            if (!border.Classes.Contains("padPressed"))
            {
                border.Classes.Add("padPressed");
            }
        }
        else
        {
            border.Classes.Remove("padPressed");
        }
    }

    private static void SetFaceVisualState(Avalonia.Controls.Button? button, bool active)
    {
        if (button == null)
        {
            return;
        }

        if (active)
        {
            if (!button.Classes.Contains("facePressed"))
            {
                button.Classes.Add("facePressed");
            }
        }
        else
        {
            button.Classes.Remove("facePressed");
        }
    }

    private async Task StartCoreAsync()
    {
        if (string.IsNullOrWhiteSpace(_selectedRomPath))
        {
            throw new InvalidOperationException("No ROM selected.");
        }

        int bootRequestSerial = unchecked(++_bootRequestSerial);
        _bootInProgress = true;
        string romPath = _selectedRomPath;
        string romDisplayName = _selectedRomDisplayName ?? Path.GetFileName(romPath);

        StopSession(clearSelection: false, footerStatus: null);
        _presentedFrames = 0;
        ResetPerfCounters();
        _viewModel.ScreenOverlayVisible = true;
        _viewModel.IsRunning = false;
        _viewModel.StatusPill = "Booting shell";
        _viewModel.ScreenTitle = "Booting";
        _viewModel.ScreenDescription = romDisplayName;
        _viewModel.SelectedConsoleLabel = GuessConsoleLabelFromPath(romPath);
        _viewModel.FooterStatus = $"Booting {romDisplayName}...";

        IEmulatorCore? loadedCore = null;
        try
        {
            (IEmulatorCore Core, string ConsoleLabel) bootResult;
            try
            {
                bootResult = await Task.Run(() => LoadCoreForRom(romPath));
            }
            catch
            {
                if (bootRequestSerial != _bootRequestSerial
                    || !string.Equals(_selectedRomPath, romPath, StringComparison.Ordinal))
                {
                    return;
                }

                throw;
            }

            loadedCore = bootResult.Core;
            string consoleLabel = bootResult.ConsoleLabel;

            if (bootRequestSerial != _bootRequestSerial
                || !string.Equals(_selectedRomPath, romPath, StringComparison.Ordinal))
            {
                return;
            }

            _core = loadedCore;
            loadedCore = null;
            _viewModel.SelectedConsoleLabel = consoleLabel;
            InitializeAudio(core: _core);
            StartEmulationLoop(_core);
            _viewModel.IsRunning = true;
            _viewModel.FooterStatus = "ROM started in Android host.";
        }
        finally
        {
            if (loadedCore is IDisposable disposableCore)
            {
                disposableCore.Dispose();
            }

            if (bootRequestSerial == _bootRequestSerial)
            {
                _bootInProgress = false;
            }
        }
    }

    private void CancelPendingBoot()
    {
        if (!_bootInProgress)
        {
            return;
        }

        unchecked
        {
            _bootRequestSerial++;
        }

        _bootInProgress = false;
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
        long nextFrameTicks = Stopwatch.GetTimestamp();

        while (_emulationThreadRunning && ReferenceEquals(core, _core))
        {
            long frameTicks = GetTargetFrameTicks(core);
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

    private static long GetTargetFrameTicks(IEmulatorCore core)
    {
        double targetFps = GetLiveTargetFps(core);
        if (targetFps <= 0)
            targetFps = DefaultTargetFrameRate;

        return Math.Max(1L, (long)Math.Round(Stopwatch.Frequency / targetFps));
    }

    private static double GetLiveTargetFps(IEmulatorCore core)
    {
        return core switch
        {
            MdTracerAdapter md => md.GetTargetFps(),
            SnesAdapter snes => snes.GetTargetFps(ConsoleRegion.Auto),
            PceCdAdapter pce => pce.GetTargetFps(),
            PsxAdapter psx => psx.GetTargetFps(),
            _ => DefaultTargetFrameRate
        };
    }

    private unsafe void PresentLatestFrame()
    {
        bool quietNativeShellUi = ShouldQuietNativeShellUi();
        if (_viewModel.PerfHeadline != _latestPerfHeadline)
        {
            _viewModel.PerfHeadline = _latestPerfHeadline;
        }

        if (!quietNativeShellUi)
        {
            UpdateOverlaySummary();
            if (_viewModel.PerfSummary != _latestPerfSummary)
            {
                _viewModel.PerfSummary = _latestPerfSummary;
            }
        }

        long serial;
        long previouslyPresentedSerial;
        int width;
        int height;
        int srcStride;
        byte[] frameBuffer;
        bool frameIsPsx;
        PsxAdapter.PresentationFrameInfo psxFrameInfo;
        bool psxInterlaceBlend;
        int psxInterlaceFieldParity;

        lock (_frameSync)
        {
            serial = _latestFrameSerial;
            previouslyPresentedSerial = _presentedFrameSerial;
            if (serial == 0 || serial == previouslyPresentedSerial)
            {
                return;
            }

            width = _lastFrameWidth;
            height = _lastFrameHeight;
            srcStride = _lastFrameStride > 0 ? _lastFrameStride : width * 4;
            frameIsPsx = _lastFrameIsPsx;
            psxFrameInfo = _lastPsxFrameInfo;
            psxInterlaceBlend = _lastPsxInterlaceBlend;
            psxInterlaceFieldParity = _lastPsxInterlaceFieldParity;
            (_presentFrameBuffer, _latestFrameBuffer) = (_latestFrameBuffer, _presentFrameBuffer);
            frameBuffer = _presentFrameBuffer;
            _presentedFrameSerial = serial;
        }

        EnsureRenderSurface(width, height);
        ApplyPresentationSizeForCore(_core, width, height);
        if (_renderSurface == null)
        {
            return;
        }

        long presentStart = _perfStopwatch.ElapsedTicks;
        bool forceOpaque = _core is PsxAdapter
            || _core is SnesAdapter
            || _core is MdTracerAdapter
            || _core is PceCdAdapter;
        var blitOptions = CreateCurrentFrameBlitOptions(forceOpaque);

        if (_renderSurface is AndroidNativeGlRenderSurface
            && frameIsPsx
            && ShouldHoldTransientPsxBlank(psxFrameInfo))
        {
            lock (_frameSync)
            {
                if (ReferenceEquals(_presentFrameBuffer, frameBuffer))
                    _presentFrameBuffer = Array.Empty<byte>();
            }

            Interlocked.Add(ref _perfAccumulatedPresentTicks, _perfStopwatch.ElapsedTicks - presentStart);
            Interlocked.Increment(ref _perfPresentedWindowFrames);
            long heldDroppedFrames = serial - previouslyPresentedSerial - 1;
            if (heldDroppedFrames > 0)
                Interlocked.Add(ref _perfDroppedWindowFrames, heldDroppedFrames);

            return;
        }

        if (frameIsPsx)
        {
            _psxInterlaceReconstructor.TryApplyInPlace(
                frameBuffer.AsSpan(0, Math.Min(frameBuffer.Length, srcStride * height)),
                width,
                height,
                srcStride,
                psxFrameInfo);
            psxInterlaceBlend = false;
            psxInterlaceFieldParity = -1;
        }

        if (_renderSurface is OpenGlRenderSurface glOwnedSurface)
        {
            glOwnedSurface.SetInterlaceBlend(psxInterlaceBlend, psxInterlaceFieldParity);
            lock (_frameSync)
            {
                if (ReferenceEquals(_presentFrameBuffer, frameBuffer))
                    _presentFrameBuffer = Array.Empty<byte>();
            }

            _ = glOwnedSurface.PresentOwnedBuffer(
                frameBuffer,
                width,
                height,
                srcStride,
                blitOptions,
                measurePerf: false);
        }
        else if (_renderSurface is AndroidNativeGlRenderSurface nativeGlOwnedSurface)
        {
            nativeGlOwnedSurface.SetInterlaceBlend(psxInterlaceBlend, psxInterlaceFieldParity);
            lock (_frameSync)
            {
                if (ReferenceEquals(_presentFrameBuffer, frameBuffer))
                    _presentFrameBuffer = Array.Empty<byte>();
            }

            _ = nativeGlOwnedSurface.Present(
                frameBuffer.AsSpan(),
                width,
                height,
                srcStride,
                blitOptions,
                measurePerf: false);
        }
        else
        {
            _ = _renderSurface.Present(
                frameBuffer,
                width,
                height,
                srcStride,
                blitOptions,
                measurePerf: false);
        }

        if (frameIsPsx)
            NotePresentedPsxFrame(psxFrameInfo);
        else
            ResetAndroidPsxTransientBlankState();

        if (TryGetRenderSurfaceFallbackReason(out string fallbackReason))
        {
            if (TryGetRenderSurfaceDebugSummary(out string renderSurfaceDebugSummary))
                _lastRenderSurfaceDebugSummary = renderSurfaceDebugSummary;
            _lastRenderSurfaceFallbackReason = fallbackReason;
            Console.WriteLine($"[Android] OpenGL fallback -> bitmap: {fallbackReason}");
            if (_renderSurface is IDisposable disposableRenderSurface)
                disposableRenderSurface.Dispose();
            else
                _renderSurface.Reset();

            _renderSurface = new WriteableBitmapRenderSurface();
            UpdateActiveRenderModeLabel();
            _viewModel.FooterStatus = "OpenGL fallback active. Open Debug for details.";
            EnsureRenderSurface(width, height);
            if (_renderSurface != null)
            {
                _ = _renderSurface.Present(
                    frameBuffer,
                    width,
                    height,
                    srcStride,
                    blitOptions,
                    measurePerf: false);
            }
        }

        Interlocked.Add(ref _perfAccumulatedPresentTicks, _perfStopwatch.ElapsedTicks - presentStart);
        Interlocked.Increment(ref _perfPresentedWindowFrames);
        long droppedFrames = serial - previouslyPresentedSerial - 1;
        if (droppedFrames > 0)
        {
            Interlocked.Add(ref _perfDroppedWindowFrames, droppedFrames);
        }

        InvalidateScreenImages();
        _presentedFrames++;
        if (_presentedFrames == 1)
        {
            _viewModel.ScreenOverlayVisible = false;
            _viewModel.FooterStatus = $"Rendering {_selectedRomDisplayName ?? "ROM"}";
        }
    }

    private void QueuePresentLatestFrame()
    {
        if (Interlocked.CompareExchange(ref _presentLatestFrameQueued, 1, 0) != 0)
        {
            return;
        }

        Dispatcher.UIThread.Post(ProcessQueuedPresentLatestFrame, DispatcherPriority.Background);
    }

    private void ProcessQueuedPresentLatestFrame()
    {
        try
        {
            while (true)
            {
                PresentLatestFrame();

                long latestSerial;
                long presentedSerial;
                lock (_frameSync)
                {
                    latestSerial = _latestFrameSerial;
                    presentedSerial = _presentedFrameSerial;
                }

                if (latestSerial == presentedSerial)
                {
                    break;
                }
            }
        }
        finally
        {
            Interlocked.Exchange(ref _presentLatestFrameQueued, 0);

            long latestSerial;
            long presentedSerial;
            lock (_frameSync)
            {
                latestSerial = _latestFrameSerial;
                presentedSerial = _presentedFrameSerial;
            }

            if (latestSerial != presentedSerial)
            {
                QueuePresentLatestFrame();
            }
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

    private void RunSavestateAction(int slotIndex, bool isLoad)
    {
        IEmulatorCore? core = _core;
        if (core is not ISavestateCapable savestateCore)
        {
            _viewModel.FooterStatus = core == null
                ? "Start a ROM before using savestates."
                : "Savestates are not supported for this core.";
            return;
        }

        bool wasRunning = _emulationThreadRunning;
        StopEmulationLoop();

        try
        {
            if (isLoad)
            {
                _savestateService.Load(savestateCore, slotIndex);
                CaptureLatestFrame(core);
                PresentLatestFrame();
                _viewModel.ScreenOverlayVisible = false;
                _viewModel.FooterStatus = $"Loaded slot {slotIndex}.";
            }
            else
            {
                _savestateService.Save(savestateCore, slotIndex);
                _viewModel.FooterStatus = $"Saved slot {slotIndex}.";
            }
        }
        catch (Exception ex)
        {
            string operation = isLoad ? "Load" : "Save";
            _viewModel.FooterStatus = $"{operation} slot {slotIndex} failed: {FormatExceptionForUi(ex)}";
        }
        finally
        {
            if (wasRunning && ReferenceEquals(core, _core))
            {
                StartEmulationLoop(core);
            }
        }
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
        Interlocked.Exchange(ref _perfAccumulatedPresentTicks, 0);
        Interlocked.Exchange(ref _perfPresentedWindowFrames, 0);
        Interlocked.Exchange(ref _perfDroppedWindowFrames, 0);
        _emulatedFrames = 0;
        _presentedFrameSerial = 0;
        _latestFrameSerial = 0;
        _lastFrameWidth = 0;
        _lastFrameHeight = 0;
        _lastFrameStride = 0;
        _lastFrameIsPsx = false;
        _lastPsxInterlaceBlend = false;
        _lastPsxInterlaceFieldParity = -1;
        _lastPsxFrameInfo = default;
        _lastPresentationWidth = 0;
        _lastPresentationHeight = 0;
        _appliedPresentationWidth = double.NaN;
        _appliedPresentationHeight = double.NaN;
        _appliedPresentationLandscape = null;
        _latestPerfSummary = "Perf idle";
        _latestPerfHeadline = "FPS --  MAX --";
        _viewModel.PerfSummary = "Perf idle";
        _viewModel.PerfHeadline = "FPS --  MAX --";
        _psxInterlaceReconstructor.Reset();
        ResetAndroidPsxTransientBlankState();
    }

    private bool ShouldHoldTransientPsxBlank(in PsxAdapter.PresentationFrameInfo frameInfo)
    {
        if (!(frameInfo.IsDisplayDisabled && frameInfo.IsMostlyBlack))
        {
            if (!frameInfo.IsMostlyBlack || !frameInfo.IsDisplayDisabled)
                _androidPsxTransientBlankBurst = 0;
            return false;
        }

        if (!_androidPsxHasStableVisibleFrame)
            return false;

        _androidPsxTransientBlankBurst++;
        return _androidPsxTransientBlankBurst <= 4;
    }

    private void NotePresentedPsxFrame(in PsxAdapter.PresentationFrameInfo frameInfo)
    {
        if (frameInfo.IsDisplayDisabled && frameInfo.IsMostlyBlack)
            return;

        _androidPsxTransientBlankBurst = 0;
        if (!frameInfo.IsMostlyBlack)
            _androidPsxHasStableVisibleFrame = true;
    }

    private void ResetAndroidPsxTransientBlankState()
    {
        _androidPsxTransientBlankBurst = 0;
        _androidPsxHasStableVisibleFrame = false;
    }

    private void UpdatePerfStats(long emuTicks, long audioTicks, long blitTicks)
    {
        _perfWindowFrames++;
        double emuMs = StopwatchTicksToMs(emuTicks);
        double audioMs = StopwatchTicksToMs(audioTicks);
        double blitMs = StopwatchTicksToMs(blitTicks);
        _perfAccumulatedEmuMs += emuMs;
        _perfAccumulatedAudioMs += audioMs;
        _perfAccumulatedBlitMs += blitMs;

        long nowTicks = _perfStopwatch.ElapsedTicks;
        double windowMs = StopwatchTicksToMs(nowTicks - _perfWindowStartTicks);
        if (windowMs < 250)
            return;

        long presentTicks = Interlocked.Exchange(ref _perfAccumulatedPresentTicks, 0);
        long presentedFrames = Interlocked.Exchange(ref _perfPresentedWindowFrames, 0);
        long droppedFrames = Interlocked.Exchange(ref _perfDroppedWindowFrames, 0);
        double avgPresentMs = presentedFrames > 0 ? StopwatchTicksToMs(presentTicks) / presentedFrames : 0;
        double avgFrameMs = windowMs / _perfWindowFrames;
        double fps = avgFrameMs > 0 ? 1000.0 / avgFrameMs : 0;
        double avgWorkMs = (_perfAccumulatedEmuMs + _perfAccumulatedAudioMs + _perfAccumulatedBlitMs) / _perfWindowFrames;
        double maxFps = avgWorkMs > 0 ? 1000.0 / avgWorkMs : 0;
        string coreLabel = _core?.GetType().Name ?? "None";
        string perfSummary =
            $"Perf  FPS:{fps:0}  Max:{maxFps:0}  Work:{avgWorkMs:0.0}ms  Emu:{_perfAccumulatedEmuMs / _perfWindowFrames:0.0}ms  Audio:{_perfAccumulatedAudioMs / _perfWindowFrames:0.0}ms  Cap:{_perfAccumulatedBlitMs / _perfWindowFrames:0.0}ms  Present:{avgPresentMs:0.0}ms  Drop:{droppedFrames}  Frame:{_emulatedFrames}  Res:{_lastFrameWidth}x{_lastFrameHeight}  Core:{coreLabel}";
        if (TryGetRenderSurfaceDebugSummary(out string glSummary))
        {
            perfSummary = $"{perfSummary}\n{glSummary}";
        }

        _latestPerfHeadline = $"FPS {fps:0}  MAX {maxFps:0}";
        if (_core is PsxAdapter psx && psx.TryGetBootProgressSummary(out string psxBoot))
        {
            perfSummary = $"{perfSummary}\n{psxBoot}";
            if (psx.TryGetFramePerfSummary(out string psxFramePerf))
            {
                perfSummary = $"{perfSummary}\n{psxFramePerf}";
            }
            if (psx.TryGetDebugState(out string psxState))
            {
                perfSummary = $"{perfSummary}\n{psxState}";
            }
            if (psxBoot.Contains("biosExited=0", StringComparison.Ordinal)
                && psx.TryGetDebugCodeWindow(out string psxCodeWindow, wordsBefore: 2, wordsAfter: 4))
            {
                string compactCodeWindow = string.Join(
                    '\n',
                    psxCodeWindow
                        .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                        .Take(7));
                perfSummary = $"{perfSummary}\n{compactCodeWindow}";
            }
        }
        else if (_core is SnesAdapter snes && snes.TryGetFramePerfSummary(out string snesFramePerf))
        {
            perfSummary = $"{perfSummary}\n{snesFramePerf}";
            string? ppuSnapshot = snes.GetPpuDebugSnapshot();
            if (!string.IsNullOrWhiteSpace(ppuSnapshot))
            {
                string compactPpu = string.Join(
                    '\n',
                    ppuSnapshot
                        .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                        .Take(6));
                if (!string.IsNullOrWhiteSpace(compactPpu))
                    perfSummary = $"{perfSummary}\n{compactPpu}";
            }
        }

        _latestPerfSummary = perfSummary;

        _perfWindowStartTicks = nowTicks;
        _perfWindowFrames = 0;
        _perfAccumulatedEmuMs = 0;
        _perfAccumulatedAudioMs = 0;
        _perfAccumulatedBlitMs = 0;
    }

    private bool ShouldQuietNativeShellUi()
    {
        return _renderSurface is AndroidNativeGlRenderSurface
            && !_viewModel.DebugVisible;
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
        Interlocked.Exchange(ref _presentLatestFrameQueued, 0);
        StopEmulationLoop();
        IEmulatorCore? core = _core;
        _core = null;
        _audioEngine?.Dispose();
        _audioEngine = null;
        _audioPullMode = false;
        ResetSnesAudioRing();
        if (_renderSurface is IDisposable disposableRenderSurface)
            disposableRenderSurface.Dispose();
        else
            _renderSurface?.Reset();
        _renderSurface = null;
        UpdateActiveRenderModeLabel();
        _presentedFrames = 0;
        ResetPerfCounters();
        lock (_frameSync)
        {
            _captureFrameBuffer = Array.Empty<byte>();
            _latestFrameBuffer = Array.Empty<byte>();
            _presentFrameBuffer = Array.Empty<byte>();
        }
        DetachRenderSurfaceFromHosts();
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

        ClearOverlayInputState();
    }

    private void ClearOverlayInputState()
    {
        lock (_inputSync)
        {
            _dpadPointerDirections.Clear();
            _dpadPointerIntensity.Clear();
            _directionPointers.Clear();
            _actionPointers.Clear();
            _facePointers.Clear();
            _directionPressCounts.Clear();
            _actionPressCounts.Clear();
            _joystickDirectionPulseFrames.Clear();
            _pressedDirections.Clear();
            _pressedActions.Clear();
            _nativePressedDirections.Clear();
            _nativePressedActions.Clear();
            _directionLatchFrames.Clear();
            _actionLatchFrames.Clear();
            UpdateDPadVisualsLocked();
            ResetDPadThumbLocked();
            UpdateFaceVisualsLocked();
            HideFaceGlowLocked();
        }

        UpdateOverlaySummary();
    }

    private void InitializeAudio(IEmulatorCore? core)
    {
        _audioEngine?.Dispose();
        _audioEngine = null;
        _audioPullMode = false;
        ResetSnesAudioRing();

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
            int targetBufferedFrames = (int)(sampleRate * 0.20);
            _audioEngine = new AudioEngine(
                new AndroidAudioSink(),
                sampleRate,
                channels,
                framesPerBatch: AndroidAudioBatchFrames,
                bufferFrames: AndroidAudioBufferFrames);
            _audioEngine.SetTargetBufferedFrames(targetBufferedFrames);
            _audioEngine.Start();

            if (core is SnesAdapter)
            {
                _audioPullMode = true;
                InitSnesAudioRing(channels);
                _audioEngine.EnablePullMode(SnesAudioPullProducer, targetBufferedFrames: targetBufferedFrames, maxFramesPerPull: AndroidAudioPullMaxFrames);
                if (!initialAudio.IsEmpty)
                {
                    EnqueueSnesAudio(initialAudio);
                }
            }
            else if (!initialAudio.IsEmpty)
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

        if (_audioPullMode && core is SnesAdapter)
        {
            EnqueueSnesAudio(audio);
            return;
        }

        audioEngine.Submit(audio);
    }

    private void InitSnesAudioRing(int channels)
    {
        if (channels <= 0)
        {
            channels = 2;
        }

        _snesAudioChannels = channels;
        int neededSamples = AndroidAudioBufferFrames * channels;
        if (_snesAudioRing.Length != neededSamples)
        {
            _snesAudioRing = new short[neededSamples];
        }

        _snesAudioTemp = Array.Empty<short>();
        _snesAudioRead = 0;
        _snesAudioWrite = 0;
        _snesAudioCount = 0;
    }

    private void ResetSnesAudioRing()
    {
        lock (_snesAudioLock)
        {
            _snesAudioRead = 0;
            _snesAudioWrite = 0;
            _snesAudioCount = 0;
        }
    }

    private void EnqueueSnesAudio(ReadOnlySpan<short> audio)
    {
        if (audio.IsEmpty)
        {
            return;
        }

        lock (_snesAudioLock)
        {
            if (_snesAudioRing.Length == 0)
            {
                InitSnesAudioRing(_snesAudioChannels);
            }

            int available = _snesAudioRing.Length - _snesAudioCount;
            int toWrite = Math.Min(audio.Length, available);
            if (toWrite <= 0)
            {
                return;
            }

            int first = Math.Min(toWrite, _snesAudioRing.Length - _snesAudioWrite);
            audio.Slice(0, first).CopyTo(_snesAudioRing.AsSpan(_snesAudioWrite));
            _snesAudioWrite = (_snesAudioWrite + first) % _snesAudioRing.Length;
            int remaining = toWrite - first;
            if (remaining > 0)
            {
                audio.Slice(first, remaining).CopyTo(_snesAudioRing.AsSpan(0));
                _snesAudioWrite = remaining;
            }

            _snesAudioCount += toWrite;
        }
    }

    private ReadOnlySpan<short> DequeueSnesAudio(int frames)
    {
        if (frames <= 0)
        {
            return ReadOnlySpan<short>.Empty;
        }

        int neededSamples = frames * _snesAudioChannels;
        lock (_snesAudioLock)
        {
            if (_snesAudioCount <= 0)
            {
                return ReadOnlySpan<short>.Empty;
            }

            int toRead = Math.Min(neededSamples, _snesAudioCount);
            if (_snesAudioTemp.Length < toRead)
            {
                _snesAudioTemp = new short[toRead];
            }

            int first = Math.Min(toRead, _snesAudioRing.Length - _snesAudioRead);
            _snesAudioRing.AsSpan(_snesAudioRead, first).CopyTo(_snesAudioTemp);
            _snesAudioRead = (_snesAudioRead + first) % _snesAudioRing.Length;
            int remaining = toRead - first;
            if (remaining > 0)
            {
                _snesAudioRing.AsSpan(0, remaining).CopyTo(_snesAudioTemp.AsSpan(first));
                _snesAudioRead = remaining;
            }

            _snesAudioCount -= toRead;
            return _snesAudioTemp.AsSpan(0, toRead);
        }
    }

    private ReadOnlySpan<short> SnesAudioPullProducer(int frames)
    {
        if (_core is SnesAdapter)
        {
            return DequeueSnesAudio(frames);
        }

        return ReadOnlySpan<short>.Empty;
    }

    private async Task<string> ImportRomAsync(IStorageProvider? storageProvider, IStorageFile file, bool isSystemFile)
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
            if (ext is ".iso" or ".img" or ".chd" or ".pbp")
            {
                throw new InvalidOperationException("Large/disc-based ROMs are not cached on Android yet. A streaming backend is still needed for PS1/CD images.");
            }
        }

        string baseDir = Path.Combine(_appDataDir, isSystemFile ? "system-files" : "rom-cache");
        Directory.CreateDirectory(baseDir);

        string safeName = SanitizeFileComponent(Path.GetFileNameWithoutExtension(file.Name), fallback: "rom");

        Stream? source = await file.OpenReadAsync();
        bool transferredToVirtualFile = false;
        try
        {
            if (!isSystemFile
                && await TryRegisterVirtualDiscSourceAsync(storageProvider, file, source) is { } virtualImport)
            {
                transferredToVirtualFile = true;
                TrackSelectedVirtualPaths(virtualImport.RegisteredPaths);
                return virtualImport.PrimaryPath;
            }

            if (!isSystemFile && source.CanSeek && source.Length > 128L * 1024 * 1024)
            {
                throw new InvalidOperationException("This ROM is too large for temporary Android caching. Disc images need direct streaming support instead.");
            }

            return await CopyToDeterministicCacheAsync(source, baseDir, safeName, extension);
        }
        finally
        {
            if (!transferredToVirtualFile)
            {
                await source.DisposeAsync();
            }
        }
    }

    private static async Task<string> CopyToDeterministicCacheAsync(Stream source, string baseDir, string safeName, string extension)
    {
        string tempPath = Path.Combine(baseDir, $".tmp-{Guid.NewGuid():N}{extension}");
        using IncrementalHash hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[1024 * 64];

        try
        {
            await using (FileStream destination = File.Create(tempPath))
            {
                while (true)
                {
                    int read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length));
                    if (read <= 0)
                        break;

                    hasher.AppendData(buffer, 0, read);
                    await destination.WriteAsync(buffer.AsMemory(0, read));
                }

                await destination.FlushAsync();
            }

            string hashPrefix = Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant()[..12];
            string finalPath = Path.Combine(baseDir, $"{safeName}-{hashPrefix}{extension}");
            if (File.Exists(finalPath))
            {
                File.Delete(tempPath);
                return finalPath;
            }

            File.Move(tempPath, finalPath);
            return finalPath;
        }
        catch
        {
            if (File.Exists(tempPath))
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch
                {
                }
            }

            throw;
        }
    }

    private static string SanitizeFileComponent(string? value, string fallback)
    {
        string candidate = string.IsNullOrWhiteSpace(value) ? fallback : value;
        foreach (char invalid in Path.GetInvalidFileNameChars())
        {
            candidate = candidate.Replace(invalid, '_');
        }

        candidate = candidate.Trim();
        return string.IsNullOrWhiteSpace(candidate) ? fallback : candidate;
    }

    private async Task<VirtualRomImport?> TryRegisterVirtualDiscSourceAsync(IStorageProvider? storageProvider, IStorageFile file, Stream source)
    {
        string ext = Path.GetExtension(file.Name).ToLowerInvariant();
        if (ext == ".cue")
        {
            return await TryRegisterVirtualCueBundleAsync(storageProvider, file, source);
        }

        if (!source.CanSeek || source.Length <= 128L * 1024 * 1024)
        {
            return null;
        }

        if (ext != ".bin")
        {
            return null;
        }

        string candidatePath = VirtualFileSystem.RegisterSharedStream(file.Name, source, ownsStream: true);

        OpticalDiscKind discKind = OpticalDiscDetector.Detect(candidatePath);
        if (discKind == OpticalDiscKind.PceCd)
        {
            VirtualFileSystem.Unregister(candidatePath);
            throw new InvalidOperationException("PC Engine CD needs the .cue file so the full disc layout can be streamed on Android.");
        }

        if (discKind is not OpticalDiscKind.Psx and not OpticalDiscKind.SegaCd)
        {
            VirtualFileSystem.Unregister(candidatePath);
            throw new InvalidOperationException("Large direct-stream discs are currently enabled for single-file PSX or Sega CD .bin images on Android.");
        }

        return new VirtualRomImport(candidatePath, new[] { candidatePath });
    }

    private async Task<VirtualRomImport?> TryRegisterVirtualCueBundleAsync(IStorageProvider? storageProvider, IStorageFile cueFile, Stream cueSource)
    {
        if (storageProvider == null)
        {
            throw new InvalidOperationException("Folder picker is unavailable for cue-based disc images on this surface.");
        }

        using var cueBuffer = new MemoryStream();
        await cueSource.CopyToAsync(cueBuffer);
        byte[] cueBytes = cueBuffer.ToArray();

        string bundleRoot = Path.Combine("/__eutherdrive_virtual__", $"cuebundle_{Guid.NewGuid():N}");
        string cueVirtualPath = VirtualFileSystem.RegisterBytesAtPath(Path.Combine(bundleRoot, cueFile.Name), cueFile.Name, cueBytes);
        var registeredPaths = new List<string> { cueVirtualPath };

        try
        {
            List<string> referencedFiles = CueSheetResolver.EnumerateReferencedFiles(cueVirtualPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (referencedFiles.Count == 0)
            {
                throw new InvalidOperationException("The selected cue file does not reference any track files.");
            }

            var folders = await storageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Choose the folder containing the cue track files",
                AllowMultiple = false
            });

            if (folders.Count == 0)
            {
                throw new InvalidOperationException("Track folder selection was cancelled.");
            }

            var candidateFiles = new List<IStorageFile>();
            await foreach (IStorageItem folderItem in folders[0].GetItemsAsync())
            {
                if (folderItem is IStorageFile candidateFile)
                {
                    candidateFiles.Add(candidateFile);
                }
            }

            if (candidateFiles.Count == 0)
            {
                throw new InvalidOperationException("The selected folder does not contain any files.");
            }

            HashSet<string> referencedExtensions = referencedFiles
                .Select(static reference => Path.GetExtension(reference))
                .Where(static extension => !string.IsNullOrWhiteSpace(extension))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (IStorageFile candidateFile in candidateFiles)
            {
                string candidateExtension = Path.GetExtension(candidateFile.Name);
                if (referencedExtensions.Count != 0 && !referencedExtensions.Contains(candidateExtension))
                {
                    continue;
                }

                Stream candidateStream = await candidateFile.OpenReadAsync();
                string virtualPath = VirtualFileSystem.RegisterSharedStreamAtPath(
                    Path.Combine(bundleRoot, candidateFile.Name),
                    candidateFile.Name,
                    candidateStream,
                    ownsStream: true);
                registeredPaths.Add(virtualPath);
            }

            foreach (string referencedFile in referencedFiles)
            {
                string resolvedPath = CueSheetResolver.ResolveReferencedPath(cueVirtualPath, referencedFile);
                if (!VirtualFileSystem.Exists(resolvedPath))
                {
                    throw new InvalidOperationException($"Missing cue track file: {Path.GetFileName(referencedFile)}");
                }
            }

            OpticalDiscKind discKind = OpticalDiscDetector.Detect(cueVirtualPath);
            if (discKind is not OpticalDiscKind.Psx and not OpticalDiscKind.PceCd and not OpticalDiscKind.SegaCd)
            {
                throw new InvalidOperationException("Android cue streaming currently supports PSX, PC Engine CD and Sega CD disc sets.");
            }

            return new VirtualRomImport(cueVirtualPath, registeredPaths);
        }
        catch
        {
            foreach (string path in registeredPaths)
            {
                VirtualFileSystem.Unregister(path);
            }

            throw;
        }
    }

    private void TrackSelectedVirtualPaths(IEnumerable<string> paths)
    {
        foreach (string path in paths)
        {
            if (!string.IsNullOrWhiteSpace(path))
            {
                _selectedVirtualPaths.Add(path);
            }
        }
    }

    private void ReleaseSelectedVirtualRom()
    {
        foreach (string path in _selectedVirtualPaths.ToArray())
        {
            VirtualFileSystem.Unregister(path);
        }

        _selectedVirtualPaths.Clear();
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
            string importedPath = await ImportRomAsync(storageProvider, files[0], isSystemFile: true);
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
    private async void OnPickSegaCdBios(object? sender, RoutedEventArgs e) => await PickSystemFileAsync("SEGA CD BIOS", "Select Sega CD BIOS", new[] { "*.bin", "*.*" });
    private void OnClearSegaCdBios(object? sender, RoutedEventArgs e) => ClearSystemFile("SEGA CD BIOS");
    private async void OnPickPsxBios(object? sender, RoutedEventArgs e) => await PickSystemFileAsync("PSX BIOS", "Select PSX BIOS", new[] { "*.bin", "*.*" });
    private void OnClearPsxBios(object? sender, RoutedEventArgs e) => ClearSystemFile("PSX BIOS");
    private void OnPsxFastLoadToggle(object? sender, RoutedEventArgs e)
    {
        _viewModel.PsxFastLoadEnabled = (sender as Avalonia.Controls.CheckBox)?.IsChecked == true;
        ApplyPsxExecutionSettings();
        SaveSettings();
        _viewModel.FooterStatus = $"PSX fast load {(_viewModel.PsxFastLoadEnabled ? "enabled" : "disabled")}.";
    }

    private void OnPsxSuperFastBootToggle(object? sender, RoutedEventArgs e)
    {
        _viewModel.PsxSuperFastBootEnabled = (sender as Avalonia.Controls.CheckBox)?.IsChecked == true;
        ApplyPsxExecutionSettings();
        SaveSettings();
        _viewModel.FooterStatus = $"PSX superfast boot {(_viewModel.PsxSuperFastBootEnabled ? "enabled" : "disabled")}. Reboot PSX content to apply.";
    }

    private void OnPsxAnalogControllerToggle(object? sender, RoutedEventArgs e)
    {
        _viewModel.PsxAnalogControllerEnabled = (sender as Avalonia.Controls.CheckBox)?.IsChecked == true;
        ApplyPsxExecutionSettings();
        SaveSettings();
        _viewModel.FooterStatus = $"PSX analog pad mode {(_viewModel.PsxAnalogControllerEnabled ? "enabled" : "disabled")}. Reboot the game if it does not detect the controller change live.";
    }

    private void OnPsxVideoStandardChanged(object? sender, SelectionChangedEventArgs e)
    {
        _viewModel.PsxVideoStandardIndex = Math.Clamp((sender as ComboBox)?.SelectedIndex ?? 0, 0, 2);
        ApplyPsxExecutionSettings();
        SaveSettings();
        _viewModel.FooterStatus = $"PSX video set to {GetPsxVideoStandardMode(_viewModel.PsxVideoStandardIndex)}.";
    }

    private void OnPsxFrameRateChanged(object? sender, SelectionChangedEventArgs e)
    {
        _viewModel.PsxFrameRateIndex = Math.Clamp((sender as ComboBox)?.SelectedIndex ?? 0, 0, 2);
        ApplyPsxExecutionSettings();
        SaveSettings();
        _viewModel.FooterStatus = $"PSX rate set to {DescribeFrameRateMode(GetPsxFrameRateMode(_viewModel.PsxFrameRateIndex))}.";
    }

    private void OnSharpPixelsToggle(object? sender, RoutedEventArgs e)
    {
        _viewModel.SharpPixelsEnabled = (sender as Avalonia.Controls.CheckBox)?.IsChecked != false;
        ApplySharpPixelsSetting();
        SaveSettings();
        _viewModel.FooterStatus = _viewModel.SharpPixelsEnabled
            ? "Sharp pixels enabled."
            : "Sharp pixels disabled.";
    }

    private void OnAndroidRenderBackendChanged(object? sender, SelectionChangedEventArgs e)
    {
        int index = Math.Clamp((sender as ComboBox)?.SelectedIndex ?? 0, 0, 2);
        if (_viewModel.AndroidRenderBackendIndex == index)
        {
            UpdateAndroidRenderBackendStatus();
            return;
        }

        _viewModel.AndroidRenderBackendIndex = index;
        UpdateAndroidRenderBackendStatus();
        SaveSettings();

        AndroidRenderBackendMode mode = GetAndroidRenderBackendModeFromIndex(index);
        _viewModel.FooterStatus = $"{AndroidRenderBackendConfig.GetDisplayName(mode)} selected. Restart app to apply.";
    }

    private void OnScanlineStrengthChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        int strength = ClampPercent((int)Math.Round(e.NewValue));
        if (_viewModel.ScanlineStrengthPercent == strength)
            return;

        _viewModel.ScanlineStrengthPercent = strength;
        SaveSettings();
        _viewModel.FooterStatus = strength > 0
            ? $"Scanline strength set to {strength}%."
            : "Scanlines disabled.";
    }

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
        PceCdAdapter.BiosPath = RegisterSystemFileVirtualPath("PCE BIOS", _viewModel.PceBiosPath, _viewModel.PceBiosDisplay);
        string? segaCdBiosPath = RegisterSystemFileVirtualPath("SEGA CD BIOS", _viewModel.SegaCdBiosPath, _viewModel.SegaCdBiosDisplay);
        PsxAdapter.BiosPath = RegisterSystemFileVirtualPath("PSX BIOS", _viewModel.PsxBiosPath, _viewModel.PsxBiosDisplay);
        ApplyPsxExecutionSettings();
        ApplySharpPixelsSetting();

        SetEnv("EUTHERDRIVE_PCE_SAVE_DIR", null);
        SetEnv("EUTHERDRIVE_SCD_BIOS", segaCdBiosPath);
        SetEnv("EUTHERDRIVE_SCD_BIOS_U", segaCdBiosPath);
        SetEnv("EUTHERDRIVE_SCD_BIOS_E", segaCdBiosPath);
        SetEnv("EUTHERDRIVE_SCD_BIOS_J", segaCdBiosPath);
        SetEnv("EUTHERDRIVE_DSP1_ROM", _viewModel.Dsp1Path);
        SetEnv("EUTHERDRIVE_DSP2_ROM", _viewModel.Dsp2Path);
        SetEnv("EUTHERDRIVE_DSP3_ROM", _viewModel.Dsp3Path);
        SetEnv("EUTHERDRIVE_DSP4_ROM", _viewModel.Dsp4Path);
        SetEnv("EUTHERDRIVE_ST010_ROM", _viewModel.St010Path);
        SetEnv("EUTHERDRIVE_ST011_ROM", _viewModel.St011Path);
        SetEnv("EUTHERDRIVE_ST018_ROM", _viewModel.St018Path);
    }

    private void UpdateAndroidRenderBackendStatus()
    {
        AndroidRenderBackendMode selected = GetAndroidRenderBackendModeFromIndex(_viewModel.AndroidRenderBackendIndex);
        AndroidRenderBackendMode active = AndroidRenderBackendConfig.StartupMode;
        string activeName = AndroidRenderBackendConfig.GetDisplayName(active);
        string selectedName = AndroidRenderBackendConfig.GetDisplayName(selected);

        _viewModel.AndroidRenderBackendStatus = selected == active
            ? $"Active on this boot: {activeName}"
            : $"Restart required. Current: {activeName}. Next: {selectedName}.";
    }

    private static AndroidRenderBackendMode GetAndroidRenderBackendModeFromIndex(int index)
        => index switch
        {
            1 => AndroidRenderBackendMode.VulkanOpenGl,
            2 => AndroidRenderBackendMode.OpenGl,
            _ => AndroidRenderBackendMode.VulkanBitmap
        };

    private static int GetAndroidRenderBackendIndex(AndroidRenderBackendMode mode)
        => mode switch
        {
            AndroidRenderBackendMode.VulkanOpenGl => 1,
            AndroidRenderBackendMode.OpenGl => 2,
            _ => 0
        };

    private void ApplyPsxExecutionSettings()
    {
        FrameRateMode frameRateMode = GetPsxFrameRateMode(_viewModel.PsxFrameRateIndex);
        PsxVideoStandardMode videoStandardMode = GetPsxVideoStandardMode(_viewModel.PsxVideoStandardIndex);

        PsxAdapter.AnalogControllerEnabled = _viewModel.PsxAnalogControllerEnabled;
        PsxAdapter.FastLoadEnabled = _viewModel.PsxFastLoadEnabled;
        PsxAdapter.SuperFastBootEnabled = _viewModel.PsxSuperFastBootEnabled;
        PsxAdapter.FrameRateMode = frameRateMode;
        PsxAdapter.VideoStandardMode = videoStandardMode;

        if (_core is PsxAdapter psx)
        {
            psx.SetAnalogControllerEnabled(_viewModel.PsxAnalogControllerEnabled);
            psx.SetFastLoadEnabled(_viewModel.PsxFastLoadEnabled);
            psx.SetSuperFastBootEnabled(_viewModel.PsxSuperFastBootEnabled);
            psx.SetFrameRateMode(frameRateMode);
            psx.SetVideoStandardMode(videoStandardMode);
        }
    }

    private static FrameRateMode GetPsxFrameRateMode(int selectedIndex)
    {
        return selectedIndex switch
        {
            1 => FrameRateMode.Hz50,
            2 => FrameRateMode.Hz60,
            _ => FrameRateMode.Auto
        };
    }

    private static PsxVideoStandardMode GetPsxVideoStandardMode(int selectedIndex)
    {
        return selectedIndex switch
        {
            1 => PsxVideoStandardMode.PAL,
            2 => PsxVideoStandardMode.NTSC,
            _ => PsxVideoStandardMode.Auto
        };
    }

    private static string DescribeFrameRateMode(FrameRateMode mode)
    {
        return mode switch
        {
            FrameRateMode.Hz50 => "50 Hz",
            FrameRateMode.Hz60 => "60 Hz",
            _ => "Auto"
        };
    }

    private FrameBlitOptions CreateCurrentFrameBlitOptions(bool forceOpaque)
    {
        int scanlineStrength = ClampPercent(_viewModel.ScanlineStrengthPercent);
        int scanlineDarkenFactor = 256 - ((scanlineStrength * 256) / 100);
        return new FrameBlitOptions(
            SharpPixels: _viewModel.SharpPixelsEnabled,
            ForceOpaque: forceOpaque,
            ApplyScanlines: scanlineStrength > 0,
            ScanlineDarkenFactor: scanlineDarkenFactor);
    }

    private static int ClampPercent(int value) => Math.Clamp(value, 0, 100);

    private void ApplySharpPixelsSetting()
    {
        if (_renderSurface?.View is not Image renderImage)
            return;

        RenderOptions.SetBitmapInterpolationMode(
            renderImage,
            _viewModel.SharpPixelsEnabled ? BitmapInterpolationMode.None : BitmapInterpolationMode.MediumQuality);
    }

    private string? RegisterSystemFileVirtualPath(string key, string? physicalPath, string? displayName)
    {
        if (_virtualSystemPaths.TryGetValue(key, out string? existingPath))
        {
            if (!string.IsNullOrEmpty(existingPath))
            {
                VirtualFileSystem.Unregister(existingPath);
            }
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
            SegaCdBiosPath = _viewModel.SegaCdBiosPath,
            SegaCdBiosDisplay = _viewModel.SegaCdBiosDisplay,
            PsxBiosPath = _viewModel.PsxBiosPath,
            PsxBiosDisplay = _viewModel.PsxBiosDisplay,
            PsxAnalogControllerEnabled = _viewModel.PsxAnalogControllerEnabled,
            PsxFastLoadEnabled = _viewModel.PsxFastLoadEnabled,
            PsxSuperFastBootEnabled = _viewModel.PsxSuperFastBootEnabled,
            PsxVideoStandardIndex = _viewModel.PsxVideoStandardIndex,
            PsxFrameRateIndex = _viewModel.PsxFrameRateIndex,
            AndroidRenderBackendMode = GetAndroidRenderBackendModeFromIndex(_viewModel.AndroidRenderBackendIndex).ToString(),
            SharpPixelsEnabled = _viewModel.SharpPixelsEnabled,
            ScanlineStrengthPercent = _viewModel.ScanlineStrengthPercent,
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
        File.WriteAllText(_settingsPath, Toml.FromModel(settings));
    }

    private void LoadSettings()
    {
        if (TryLoadTomlSettings(_settingsPath, out AndroidSettings? settings) && settings != null)
        {
            ApplyLoadedSettings(settings);
            return;
        }

        if (TryMigrateJsonSettings(out settings) && settings != null)
        {
            ApplyLoadedSettings(settings);
            return;
        }

        if (!File.Exists(_settingsPath) && !File.Exists(_legacyJsonSettingsPath))
        {
            return;
        }

        _viewModel.SettingsHint = "Settings file was unreadable and will be recreated on next save.";
    }

    private bool TryLoadTomlSettings(string path, out AndroidSettings? settings)
    {
        settings = null;
        if (!File.Exists(path))
            return false;

        try
        {
            settings = Toml.ToModel<AndroidSettings>(File.ReadAllText(path));
            return settings != null;
        }
        catch
        {
            settings = null;
            return false;
        }
    }

    private bool TryMigrateJsonSettings(out AndroidSettings? settings)
    {
        settings = null;
        if (!File.Exists(_legacyJsonSettingsPath))
            return false;

        try
        {
            settings = JsonSerializer.Deserialize<AndroidSettings>(File.ReadAllText(_legacyJsonSettingsPath));
            if (settings == null)
                return false;

            Directory.CreateDirectory(_appDataDir);
            File.WriteAllText(_settingsPath, Toml.FromModel(settings));
            File.Move(_legacyJsonSettingsPath, _legacyJsonSettingsPath + ".bak", overwrite: true);
            return true;
        }
        catch
        {
            settings = null;
            return false;
        }
    }

    private void ApplyLoadedSettings(AndroidSettings settings)
    {
        _viewModel.PceBiosPath = settings.PceBiosPath;
        _viewModel.PceBiosDisplay = settings.PceBiosDisplay ?? "(auto)";
        _viewModel.SegaCdBiosPath = settings.SegaCdBiosPath;
        _viewModel.SegaCdBiosDisplay = settings.SegaCdBiosDisplay ?? "(none)";
        _viewModel.PsxBiosPath = settings.PsxBiosPath;
        _viewModel.PsxBiosDisplay = settings.PsxBiosDisplay ?? "(none)";
        _viewModel.PsxAnalogControllerEnabled = settings.PsxAnalogControllerEnabled;
        _viewModel.PsxFastLoadEnabled = settings.PsxFastLoadEnabled;
        _viewModel.PsxSuperFastBootEnabled = settings.PsxSuperFastBootEnabled;
        _viewModel.PsxVideoStandardIndex = Math.Clamp(settings.PsxVideoStandardIndex, 0, 2);
        _viewModel.PsxFrameRateIndex = Math.Clamp(settings.PsxFrameRateIndex, 0, 2);
        AndroidRenderBackendMode backendMode = AndroidRenderBackendConfig.TryGetEnvironmentOverride(out AndroidRenderBackendMode envBackendMode)
            ? envBackendMode
            : AndroidRenderBackendConfig.Parse(settings.AndroidRenderBackendMode);
        _viewModel.AndroidRenderBackendIndex = GetAndroidRenderBackendIndex(backendMode);
        _viewModel.SharpPixelsEnabled = settings.SharpPixelsEnabled;
        _viewModel.ScanlineStrengthPercent = ClampPercent(settings.ScanlineStrengthPercent);
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

    private void ApplyOverlayInput(IEmulatorCore core)
    {
        lock (_inputSync)
        {
            PadType padType = core is MdTracerAdapter or EutherDrive.Core.SegaCd.SegaCdAdapter
                ? PadType.SixButton
                : PadType.ThreeButton;
            bool throttlePsxJoystickDirections = core is PsxAdapter;

            if (throttlePsxJoystickDirections)
            {
                StepJoystickDirectionPulseFramesLocked();
            }
            else if (_joystickDirectionPulseFrames.Count != 0)
            {
                _joystickDirectionPulseFrames.Clear();
            }

            var input = new ExtendedInputState(
                Up: IsDirectionActiveLocked("Up", throttlePsxJoystickDirections),
                Down: IsDirectionActiveLocked("Down", throttlePsxJoystickDirections),
                Left: IsDirectionActiveLocked("Left", throttlePsxJoystickDirections),
                Right: IsDirectionActiveLocked("Right", throttlePsxJoystickDirections),
                South: IsActionActiveLocked("A"),
                East: IsActionActiveLocked("B"),
                West: IsActionActiveLocked("Y"),
                North: IsActionActiveLocked("X"),
                Start: IsActionActiveLocked("Start"),
                Select: IsActionActiveLocked("Select"),
                Menu: IsActionActiveLocked("Menu"),
                L1: IsActionActiveLocked("L1"),
                L2: IsActionActiveLocked("L2"),
                R1: IsActionActiveLocked("R1"),
                R2: IsActionActiveLocked("R2"),
                PadType: padType);

            if (core is IExtendedInputHandler extendedInputHandler)
            {
                extendedInputHandler.SetExtendedInputState(input);
            }
            else
            {
                core.SetInputState(
                    input.Up,
                    input.Down,
                    input.Left,
                    input.Right,
                    input.South,
                    input.East,
                    input.West,
                    input.Start,
                    input.North,
                    input.L1,
                    input.R1,
                    input.Select || input.Menu,
                    input.PadType);
            }

            AdvanceLatchFrames(_directionLatchFrames, GetCombinedPressedDirectionsLocked());
            AdvanceLatchFrames(_actionLatchFrames, GetCombinedPressedActionsLocked());
        }
    }

    private void StepJoystickDirectionPulseFramesLocked()
    {
        foreach (string tag in new[] { "Up", "Down", "Left", "Right" })
        {
            if (TryGetJoystickOnlyDirectionStrengthLocked(tag, out _))
            {
                if (_joystickDirectionPulseFrames.TryGetValue(tag, out int frames))
                {
                    _joystickDirectionPulseFrames[tag] = frames + 1;
                }
                else
                {
                    _joystickDirectionPulseFrames[tag] = 0;
                }
            }
            else
            {
                _joystickDirectionPulseFrames.Remove(tag);
            }
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

    private bool IsDirectionActiveLocked(string tag, bool throttleJoystickDirections = false)
    {
        if (_pressedDirections.Contains(tag) || _nativePressedDirections.Contains(tag))
        {
            if (!throttleJoystickDirections)
            {
                return true;
            }

            if (TryGetJoystickOnlyDirectionStrengthLocked(tag, out double strength)
                && _joystickDirectionPulseFrames.TryGetValue(tag, out int pulseFrame))
            {
                return IsJoystickDirectionPulseActive(strength, pulseFrame);
            }

            return true;
        }

        return _directionLatchFrames.TryGetValue(tag, out int frames) && frames > 0;
    }

    private bool IsActionActiveLocked(string tag)
    {
        return _pressedActions.Contains(tag)
            || _nativePressedActions.Contains(tag)
            || (_actionLatchFrames.TryGetValue(tag, out int frames) && frames > 0);
    }

    private HashSet<string> GetCombinedPressedDirectionsLocked()
    {
        if (_nativePressedDirections.Count == 0)
            return _pressedDirections;

        var combined = new HashSet<string>(_pressedDirections, StringComparer.OrdinalIgnoreCase);
        combined.UnionWith(_nativePressedDirections);
        return combined;
    }

    private HashSet<string> GetCombinedPressedActionsLocked()
    {
        if (_nativePressedActions.Count == 0)
            return _pressedActions;

        var combined = new HashSet<string>(_pressedActions, StringComparer.OrdinalIgnoreCase);
        combined.UnionWith(_nativePressedActions);
        return combined;
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

    private bool TryGetJoystickOnlyDirectionStrengthLocked(string tag, out double strength)
    {
        strength = 0;
        if (!_directionPressCounts.TryGetValue(tag, out int totalCount) || totalCount <= 0)
        {
            return false;
        }

        int joystickCount = 0;
        foreach ((IPointer pointer, HashSet<string> directions) in _dpadPointerDirections)
        {
            if (!directions.Contains(tag))
            {
                continue;
            }

            joystickCount++;
            if (_dpadPointerIntensity.TryGetValue(pointer, out double pointerStrength) && pointerStrength > strength)
            {
                strength = pointerStrength;
            }
        }

        if (joystickCount <= 0 || joystickCount != totalCount)
        {
            return false;
        }

        if (strength <= 0)
        {
            strength = 1;
        }

        return true;
    }

    private static bool IsJoystickDirectionPulseActive(double strength, int pulseFrame)
    {
        if (strength >= 0.90)
        {
            return true;
        }

        if (strength >= 0.74)
        {
            return pulseFrame % 3 != 2;
        }

        if (strength >= 0.56)
        {
            return pulseFrame % 2 == 0;
        }

        if (strength >= 0.40)
        {
            return pulseFrame % 3 == 0;
        }

        return pulseFrame % 4 == 0;
    }

    private string FormatActiveActionsLocked()
    {
        var active = new List<string>(12);
        foreach (string tag in new[] { "A", "B", "X", "Y", "L1", "L2", "R1", "R2", "Start", "Select", "Menu" })
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
        if (core is PsxAdapter psx && psx.TrySwapPresentationBuffer(ref _captureFrameBuffer, out int swapWidth, out int swapHeight, out int swapStride, out double swapPresentationWidth, out double swapPresentationHeight, out PsxAdapter.PresentationFrameInfo psxFrameInfo))
        {
            lock (_frameSync)
            {
                _lastFrameWidth = swapWidth;
                _lastFrameHeight = swapHeight;
                _lastFrameStride = swapStride;
                _lastFrameIsPsx = true;
                _lastPsxInterlaceBlend = false;
                _lastPsxInterlaceFieldParity = -1;
                _lastPsxFrameInfo = psxFrameInfo;
                _lastPresentationWidth = swapPresentationWidth;
                _lastPresentationHeight = swapPresentationHeight;
                (_captureFrameBuffer, _latestFrameBuffer) = (_latestFrameBuffer, _captureFrameBuffer);
                _emulatedFrames++;
                _latestFrameSerial++;
            }

            QueuePresentLatestFrame();
            return;
        }

        _psxInterlaceReconstructor.Reset();

        if (core is SnesAdapter snes && snes.TrySwapPresentationBuffer(ref _captureFrameBuffer, out int snesWidth, out int snesHeight, out int snesStride))
        {
            _psxInterlaceReconstructor.Reset();
            lock (_frameSync)
            {
                _lastFrameWidth = snesWidth;
                _lastFrameHeight = snesHeight;
                _lastFrameStride = snesStride;
                _lastFrameIsPsx = false;
                _lastPsxInterlaceBlend = false;
                _lastPsxInterlaceFieldParity = -1;
                _lastPsxFrameInfo = default;
                _lastPresentationWidth = 0;
                _lastPresentationHeight = 0;
                (_captureFrameBuffer, _latestFrameBuffer) = (_latestFrameBuffer, _captureFrameBuffer);
                _emulatedFrames++;
                _latestFrameSerial++;
            }

            QueuePresentLatestFrame();
            return;
        }

        if (core is MdTracerAdapter md && md.TrySwapPresentationBuffer(ref _captureFrameBuffer, out int mdWidth, out int mdHeight, out int mdStride))
        {
            _psxInterlaceReconstructor.Reset();
            lock (_frameSync)
            {
                _lastFrameWidth = mdWidth;
                _lastFrameHeight = mdHeight;
                _lastFrameStride = mdStride;
                _lastFrameIsPsx = false;
                _lastPsxInterlaceBlend = false;
                _lastPsxInterlaceFieldParity = -1;
                _lastPsxFrameInfo = default;
                _lastPresentationWidth = 0;
                _lastPresentationHeight = 0;
                (_captureFrameBuffer, _latestFrameBuffer) = (_latestFrameBuffer, _captureFrameBuffer);
                _emulatedFrames++;
                _latestFrameSerial++;
            }

            QueuePresentLatestFrame();
            return;
        }

        var src = core.GetFrameBuffer(out int width, out int height, out int srcStride);
        if (src.IsEmpty || width <= 0 || height <= 0 || srcStride <= 0)
        {
            return;
        }

        int dstStride = width * 4;
        int rowBytes = Math.Min(dstStride, srcStride);
        if (rowBytes <= 0)
        {
            return;
        }

        int requiredBytes = dstStride * height;
        EnsureFrameBufferCapacity(ref _captureFrameBuffer, requiredBytes);

        fixed (byte* srcPtr = src)
        fixed (byte* dstPtr = _captureFrameBuffer)
        {
            CopyFrameRows(srcPtr, srcStride, dstPtr, dstStride, height, rowBytes, clearDestinationTail: true);
        }

        lock (_frameSync)
        {
            _lastFrameWidth = width;
            _lastFrameHeight = height;
            _lastFrameStride = dstStride;
            _lastFrameIsPsx = false;
            _lastPsxInterlaceBlend = false;
            _lastPsxInterlaceFieldParity = -1;
            _lastPsxFrameInfo = default;
            _lastPresentationWidth = 0;
            _lastPresentationHeight = 0;
            (_captureFrameBuffer, _latestFrameBuffer) = (_latestFrameBuffer, _captureFrameBuffer);
            _emulatedFrames++;
            _latestFrameSerial++;
        }

        QueuePresentLatestFrame();
    }

    private static void EnsureFrameBufferCapacity(ref byte[] buffer, int requiredBytes)
    {
        if (buffer.Length < requiredBytes)
        {
            buffer = new byte[requiredBytes];
        }
    }

    private static unsafe void CopyFrameRows(byte* srcPtr, int srcStride, byte* dstPtr, int dstStride, int height, int rowBytes, bool clearDestinationTail)
    {
        if (height <= 0 || rowBytes <= 0)
        {
            return;
        }

        if (rowBytes == srcStride && rowBytes == dstStride)
        {
            long totalBytes = (long)rowBytes * height;
            Buffer.MemoryCopy(srcPtr, dstPtr, totalBytes, totalBytes);
            return;
        }

        for (int y = 0; y < height; y++)
        {
            byte* srcRow = srcPtr + (y * srcStride);
            byte* dstRow = dstPtr + (y * dstStride);
            Buffer.MemoryCopy(srcRow, dstRow, dstStride, rowBytes);

            if (clearDestinationTail && rowBytes < dstStride)
            {
                new Span<byte>(dstRow + rowBytes, dstStride - rowBytes).Clear();
            }
        }
    }

    private IGameRenderSurface CreateRenderSurface()
    {
        AndroidRenderBackendMode mode = AndroidRenderBackendConfig.StartupMode;

        if (mode == AndroidRenderBackendMode.VulkanBitmap)
            return new WriteableBitmapRenderSurface();

        if (ShouldUseAndroidNativeGlSurface())
        {
            var nativeSurface = new AndroidNativeGlRenderSurface();
            nativeSurface.SetOverlayInputCallbacks(SetNativeActionState, SetNativeDirections);
            nativeSurface.SetScreenTapCallback(OnNativeScreenTapped);
            nativeSurface.SetLandscapeOverlayEnabled(_viewModel.IsLandscapeMode);
            return nativeSurface;
        }

        return new OpenGlRenderSurface();
    }

    private static bool ShouldUseAndroidNativeGlSurface()
    {
        string? raw = Environment.GetEnvironmentVariable("EUTHERDRIVE_ANDROID_NATIVE_GL");
        if (string.IsNullOrWhiteSpace(raw))
            return true;

        raw = raw.Trim();
        if (raw == "0"
            || raw.Equals("false", StringComparison.OrdinalIgnoreCase)
            || raw.Equals("no", StringComparison.OrdinalIgnoreCase)
            || raw.Equals("off", StringComparison.OrdinalIgnoreCase)) {
            return false;
        }

        return true;
    }
    private void UpdateActiveRenderModeLabel()
    {
        AndroidRenderBackendMode startupMode = AndroidRenderBackendConfig.StartupMode;
        string nextLabel = _renderSurface switch
        {
            WriteableBitmapRenderSurface when startupMode == AndroidRenderBackendMode.VulkanBitmap
                => $"Active: {AndroidRenderBackendConfig.GetDisplayName(startupMode)}",
            WriteableBitmapRenderSurface => "Active: Bitmap fallback",
            AndroidNativeGlRenderSurface => $"Active: {AndroidRenderBackendConfig.GetDisplayName(startupMode)} / Native GL",
            OpenGlRenderSurface => $"Active: {AndroidRenderBackendConfig.GetDisplayName(startupMode)} / Shared GL",
            _ => $"Active: {AndroidRenderBackendConfig.GetDisplayName(startupMode)}"
        };

        if (!string.Equals(_viewModel.ActiveRenderModeLabel, nextLabel, StringComparison.Ordinal))
        {
            _viewModel.ActiveRenderModeLabel = nextLabel;
            UpdateLandscapeNativeOverlayVisibility();
        }
    }

    private void UpdateLandscapeNativeOverlayVisibility()
    {
        if (_renderSurface is AndroidNativeGlRenderSurface nativeSurface)
            nativeSurface.SetLandscapeOverlayEnabled(_viewModel.IsLandscapeMode);

        bool useNativeOverlay = _viewModel.IsLandscapeMode && _renderSurface is AndroidNativeGlRenderSurface;
        LandscapeTopLeftControls.IsVisible = !useNativeOverlay;
        LandscapeTopRightControls.IsVisible = !useNativeOverlay;
        LandscapeDPadSurface.IsVisible = !useNativeOverlay;
        LandscapeFaceSurface.IsVisible = !useNativeOverlay;
    }

    private void UpdateNativeRenderSurfaceVisibility()
    {
        if (_renderSurface is AndroidNativeGlRenderSurface nativeSurface)
            nativeSurface.SetHostVisible(_viewModel.MainShellVisible && !_viewModel.SettingsVisible);
    }

    private bool TryGetRenderSurfaceFallbackReason(out string reason)
    {
        switch (_renderSurface)
        {
            case AndroidNativeGlRenderSurface nativeGlSurface when nativeGlSurface.ShouldFallbackToBitmap(out reason):
                return true;
            case OpenGlRenderSurface glSurface when glSurface.ShouldFallbackToBitmap(out reason):
                return true;
            default:
                reason = string.Empty;
                return false;
        }
    }

    private bool TryGetRenderSurfaceDebugSummary(out string summary)
    {
        switch (_renderSurface)
        {
            case AndroidNativeGlRenderSurface nativeGlSurface when nativeGlSurface.TryGetDebugSummary(out summary):
                return true;
            case OpenGlRenderSurface glSurface when glSurface.TryGetDebugSummary(out summary):
                return true;
            default:
                if (!string.IsNullOrEmpty(_lastRenderSurfaceDebugSummary))
                {
                    summary = _lastRenderSurfaceDebugSummary;
                    if (!string.IsNullOrEmpty(_lastRenderSurfaceFallbackReason))
                        summary = $"{summary}\nGL Fallback:{_lastRenderSurfaceFallbackReason}";
                    return true;
                }

                summary = string.Empty;
                return false;
        }
    }

    private void EnsureRenderSurface(int width, int height)
    {
        if (_renderSurface == null)
        {
            _renderSurface = CreateRenderSurface();
            _lastRenderSurfaceDebugSummary = string.Empty;
            _lastRenderSurfaceFallbackReason = string.Empty;
        }
        UpdateActiveRenderModeLabel();
        UpdateNativeRenderSurfaceVisibility();
        if (_renderSurface is AndroidNativeGlRenderSurface nativeSurface)
        {
            double targetFps = _core != null ? GetLiveTargetFps(_core) : 0;
            nativeSurface.SetPreferredFrameRate(targetFps);
        }
        bool sizeChanged = _renderSurface.EnsureSize(width, height);
        bool hostMismatch = !IsRenderSurfaceAttachedToExpectedHost();
        bool shouldAttach = hostMismatch || (sizeChanged && _renderSurface is not AndroidNativeGlRenderSurface);
        if (shouldAttach)
            AttachRenderSurfaceToActiveHost();
        ApplySharpPixelsSetting();
    }

    private void ApplyPresentationSizeForCore(IEmulatorCore? core, int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            return;
        }

        double targetWidth = _lastPresentationWidth > 0 ? _lastPresentationWidth : width;
        double targetHeight = _lastPresentationHeight > 0 ? _lastPresentationHeight : height;

        if (_lastPresentationWidth <= 0 && _lastPresentationHeight <= 0
            && core is PsxAdapter psx
            && psx.TryGetPresentationSize(out double adapterWidth, out double adapterHeight))
        {
            targetWidth = adapterWidth;
            targetHeight = adapterHeight;
        }

        bool isLandscape = _viewModel.IsLandscapeMode;
        Control targetHost = isLandscape ? LandscapeScreenHost : PortraitScreenHost;
        if (_renderSurface is AndroidNativeGlRenderSurface nativeSurface)
        {
            nativeSurface.SetPresentationSize(targetWidth, targetHeight);
            if (targetHost.Parent is Control nativeParent)
            {
                ApplyPresentationSize(targetHost, nativeParent.Bounds.Width, nativeParent.Bounds.Height);
                _appliedPresentationWidth = nativeParent.Bounds.Width;
                _appliedPresentationHeight = nativeParent.Bounds.Height;
                _appliedPresentationLandscape = isLandscape;
                return;
            }
        }

        (double appliedWidth, double appliedHeight) = ComputePixelPerfectPresentationSize(targetHost, targetWidth, targetHeight);
        if (_appliedPresentationLandscape == isLandscape
            && !double.IsNaN(_appliedPresentationWidth)
            && !double.IsNaN(_appliedPresentationHeight)
            && Math.Abs(_appliedPresentationWidth - appliedWidth) <= 0.5
            && Math.Abs(_appliedPresentationHeight - appliedHeight) <= 0.5)
        {
            return;
        }

        ApplyPresentationSize(targetHost, appliedWidth, appliedHeight);

        _appliedPresentationWidth = appliedWidth;
        _appliedPresentationHeight = appliedHeight;
        _appliedPresentationLandscape = isLandscape;
    }

    private (double width, double height) ComputePixelPerfectPresentationSize(Control host, double sourceWidth, double sourceHeight)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0)
            return (sourceWidth, sourceHeight);

        if (host.Parent is not Control parent)
            return (sourceWidth, sourceHeight);

        double availableWidth = parent.Bounds.Width;
        double availableHeight = parent.Bounds.Height;
        if (availableWidth <= 0 || availableHeight <= 0)
            return (sourceWidth, sourceHeight);

        double renderScale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        if (renderScale <= 0)
            renderScale = 1.0;

        double availablePixelWidth = availableWidth * renderScale;
        double availablePixelHeight = availableHeight * renderScale;
        double fitScale = Math.Min(
            availablePixelWidth / sourceWidth,
            availablePixelHeight / sourceHeight);
        int integerScale = (int)Math.Floor(fitScale);

        if (_viewModel.IsLandscapeMode)
        {
            // Landscape should use as much of the playfield as possible because the touch
            // controls are translucent overlays. On larger screens, snap back to an integer
            // multiple when we are already very close so pixels stay perfectly even there.
            if (integerScale >= 1 && fitScale - integerScale <= LandscapeIntegerSnapThreshold)
            {
                return (
                    (sourceWidth * integerScale) / renderScale,
                    (sourceHeight * integerScale) / renderScale);
            }

            if (fitScale > 0)
            {
                return (
                    (sourceWidth * fitScale) / renderScale,
                    (sourceHeight * fitScale) / renderScale);
            }

            return (sourceWidth, sourceHeight);
        }

        if (integerScale >= 1)
        {
            return (
                (sourceWidth * integerScale) / renderScale,
                (sourceHeight * integerScale) / renderScale);
        }

        if (fitScale <= 0)
            return (sourceWidth, sourceHeight);

        return (
            (sourceWidth * fitScale) / renderScale,
            (sourceHeight * fitScale) / renderScale);
    }

    private static void ApplyPresentationSize(Control control, double width, double height)
    {
        if (double.IsNaN(control.Width) || Math.Abs(control.Width - width) > 0.5)
        {
            control.Width = width;
        }

        if (double.IsNaN(control.Height) || Math.Abs(control.Height - height) > 0.5)
        {
            control.Height = height;
        }
    }

    private bool IsRenderSurfaceAttachedToExpectedHost()
    {
        if (_renderSurface == null)
            return false;

        Panel expectedHost = _viewModel.IsLandscapeMode ? LandscapeScreenHost : PortraitScreenHost;
        return ReferenceEquals(_renderSurface.View.Parent, expectedHost) && expectedHost.Children.Contains(_renderSurface.View);
    }

    private void AttachRenderSurfaceToActiveHost()
    {
        if (_renderSurface == null)
            return;

        Panel targetHost = _viewModel.IsLandscapeMode ? LandscapeScreenHost : PortraitScreenHost;
        Panel inactiveHost = _viewModel.IsLandscapeMode ? PortraitScreenHost : LandscapeScreenHost;
        Control view = _renderSurface.View;

        if (view.Parent is Panel existingParent && !ReferenceEquals(existingParent, targetHost))
            existingParent.Children.Remove(view);

        if (inactiveHost.Children.Contains(view))
            inactiveHost.Children.Remove(view);

        if (!targetHost.Children.Contains(view))
        {
            targetHost.Children.Clear();
            targetHost.Children.Add(view);
        }

        view.Width = double.NaN;
        view.Height = double.NaN;
    }

    private void DetachRenderSurfaceFromHosts()
    {
        if (_renderSurface?.View.Parent is Panel parent)
            parent.Children.Remove(_renderSurface.View);

        PortraitScreenHost.Children.Clear();
        LandscapeScreenHost.Children.Clear();
    }

    private void InvalidateScreenImages()
    {
        if (_renderSurface is AndroidNativeGlRenderSurface)
            return;

        _renderSurface?.View.InvalidateVisual();
    }

    private void SetNativeActionState(string tag, bool pressed)
    {
        lock (_inputSync)
        {
            if (pressed)
            {
                _nativePressedActions.Add(tag);
                _viewModel.SetLastPressed(tag);
            }
            else
            {
                _nativePressedActions.Remove(tag);
            }

            _actionLatchFrames[tag] = InputLatchFrames;
            UpdateFaceVisualsLocked();
        }

        UpdateOverlaySummary();
    }

    private void SetNativeDirections(IReadOnlyCollection<string> directions)
    {
        lock (_inputSync)
        {
            foreach (string released in _nativePressedDirections.Except(directions).ToArray())
                _directionLatchFrames[released] = InputLatchFrames;

            _nativePressedDirections.Clear();
            foreach (string direction in directions)
            {
                _nativePressedDirections.Add(direction);
                _directionLatchFrames[direction] = InputLatchFrames;
            }

            if (directions.Count > 0)
                _viewModel.SetLastPressed(string.Join(" ", directions.OrderBy(static value => value)));

            UpdateDPadVisualsLocked();
        }

        UpdateOverlaySummary();
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
        if (ext == ".cue")
        {
            throw new InvalidOperationException("Cue disc type could not be detected. Check that the selected cue bundle resolves to a valid PSX or PCE data track.");
        }

        if (ext == ".pce")
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

    private static (IEmulatorCore Core, string ConsoleLabel) LoadCoreForRom(string romPath)
    {
        IEmulatorCore core = CreateCoreForRom(romPath);
        try
        {
            if (core is MdTracerAdapter md)
            {
                md.PowerCycleAndLoadRom(romPath);
                md.SetMasterVolumePercent(100);
            }
            else
            {
                core.LoadRom(romPath);
                SetDefaultCoreVolume(core);
            }

            return (core, GetConsoleLabelForCore(core, romPath));
        }
        catch
        {
            if (core is IDisposable disposable)
            {
                disposable.Dispose();
            }

            throw;
        }
    }

    private static void SetDefaultCoreVolume(IEmulatorCore core)
    {
        switch (core)
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

    private static string GetConsoleLabelForCore(IEmulatorCore core, string romPath)
    {
        return core switch
        {
            PsxAdapter => "PlayStation",
            EutherDrive.Core.SegaCd.SegaCdAdapter => "Sega CD",
            PceCdAdapter => "PC Engine CD",
            N64Adapter => "Nintendo 64",
            SnesAdapter => "SNES",
            NesAdapter => "NES",
            MdTracerAdapter => GuessConsoleLabelFromPath(romPath),
            _ => GuessConsoleLabelFromPath(romPath)
        };
    }

    private static string GuessConsoleLabelFromPath(string romPath)
    {
        OpticalDiscKind discKind = OpticalDiscDetector.Detect(romPath);
        return discKind switch
        {
            OpticalDiscKind.Psx => "PlayStation",
            OpticalDiscKind.SegaCd => "Sega CD",
            OpticalDiscKind.PceCd => "PC Engine CD",
            _ => MainViewModel.GuessConsole(romPath)
        };
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
        private string _perfHeadline = "FPS --  MAX --";
        private string _activeRenderModeLabel = $"Active: {AndroidRenderBackendConfig.GetDisplayName(AndroidRenderBackendConfig.StartupMode)}";
        private string _overlaySummary = "D:-  A:-";
        private bool _isFocusMode;
        private bool _isLandscapeMode;
        private bool _settingsVisible;
        private bool _debugVisible;
        private int _debugPageIndex;
        private bool _mainShellVisible = true;
        private bool _debugGamePageVisible;
        private bool _debugTracePageVisible;
        private bool _normalTopButtonsVisible = true;
        private bool _quickSaveButtonsVisible;
        private bool _screenHudVisible = true;
        private Thickness _rootMargin = new(14);
        private double _rootRowSpacing = 12;
        private Thickness _screenSurfacePadding = new(10);
        private Thickness _screenFrameMargin = new(6);
        private Thickness _screenContentMargin = new(12);
        private string _settingsHint = "Pick BIOS and chip ROMs here.";
        private string _pceBiosDisplay = "(auto)";
        private string _segaCdBiosDisplay = "(none)";
        private string _psxBiosDisplay = "(none)";
        private bool _psxAnalogControllerEnabled;
        private bool _psxFastLoadEnabled;
        private bool _psxSuperFastBootEnabled;
        private int _psxVideoStandardIndex;
        private int _psxFrameRateIndex;
        private int _androidRenderBackendIndex = GetAndroidRenderBackendIndex(AndroidRenderBackendConfig.ResolvePreferredMode());
        private string _androidRenderBackendStatus = $"Active on this boot: {AndroidRenderBackendConfig.GetDisplayName(AndroidRenderBackendConfig.StartupMode)}";
        private bool _sharpPixelsEnabled = true;
        private int _scanlineStrengthPercent = DefaultScanlineStrengthPercent;
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
        private string? _segaCdBiosPath;
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

        public string PerfHeadline
        {
            get => _perfHeadline;
            set => SetField(ref _perfHeadline, value);
        }

        public string ActiveRenderModeLabel
        {
            get => _activeRenderModeLabel;
            set => SetField(ref _activeRenderModeLabel, value);
        }

        public string OverlaySummary
        {
            get => _overlaySummary;
            set => SetField(ref _overlaySummary, value);
        }

        public bool IsFocusMode
        {
            get => _isFocusMode;
            set
            {
                if (!SetField(ref _isFocusMode, value))
                {
                    return;
                }

                NormalTopButtonsVisible = !value;
                QuickSaveButtonsVisible = value;
                ScreenHudVisible = !value;
                ScreenSurfacePadding = value ? new Thickness(0) : new Thickness(10);
                ScreenFrameMargin = value ? new Thickness(0) : new Thickness(6);
                ScreenContentMargin = value ? new Thickness(2) : new Thickness(12);
            }
        }

        public bool IsLandscapeMode
        {
            get => _isLandscapeMode;
            set
            {
                if (!SetField(ref _isLandscapeMode, value))
                {
                    return;
                }

                RootMargin = value ? new Thickness(0) : new Thickness(14);
                RootRowSpacing = value ? 0 : 12;
                OnPropertyChanged(nameof(IsPortraitMode));
            }
        }

        public bool IsPortraitMode => !_isLandscapeMode;

        public Thickness RootMargin
        {
            get => _rootMargin;
            set => SetField(ref _rootMargin, value);
        }

        public double RootRowSpacing
        {
            get => _rootRowSpacing;
            set => SetField(ref _rootRowSpacing, value);
        }

        public bool SettingsVisible
        {
            get => _settingsVisible;
            set => SetField(ref _settingsVisible, value);
        }

        public bool DebugVisible
        {
            get => _debugVisible;
            set
            {
                if (!SetField(ref _debugVisible, value))
                {
                    return;
                }

                UpdateDebugLayoutState();
            }
        }

        public int DebugPageIndex
        {
            get => _debugPageIndex;
            set
            {
                int normalized = value <= 0 ? 0 : 1;
                if (!SetField(ref _debugPageIndex, normalized))
                {
                    return;
                }

                UpdateDebugLayoutState();
            }
        }

        public bool MainShellVisible
        {
            get => _mainShellVisible;
            set => SetField(ref _mainShellVisible, value);
        }

        public bool DebugGamePageVisible
        {
            get => _debugGamePageVisible;
            set => SetField(ref _debugGamePageVisible, value);
        }

        public bool DebugTracePageVisible
        {
            get => _debugTracePageVisible;
            set => SetField(ref _debugTracePageVisible, value);
        }

        public bool NormalTopButtonsVisible
        {
            get => _normalTopButtonsVisible;
            set => SetField(ref _normalTopButtonsVisible, value);
        }

        public bool QuickSaveButtonsVisible
        {
            get => _quickSaveButtonsVisible;
            set => SetField(ref _quickSaveButtonsVisible, value);
        }

        public bool ScreenHudVisible
        {
            get => _screenHudVisible;
            set => SetField(ref _screenHudVisible, value);
        }

        public Thickness ScreenSurfacePadding
        {
            get => _screenSurfacePadding;
            set => SetField(ref _screenSurfacePadding, value);
        }

        public Thickness ScreenFrameMargin
        {
            get => _screenFrameMargin;
            set => SetField(ref _screenFrameMargin, value);
        }

        public Thickness ScreenContentMargin
        {
            get => _screenContentMargin;
            set => SetField(ref _screenContentMargin, value);
        }

        public string SettingsHint
        {
            get => _settingsHint;
            set => SetField(ref _settingsHint, value);
        }

        public string PceBiosDisplay { get => _pceBiosDisplay; set => SetField(ref _pceBiosDisplay, value); }
        public string SegaCdBiosDisplay { get => _segaCdBiosDisplay; set => SetField(ref _segaCdBiosDisplay, value); }
        public string PsxBiosDisplay { get => _psxBiosDisplay; set => SetField(ref _psxBiosDisplay, value); }
        public bool PsxAnalogControllerEnabled { get => _psxAnalogControllerEnabled; set => SetField(ref _psxAnalogControllerEnabled, value); }
        public bool PsxFastLoadEnabled { get => _psxFastLoadEnabled; set => SetField(ref _psxFastLoadEnabled, value); }
        public bool PsxSuperFastBootEnabled { get => _psxSuperFastBootEnabled; set => SetField(ref _psxSuperFastBootEnabled, value); }
        public int PsxVideoStandardIndex { get => _psxVideoStandardIndex; set => SetField(ref _psxVideoStandardIndex, Math.Clamp(value, 0, 2)); }
        public int PsxFrameRateIndex { get => _psxFrameRateIndex; set => SetField(ref _psxFrameRateIndex, Math.Clamp(value, 0, 2)); }
        public int AndroidRenderBackendIndex { get => _androidRenderBackendIndex; set => SetField(ref _androidRenderBackendIndex, Math.Clamp(value, 0, 2)); }
        public string AndroidRenderBackendStatus { get => _androidRenderBackendStatus; set => SetField(ref _androidRenderBackendStatus, value); }
        public bool SharpPixelsEnabled { get => _sharpPixelsEnabled; set => SetField(ref _sharpPixelsEnabled, value); }
        public int ScanlineStrengthPercent
        {
            get => _scanlineStrengthPercent;
            set
            {
                int normalized = ClampPercent(value);
                if (!SetField(ref _scanlineStrengthPercent, normalized))
                {
                    return;
                }

                OnPropertyChanged(nameof(ScanlineStrengthDisplay));
            }
        }
        public string ScanlineStrengthDisplay => $"{_scanlineStrengthPercent}%";
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

        private void UpdateDebugLayoutState()
        {
            MainShellVisible = !_debugVisible || _debugPageIndex == 0;
            DebugGamePageVisible = _debugVisible && _debugPageIndex == 0;
            DebugTracePageVisible = _debugVisible && _debugPageIndex == 1;
        }

        public string? PceBiosPath { get => _pceBiosPath; set => SetField(ref _pceBiosPath, value); }
        public string? SegaCdBiosPath { get => _segaCdBiosPath; set => SetField(ref _segaCdBiosPath, value); }
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
                case "SEGA CD BIOS":
                    SegaCdBiosPath = path;
                    SegaCdBiosDisplay = label;
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

        internal static string GuessConsole(string romPath)
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
        public string? SegaCdBiosPath { get; set; }
        public string? SegaCdBiosDisplay { get; set; }
        public string? PsxBiosPath { get; set; }
        public string? PsxBiosDisplay { get; set; }
        public bool PsxAnalogControllerEnabled { get; set; }
        public bool PsxFastLoadEnabled { get; set; }
        public bool PsxSuperFastBootEnabled { get; set; }
        public int PsxVideoStandardIndex { get; set; }
        public int PsxFrameRateIndex { get; set; }
        public string? AndroidRenderBackendMode { get; set; }
        public bool SharpPixelsEnabled { get; set; } = true;
        public int ScanlineStrengthPercent { get; set; } = DefaultScanlineStrengthPercent;
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

    private sealed record VirtualRomImport(string PrimaryPath, IReadOnlyList<string> RegisteredPaths);
}
