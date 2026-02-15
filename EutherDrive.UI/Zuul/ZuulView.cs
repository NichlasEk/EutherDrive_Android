using System;
using System.Collections.Generic;
using System.IO;
using System.Buffers.Binary;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Input;
using Avalonia.Threading;
using EutherDrive.Audio;
using SdlApi = Silk.NET.SDL.Sdl;
using GameControllerAxis = Silk.NET.SDL.GameControllerAxis;
using GameControllerButton = Silk.NET.SDL.GameControllerButton;

namespace EutherDrive.UI.Zuul;

internal sealed class ZuulView : Control
{
    private readonly JoxRuntime _runtime = new();
    private readonly DispatcherTimer _timer = new();
    private bool _loaded;
    private Sdl2AudioSink? _audioSink;
    private short[] _roarSamples = Array.Empty<short>();
    private int _roarRate = 22050;
    private int _roarChannels = 1;
    private bool _dragging;
    private Point _lastPointerPos;
    private readonly HashSet<Key> _keysDown = new();
    private SdlApi? _sdl;
    private IntPtr _gamepad = IntPtr.Zero;
    private bool _sdlReady;

    public ZuulView()
    {
        _runtime.EventEmitted += HandleEvent;
        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;
        KeyDown += OnKeyDown;
        KeyUp += OnKeyUp;
        _timer.Tick += (_, _) =>
        {
            _runtime.Tick();
            ApplyInputRotation();
            InvalidateVisual();
        };
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _dragging = true;
        _lastPointerPos = e.GetPosition(this);
        e.Pointer.Capture(this);
        Focus();
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_dragging)
            return;
        Point pos = e.GetPosition(this);
        double dx = pos.X - _lastPointerPos.X;
        double dy = pos.Y - _lastPointerPos.Y;
        _lastPointerPos = pos;

