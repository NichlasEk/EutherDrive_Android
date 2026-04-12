using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace EutherDrive.UI.Controls;

public class EmbossTextureOverlay : Control
{
    public static readonly StyledProperty<bool> TextureEnabledProperty =
        AvaloniaProperty.Register<EmbossTextureOverlay, bool>(nameof(TextureEnabled), false);

    public static readonly StyledProperty<double> TextureOpacityProperty =
        AvaloniaProperty.Register<EmbossTextureOverlay, double>(nameof(TextureOpacity), 0.16);

    public static readonly StyledProperty<double> PatternScaleProperty =
        AvaloniaProperty.Register<EmbossTextureOverlay, double>(nameof(PatternScale), 1.0);

    public static readonly StyledProperty<double> DepthProperty =
        AvaloniaProperty.Register<EmbossTextureOverlay, double>(nameof(Depth), 1.0);

    public static readonly StyledProperty<Color> TintColorProperty =
        AvaloniaProperty.Register<EmbossTextureOverlay, Color>(nameof(TintColor), Color.Parse("#1A2B36"));

    public static readonly StyledProperty<Color> HighlightColorProperty =
        AvaloniaProperty.Register<EmbossTextureOverlay, Color>(nameof(HighlightColor), Color.Parse("#D8B24A"));

    public static readonly StyledProperty<Color> ShadowColorProperty =
        AvaloniaProperty.Register<EmbossTextureOverlay, Color>(nameof(ShadowColor), Color.Parse("#050607"));

    public bool TextureEnabled
    {
        get => GetValue(TextureEnabledProperty);
        set => SetValue(TextureEnabledProperty, value);
    }

    public double TextureOpacity
    {
        get => GetValue(TextureOpacityProperty);
        set => SetValue(TextureOpacityProperty, value);
    }

    public double PatternScale
    {
        get => GetValue(PatternScaleProperty);
        set => SetValue(PatternScaleProperty, value);
    }

    public double Depth
    {
        get => GetValue(DepthProperty);
        set => SetValue(DepthProperty, value);
    }

    public Color TintColor
    {
        get => GetValue(TintColorProperty);
        set => SetValue(TintColorProperty, value);
    }

    public Color HighlightColor
    {
        get => GetValue(HighlightColorProperty);
        set => SetValue(HighlightColorProperty, value);
    }

