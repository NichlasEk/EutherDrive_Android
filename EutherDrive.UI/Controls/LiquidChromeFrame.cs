using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace EutherDrive.UI.Controls;

public class LiquidChromeFrame : Decorator
{
    public static readonly StyledProperty<Thickness> PaddingProperty =
        AvaloniaProperty.Register<LiquidChromeFrame, Thickness>(nameof(Padding), new Thickness(0));

    public static readonly StyledProperty<CornerRadius> CornerRadiusProperty =
        AvaloniaProperty.Register<LiquidChromeFrame, CornerRadius>(nameof(CornerRadius), new CornerRadius(0));

    public static readonly StyledProperty<Thickness> BorderThicknessProperty =
        AvaloniaProperty.Register<LiquidChromeFrame, Thickness>(nameof(BorderThickness), new Thickness(0));

    public static readonly StyledProperty<IBrush?> BorderBrushProperty =
        AvaloniaProperty.Register<LiquidChromeFrame, IBrush?>(nameof(BorderBrush));

    public static readonly StyledProperty<Color> BaseColorProperty =
        AvaloniaProperty.Register<LiquidChromeFrame, Color>(nameof(BaseColor), Color.Parse("#1A212A"));

    public static readonly StyledProperty<Color> SpecularColorProperty =
        AvaloniaProperty.Register<LiquidChromeFrame, Color>(nameof(SpecularColor), Color.Parse("#F8FBFF"));

    public static readonly StyledProperty<Color> ShadowColorProperty =
        AvaloniaProperty.Register<LiquidChromeFrame, Color>(nameof(ShadowColor), Color.Parse("#05080C"));

    public static readonly StyledProperty<bool> ChromeEnabledProperty =
        AvaloniaProperty.Register<LiquidChromeFrame, bool>(nameof(ChromeEnabled), false);

    public static readonly StyledProperty<double> ChromeIntensityProperty =
        AvaloniaProperty.Register<LiquidChromeFrame, double>(nameof(ChromeIntensity), 0.92);

    public static readonly StyledProperty<double> ChromeWarpProperty =
        AvaloniaProperty.Register<LiquidChromeFrame, double>(nameof(ChromeWarp), 1.0);

    public static readonly StyledProperty<int> ChromeBandCountProperty =
        AvaloniaProperty.Register<LiquidChromeFrame, int>(nameof(ChromeBandCount), 6);

    public static readonly StyledProperty<double> ChromeCoolnessProperty =
        AvaloniaProperty.Register<LiquidChromeFrame, double>(nameof(ChromeCoolness), 0.18);

    public Thickness Padding
    {
        get => GetValue(PaddingProperty);
        set => SetValue(PaddingProperty, value);
    }

