using System;
using System.Buffers.Binary;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using EutherDrive.UI.Zuul;

namespace EutherDrive.Android.Zuul;

public sealed class ZuulView : Control
{
    private static readonly IBrush BackgroundBrush = Brushes.Black;
    private static readonly Pen GlowPen = new(new SolidColorBrush(Color.FromArgb(120, 255, 20, 20)), 3);
    private static readonly Pen CorePen = new(new SolidColorBrush(Color.FromArgb(220, 255, 80, 60)), 1.5);

    private readonly JoxRuntime _runtime = new();
    private readonly DispatcherTimer _timer = new();
    private bool _assetsLoaded;
    private bool _isAttached;
    private bool _isActive;
    private bool _dragging;
    private Point _lastPointerPos;
    private AndroidAudioSink? _audioSink;
    private bool _audioStarted;
    private short[] _roarSamples = Array.Empty<short>();
    private int _roarRate = 22050;
    private int _roarChannels = 1;

    public ZuulView()
    {
        _runtime.EventEmitted += HandleEvent;
        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;
        _timer.Tick += (_, _) =>
        {
            _runtime.Tick();
            InvalidateVisual();
        };
    }

    public void SetActive(bool isActive)
    {
        if (_isActive == isActive)
        {
            return;
        }

        _isActive = isActive;
        if (!_isAttached)
        {
            return;
        }

        if (isActive)
        {
            EnsureLoaded();
            StartTimer();
            return;
        }

        StopPlayback();
    }

    public void LoadDefault()
    {
        try
        {
            LoadEmbedded("zuul_svg.jox");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Android.ZuulView] failed to load default JOX: {ex.Message}");
        }
    }

    public void LoadEmbedded(string assetName)
    {
        try
        {
            var uri = new Uri($"avares://EutherDrive.Android/Assets/{assetName}");
            using Stream stream = AssetLoader.Open(uri);
            LoadFromStream(stream);
            EnsureRoarLoaded();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Android.ZuulView] failed to load embedded JOX '{assetName}': {ex.Message}");
        }
    }

    public void LoadFromStream(Stream stream)
    {
        var file = JoxFile.Load(stream);
        _runtime.Load(file);
        _assetsLoaded = true;
        if (_isActive && _isAttached)
        {
            StartTimer();
        }
        InvalidateVisual();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _isAttached = true;
        Focusable = true;
        EnsureLoaded();
        if (_isActive)
        {
            StartTimer();
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _isAttached = false;
        _dragging = false;
        StopPlayback();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        Rect bounds = new(Bounds.Size);
        context.FillRectangle(BackgroundBrush, bounds);

        double w = bounds.Width;
        double h = bounds.Height;
        if (w <= 0 || h <= 0)
        {
            return;
        }

        double scale = Math.Min(w, h) * 0.45;
        double cx = w * 0.5;
        double cy = h * 0.5;

        foreach (var line in _runtime.Lines)
        {
            Point a = new(cx + line.A.X * scale, cy + line.A.Y * scale);
            Point b = new(cx + line.B.X * scale, cy + line.B.Y * scale);
            context.DrawLine(GlowPen, a, b);
        }

        foreach (var line in _runtime.Lines)
        {
            Point a = new(cx + line.A.X * scale, cy + line.A.Y * scale);
            Point b = new(cx + line.B.X * scale, cy + line.B.Y * scale);
            context.DrawLine(CorePen, a, b);
        }
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!_isActive)
        {
            return;
        }

        _dragging = true;
        _lastPointerPos = e.GetPosition(this);
        e.Pointer.Capture(this);
        Focus();
        e.Handled = true;
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isActive || !_dragging)
        {
            return;
        }

        Point pos = e.GetPosition(this);
        double dx = pos.X - _lastPointerPos.X;
        double dy = pos.Y - _lastPointerPos.Y;
        _lastPointerPos = pos;

        _runtime.AddRotation((float)(dx * 0.01), (float)(dy * 0.01));
        InvalidateVisual();
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _dragging = false;
        if (e.Pointer.Captured == this)
        {
            e.Pointer.Capture(null);
        }
    }

    private void EnsureLoaded()
    {
        if (_assetsLoaded)
        {
            return;
        }

        LoadDefault();
    }

    private void StartTimer()
    {
        int tps = (int)Math.Clamp(_runtime.TicksPerSecond, 10, 240);
        _timer.Interval = TimeSpan.FromSeconds(1.0 / tps);
        if (!_timer.IsEnabled)
        {
            _timer.Start();
        }
    }

    private void StopPlayback()
    {
        _timer.Stop();
        _audioSink?.Dispose();
        _audioSink = null;
        _audioStarted = false;
    }

    private void HandleEvent(ushort eventId)
    {
        if (eventId == 1)
        {
            PlayRoar();
        }
    }

    private void EnsureRoarLoaded()
    {
        if (_roarSamples.Length != 0)
        {
            return;
        }

        try
        {
            var uri = new Uri("avares://EutherDrive.Android/Assets/jox.wav");
            using Stream stream = AssetLoader.Open(uri);
            LoadWav(stream, out _roarSamples, out _roarRate, out _roarChannels);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Android.ZuulView] failed to load jox.wav: {ex.Message}");
        }
    }

    private void PlayRoar()
    {
        EnsureRoarLoaded();
        if (_roarSamples.Length == 0)
        {
            return;
        }

        _audioSink ??= new AndroidAudioSink();
        if (!_audioStarted)
        {
            _audioSink.Start(_roarRate, _roarChannels);
            _audioStarted = true;
        }

        _audioSink.Submit(_roarSamples);
    }

    private static void LoadWav(Stream stream, out short[] samples, out int sampleRate, out int channels)
    {
        samples = Array.Empty<short>();
        sampleRate = 22050;
        channels = 1;

        using var br = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        Span<byte> id = stackalloc byte[4];
        if (br.Read(id) != 4 || id[0] != (byte)'R' || id[1] != (byte)'I' || id[2] != (byte)'F' || id[3] != (byte)'F')
        {
            throw new InvalidDataException("Not a RIFF file.");
        }

        br.ReadInt32();
        if (br.Read(id) != 4 || id[0] != (byte)'W' || id[1] != (byte)'A' || id[2] != (byte)'V' || id[3] != (byte)'E')
        {
            throw new InvalidDataException("Not a WAVE file.");
        }

        ushort fmt = 0;
        ushort bits = 0;
        int dataSize = 0;
        long dataPos = 0;

        while (stream.Position + 8 <= stream.Length)
        {
            if (br.Read(id) != 4)
            {
                break;
            }

            int size = br.ReadInt32();
            string chunk = System.Text.Encoding.ASCII.GetString(id);
            long next = stream.Position + size;
            if (chunk == "fmt ")
            {
                fmt = br.ReadUInt16();
                channels = br.ReadUInt16();
                sampleRate = br.ReadInt32();
                br.ReadInt32();
                br.ReadUInt16();
                bits = br.ReadUInt16();
            }
            else if (chunk == "data")
            {
                dataPos = stream.Position;
                dataSize = size;
            }

            stream.Position = next + (size % 2);
        }

        if (fmt != 1 || bits != 16 || dataSize == 0)
        {
            throw new InvalidDataException("Unsupported WAV (need PCM16).");
        }

        stream.Position = dataPos;
        byte[] raw = br.ReadBytes(dataSize);
        int count = raw.Length / 2;
        samples = new short[count];
        for (int i = 0; i < count; i++)
        {
            samples[i] = (short)BinaryPrimitives.ReadInt16LittleEndian(raw.AsSpan(i * 2, 2));
        }
    }
}