    public Color ShadowColor
    {
        get => GetValue(ShadowColorProperty);
        set => SetValue(ShadowColorProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        if (!TextureEnabled)
            return;

        Rect bounds = new(Bounds.Size);
        if (bounds.Width <= 0 || bounds.Height <= 0)
            return;

        double opacity = Math.Clamp(TextureOpacity, 0.0, 1.0);
        double scale = Math.Clamp(PatternScale, 0.4, 2.4);
        double depth = Math.Clamp(Depth, 0.3, 2.0);

        DrawBaseWash(context, bounds, opacity);
        DrawDiamondRelief(context, bounds, opacity, scale, depth);
        DrawRosettes(context, bounds, opacity, scale, depth);
        DrawEdgeVignette(context, bounds, opacity, depth);
    }

    private void DrawBaseWash(DrawingContext context, Rect bounds, double opacity)
    {
        var wash = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(WithOpacity(Blend(TintColor, HighlightColor, 0.14), opacity * 0.34), 0.0),
                new GradientStop(WithOpacity(TintColor, opacity * 0.18), 0.48),
                new GradientStop(WithOpacity(Blend(TintColor, ShadowColor, 0.28), opacity * 0.28), 1.0)
            }
        };
        context.DrawRectangle(wash, null, bounds);
    }

    private void DrawDiamondRelief(DrawingContext context, Rect bounds, double opacity, double scale, double depth)
    {
        double spacing = 64 / scale;
        double inset = spacing * 0.18;
        double lineWidth = Math.Max(0.8, 1.2 * depth);

        var shadowPen = new Pen(new SolidColorBrush(WithOpacity(ShadowColor, opacity * 0.42)), lineWidth);
        var highlightPen = new Pen(new SolidColorBrush(WithOpacity(HighlightColor, opacity * 0.24)), Math.Max(0.6, lineWidth * 0.72));

        for (double y = -spacing; y < bounds.Height + spacing; y += spacing)
        {
            for (double x = -spacing; x < bounds.Width + spacing; x += spacing)
            {
                Point top = new(x + (spacing * 0.5), y + inset);
                Point right = new(x + spacing - inset, y + (spacing * 0.5));
                Point bottom = new(x + (spacing * 0.5), y + spacing - inset);
                Point left = new(x + inset, y + (spacing * 0.5));

                DrawLinePair(context, shadowPen, highlightPen, top, right, -0.9);
                DrawLinePair(context, shadowPen, highlightPen, right, bottom, -0.9);
                DrawLinePair(context, shadowPen, highlightPen, bottom, left, -0.9);
                DrawLinePair(context, shadowPen, highlightPen, left, top, -0.9);
            }
        }
    }

    private void DrawRosettes(DrawingContext context, Rect bounds, double opacity, double scale, double depth)
    {
        double spacing = 128 / scale;
        double radius = Math.Max(12, 18 / scale);

        for (double y = spacing * 0.5; y < bounds.Height; y += spacing)
        {
            for (double x = spacing * 0.5; x < bounds.Width; x += spacing)
            {
                Color center = WithOpacity(Blend(HighlightColor, TintColor, 0.52), opacity * 0.18);
                Color rim = WithOpacity(Blend(ShadowColor, TintColor, 0.35), opacity * 0.32);
                var brush = new RadialGradientBrush
                {
                    Center = new RelativePoint(x, y, RelativeUnit.Absolute),
                    GradientOrigin = new RelativePoint(0.35, 0.35, RelativeUnit.Relative),
                    Radius = 1.0,
                    GradientStops =
                    {
                        new GradientStop(center, 0.0),
                        new GradientStop(WithOpacity(HighlightColor, opacity * 0.10), 0.28),
                        new GradientStop(rim, 1.0)
                    }
                };

                Rect rosetteRect = new(x - radius, y - radius, radius * 2.0, radius * 2.0);
                context.DrawRectangle(brush, null, rosetteRect);

                var pen = new Pen(new SolidColorBrush(WithOpacity(HighlightColor, opacity * 0.13)), Math.Max(0.7, depth));
                context.DrawEllipse(null, pen, new Point(x, y), radius * 0.7, radius * 0.7);
            }
        }
    }

    private void DrawEdgeVignette(DrawingContext context, Rect bounds, double opacity, double depth)
    {
        var top = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(WithOpacity(Blend(ShadowColor, Colors.Black, 0.55), opacity * 0.58 * depth), 0.0),
                new GradientStop(WithOpacity(ShadowColor, 0), 1.0)
            }
        };
        var left = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(WithOpacity(Blend(ShadowColor, Colors.Black, 0.55), opacity * 0.42 * depth), 0.0),
                new GradientStop(WithOpacity(ShadowColor, 0), 1.0)
            }
        };

        context.DrawRectangle(top, null, new Rect(bounds.X, bounds.Y, bounds.Width, Math.Max(48, bounds.Height * 0.14)));
        context.DrawRectangle(left, null, new Rect(bounds.X, bounds.Y, Math.Max(42, bounds.Width * 0.08), bounds.Height));
        context.DrawRectangle(left, null, new Rect(bounds.Right - Math.Max(42, bounds.Width * 0.08), bounds.Y, Math.Max(42, bounds.Width * 0.08), bounds.Height));
    }

    private static void DrawLinePair(DrawingContext context, Pen shadowPen, Pen highlightPen, Point a, Point b, double highlightOffset)
    {
        context.DrawLine(shadowPen, a, b);
        context.DrawLine(highlightPen, new Point(a.X, a.Y + highlightOffset), new Point(b.X, b.Y + highlightOffset));
    }

    private static Color Blend(Color a, Color b, double amount)
    {
        double t = Math.Clamp(amount, 0.0, 1.0);
        return Color.FromArgb(
            (byte)Math.Clamp((int)Math.Round(a.A + ((b.A - a.A) * t)), 0, 255),
            (byte)Math.Clamp((int)Math.Round(a.R + ((b.R - a.R) * t)), 0, 255),
            (byte)Math.Clamp((int)Math.Round(a.G + ((b.G - a.G) * t)), 0, 255),
            (byte)Math.Clamp((int)Math.Round(a.B + ((b.B - a.B) * t)), 0, 255));
    }

    private static Color WithOpacity(Color color, double opacity)
        => Color.FromArgb(
            (byte)Math.Clamp((int)Math.Round(opacity * 255.0), 0, 255),
            color.R,
            color.G,
            color.B);
}
