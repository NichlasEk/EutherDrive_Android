using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace EutherDrive.UI.Controls;

public class MetalSheenOverlay : Control
{
    private bool _sheenEnabled;
    private double _sheenOpacity = 0.18;
    private double _sheenAngleDegrees = -24.0;
    private double _bandThickness = 0.14;
    private double _edgeOpacity = 0.12;
    private bool _rivetsEnabled;
    private int _rivetCount = 6;
    private double _rivetRadius = 4.0;
    private double _rivetInset = 14.0;
    private double _rivetOpacity = 0.7;
    private Color _tintColor = Color.Parse("#E6EEF7");
    private Color _edgeColor = Color.Parse("#F6FAFF");
    private Color _rivetColor = Color.Parse("#C8D2DC");

    public bool SheenEnabled
    {
        get => _sheenEnabled;
        set
        {
            if (_sheenEnabled == value)
                return;

            _sheenEnabled = value;
            IsVisible = value;
            InvalidateVisual();
        }
    }

    public double SheenOpacity
    {
        get => _sheenOpacity;
        set
        {
            double clamped = Math.Clamp(value, 0.0, 1.0);
            if (Math.Abs(_sheenOpacity - clamped) < 0.0001)
                return;

            _sheenOpacity = clamped;
            InvalidateVisual();
        }
    }

    public double SheenAngleDegrees
    {
        get => _sheenAngleDegrees;
        set
        {
            if (Math.Abs(_sheenAngleDegrees - value) < 0.0001)
                return;

            _sheenAngleDegrees = value;
            InvalidateVisual();
        }
    }

    public double BandThickness
    {
        get => _bandThickness;
        set
        {
            double clamped = Math.Clamp(value, 0.02, 0.45);
            if (Math.Abs(_bandThickness - clamped) < 0.0001)
                return;

            _bandThickness = clamped;
            InvalidateVisual();
        }
    }

    public double EdgeOpacity
    {
        get => _edgeOpacity;
        set
        {
            double clamped = Math.Clamp(value, 0.0, 1.0);
            if (Math.Abs(_edgeOpacity - clamped) < 0.0001)
                return;

            _edgeOpacity = clamped;
            InvalidateVisual();
        }
    }

    public bool RivetsEnabled
    {
        get => _rivetsEnabled;
        set
        {
            if (_rivetsEnabled == value)
                return;

            _rivetsEnabled = value;
            InvalidateVisual();
        }
    }

    public int RivetCount
    {
        get => _rivetCount;
        set
        {
            int clamped = Math.Clamp(value, 2, 24);
            if (_rivetCount == clamped)
                return;

            _rivetCount = clamped;
            InvalidateVisual();
        }
    }

    public double RivetRadius
    {
        get => _rivetRadius;
        set
        {
            double clamped = Math.Clamp(value, 1.5, 20.0);
            if (Math.Abs(_rivetRadius - clamped) < 0.0001)
                return;

            _rivetRadius = clamped;
            InvalidateVisual();
        }
    }

    public double RivetInset
    {
        get => _rivetInset;
        set
        {
            double clamped = Math.Clamp(value, 4.0, 64.0);
            if (Math.Abs(_rivetInset - clamped) < 0.0001)
                return;

            _rivetInset = clamped;
            InvalidateVisual();
        }
    }

    public double RivetOpacity
    {
        get => _rivetOpacity;
        set
        {
            double clamped = Math.Clamp(value, 0.0, 1.0);
            if (Math.Abs(_rivetOpacity - clamped) < 0.0001)
                return;

            _rivetOpacity = clamped;
            InvalidateVisual();
        }
    }

    public Color TintColor
    {
        get => _tintColor;
        set
        {
            if (_tintColor == value)
                return;

            _tintColor = value;
            InvalidateVisual();
        }
    }

    public Color EdgeColor
    {
        get => _edgeColor;
        set
        {
            if (_edgeColor == value)
                return;

            _edgeColor = value;
            InvalidateVisual();
        }
    }

    public Color RivetColor
    {
        get => _rivetColor;
        set
        {
            if (_rivetColor == value)
                return;

            _rivetColor = value;
            InvalidateVisual();
        }
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        Rect bounds = new(Bounds.Size);
        if (!_sheenEnabled || _sheenOpacity <= 0.001 || bounds.Width < 8 || bounds.Height < 8)
            return;

        using IDisposable clip = context.PushClip(bounds);

        DrawEdgeSheen(context, bounds);
        DrawBand(context, bounds, -0.42, 0.58);
        DrawBand(context, bounds, 0.08, 1.0);
        DrawBand(context, bounds, 0.56, 0.42);

        if (_rivetsEnabled)
            DrawRivets(context, bounds);
    }

