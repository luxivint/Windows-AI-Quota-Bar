using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace QuotaBar.App;

/// <summary>
/// "% left" over time for one usage window.
/// Cycle mode: the current window only, with the even-use guide (dashed diagonal) and the projection at
/// the current pace. Range mode: the last N days.
/// </summary>
public sealed class UsageChart : FrameworkElement
{
    readonly List<(DateTimeOffset t, double left)> _pts;
    readonly DateTimeOffset _from, _to, _now;
    readonly Color _color;
    readonly bool _cycle;
    readonly UsageWindow _w;

    public UsageChart(UsageWindow w, IEnumerable<HistoryPoint> points, string kind, bool cycle, DateTimeOffset from, DateTimeOffset to, DateTimeOffset now)
    {
        _w = w; _cycle = cycle; _from = from; _to = to; _now = now;
        _pts = points.Select(p => (p.At.ToLocalTime(), 100 - p.Used)).ToList();
        _color = Visuals.BrandColor(kind);
        Height = 96;
        Margin = new Thickness(0, 8, 0, 2);
        SnapsToDevicePixels = true;
        System.Windows.Controls.ToolTipService.SetInitialShowDelay(this, 100);
    }

    const double Bottom = 16; // room for the time labels

    double X(DateTimeOffset t) => (t - _from).TotalSeconds / Math.Max(1, (_to - _from).TotalSeconds) * ActualWidth;
    double Y(double left) => (1 - Math.Clamp(left, 0, 100) / 100) * (ActualHeight - Bottom - 2) + 1;

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth; var h = ActualHeight - Bottom;
        if (w <= 0 || h <= 0) return;
        var grid = new Pen(Theme.Border, 1) { DashStyle = new DashStyle(new double[] { 2, 3 }, 0) };
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, ActualHeight)); // hit-testable for the tooltip
        foreach (var lvl in new[] { 100.0, 50, 0 }) dc.DrawLine(grid, new Point(0, Y(lvl)), new Point(w, Y(lvl)));

        var line = new Pen(new SolidColorBrush(_color), 1.8) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        var dim = new Pen(Theme.SubText, 1.2) { DashStyle = new DashStyle(new double[] { 3, 3 }, 0) };

        if (_cycle && _w.WindowSeconds is { } len && _w.ResetsAt is { } reset)
        {
            // Even use: 100% at the start of the window, 0% when it resets.
            var start = reset.AddSeconds(-len).ToLocalTime();
            dc.DrawLine(dim, new Point(X(start), Y(100)), new Point(X(reset.ToLocalTime()), Y(0)));
        }

        // Data line (breaks where readings are far apart, e.g. PC was off).
        var visible = _pts.Where(p => p.t >= _from && p.t <= _to).ToList();
        var gap = TimeSpan.FromSeconds(Math.Max(2 * 3600, (_to - _from).TotalSeconds / 40));
        var fill = new SolidColorBrush(Color.FromArgb((byte)(Theme.Dark ? 46 : 34), _color.R, _color.G, _color.B));
        int i = 0;
        while (i < visible.Count)
        {
            int j = i;
            while (j + 1 < visible.Count && visible[j + 1].t - visible[j].t <= gap) j++;
            var seg = visible.GetRange(i, j - i + 1);
            if (seg.Count == 1) dc.DrawEllipse(line.Brush, null, new Point(X(seg[0].t), Y(seg[0].left)), 2, 2);
            else
            {
                // Step line: usage only changes at readings.
                var geo = new StreamGeometry();
                using (var g = geo.Open())
                {
                    g.BeginFigure(new Point(X(seg[0].t), h), true, true);
                    g.LineTo(new Point(X(seg[0].t), Y(seg[0].left)), false, false);
                    for (int k = 1; k < seg.Count; k++)
                    {
                        g.LineTo(new Point(X(seg[k].t), Y(seg[k - 1].left)), false, true);
                        g.LineTo(new Point(X(seg[k].t), Y(seg[k].left)), false, true);
                    }
                    g.LineTo(new Point(X(seg[^1].t), h), false, false);
                }
                geo.Freeze();
                dc.DrawGeometry(fill, null, geo);
                var path = new StreamGeometry();
                using (var g = path.Open())
                {
                    g.BeginFigure(new Point(X(seg[0].t), Y(seg[0].left)), false, false);
                    for (int k = 1; k < seg.Count; k++)
                    {
                        g.LineTo(new Point(X(seg[k].t), Y(seg[k - 1].left)), true, true);
                        g.LineTo(new Point(X(seg[k].t), Y(seg[k].left)), true, true);
                    }
                }
                path.Freeze();
                dc.DrawGeometry(null, line, path);
            }
            i = j + 1;
        }

        // Projection at the current pace, from now to run-out (or to the reset).
        if (_cycle && _w.ResetsAt is { } rs && _w.WindowSeconds is { } wl)
        {
            var elapsed = wl - (rs - _now).TotalSeconds;
            if (elapsed > 60 && _w.UsedPercent > 0)
            {
                var rate = _w.UsedPercent / elapsed; // % per second
                var end = _w.ProjectedRunOut(_now) ?? rs;
                var leftAtEnd = Math.Max(0, _w.LeftPercent - rate * (end - _now).TotalSeconds);
                var proj = new Pen(new SolidColorBrush(Color.FromArgb(170, _color.R, _color.G, _color.B)), 1.6) { DashStyle = new DashStyle(new double[] { 2, 2 }, 0) };
                dc.DrawLine(proj, new Point(X(_now.ToLocalTime()), Y(_w.LeftPercent)), new Point(X(end.ToLocalTime()), Y(leftAtEnd)));
            }
            dc.DrawEllipse(line.Brush, new Pen(Theme.Bg, 1.5), new Point(X(_now.ToLocalTime()), Y(_w.LeftPercent)), 3.5, 3.5);
        }

        // Time labels.
        var span = _to - _from;
        string Fmt(DateTimeOffset t) => span.TotalHours <= 26 ? t.ToString("HH:mm") : t.ToString(span.TotalDays <= 8 ? "ddd" : "d MMM", CultureInfo.CurrentUICulture);
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        FormattedText Text(string s) => new(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 10, Theme.SubText, dpi);
        var a = Text(Fmt(_from)); dc.DrawText(a, new Point(0, h + 3));
        var b = Text(_cycle ? "↻ " + Fmt(_to) : L.T("now", "şimdi")); dc.DrawText(b, new Point(w - b.Width, h + 3));
    }

    protected override void OnMouseMove(System.Windows.Input.MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_pts.Count == 0) return;
        var x = e.GetPosition(this).X;
        var t = _from + TimeSpan.FromSeconds(x / Math.Max(1, ActualWidth) * (_to - _from).TotalSeconds);
        var near = _pts.Where(p => p.t <= t).DefaultIfEmpty(_pts[0]).Last();
        ToolTip = $"{near.t:ddd HH:mm} · {near.left:0}% " + L.T("left", "kaldı");
    }
}
