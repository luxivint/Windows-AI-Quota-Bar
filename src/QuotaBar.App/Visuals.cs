using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace QuotaBar.App;

/// <summary>Provider glyphs (simple original vector marks) and reusable UI pieces.</summary>
public static class Visuals
{
    public static Color BrandColor(string kind) => kind switch
    {
        "claude" => Color.FromRgb(0xD9, 0x77, 0x57),
        "codex" => Color.FromRgb(0x10, 0xA3, 0x7F),
        "antigravity" => Color.FromRgb(0x42, 0x85, 0xF4),
        "openrouter" => Color.FromRgb(0x6A, 0x6F, 0xF5),
        "copilot" => Color.FromRgb(0x8B, 0x5C, 0xF6),
        "zai" => Color.FromRgb(0x25, 0x63, 0xEB),
        "kimi" => Color.FromRgb(0x1F, 0x2A, 0x44),
        "minimax" => Color.FromRgb(0xE1, 0x1D, 0x48),
        "deepseek" => Color.FromRgb(0x4D, 0x6B, 0xFE),
        "moonshot" => Color.FromRgb(0x2B, 0x2F, 0x3A),
        "opencode-go" => Theme.Dark ? Color.FromRgb(0x8E, 0x9B, 0xFF) : Color.FromRgb(0x4F, 0x5B, 0xD5),
        _ => Color.FromRgb(0x88, 0x88, 0x88),
    };

    static readonly Dictionary<string, (Geometry geo, bool brand)?> _svgCache = new();

    /// <summary>Loads the single-path SVG icons shipped in Assets/icons (24x24 viewBox).</summary>
    static (Geometry geo, bool brand)? Svg(string file)
    {
        if (_svgCache.TryGetValue(file, out var cached)) return cached;
        (Geometry, bool)? result = null;
        try
        {
            var info = Application.GetResourceStream(new Uri($"pack://application:,,,/Assets/icons/{file}"));
            if (info != null)
            {
                using var r = new System.IO.StreamReader(info.Stream);
                var text = r.ReadToEnd();
                var d = System.Text.RegularExpressions.Regex.Match(text, "<path[^>]*\\sd=\"([^\"]+)\"").Groups[1].Value;
                var geo = Geometry.Parse(d).Clone();
                if (geo is PathGeometry pg && text.Contains("evenodd")) pg.FillRule = FillRule.EvenOdd;
                geo.Freeze();
                var brand = !text.Contains("currentColor") && !text.Contains("fill=\"#111\"");
                result = (geo, brand);
            }
        }
        catch { }
        _svgCache[file] = result;
        return result;
    }

    static System.Windows.Media.Imaging.BitmapImage? _agy;

    public static FrameworkElement Icon(string kind, double size, Brush? overrideBrush = null)
    {
        switch (kind)
        {
            case "claude":
            case "codex":
            case "opencode-go":
            {
                var file = kind switch { "claude" => "claude.svg", "codex" => "codex.svg", _ => "opencode.svg" };
                if (Svg(file) is { } svg)
                {
                    // Claude keeps its brand orange; monochrome marks follow the text color (or the taskbar color).
                    var fill = svg.brand ? new SolidColorBrush(BrandColor(kind)) : overrideBrush ?? Theme.Text;
                    var path = new Path { Data = svg.geo, Fill = fill, Stretch = Stretch.Uniform };
                    return new Viewbox { Width = size, Height = size, Child = new Grid { Width = 24, Height = 24, Children = { path } } };
                }
                break;
            }
            case "antigravity":
                try
                {
                    _agy ??= new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/Assets/icons/antigravity.png"));
                    var img = new Image { Source = _agy, Width = size, Height = size, Stretch = Stretch.Uniform };
                    RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                    return img;
                }
                catch { }
                break;
        }
        return Monogram(kind, size);
    }

