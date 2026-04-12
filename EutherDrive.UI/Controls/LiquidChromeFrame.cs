using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace EutherDrive.UI.Controls;

public class LiquidChromeFrame : Decorator
{
    private readonly DispatcherTimer _animationTimer;
    private double _chromePhase;

    public new static readonly StyledProperty<Thickness> PaddingProperty =
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

    public static readonly StyledProperty<bool> ReliefEnabledProperty =
        AvaloniaProperty.Register<LiquidChromeFrame, bool>(nameof(ReliefEnabled), false);

    public static readonly StyledProperty<double> ReliefOpacityProperty =
        AvaloniaProperty.Register<LiquidChromeFrame, double>(nameof(ReliefOpacity), 0.16);

    public static readonly StyledProperty<double> ReliefScaleProperty =
        AvaloniaProperty.Register<LiquidChromeFrame, double>(nameof(ReliefScale), 1.0);

    public static readonly StyledProperty<Color> ReliefHighlightColorProperty =
        AvaloniaProperty.Register<LiquidChromeFrame, Color>(nameof(ReliefHighlightColor), Color.Parse("#D6B23A"));

    public static readonly StyledProperty<Color> ReliefShadowColorProperty =
        AvaloniaProperty.Register<LiquidChromeFrame, Color>(nameof(ReliefShadowColor), Color.Parse("#050607"));

    public new Thickness Padding
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

    public bool ReliefEnabled
    {
        get => GetValue(ReliefEnabledProperty);
        set => SetValue(ReliefEnabledProperty, value);
    }

    public double ReliefOpacity
    {
        get => GetValue(ReliefOpacityProperty);
        set => SetValue(ReliefOpacityProperty, value);
    }

    public double ReliefScale
    {
        get => GetValue(ReliefScaleProperty);
        set => SetValue(ReliefScaleProperty, value);
    }

    public Color ReliefHighlightColor
    {
        get => GetValue(ReliefHighlightColorProperty);
        set => SetValue(ReliefHighlightColorProperty, value);
    }

    public Color ReliefShadowColor
    {
        get => GetValue(ReliefShadowColorProperty);
        set => SetValue(ReliefShadowColorProperty, value);
    }