    public CornerRadius CornerRadius
    {
        get => GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    public Thickness BorderThickness
    {
        get => GetValue(BorderThicknessProperty);
        set => SetValue(BorderThicknessProperty, value);
    }

    public IBrush? BorderBrush
    {
        get => GetValue(BorderBrushProperty);
        set => SetValue(BorderBrushProperty, value);
    }

    public Color BaseColor
    {
        get => GetValue(BaseColorProperty);
        set => SetValue(BaseColorProperty, value);
    }

    public Color SpecularColor
    {
        get => GetValue(SpecularColorProperty);
        set => SetValue(SpecularColorProperty, value);
    }

    public Color ShadowColor
    {
        get => GetValue(ShadowColorProperty);
        set => SetValue(ShadowColorProperty, value);
    }

    public bool ChromeEnabled
    {
        get => GetValue(ChromeEnabledProperty);
        set => SetValue(ChromeEnabledProperty, value);
    }

    public double ChromeIntensity
    {
        get => GetValue(ChromeIntensityProperty);
        set => SetValue(ChromeIntensityProperty, value);
    }

    public double ChromeWarp
    {
        get => GetValue(ChromeWarpProperty);
        set => SetValue(ChromeWarpProperty, value);
    }

    public int ChromeBandCount
    {
        get => GetValue(ChromeBandCountProperty);
        set => SetValue(ChromeBandCountProperty, value);
    }

    public double ChromeCoolness
    {
        get => GetValue(ChromeCoolnessProperty);
        set => SetValue(ChromeCoolnessProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        Thickness chromeThickness = GetChromeThickness();
        Size childAvailable = new(
            Math.Max(0, availableSize.Width - chromeThickness.Left - chromeThickness.Right),
            Math.Max(0, availableSize.Height - chromeThickness.Top - chromeThickness.Bottom));

        Child?.Measure(childAvailable);

        if (Child is null)
            return new Size(chromeThickness.Left + chromeThickness.Right, chromeThickness.Top + chromeThickness.Bottom);

        return new Size(
            Child.DesiredSize.Width + chromeThickness.Left + chromeThickness.Right,
            Child.DesiredSize.Height + chromeThickness.Top + chromeThickness.Bottom);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Child != null)
        {
            Thickness chromeThickness = GetChromeThickness();
            Rect childRect = new(
                chromeThickness.Left,
                chromeThickness.Top,
                Math.Max(0, finalSize.Width - chromeThickness.Left - chromeThickness.Right),
                Math.Max(0, finalSize.Height - chromeThickness.Top - chromeThickness.Bottom));
            Child.Arrange(childRect);
        }

        return finalSize;
    }

    public override void Render(DrawingContext context)
    {
        Rect bounds = new(Bounds.Size);
        if (bounds.Width <= 0 || bounds.Height <= 0)
            return;

        Thickness borderThickness = BorderThickness;
        RoundedRect outer = new(bounds, CornerRadius);
        RoundedRect inner = new(DeflateRect(bounds, borderThickness), DeflateCornerRadius(CornerRadius, borderThickness));

        using (context.PushClip(outer))
        {
            if (ChromeEnabled)
            {
                DrawChromeMaterial(context, bounds);
            }
            else
            {
                context.DrawRectangle(new SolidColorBrush(BaseColor), null, outer);
            }
        }

        base.Render(context);

        DrawBorder(context, outer);
    }

    private void DrawChromeMaterial(DrawingContext context, Rect bounds)
    {
        double intensity = Math.Clamp(ChromeIntensity, 0.0, 1.4);
        double warp = Math.Clamp(ChromeWarp, 0.1, 2.5);
        int bandCount = Math.Clamp(ChromeBandCount, 3, 12);
        double coolness = Math.Clamp(ChromeCoolness, 0.0, 1.0);

        Color coolSpec = Blend(SpecularColor, Color.Parse("#B7DBFF"), coolness * 0.36);
        Color baseTop = Blend(Lighten(BaseColor, 0.12 + (intensity * 0.1)), coolSpec, 0.20 + (intensity * 0.1));
        Color baseBottom = Blend(Darken(BaseColor, 0.48), ShadowColor, 0.58);

        var baseBrush = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(baseTop, 0.0),
                new GradientStop(Blend(BaseColor, coolSpec, 0.12), 0.28),
                new GradientStop(Blend(BaseColor, ShadowColor, 0.18), 0.62),
                new GradientStop(baseBottom, 1.0)
            }
        };
        context.DrawRectangle(baseBrush, null, bounds);

        double width = bounds.Width;
        double height = bounds.Height;
        double diagonal = Math.Sqrt((width * width) + (height * height));
        double bandSpacing = height / (bandCount + 1.0);