    /// <summary>Rounded tile with initials for providers without a shipped icon.</summary>
    static FrameworkElement Monogram(string kind, double size)
    {
        var name = QuotaBar.Core.Providers.KeyProviders.Find(kind)?.Name ?? kind;
        var letters = string.Concat(name.Split(' ', '.', '-').Where(w => w.Length > 0).Take(kind == "openrouter" ? 1 : 1).Select(w => char.ToUpperInvariant(w[0])));
        if (kind == "openrouter") letters = "OR";
        if (kind == "zai") letters = "Z";
        var c = BrandColor(kind);
        return new Border
        {
            Width = size, Height = size, CornerRadius = new CornerRadius(size * 0.28),
            Background = new SolidColorBrush(c),
            Child = new TextBlock
            {
                Text = letters, Foreground = Brushes.White, FontWeight = FontWeights.Bold,
                FontSize = size * (letters.Length > 1 ? 0.42 : 0.55), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            },
        };
    }

    static Path Stroke(string data, Brush b, double t) => new()
    {
        Data = Geometry.Parse(data), Stroke = b, StrokeThickness = t,
        StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
    };

    public static TextBlock Glyph(string glyph, double size = 16, Brush? fg = null) => new()
    {
        Text = glyph, FontFamily = (FontFamily)Application.Current.Resources["IconFont"], FontSize = size,
        Foreground = fg ?? Theme.Text, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center,
    };