    public LiquidChromeFrame()
    {
        _animationTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(16.666), DispatcherPriority.Render, (_, _) =>
        {
            _chromePhase += 0.018;
            InvalidateVisual();
        });
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        UpdateAnimationState();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _animationTimer.Stop();
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == ChromeEnabledProperty)
            UpdateAnimationState();
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

            if (ReliefEnabled)
                DrawReliefTexture(context, bounds);
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
        Color baseTop = Blend(Lighten(BaseColor, 0.10 + (intensity * 0.08)), coolSpec, 0.18 + (intensity * 0.08));
        Color baseBottom = Blend(Darken(BaseColor, 0.42), ShadowColor, 0.64);

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

        DrawEnvironmentField(context, bounds, coolSpec, intensity, warp);
        DrawReflectionTiles(context, bounds, coolSpec, intensity, warp);
        DrawFresnel(context, bounds, coolSpec, intensity);
        DrawArmorSeams(context, bounds, coolSpec, intensity);

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

        DrawReflectionSweep(context, bounds, coolSpec, intensity, warp);
        DrawHotSpots(context, bounds, coolSpec, intensity, warp);
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

    private void DrawEnvironmentField(DrawingContext context, Rect bounds, Color coolSpec, double intensity, double warp)
    {
        int columns = Math.Clamp((int)(bounds.Width / 10), 36, 160);
        double columnWidth = bounds.Width / columns;
        double height = bounds.Height;

        for (int i = 0; i < columns; i++)
        {
            double t = i / (double)Math.Max(1, columns - 1);
            double sweep = (t * 2.0) - 1.0;
            double flow = (_chromePhase * 0.42) + (sweep * warp * 0.7);
            double warpField = Math.Sin((sweep * 6.2) + flow)
                             + (Math.Cos((sweep * 11.7) - (_chromePhase * 0.31)) * 0.55)
                             + (Math.Sin((sweep * 18.0) + (_chromePhase * 0.83)) * 0.28);
            double normalized = Math.Clamp((warpField + 1.9) / 3.8, 0.0, 1.0);

            double hotBand = Math.Exp(-Math.Pow((sweep - Math.Sin(_chromePhase * 0.34) * 0.22) / 0.16, 2.0));
            double secondaryBand = Math.Exp(-Math.Pow((sweep + 0.46 + (Math.Cos(_chromePhase * 0.27) * 0.08)) / 0.24, 2.0));
            double chromeValue = Math.Clamp((normalized * 0.72) + (hotBand * 0.9) + (secondaryBand * 0.52), 0.0, 1.0);

            Color dark = Blend(ShadowColor, BaseColor, 0.24);
            Color mid = Blend(BaseColor, coolSpec, 0.20 + (intensity * 0.12));
            Color bright = Blend(coolSpec, Colors.White, 0.55 + (intensity * 0.18));

            var brush = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Blend(mid, bright, chromeValue * 0.62), 0.0),
                    new GradientStop(Blend(dark, bright, chromeValue), 0.18 + (Math.Sin((t * Math.PI * 3.0) + _chromePhase) * 0.04)),
                    new GradientStop(Blend(mid, Colors.White, chromeValue * 0.85), 0.46),
                    new GradientStop(Blend(dark, coolSpec, chromeValue * 0.44), 0.74),
                    new GradientStop(Blend(ShadowColor, dark, 0.45), 1.0)
                }
            };

            Rect columnRect = new(bounds.X + (i * columnWidth), bounds.Y, columnWidth + 1.2, height);
            context.DrawRectangle(brush, null, columnRect);
        }
    }

    private void DrawReflectionTiles(DrawingContext context, Rect bounds, Color coolSpec, double intensity, double warp)
    {
        int columns = Math.Clamp((int)(bounds.Width / 22), 20, 64);
        int rows = Math.Clamp((int)(bounds.Height / 26), 8, 26);
        double cellWidth = bounds.Width / columns;
        double cellHeight = bounds.Height / rows;

        for (int y = 0; y < rows; y++)
        {
            double v = y / (double)Math.Max(1, rows - 1);
            for (int x = 0; x < columns; x++)
            {
                double u = x / (double)Math.Max(1, columns - 1);
                double nx = (u * 2.0) - 1.0;
                double ny = (v * 2.0) - 1.0;

                double distortionX = (Math.Sin((ny * 5.4) + (_chromePhase * 0.42) + (nx * 6.8)) * 0.24)
                                   + (Math.Cos((nx * 10.6) - (_chromePhase * 0.27) + (ny * 4.2)) * 0.16);
                double distortionY = (Math.Cos((nx * 7.1) + (_chromePhase * 0.34) - (ny * 5.7)) * 0.18)
                                   + (Math.Sin((ny * 13.8) + (_chromePhase * 0.22) + (nx * 4.8)) * 0.12);

                double sampleU = Math.Clamp(u + (distortionX * 0.18 * warp), 0.0, 1.0);
                double sampleV = Math.Clamp(v + (distortionY * 0.22 * warp), 0.0, 1.0);

                Color envColor = SampleEnvironmentColor(sampleU, sampleV, coolSpec, intensity);
                double reflectivity = Math.Clamp(0.10 + (Math.Abs(distortionX) * 0.24) + (Math.Abs(distortionY) * 0.16), 0.0, 0.34);
                var brush = new SolidColorBrush(WithOpacity(envColor, reflectivity));

                Rect tile = new(
                    bounds.X + (x * cellWidth),
                    bounds.Y + (y * cellHeight),
                    cellWidth + 0.8,
                    cellHeight + 0.8);
                context.DrawRectangle(brush, null, tile);
            }
        }
    }

    private void DrawFresnel(DrawingContext context, Rect bounds, Color coolSpec, double intensity)
    {
        double edgeAlpha = Math.Clamp(0.11 + (intensity * 0.08), 0.0, 0.28);
        Color edgeHighlight = WithOpacity(Blend(coolSpec, Colors.White, 0.72), edgeAlpha);
        Color edgeShadow = WithOpacity(Blend(ShadowColor, Colors.Black, 0.55), 0.30);

        var leftBrush = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(edgeHighlight, 0.0),
                new GradientStop(WithOpacity(edgeHighlight, edgeAlpha * 0.35), 0.22),
                new GradientStop(WithOpacity(edgeHighlight, 0), 1.0)
            }
        };

        var rightBrush = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(edgeHighlight, 0.0),
                new GradientStop(WithOpacity(edgeHighlight, edgeAlpha * 0.42), 0.18),
                new GradientStop(WithOpacity(edgeHighlight, 0), 1.0)
            }
        };

        var topBrush = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(WithOpacity(Blend(coolSpec, Colors.White, 0.78), edgeAlpha * 1.35), 0.0),
                new GradientStop(WithOpacity(coolSpec, edgeAlpha * 0.58), 0.2),
                new GradientStop(WithOpacity(coolSpec, 0), 1.0)
            }
        };

        var bottomBrush = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(edgeShadow, 0.0),
                new GradientStop(WithOpacity(edgeShadow, 0.12), 0.26),
                new GradientStop(WithOpacity(edgeShadow, 0), 1.0)
            }
        };

        double edgeWidth = Math.Max(8, bounds.Width * 0.04);
        double edgeHeight = Math.Max(8, bounds.Height * 0.12);

        context.DrawRectangle(leftBrush, null, new Rect(bounds.X, bounds.Y, edgeWidth, bounds.Height));
        context.DrawRectangle(rightBrush, null, new Rect(bounds.Right - edgeWidth, bounds.Y, edgeWidth, bounds.Height));
        context.DrawRectangle(topBrush, null, new Rect(bounds.X, bounds.Y, bounds.Width, edgeHeight));
        context.DrawRectangle(bottomBrush, null, new Rect(bounds.X, bounds.Bottom - edgeHeight, bounds.Width, edgeHeight));
    }

    private void DrawArmorSeams(DrawingContext context, Rect bounds, Color coolSpec, double intensity)
    {
        Color seamHighlight = WithOpacity(Blend(coolSpec, Colors.White, 0.48), 0.16 + (intensity * 0.04));
        Color seamShadow = WithOpacity(Blend(ShadowColor, Colors.Black, 0.7), 0.28);

        double topInset = Math.Max(18, bounds.Height * 0.13);
        double sideInset = Math.Max(18, bounds.Width * 0.085);
        double lowerInset = Math.Max(20, bounds.Height * 0.18);
        double diagonalDepth = Math.Max(16, bounds.Width * 0.06);

        var shadowPen = new Pen(new SolidColorBrush(seamShadow), 1.2);
        var highlightPen = new Pen(new SolidColorBrush(seamHighlight), 0.9);

        Point leftA = new(bounds.X + sideInset, bounds.Y + topInset);
        Point leftB = new(bounds.X + sideInset + diagonalDepth, bounds.Bottom - lowerInset);
        Point rightA = new(bounds.Right - sideInset, bounds.Y + topInset);
        Point rightB = new(bounds.Right - sideInset - diagonalDepth, bounds.Bottom - lowerInset);
        Point midLeft = new(bounds.X + bounds.Width * 0.32, bounds.Y + bounds.Height * 0.16);
        Point midRight = new(bounds.X + bounds.Width * 0.68, bounds.Y + bounds.Height * 0.16);

        DrawInsetLine(context, shadowPen, highlightPen, leftA, leftB, 1.2);
        DrawInsetLine(context, shadowPen, highlightPen, rightA, rightB, 1.2);
        DrawInsetLine(context, shadowPen, highlightPen, midLeft, midRight, 1.0);
    }

    private void DrawReliefTexture(DrawingContext context, Rect bounds)
    {
        double opacity = Math.Clamp(ReliefOpacity, 0.0, 1.0);
        double scale = Math.Clamp(ReliefScale, 0.4, 2.2);
        double spacing = 72 / scale;
        double inset = spacing * 0.2;

        var shadowPen = new Pen(new SolidColorBrush(WithOpacity(ReliefShadowColor, opacity * 0.42)), 1.1);
        var highlightPen = new Pen(new SolidColorBrush(WithOpacity(ReliefHighlightColor, opacity * 0.26)), 0.8);

        for (double y = -spacing; y < bounds.Height + spacing; y += spacing)
        {
            for (double x = -spacing; x < bounds.Width + spacing; x += spacing)
            {
                Point top = new(x + (spacing * 0.5), y + inset);
                Point right = new(x + spacing - inset, y + (spacing * 0.5));
                Point bottom = new(x + (spacing * 0.5), y + spacing - inset);
                Point left = new(x + inset, y + (spacing * 0.5));

                DrawInsetLine(context, shadowPen, highlightPen, top, right, -0.7);
                DrawInsetLine(context, shadowPen, highlightPen, right, bottom, -0.7);
                DrawInsetLine(context, shadowPen, highlightPen, bottom, left, -0.7);
                DrawInsetLine(context, shadowPen, highlightPen, left, top, -0.7);
            }
        }

        double rosetteSpacing = 144 / scale;
        double radius = Math.Max(14, 18 / scale);
        for (double y = rosetteSpacing * 0.5; y < bounds.Height; y += rosetteSpacing)
        {
            for (double x = rosetteSpacing * 0.5; x < bounds.Width; x += rosetteSpacing)
            {
                var brush = new RadialGradientBrush
                {
                    Center = new RelativePoint(x, y, RelativeUnit.Absolute),
                    GradientOrigin = new RelativePoint(0.35, 0.35, RelativeUnit.Relative),
                    Radius = 1.0,
                    GradientStops =
                    {
                        new GradientStop(WithOpacity(ReliefHighlightColor, opacity * 0.18), 0.0),
                        new GradientStop(WithOpacity(ReliefHighlightColor, opacity * 0.08), 0.28),
                        new GradientStop(WithOpacity(ReliefShadowColor, opacity * 0.24), 1.0)
                    }
                };

                context.DrawRectangle(brush, null, new Rect(x - radius, y - radius, radius * 2.0, radius * 2.0));
            }
        }
    }

    private void DrawReflectionSweep(DrawingContext context, Rect bounds, Color coolSpec, double intensity, double warp)
    {
        double width = bounds.Width;
        double height = bounds.Height;
        double yBase = height * (0.28 + (Math.Sin(_chromePhase * 0.21) * 0.08));
        double thickness = Math.Max(18, Math.Sqrt((width * width) + (height * height)) * 0.08 * intensity);
        double amplitude = Math.Max(24, height * 0.22 * warp);
        double phase = 1.2 + (_chromePhase * 0.55);

        StreamGeometry sweepGeometry = CreateBandGeometry(
            width,
            yBase,
            thickness,
            amplitude,
            1.6 / Math.Max(240.0, width),
            4.3 / Math.Max(180.0, width),
            phase,
            0.78);

        var sweepBrush = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(WithOpacity(coolSpec, 0), 0.0),
                new GradientStop(WithOpacity(Blend(coolSpec, Colors.White, 0.82), 0.08), 0.24),
                new GradientStop(WithOpacity(Colors.White, 0.24 + (intensity * 0.12)), 0.46),
                new GradientStop(WithOpacity(Blend(coolSpec, Colors.White, 0.70), 0.10), 0.66),
                new GradientStop(WithOpacity(coolSpec, 0), 1.0)
            }
        };

        context.DrawGeometry(sweepBrush, null, sweepGeometry);
    }

    private void DrawHotSpots(DrawingContext context, Rect bounds, Color coolSpec, double intensity, double warp)
    {
        int hotspotCount = 3;
        for (int i = 0; i < hotspotCount; i++)
        {
            double t = (i + 1.0) / (hotspotCount + 1.0);
            double drift = Math.Sin((_chromePhase * (0.4 + (i * 0.11))) + (i * 1.37)) * 0.12 * warp;
            double centerX = bounds.X + ((t + drift) * bounds.Width);
            double centerY = bounds.Y + bounds.Height * (0.18 + (0.24 * i)) + (Math.Cos((_chromePhase * 0.52) + i) * bounds.Height * 0.05);
            double radiusX = Math.Max(34, bounds.Width * (0.10 + (i * 0.025)));
            double radiusY = Math.Max(18, bounds.Height * (0.08 + (i * 0.018)));

            var brush = new RadialGradientBrush
            {
                GradientOrigin = new RelativePoint(0.42, 0.38, RelativeUnit.Relative),
                Center = new RelativePoint(centerX, centerY, RelativeUnit.Absolute),
                Radius = 1.0,
                GradientStops =
                {
                    new GradientStop(WithOpacity(Colors.White, 0.22 + (intensity * 0.1)), 0.0),
                    new GradientStop(WithOpacity(Blend(coolSpec, Colors.White, 0.58), 0.16 + (intensity * 0.08)), 0.22),
                    new GradientStop(WithOpacity(coolSpec, 0.06), 0.55),
                    new GradientStop(WithOpacity(coolSpec, 0), 1.0)
                }
            };

            context.DrawRectangle(
                brush,
                null,
                new Rect(centerX - radiusX, centerY - radiusY, radiusX * 2.0, radiusY * 2.0));
        }
    }

    private static void DrawInsetLine(DrawingContext context, Pen shadowPen, Pen highlightPen, Point a, Point b, double offset)
    {
        context.DrawLine(shadowPen, a, b);
        context.DrawLine(
            highlightPen,
            new Point(a.X, a.Y - offset),
            new Point(b.X, b.Y - offset));
    }

    private Color SampleEnvironmentColor(double u, double v, Color coolSpec, double intensity)
    {
        double horizon = Math.Exp(-Math.Pow((v - 0.18) / 0.16, 2.0));
        double lowerGlow = Math.Exp(-Math.Pow((v - 0.74) / 0.24, 2.0));
        double verticalBands = (Math.Sin((u * Math.PI * 7.0) + (_chromePhase * 0.23))
                              + Math.Cos((u * Math.PI * 15.0) - (_chromePhase * 0.14))
                              + 2.0) / 4.0;
        double lateralBloom = Math.Exp(-Math.Pow((u - 0.34 - (Math.Sin(_chromePhase * 0.19) * 0.08)) / 0.12, 2.0))
                            + (Math.Exp(-Math.Pow((u - 0.72 + (Math.Cos(_chromePhase * 0.16) * 0.07)) / 0.14, 2.0)) * 0.72);

        Color deep = Blend(ShadowColor, BaseColor, 0.18);
        Color mid = Blend(BaseColor, coolSpec, 0.24 + (intensity * 0.1));
        Color sky = Blend(coolSpec, Colors.White, 0.82);
        Color floor = Blend(ShadowColor, coolSpec, 0.16);

        Color topMix = Blend(mid, sky, Math.Clamp((horizon * 0.88) + (verticalBands * 0.22), 0.0, 1.0));
        Color bottomMix = Blend(deep, floor, Math.Clamp((lowerGlow * 0.75) + (verticalBands * 0.12), 0.0, 1.0));
        Color combined = Blend(bottomMix, topMix, Math.Clamp(0.18 + (horizon * 0.66) + (lateralBloom * 0.18), 0.0, 1.0));
        return Blend(combined, Colors.White, Math.Clamp(lateralBloom * 0.42, 0.0, 0.42));
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

    private static Color WithOpacity(Color color, double opacity)
        => Color.FromArgb(
            (byte)Math.Clamp((int)Math.Round(opacity * 255.0), 0, 255),
            color.R,
            color.G,
            color.B);

    private void UpdateAnimationState()
    {
        if (ChromeEnabled && VisualRoot != null)
        {
            if (!_animationTimer.IsEnabled)
                _animationTimer.Start();
        }
        else
        {
            _animationTimer.Stop();
        }
    }
}