        float yawDelta = (float)(dx * 0.01);
        float pitchDelta = (float)(dy * 0.01);
        _runtime.AddRotation(yawDelta, pitchDelta);
        InvalidateVisual();
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _dragging = false;
        if (e.Pointer.Captured == this)
            e.Pointer.Capture(null);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        _keysDown.Add(e.Key);
    }

    private void OnKeyUp(object? sender, KeyEventArgs e)
    {
        _keysDown.Remove(e.Key);
    }

    public void InjectKeyDown(Key key)
    {
        _keysDown.Add(key);
    }

    public void InjectKeyUp(Key key)
    {
        _keysDown.Remove(key);
    }

    public void LoadDefault()
    {
        try
        {
            LoadEmbedded("zuul_demo.jox");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ZuulView] failed to load default JOX: {ex.Message}");
        }
    }

    public void LoadEmbedded(string assetName)
    {
        try
        {
            var uri = new Uri($"avares://EutherDrive.UI/Assets/{assetName}");
            using Stream stream = AssetLoader.Open(uri);
            LoadFromStream(stream);
            EnsureRoarLoaded();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ZuulView] failed to load embedded JOX '{assetName}': {ex.Message}");
        }
    }

    public void LoadFromStream(Stream stream)
    {
        var file = JoxFile.Load(stream);
        _runtime.Load(file);
        StartTimer();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_loaded)
            return;
        _loaded = true;
        Focusable = true;
        InitGamepad();
        if (_runtime.Lines.Count == 0)
            LoadDefault();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _timer.Stop();
        _audioSink?.Dispose();
        _audioSink = null;
        CloseGamepad();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        Rect bounds = new Rect(Bounds.Size);
        context.FillRectangle(Brushes.Black, bounds);

        double w = bounds.Width;
        double h = bounds.Height;
        double scale = Math.Min(w, h) * 0.45;
        double cx = w * 0.5;
        double cy = h * 0.5;

        var glowPen = new Pen(new SolidColorBrush(Color.FromArgb(120, 255, 20, 20)), 3);
        var corePen = new Pen(new SolidColorBrush(Color.FromArgb(220, 255, 80, 60)), 1.5);

        foreach (var line in _runtime.Lines)
        {
            var a = new Point(cx + line.A.X * scale, cy + line.A.Y * scale);
            var b = new Point(cx + line.B.X * scale, cy + line.B.Y * scale);
            context.DrawLine(glowPen, a, b);
        }

        foreach (var line in _runtime.Lines)
        {
            var a = new Point(cx + line.A.X * scale, cy + line.A.Y * scale);
            var b = new Point(cx + line.B.X * scale, cy + line.B.Y * scale);
            context.DrawLine(corePen, a, b);
        }
    }

    private void StartTimer()
    {
        int tps = (int)Math.Clamp(_runtime.TicksPerSecond, 10, 240);
        _timer.Interval = TimeSpan.FromSeconds(1.0 / tps);
        if (!_timer.IsEnabled)
            _timer.Start();
    }

    private void ApplyInputRotation()
    {
        float yaw = 0f;
        float pitch = 0f;

        if (_keysDown.Contains(Key.Left))
            yaw -= 0.03f;
        if (_keysDown.Contains(Key.Right))
            yaw += 0.03f;
        if (_keysDown.Contains(Key.Up))
            pitch -= 0.03f;
        if (_keysDown.Contains(Key.Down))
            pitch += 0.03f;

        if (_sdlReady && _gamepad != IntPtr.Zero && _sdl != null)
        {
            unsafe
            {
                var controller = (Silk.NET.SDL.GameController*)_gamepad;
                float rx = NormalizeAxis(_sdl.GameControllerGetAxis(controller, GameControllerAxis.Rightx));
                float ry = NormalizeAxis(_sdl.GameControllerGetAxis(controller, GameControllerAxis.Righty));
                yaw += rx * 0.05f;
                pitch += ry * 0.05f;

                if (_sdl.GameControllerGetButton(controller, GameControllerButton.DpadLeft) != 0)
                    yaw -= 0.03f;
                if (_sdl.GameControllerGetButton(controller, GameControllerButton.DpadRight) != 0)
                    yaw += 0.03f;
                if (_sdl.GameControllerGetButton(controller, GameControllerButton.DpadUp) != 0)
                    pitch -= 0.03f;
                if (_sdl.GameControllerGetButton(controller, GameControllerButton.DpadDown) != 0)
                    pitch += 0.03f;
            }
        }

        if (yaw != 0f || pitch != 0f)
            _runtime.AddRotation(yaw, pitch);
    }

    private static float NormalizeAxis(short value)
    {
        const float deadZone = 8000f;
        if (value > -deadZone && value < deadZone)
            return 0f;
        return Math.Clamp(value / 32767f, -1f, 1f);
    }

    private void InitGamepad()
    {
        try
        {
            _sdl = SdlApi.GetApi();
            int rc = _sdl.Init(SdlApi.InitGamecontroller | SdlApi.InitJoystick | SdlApi.InitEvents);
            if (rc != 0)
            {
                _sdlReady = false;
                return;
            }
            _sdlReady = true;
            TryOpenFirstGamepad();
        }
        catch
        {
            _sdlReady = false;
        }
    }

    private void TryOpenFirstGamepad()
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
                    _gamepad = (IntPtr)controller;
                    return;
                }
            }
        }
    }

    private void CloseGamepad()
    {
        if (_sdl == null || _gamepad == IntPtr.Zero)
            return;
        unsafe
        {
            var controller = (Silk.NET.SDL.GameController*)_gamepad;
            _sdl.GameControllerClose(controller);
        }
        _gamepad = IntPtr.Zero;
    }

    private void HandleEvent(ushort eventId)
    {
        if (eventId == 1)
            PlayRoar();
    }

    private void EnsureRoarLoaded()
    {
        if (_roarSamples.Length != 0)
            return;
        try
        {
            var uri = new Uri("avares://EutherDrive.UI/Assets/jox.wav");
            using Stream stream = AssetLoader.Open(uri);
            LoadWav(stream, out _roarSamples, out _roarRate, out _roarChannels);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ZuulView] failed to load jox.wav: {ex.Message}");
        }
    }

    private void PlayRoar()
    {
        EnsureRoarLoaded();
        if (_roarSamples.Length == 0)
            return;
        _audioSink ??= Sdl2AudioSink.TryCreate();
        if (_audioSink == null)
            return;
        _audioSink.Start(_roarRate, _roarChannels);
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
            throw new InvalidDataException("Not a RIFF file.");
        br.ReadInt32();
        if (br.Read(id) != 4 || id[0] != (byte)'W' || id[1] != (byte)'A' || id[2] != (byte)'V' || id[3] != (byte)'E')
            throw new InvalidDataException("Not a WAVE file.");

        ushort fmt = 0;
        ushort bits = 0;
        int dataSize = 0;
        long dataPos = 0;

        while (stream.Position + 8 <= stream.Length)
        {
            if (br.Read(id) != 4)
                break;
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
            throw new InvalidDataException("Unsupported WAV (need PCM16).");

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