    public static TextBlock Label(string text, double size = 13, Brush? fg = null, FontWeight? weight = null) => new()
    {
        Text = text, FontSize = size, Foreground = fg ?? Theme.Text, FontWeight = weight ?? FontWeights.Normal,
        FontFamily = (FontFamily)Application.Current.Resources["UiFont"], TextTrimming = TextTrimming.CharacterEllipsis,
        VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>Fill brush: brand gradient while healthy, amber/red when running low.</summary>
    public static Brush LeftBrush(string? kind, double left)
    {
        if (left <= 10) return Theme.Bad;
        if (left <= 25) return Theme.Warn;
        if (kind is null) return Theme.Fill;
        var c = BrandColor(kind);
        var light = Mix(c, Colors.White, Theme.Dark ? 0.35 : 0.25);
        var g = new LinearGradientBrush(light, c, 0);
        g.Freeze();
        return g;
    }

    public static Color Mix(Color a, Color b, double t) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

    public static SolidColorBrush Tint(string kind, byte alpha)
    {
        var c = BrandColor(kind);
        var b = new SolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B));
        b.Freeze();
        return b;
    }

    /// <summary>Usage bar: filled part = what is LEFT, optional tick = where an even pace would be. Animates in.</summary>
    public static FrameworkElement Bar(double leftPercent, double? paceLeftPercent, double height = 8, string? kind = null)
    {
        var g = new Grid { Height = height, ClipToBounds = false };
        g.Children.Add(new Border { Background = Theme.Track, CornerRadius = new CornerRadius(height / 2) });
        var fill = new Border { Background = LeftBrush(kind, leftPercent), CornerRadius = new CornerRadius(height / 2), HorizontalAlignment = HorizontalAlignment.Left, Width = 0 };
        g.Children.Add(fill);
        var tick = new Border { Width = 2, Background = Theme.Text, Opacity = 0.5, CornerRadius = new CornerRadius(1), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, -3, 0, -3), Visibility = Visibility.Collapsed };
        g.Children.Add(tick);
        bool animated = false;
        g.SizeChanged += (_, _) =>
        {
            var target = Math.Max(leftPercent > 0 ? height : 0, g.ActualWidth * Math.Clamp(leftPercent, 0, 100) / 100);
            if (!animated && g.ActualWidth > 0)
            {
                animated = true;
                fill.BeginAnimation(FrameworkElement.WidthProperty, new System.Windows.Media.Animation.DoubleAnimation(0, target, TimeSpan.FromMilliseconds(550))
                { EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut } });
            }
            else { fill.BeginAnimation(FrameworkElement.WidthProperty, null); fill.Width = target; }
            if (paceLeftPercent is { } p && p > 1 && p < 99)
            {
                tick.Visibility = Visibility.Visible;
                tick.Margin = new Thickness(g.ActualWidth * p / 100 - 1, -3, 0, -3);
            }
        };
        return g;
    }

    /// <summary>Donut gauge, filled clockwise from 12 o'clock with what is LEFT. Optional content in the middle.</summary>
    public static FrameworkElement Ring(double leftPercent, double size, double thickness, string? kind, UIElement? center = null, Brush? track = null)
    {
        var g = new Grid { Width = size, Height = size };
        var r = (size - thickness) / 2;
        g.Children.Add(new Ellipse { Width = size - thickness, Height = size - thickness, Stroke = track ?? Theme.Track, StrokeThickness = thickness, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
        var f = Math.Clamp(leftPercent / 100, 0, 1);
        var brush = LeftBrush(kind, leftPercent);
        if (f >= 0.999)
            g.Children.Add(new Ellipse { Width = size - thickness, Height = size - thickness, Stroke = brush, StrokeThickness = thickness, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
        else if (f > 0.005)
        {
            var c = size / 2;
            var angle = f * 2 * Math.PI;
            var start = new Point(c, c - r);
            var end = new Point(c + r * Math.Sin(angle), c - r * Math.Cos(angle));
            var geo = new StreamGeometry();
            using (var ctx = geo.Open())
            {
                ctx.BeginFigure(start, false, false);
                ctx.ArcTo(end, new Size(r, r), 0, angle > Math.PI, SweepDirection.Clockwise, true, false);
            }
            geo.Freeze();
            g.Children.Add(new Path { Data = geo, Stroke = brush, StrokeThickness = thickness, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round });
        }
        if (center != null) g.Children.Add(center);
        return g;
    }

    public static Button LinkButton(string text, Action onClick, string glyph = "")
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        sp.Children.Add(Label(text, 12.5));
        if (glyph.Length > 0) sp.Children.Add(new TextBlock
        {
            Text = glyph, FontFamily = (FontFamily)Application.Current.Resources["IconFont"], FontSize = 11,
            Foreground = Theme.Text, Margin = new Thickness(6, 1, 0, 0), VerticalAlignment = VerticalAlignment.Center,
        });
        var b = new Button { Content = sp, Margin = new Thickness(0, 0, 8, 0), Cursor = Cursors.Hand, Template = ChipTemplate(), Padding = new Thickness(10, 4, 10, 5) };
        b.Click += (_, _) => onClick();
        return b;
    }

    static ControlTemplate? _chip;
    public static ControlTemplate ChipTemplate()
    {
        if (_chip != null) return _chip;
        var border = new FrameworkElementFactory(typeof(Border), "bd");
        border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
        border.SetBinding(Border.PaddingProperty, new System.Windows.Data.Binding("Padding") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        border.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        border.SetBinding(Border.BorderBrushProperty, new System.Windows.Data.Binding("BorderBrush") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        var cp = new FrameworkElementFactory(typeof(ContentPresenter));
        cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        cp.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(cp);
        _chip = new ControlTemplate(typeof(Button)) { VisualTree = border };
        return _chip;
    }

    public static Button Chip(string text, Action onClick, bool primary = false)
    {
        var b = new Button
        {
            Content = Label(text, 12.5, primary ? Brushes.White : Theme.Text),
            Template = ChipTemplate(), Cursor = Cursors.Hand, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(10, 4, 10, 5),
            Background = primary ? Theme.Accent : Theme.Chip, BorderBrush = primary ? Theme.Accent : Theme.Border,
        };
        b.Click += (_, _) => onClick();
        return b;
    }

    public static Ellipse Dot(Brush b, double size = 8) => new() { Width = size, Height = size, Fill = b, Margin = new Thickness(8, 1, 0, 0), VerticalAlignment = VerticalAlignment.Center };

    /// <summary>Status color for a window: red if projected to run out before reset, amber if ahead of pace.</summary>
    public static Brush WindowStatus(UsageWindow w, DateTimeOffset now)
    {
        if (w.LeftPercent <= 10 || w.ProjectedRunOut(now) != null) return Theme.Bad;
        if (w.PaceDelta(now) is > 5) return Theme.Warn;
        return Theme.Good;
    }

    public static string ShortName(ProviderSnapshot p) =>
        p.Account is { } a && p.ProviderKind is "antigravity" ? a.Split('@')[0] : p.Account is { } acc && p.ProviderKind is "opencode-go" ? acc : p.DisplayName;
}