    private void DrawEdgeSheen(DrawingContext context, Rect bounds)
    {
        double edgeHeight = Math.Max(3, Math.Min(bounds.Height * 0.075, 34));
        double sideWidth = Math.Max(2, Math.Min(bounds.Width * 0.025, 20));
        byte edgeAlpha = ToAlpha(_edgeOpacity * _sheenOpacity);

        var topBrush = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Absolute),
            EndPoint = new RelativePoint(0, edgeHeight, RelativeUnit.Absolute),
            GradientStops =
            {
                new GradientStop(WithAlpha(_edgeColor, edgeAlpha), 0.0),
                new GradientStop(WithAlpha(_edgeColor, (byte)(edgeAlpha * 0.55)), 0.35),
                new GradientStop(WithAlpha(_edgeColor, 0), 1.0)
            }
        };
        context.DrawRectangle(topBrush, null, new Rect(0, 0, bounds.Width, edgeHeight));

        var leftBrush = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Absolute),
            EndPoint = new RelativePoint(sideWidth, 0, RelativeUnit.Absolute),
            GradientStops =
            {
                new GradientStop(WithAlpha(_edgeColor, (byte)(edgeAlpha * 0.55)), 0.0),
                new GradientStop(WithAlpha(_edgeColor, 0), 1.0)
            }
        };
        context.DrawRectangle(leftBrush, null, new Rect(0, 0, sideWidth, bounds.Height));
    }

    private void DrawBand(DrawingContext context, Rect bounds, double normalizedOffset, double intensity)
    {
        double radians = _sheenAngleDegrees * Math.PI / 180.0;
        Vector direction = new(Math.Cos(radians), Math.Sin(radians));
        Vector normal = new(-direction.Y, direction.X);
        Point center = bounds.Center;
        double diagonal = Math.Sqrt((bounds.Width * bounds.Width) + (bounds.Height * bounds.Height));
        double halfLength = diagonal;
        double halfThickness = Math.Max(8, Math.Min(bounds.Width, bounds.Height) * _bandThickness * 0.5 * intensity);
        Point bandCenter = center + (normal * (normalizedOffset * diagonal * 0.5));

        Point p0 = bandCenter - (direction * halfLength) - (normal * halfThickness);
        Point p1 = bandCenter + (direction * halfLength) - (normal * halfThickness);
        Point p2 = bandCenter + (direction * halfLength) + (normal * halfThickness);
        Point p3 = bandCenter - (direction * halfLength) + (normal * halfThickness);

        var geometry = new StreamGeometry();
        using (StreamGeometryContext geo = geometry.Open())
        {
            geo.BeginFigure(p0, true);
            geo.LineTo(p1);
            geo.LineTo(p2);
            geo.LineTo(p3);
            geo.EndFigure(true);
        }

        byte alpha = ToAlpha(_sheenOpacity * 0.55 * intensity);
        byte midAlpha = ToAlpha(_sheenOpacity * 0.20 * intensity);
        var brush = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(p0, RelativeUnit.Absolute),
            EndPoint = new RelativePoint(p3, RelativeUnit.Absolute),
            GradientStops =
            {
                new GradientStop(WithAlpha(_tintColor, 0), 0.0),
                new GradientStop(WithAlpha(_tintColor, midAlpha), 0.18),
                new GradientStop(WithAlpha(_edgeColor, alpha), 0.5),
                new GradientStop(WithAlpha(_tintColor, midAlpha), 0.82),
                new GradientStop(WithAlpha(_tintColor, 0), 1.0)
            }
        };

        context.DrawGeometry(brush, null, geometry);
    }

    private void DrawRivets(DrawingContext context, Rect bounds)
    {
        double inset = Math.Min(_rivetInset, Math.Min(bounds.Width, bounds.Height) * 0.25);
        double radius = Math.Min(_rivetRadius, Math.Max(1.5, Math.Min(bounds.Width, bounds.Height) * 0.03));
        int count = _rivetCount;
        byte baseAlpha = ToAlpha(_rivetOpacity);
        byte shadowAlpha = ToAlpha(_rivetOpacity * 0.26);

        for (int i = 0; i < count; i++)
        {
            double t = count == 1 ? 0.5 : (double)i / (count - 1);
            double x = inset + ((bounds.Width - (inset * 2)) * t);
            DrawRivet(context, new Point(x, inset), radius, baseAlpha, shadowAlpha);

            if (i != 0 && i != count - 1)
            {
                double y = inset + ((bounds.Height - (inset * 2)) * t);
                DrawRivet(context, new Point(inset, y), radius, baseAlpha, shadowAlpha);
                DrawRivet(context, new Point(bounds.Width - inset, y), radius, baseAlpha, shadowAlpha);
            }
        }
    }

    private void DrawRivet(DrawingContext context, Point center, double radius, byte baseAlpha, byte shadowAlpha)
    {
        var shadowBrush = new SolidColorBrush(WithAlpha(Colors.Black, shadowAlpha));
        context.DrawEllipse(shadowBrush, null, center + new Vector(1.2, 1.4), radius, radius);

        var rivetBrush = new RadialGradientBrush
        {
            Center = new RelativePoint(0.35, 0.32, RelativeUnit.Relative),
            GradientOrigin = new RelativePoint(0.35, 0.32, RelativeUnit.Relative),
            Radius = 0.8,
            GradientStops =
            {
                new GradientStop(WithAlpha(_edgeColor, (byte)Math.Min(255, baseAlpha + 24)), 0.0),
                new GradientStop(WithAlpha(_rivetColor, baseAlpha), 0.38),
                new GradientStop(WithAlpha(Darken(_rivetColor, 0.68), baseAlpha), 1.0)
            }
        };

        var pen = new Pen(new SolidColorBrush(WithAlpha(Darken(_rivetColor, 0.52), baseAlpha)), 1);
        context.DrawEllipse(rivetBrush, pen, center, radius, radius);
        context.DrawEllipse(null, new Pen(new SolidColorBrush(WithAlpha(_edgeColor, (byte)(baseAlpha * 0.75))), 1), center + new Vector(-0.7, -0.7), radius * 0.46, radius * 0.34);
    }

    private static byte ToAlpha(double opacity)
        => (byte)Math.Clamp((int)Math.Round(opacity * 255), 0, 255);

    private static Color WithAlpha(Color color, byte alpha)
        => Color.FromArgb(alpha, color.R, color.G, color.B);

    private static Color Darken(Color color, double factor)
        => Color.FromArgb(
            color.A,
            (byte)Math.Clamp((int)Math.Round(color.R * factor), 0, 255),
            (byte)Math.Clamp((int)Math.Round(color.G * factor), 0, 255),
            (byte)Math.Clamp((int)Math.Round(color.B * factor), 0, 255));
}
