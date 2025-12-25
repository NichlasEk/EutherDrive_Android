using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
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
    private readonly Action _presentOnUiAction;
    private IEmulatorCore? _pendingPresentCore;

    private string? _romPath;

    // Input “håll nere”
    private readonly HashSet<Key> _keysDown = new();
    private OpenAlAudioOutput? _audioOutput;
    private TextWriter? _originalConsoleOut;
    private StreamWriter? _romLogWriter;

    // UI heartbeat
    private readonly bool _heartbeatEnabled = Environment.GetEnvironmentVariable("EUTHERDRIVE_UI_HEARTBEAT") == "1";
    private DispatcherTimer? _heartbeatTimer;
    private int _heartbeatTicks;
    private int _tickTraceCount;
    private readonly bool _tickTraceEnabled = Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_TICK") == "1";
    private bool _heartbeatState;

    public MainWindow()
    {
        InitializeComponent();

        HookInput();

        Focusable = true;
        AttachedToVisualTree += (_, __) => Focus();

        StatusText.Text = "Idle";

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16.666) };
        _timer.Tick += (_, _) => Tick();
        _presentOnUiAction = PresentPendingFrame;
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
        }
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

    private void HookInput()
    {
        Focusable = true;

        KeyDown += (_, e) =>
        {
            if (e.Source is TextBox)
                return;

            _keysDown.Add(e.Key);
            e.Handled = true;
        };

        KeyUp += (_, e) =>
        {
            if (e.Source is TextBox)
                return;

            _keysDown.Remove(e.Key);
            e.Handled = true;
        };

        // klick var som helst (utom textfält) => ta tillbaka fokus
        PointerPressed += (_, args) =>
        {
            if (args.Source is TextBox)
                return;
            Focus();
        };
    }

    private async void OnOpenRom(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
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
        });

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
            _frames = 0;
            _fpsSw.Restart();
            _earlyMagentaTimer.Restart();
            _earlyMagentaReported = false;

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
                        RomInfoText.Text = m.RomInfo;

                        // OCH i terminal (om du kör från terminal)
                        Console.WriteLine(m.RomInfo);
                    }
                    else
                    {
                        _core.LoadRom(_romPath);
                    }
                }
                else
                {
                    StatusText.Text = "No ROM selected";
                }

                // LoadRom() kallar Reset() redan i vår adapter.
                // Så du behöver inte _core.Reset() här.
            }

            _audioOutput?.Dispose();
            _audioOutput = OpenAlAudioOutput.TryCreate();
            StatusText.Text = _audioOutput is null
                ? "Audio output unavailable"
                : "Audio output ready";



            // skapa bitmap utifrån core-storlek
            EnsureBitmapFromCore();
            StartHeartbeat();

            _timer.Start();
            Focus();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Start failed: {ex.Message}";
            Console.WriteLine(ex.ToString());
        }
    }

    private void OnStop(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _timer.Stop();
        StatusText.Text = "Stopped";
        if (_heartbeatTimer != null)
        {
            _heartbeatTimer.Stop();
            _heartbeatTimer = null;
        }
    }

    private void Tick()
    {
        if (_core == null)
            return;
        MaybeUpdateStatusText();

        // input → core
        ApplyInputToCore(_core);

        // emulera frame
        try
        {
            _core.RunFrame();
        }
        catch (Exception ex)
        {
            Console.WriteLine("[UI] RunFrame exception: " + ex);
            return;
        }

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

        SubmitAudio();

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

        string keys = _keysDown.Count == 0
            ? "-"
            : string.Join(", ", _keysDown.OrderBy(k => k.ToString()));

        if (keys == _lastStatusKeys && now - _lastStatusUpdateMs < 1000)
            return;

        _lastStatusUpdateMs = now;
        _lastStatusKeys = keys;
        StatusText.Text = $"Core: {_core.GetType().Name}  Keys: {keys}";
    }

    private void SubmitAudio()
    {
        if (_core is null || _audioOutput is null)
            return;

        var audio = _core.GetAudioBuffer(out int sampleRate, out int channels);
        if (audio.IsEmpty)
            return;

        _audioOutput.Submit(audio, sampleRate, channels);
    }

    private void ApplyInputToCore(IEmulatorCore core)
    {
        bool up    = _keysDown.Contains(Key.Up)    || _keysDown.Contains(Key.W);
        bool down  = _keysDown.Contains(Key.Down)  || _keysDown.Contains(Key.S);
        bool left  = _keysDown.Contains(Key.Left)  || _keysDown.Contains(Key.A);
        bool right = _keysDown.Contains(Key.Right) || _keysDown.Contains(Key.D);

        // Knappar: flera alternativ för att slippa layout-strul
        bool a = _keysDown.Contains(Key.Z) || _keysDown.Contains(Key.J) || _keysDown.Contains(Key.Q) || _keysDown.Contains(Key.Space);
        bool b = _keysDown.Contains(Key.X) || _keysDown.Contains(Key.K) || _keysDown.Contains(Key.E);
        bool c = _keysDown.Contains(Key.C) || _keysDown.Contains(Key.L) || _keysDown.Contains(Key.R);
        bool start = _keysDown.Contains(Key.Enter);
        bool x = _keysDown.Contains(Key.A);
        bool y = _keysDown.Contains(Key.S);
        bool z = _keysDown.Contains(Key.D);
        bool mode = _keysDown.Contains(Key.LeftShift) || _keysDown.Contains(Key.RightShift);
        PadType padType = ThreeButtonPadCheck?.IsChecked == true ? PadType.ThreeButton : PadType.SixButton;

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

        using var fb = _wb.Lock();
        int dstStride = fb.RowBytes;

        int copyBytesPerRow = Math.Min(w * 4, Math.Min(srcStride, dstStride));

        fixed (byte* pSrc0 = src)
        {
            byte* pDst0 = (byte*)fb.Address.ToPointer();

            if (FrameBufferTraceEnabled)
            {
                _presentedFrames++;
                Console.WriteLine($"[MainWindow] Present frame={_presentedFrames} srcPtr=0x{(nint)pSrc0:X} size={w}x{h} stride={srcStride} bytes={src.Length}");
            }

            for (int y = 0; y < h; y++)
            {
                byte* pSrcRow = pSrc0 + (y * srcStride);
                byte* pDstRow = pDst0 + (y * dstStride);
                Buffer.MemoryCopy(pSrcRow, pDstRow, dstStride, copyBytesPerRow);
            }
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
