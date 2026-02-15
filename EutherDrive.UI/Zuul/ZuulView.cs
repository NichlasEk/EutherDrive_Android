using System;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;

namespace EutherDrive.UI.Zuul;

internal sealed class ZuulView : Control
{
    private readonly JoxRuntime _runtime = new();
    private readonly DispatcherTimer _timer = new();
    private bool _loaded;

    public ZuulView()
    {
        _timer.Tick += (_, _) =>
        {
            _runtime.Tick();
            InvalidateVisual();
        };
    }

    public void LoadDefault()
    {
        try
        {
            var uri = new Uri("avares://EutherDrive.UI/Assets/zuul_demo.jox");
            using Stream stream = AssetLoader.Open(uri);
            LoadFromStream(stream);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ZuulView] failed to load default JOX: {ex.Message}");
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
        if (_runtime.Lines.Count == 0)
            LoadDefault();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _timer.Stop();
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
}