        for (int i = 0; i < bandCount; i++)
        {
            double normalized = (i + 1.0) / (bandCount + 1.0);
            double yBase = normalized * height;
            double thickness = Math.Max(12, diagonal * (0.09 + ((i % 3) * 0.018)) * intensity);
            double amplitude = (18 + (28 * warp) + ((i % 2) * 10)) * Math.Max(0.55, height / 320.0);
            double primaryFrequency = (1.1 + (i * 0.21)) / Math.Max(220.0, width);
            double secondaryFrequency = (2.8 + (i * 0.14)) / Math.Max(160.0, width);
            double phase = 0.7 + (i * 0.92);
            double tension = 0.50 + ((i % 4) * 0.07);

            StreamGeometry bandGeometry = CreateBandGeometry(width, yBase, thickness, amplitude, primaryFrequency, secondaryFrequency, phase, tension);
            Color brightCore = Blend(coolSpec, Colors.White, 0.36 + (intensity * 0.18));
            Color shoulder = Blend(BaseColor, coolSpec, 0.32 + (intensity * 0.22));
            Color edge = Blend(ShadowColor, BaseColor, 0.18);

            var bandBrush = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, yBase - thickness, RelativeUnit.Absolute),
                EndPoint = new RelativePoint(0, yBase + thickness, RelativeUnit.Absolute),
                GradientStops =
                {
                    new GradientStop(WithAlpha(edge, 0), 0.0),
                    new GradientStop(WithAlpha(shoulder, 150), 0.22),
                    new GradientStop(WithAlpha(brightCore, 245), 0.48),
                    new GradientStop(WithAlpha(Colors.White, 230), 0.56),
                    new GradientStop(WithAlpha(shoulder, 160), 0.72),
                    new GradientStop(WithAlpha(edge, 0), 1.0)
                }
            };
            context.DrawGeometry(bandBrush, null, bandGeometry);

            StreamGeometry coreGeometry = CreateBandGeometry(width, yBase + (bandSpacing * 0.04), thickness * 0.26, amplitude * 0.62, primaryFrequency, secondaryFrequency, phase + 0.35, tension);
            var coreBrush = new SolidColorBrush(WithAlpha(Blend(Colors.White, coolSpec, 0.45), 120));
            context.DrawGeometry(coreBrush, null, coreGeometry);
        }

        DrawMicroSpecular(context, bounds, coolSpec, intensity);
    }

    private static StreamGeometry CreateBandGeometry(
        double width,
        double yBase,
        double thickness,
        double amplitude,
        double primaryFrequency,
        double secondaryFrequency,
        double phase,
        double tension)
    {
        int segments = Math.Clamp((int)(width / 12), 24, 180);
        List<Point> top = new(segments + 1);
        List<Point> bottom = new(segments + 1);

        for (int i = 0; i <= segments; i++)
        {
            double t = i / (double)segments;
            double x = width * t;
            double wave = SampleWave(x, amplitude, primaryFrequency, secondaryFrequency, phase, tension);
            double localThickness = thickness * (0.78 + (0.24 * Math.Sin((t * Math.PI * 2.0) + (phase * 0.7))));
            double centerY = yBase + wave;
            top.Add(new Point(x, centerY - localThickness));
            bottom.Add(new Point(x, centerY + localThickness));
        }

        var geometry = new StreamGeometry();
        using StreamGeometryContext geo = geometry.Open();
        geo.BeginFigure(top[0], true);
        for (int i = 1; i < top.Count; i++)
            geo.LineTo(top[i]);
        for (int i = bottom.Count - 1; i >= 0; i--)
            geo.LineTo(bottom[i]);
        geo.EndFigure(true);
        return geometry;
    }

    private static double SampleWave(double x, double amplitude, double primaryFrequency, double secondaryFrequency, double phase, double tension)
    {
        double primary = Math.Sin((x * primaryFrequency * Math.PI * 2.0) + phase);
        double secondary = Math.Sin((x * secondaryFrequency * Math.PI * 2.0) + (phase * 1.73));
        double tertiary = Math.Cos((x * secondaryFrequency * Math.PI * 3.15) + (phase * 0.37));
        return (primary * amplitude * tension)
             + (secondary * amplitude * 0.55)
             + (tertiary * amplitude * 0.24);
    }

    private static void DrawMicroSpecular(DrawingContext context, Rect bounds, Color coolSpec, double intensity)
    {
        int lineCount = Math.Clamp((int)(bounds.Width / 18), 18, 90);
        double width = bounds.Width;
        double height = bounds.Height;

        for (int i = 0; i < lineCount; i++)
        {
            double t = i / (double)Math.Max(1, lineCount - 1);
            double x = width * t;
            double y0 = (height * 0.06) + (Math.Sin((t * Math.PI * 8.0) + 0.5) * height * 0.04);
            double y1 = height - (height * 0.1) + (Math.Cos((t * Math.PI * 7.0) + 0.3) * height * 0.05);
            byte alpha = (byte)Math.Clamp((int)Math.Round((10 + (Math.Sin(t * Math.PI * 5.0) * 18) + (intensity * 22))), 4, 54);
            var pen = new Pen(new SolidColorBrush(WithAlpha(coolSpec, alpha)), Math.Max(0.6, width / 900));
            context.DrawLine(pen, new Point(x, y0), new Point(x + (width * 0.045), y1));
        }
    }

    private void DrawBorder(DrawingContext context, RoundedRect outer)
    {
        Thickness borderThickness = BorderThickness;
        if (BorderBrush == null || (borderThickness.Left <= 0 && borderThickness.Top <= 0 && borderThickness.Right <= 0 && borderThickness.Bottom <= 0))
            return;

        double thickness = Math.Max(borderThickness.Left, Math.Max(borderThickness.Top, Math.Max(borderThickness.Right, borderThickness.Bottom)));
        context.DrawRectangle(null, new Pen(BorderBrush, thickness), outer);
    }

    private Thickness GetChromeThickness()
    {
        Thickness border = BorderThickness;
        return new Thickness(
            border.Left + Padding.Left,
            border.Top + Padding.Top,
            border.Right + Padding.Right,
            border.Bottom + Padding.Bottom);
    }

    private static Rect DeflateRect(Rect rect, Thickness thickness)
        => new(
            rect.X + thickness.Left,
            rect.Y + thickness.Top,
            Math.Max(0, rect.Width - thickness.Left - thickness.Right),
            Math.Max(0, rect.Height - thickness.Top - thickness.Bottom));

    private static CornerRadius DeflateCornerRadius(CornerRadius radius, Thickness thickness)
    {
        double inset = Math.Max(thickness.Left, Math.Max(thickness.Top, Math.Max(thickness.Right, thickness.Bottom)));
        return new CornerRadius(
            Math.Max(0, radius.TopLeft - inset),
            Math.Max(0, radius.TopRight - inset),
            Math.Max(0, radius.BottomRight - inset),
            Math.Max(0, radius.BottomLeft - inset));
    }

    private static Color Lighten(Color color, double amount)
        => Color.FromArgb(
            color.A,
            (byte)Math.Clamp((int)Math.Round(color.R + ((255 - color.R) * amount)), 0, 255),
            (byte)Math.Clamp((int)Math.Round(color.G + ((255 - color.G) * amount)), 0, 255),
            (byte)Math.Clamp((int)Math.Round(color.B + ((255 - color.B) * amount)), 0, 255));

    private static Color Darken(Color color, double factor)
        => Color.FromArgb(
            color.A,
            (byte)Math.Clamp((int)Math.Round(color.R * factor), 0, 255),
            (byte)Math.Clamp((int)Math.Round(color.G * factor), 0, 255),
            (byte)Math.Clamp((int)Math.Round(color.B * factor), 0, 255));

    private static Color Blend(Color a, Color b, double amount)
    {
        double t = Math.Clamp(amount, 0.0, 1.0);
        return Color.FromArgb(
            (byte)Math.Clamp((int)Math.Round(a.A + ((b.A - a.A) * t)), 0, 255),
            (byte)Math.Clamp((int)Math.Round(a.R + ((b.R - a.R) * t)), 0, 255),
            (byte)Math.Clamp((int)Math.Round(a.G + ((b.G - a.G) * t)), 0, 255),
            (byte)Math.Clamp((int)Math.Round(a.B + ((b.B - a.B) * t)), 0, 255));
    }

    private static Color WithAlpha(Color color, byte alpha)
        => Color.FromArgb(alpha, color.R, color.G, color.B);
}
