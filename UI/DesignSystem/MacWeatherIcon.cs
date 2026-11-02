using System;
using System.Windows;
using System.Windows.Media;

namespace SkyBar.UI.DesignSystem;

/// <summary>
/// Menggambar ikon cuaca bergaya 3D volumetrik Apple macOS Weather
/// (lengkap dengan badge squircle biru langit, matahari 3D bergradien emas,
/// awan 3D berlapis dengan bias cahaya hangat matahari, dan tetesan hujan).
/// </summary>
public sealed class MacWeatherIcon : FrameworkElement
{
    public static readonly DependencyProperty IconKindProperty =
        DependencyProperty.Register(
            nameof(IconKind),
            typeof(string),
            typeof(MacWeatherIcon),
            new FrameworkPropertyMetadata("PartlyCloudy", FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ShowSquircleBadgeProperty =
        DependencyProperty.Register(
            nameof(ShowSquircleBadge),
            typeof(bool),
            typeof(MacWeatherIcon),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public string IconKind
    {
        get => (string)GetValue(IconKindProperty);
        set => SetValue(IconKindProperty, value);
    }

    public bool ShowSquircleBadge
    {
        get => (bool)GetValue(ShowSquircleBadgeProperty);
        set => SetValue(ShowSquircleBadgeProperty, value);
    }

    private static readonly LinearGradientBrush SquircleSkyBrush = CreateLinear(
        Color.FromRgb(0x2B, 0x86, 0xFF),
        Color.FromRgb(0x0D, 0x52, 0xC6),
        new Point(0.2, 0), new Point(0.8, 1));

    private static readonly Pen SquircleBorderPen = CreatePen(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF), 1.0);

    private static readonly RadialGradientBrush SunSphereBrush = CreateRadial(
        Color.FromRgb(0xFF, 0xF3, 0x94),
        Color.FromRgb(0xFF, 0xC1, 0x1E),
        Color.FromRgb(0xF0, 0x8C, 0x00),
        new Point(0.35, 0.30));

    private static readonly RadialGradientBrush SunGlowBrush = CreateRadial(
        Color.FromArgb(0x66, 0xFF, 0xCC, 0x33),
        Color.FromArgb(0x22, 0xFF, 0xAE, 0x00),
        Color.FromArgb(0x00, 0xFF, 0xAE, 0x00),
        new Point(0.5, 0.5));

    private static readonly RadialGradientBrush MoonSphereBrush = CreateRadial(
        Color.FromRgb(0xFF, 0xFA, 0xD6),
        Color.FromRgb(0xE2, 0xEA, 0xF8),
        Color.FromRgb(0x9E, 0xB4, 0xD8),
        new Point(0.35, 0.35));

    private static readonly LinearGradientBrush CloudBodyBrush = CreateLinear(
        Color.FromRgb(0xFF, 0xFF, 0xFF),
        Color.FromRgb(0xCE, 0xDF, 0xF5),
        new Point(0.5, 0.0), new Point(0.5, 1.0));

    private static readonly RadialGradientBrush CloudWarmBleedBrush = CreateRadial(
        Color.FromArgb(0x80, 0xFF, 0xD8, 0x5E),
        Color.FromArgb(0x30, 0xFF, 0xC8, 0x3B),
        Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF),
        new Point(0.68, 0.38));

    private static readonly RadialGradientBrush CloudDomeHighlightBrush = CreateRadial(
        Color.FromArgb(0xEE, 0xFF, 0xFF, 0xFF),
        Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF),
        Color.FromArgb(0x00, 0xDF, 0xEC, 0xFA),
        new Point(0.32, 0.28));

    private static readonly Pen CloudRimPen = CreatePen(Color.FromArgb(0x70, 0xFF, 0xFF, 0xFF), 0.8);

    private static readonly LinearGradientBrush RainDropBrush = CreateLinear(
        Color.FromRgb(0x66, 0xCA, 0xFF),
        Color.FromRgb(0x1E, 0x88, 0xFF),
        new Point(0, 0), new Point(0, 1));

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth > 0 ? ActualWidth : (Width > 0 ? Width : 32);
        double h = ActualHeight > 0 ? ActualHeight : (Height > 0 ? Height : 32);
        double size = Math.Min(w, h);
        if (size <= 2) return;

        double ox = (w - size) / 2.0;
        double oy = (h - size) / 2.0;

