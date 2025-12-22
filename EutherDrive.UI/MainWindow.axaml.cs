using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using EutherDrive.Core;

namespace EutherDrive.UI;

public partial class MainWindow : Window
{
    private IEmulatorCore? _core;

    // EN bitmap som vi alltid blitar till
    private WriteableBitmap? _wb;

    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _fpsSw = Stopwatch.StartNew();
    private int _frames;
    private long _lastStatusUpdateMs;
    private string _lastStatusKeys = string.Empty;

    private string? _romPath;

    // Input “håll nere”
    private readonly HashSet<Key> _keysDown = new();

    public MainWindow()
    {
        InitializeComponent();

        HookInput();

        Focusable = true;
        AttachedToVisualTree += (_, __) => Focus();

        StatusText.Text = "Idle";

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16.666) };
        _timer.Tick += (_, _) => Tick();
    }

    private void HookInput()
    {
        Focusable = true;

        KeyDown += (_, e) =>
        {
            _keysDown.Add(e.Key);
            e.Handled = true;
        };

        KeyUp += (_, e) =>
        {
            _keysDown.Remove(e.Key);
            e.Handled = true;
        };

        // klick var som helst => ta tillbaka fokus
        PointerPressed += (_, __) => Focus();
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
                    _core.LoadRom(_romPath);

                    if (_core is MdTracerAdapter m)
                    {
                        // Visa i UI direkt (snabbast att se)
                        RomInfoText.Text = m.RomInfo;

                        // OCH i terminal (om du kör från terminal)
                        Console.WriteLine(m.RomInfo);
                    }
                }
                else
                {
                    StatusText.Text = "No ROM selected";
                }

                // LoadRom() kallar Reset() redan i vår adapter.
                // Så du behöver inte _core.Reset() här.
            }



            // skapa bitmap utifrån core-storlek
            EnsureBitmapFromCore();

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
    }

    private void Tick()
    {
        if (_core == null)
            return;
        MaybeUpdateStatusText();

        // input → core
        ApplyInputToCore(_core);

        // emulera frame
        _core.RunFrame();

        // rendera frame
        RenderFrame(_core);

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

        core.SetInputState(up, down, left, right, a, b, c, start);

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
                                      PixelFormat.Rgba8888,
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

            for (int y = 0; y < h; y++)
            {
                byte* pSrcRow = pSrc0 + (y * srcStride);
                byte* pDstRow = pDst0 + (y * dstStride);
                Buffer.MemoryCopy(pSrcRow, pDstRow, dstStride, copyBytesPerRow);
            }
        }

        // VIKTIGT: tvinga repaint
        ScreenImage.InvalidateVisual();
    }
}