        dc.PushTransform(new TranslateTransform(ox, oy));
        try
        {
            if (ShowSquircleBadge)
            {
                double r = size * 0.24;
                dc.DrawRoundedRectangle(SquircleSkyBrush, SquircleBorderPen, new Rect(0, 0, size, size), r, r);
            }

            string kind = IconKind ?? "PartlyCloudy";

            if (kind.Equals("Sun", StringComparison.OrdinalIgnoreCase))
            {
                Point center = new(size * 0.50, size * 0.50);
                dc.DrawEllipse(SunGlowBrush, null, center, size * 0.42, size * 0.42);
                dc.DrawEllipse(SunSphereBrush, null, center, size * 0.29, size * 0.29);
                return;
            }

            // 1. Matahari / Bulan di kanan atas belakang awan
            if (kind.Equals("Night", StringComparison.OrdinalIgnoreCase))
            {
                Point moonCenter = new(size * 0.64, size * 0.36);
                dc.DrawEllipse(MoonSphereBrush, null, moonCenter, size * 0.20, size * 0.20);
            }
            else if (!kind.Equals("Cloudy", StringComparison.OrdinalIgnoreCase))
            {
                Point sunCenter = new(size * 0.63, size * 0.37);
                dc.DrawEllipse(SunGlowBrush, null, sunCenter, size * 0.31, size * 0.31);
                dc.DrawEllipse(SunSphereBrush, null, sunCenter, size * 0.21, size * 0.21);
            }

            // 2. Siluet Awan 3D Volumetrik khas macOS Weather
            double cloudOffsetY = kind.Equals("Rain", StringComparison.OrdinalIgnoreCase) ? -size * 0.05 : 0;
            Geometry cloudGeo = BuildCloudGeometry(size, cloudOffsetY);

            dc.DrawGeometry(CloudBodyBrush, CloudRimPen, cloudGeo);

            // Bias cahaya hangat matahari yang menembus awan (seperti pada ikon Apple Weather)
            if (!kind.Equals("Cloudy", StringComparison.OrdinalIgnoreCase) &&
                !kind.Equals("Night", StringComparison.OrdinalIgnoreCase))
            {
                dc.PushClip(cloudGeo);
                dc.DrawEllipse(
                    CloudWarmBleedBrush,
                    null,
                    new Point(size * 0.60, size * 0.46 + cloudOffsetY),
                    size * 0.28,
                    size * 0.24);
                dc.Pop();
            }

            // Tonjolan kubah kiri 3D lembut di dalam awan
            dc.PushClip(cloudGeo);
            dc.DrawEllipse(
                CloudDomeHighlightBrush,
                null,
                new Point(size * 0.36, size * 0.49 + cloudOffsetY),
                size * 0.22,
                size * 0.20);
            dc.Pop();

            // 3. Tetesan hujan bila kondisi Rain
            if (kind.Equals("Rain", StringComparison.OrdinalIgnoreCase))
            {
                var pen = new Pen(RainDropBrush, Math.Max(1.4, size * 0.065))
                {
                    StartLineCap = PenLineCap.Round,
                    EndLineCap = PenLineCap.Round
                };
                pen.Freeze();

                double yTop = size * 0.74;
                double yBot = size * 0.86;
                double dx = -size * 0.04;

                dc.DrawLine(pen, new Point(size * 0.33, yTop), new Point(size * 0.33 + dx, yBot));
                dc.DrawLine(pen, new Point(size * 0.49, yTop + size * 0.02), new Point(size * 0.49 + dx, yBot + size * 0.02));
                dc.DrawLine(pen, new Point(size * 0.65, yTop), new Point(size * 0.65 + dx, yBot));
            }
        }
        finally
        {
            dc.Pop();
        }
    }

    private static Geometry BuildCloudGeometry(double s, double dy)
    {
        // Menggabungkan kapsul dasar awan dengan kubah-kubah bulat halus
        var group = new GeometryGroup { FillRule = FillRule.Nonzero };

        // Dasar awan membulat
        double baseLeft = s * 0.16;
        double baseTop = s * 0.50 + dy;
        double baseWidth = s * 0.66;
        double baseHeight = s * 0.24;
        double baseRadius = baseHeight / 2.0;
        group.Children.Add(new RectangleGeometry(new Rect(baseLeft, baseTop, baseWidth, baseHeight), baseRadius, baseRadius));

        // Kubah utama kiri-tengah (besar & tinggi seperti ikon Apple Weather)
        group.Children.Add(new EllipseGeometry(new Point(s * 0.38, s * 0.48 + dy), s * 0.19, s * 0.18));

        // Kubah kanan-tengah
        group.Children.Add(new EllipseGeometry(new Point(s * 0.57, s * 0.53 + dy), s * 0.15, s * 0.14));

        // Kubah ujung kiri
        group.Children.Add(new EllipseGeometry(new Point(s * 0.27, s * 0.58 + dy), s * 0.11, s * 0.11));

        group.Freeze();
        return Geometry.Combine(group, Geometry.Empty, GeometryCombineMode.Union, null);
    }

    private static LinearGradientBrush CreateLinear(Color c1, Color c2, Point start, Point end)
    {
        var b = new LinearGradientBrush(c1, c2, start, end);
        b.Freeze();
        return b;
    }

    private static RadialGradientBrush CreateRadial(Color c1, Color c2, Color c3, Point origin)
    {
        var stops = new GradientStopCollection
        {
            new GradientStop(c1, 0.0),
            new GradientStop(c2, 0.55),
            new GradientStop(c3, 1.0)
        };
        var b = new RadialGradientBrush(stops)
        {
            GradientOrigin = origin,
            Center = new Point(0.5, 0.5),
            RadiusX = 0.5,
            RadiusY = 0.5
        };
        b.Freeze();
        return b;
    }

    private static Pen CreatePen(Color c, double thickness)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        var p = new Pen(b, thickness);
        p.Freeze();
        return p;
    }
}

