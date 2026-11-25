using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using MultiServerSync.Services;

namespace MultiServerSync.Views.Controls;

/// <summary>Величина на графике: подпись (в подсказке и у «зеркала»), значение, пик шага и момент пика за период.</summary>
public sealed record ChartRow(string Label, Func<MonitorPoint, double> Value, Func<MonitorPoint, double>? Peak = null, long? PeakT = null);

/// <summary>Что нарисовать: точки, окно периода, шаг, верх шкалы и как писать числа.</summary>
public sealed class ChartData
{
    public required IReadOnlyList<MonitorPoint> Points { get; init; }

    public required IReadOnlyList<ChartRow> Rows { get; init; }

    public required long From { get; init; }

    public required long To { get; init; }

    public required long Step { get; init; }

    public required double Top { get; init; }

    public required Func<double, string> Format { get; init; }

    /// <summary>Две величины: первая вверх от нуля, вторая вниз, шкала общая (сеть, диск).</summary>
    public bool Mirror { get; init; }

    public MonitorRange Range { get; init; }
}

/// <summary>
/// График карточки «Мониторинга» — как в макете «Здоровье сервера»: плавная линия среднего с заливкой, тающей к нулю;
/// слева верх шкалы и ноль, снизу время; у сети и диска «зеркало» — входящий (чтение) вверх, исходящий (запись) вниз.
/// Момент пика за период — тонкая черта с ромбом на краю поля. Наведение — перекрестие, точки и подсказка с шагом.
/// Где данных нет (до первой точки, в простое дольше 2,5 шага, после последней) — линия лежит на нуле;
/// недописанный хвост (sysstat пишет раз в 10 мин) тянется до края последним значением.
/// </summary>
public sealed class MonitorChart : FrameworkElement
{
    private const double PadT = 6;
    private const double PadR = 6;
    private const double PadB = 18;
    private const double AxisSize = 10.5;

    private ChartData? _data;
    private double? _hoverX;

    public MonitorChart()
    {
        SnapsToDevicePixels = true;
        MouseMove += (_, e) => { _hoverX = e.GetPosition(this).X; InvalidateVisual(); };
        MouseLeave += (_, _) => { _hoverX = null; InvalidateVisual(); };

        // цвета берутся при рисовании — после смены темы рисуем заново
        Core.AppTheme.Follow(this, InvalidateVisual);
    }

    public void Show(ChartData data)
    {
        _data = data;
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize) => new(0, 0);

    private Brush Res(string key, Brush fallback) => TryFindResource(key) as Brush ?? fallback;

    private FormattedText Text(string text, double size, Brush brush, FontWeight? weight = null) => new(
        text,
        CultureInfo.CurrentUICulture,
        FlowDirection.LeftToRight,
        new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, weight ?? FontWeights.Normal, FontStretches.Normal),
        size,
        brush,
        null,
        TextFormattingMode.Display,
        VisualTreeHelper.GetDpi(this).PixelsPerDip);

    /// <summary>Подпись верха шкалы: без «/с» и без «.0%».</summary>
    private static string TopText(ChartData d) => d.Format(d.Top).Replace("/с", string.Empty).Replace(".0%", "%");

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;

        // прозрачная подложка: график ловит мышь целиком, а не только по линиям
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, Math.Max(0, w), Math.Max(0, h)));

        if (_data is not { } d || d.Rows.Count == 0 || w < 40 || h < PadT + PadB + 12)
        {
            return;
        }

        var axis = Res("TextFillColorTertiaryBrush", Brushes.Gray);
        var text2 = Res("TextFillColorSecondaryBrush", Brushes.LightGray);
        var hair = Res("ControlStrokeColorDefaultBrush", Brushes.DimGray);
        var strong = Res("SurfaceStrokeColorDefaultBrush", Brushes.Gray);
        var card = Res("SolidBackgroundFillColorSecondaryBrush", Brushes.Black);
        var series = Palette.Series1;

        var top = TopText(d);
        var topLabel = Text(top, AxisSize, axis);
        var zeroLabel = Text("0", AxisSize, axis);
        var padL = Math.Max(topLabel.WidthIncludingTrailingWhitespace, zeroLabel.WidthIncludingTrailingWhitespace) + 10;
        var iw = w - padL - PadR;
        var plotH = h - PadT - PadB;
        var mirror = d.Mirror && d.Rows.Count > 1;
        var ih = mirror ? plotH / 2 : plotH;
        var zeroY = PadT + ih;
        var span = (double)Math.Max(1, d.To - d.From);

        double X(long t) => padL + Math.Clamp((t - d.From) / span, 0, 1) * iw;
        double Y(int row, double v)
        {
            var k = Math.Min(1, Math.Max(0, v / (d.Top > 0 ? d.Top : 1))) * ih;
            return mirror && row == 1 ? zeroY + k : zeroY - k;
        }

        double Mid(MonitorPoint p) => X(Math.Min(d.To, p.T + d.Step / 2));

        var gl = new Pen(hair, 1);
        gl.Freeze();
        var zl = new Pen(strong, 1);
        zl.Freeze();

        void HLine(Pen pen, double y) => dc.DrawLine(pen, new Point(padL, Math.Round(y) + 0.5), new Point(w - PadR, Math.Round(y) + 0.5));
        void Label(FormattedText t, double y) => dc.DrawText(t, new Point(padL - 6 - t.WidthIncludingTrailingWhitespace, y - t.Height / 2));

        HLine(gl, PadT);
        Label(topLabel, PadT);
        HLine(mirror ? zl : gl, zeroY);
        Label(zeroLabel, zeroY);

        if (mirror)
        {
            HLine(gl, PadT + plotH);
            Label(Text(top, AxisSize, axis), PadT + plotH);
        }

        // время снизу: начало, две трети и конец окна
        for (var i = 0; i <= 3; i++)
        {
            var t = d.From + (long)(span * i / 3);
            var at = DateTimeOffset.FromUnixTimeSeconds(t).LocalDateTime;
            var s = MonitorRanges.Hourly(d.Range) ? at.ToString("dd.MM", CultureInfo.InvariantCulture)
                : d.Range == MonitorRange.Live ? at.ToString("HH:mm:ss", CultureInfo.InvariantCulture)
                : at.ToString("HH:mm", CultureInfo.InvariantCulture);
            var label = Text(s, AxisSize, axis);
            var x = padL + iw * i / 3;
            var left = i == 0 ? x : i == 3 ? x - label.WidthIncludingTrailingWhitespace : x - label.WidthIncludingTrailingWhitespace / 2;
            dc.DrawText(label, new Point(left, h - label.Height));
        }

        // метки пика: черта в момент самого высокого замера и ромб на краю поля
        var peaks = new List<Point>();

        for (var i = 0; i < d.Rows.Count && i < (mirror ? 2 : 1); i++)
        {
            if (d.Rows[i].PeakT is { } pt && pt >= d.From - d.Step && pt <= d.To)
            {
                var px = X(pt);
                var edge = mirror && i == 1 ? PadT + plotH : PadT;
                peaks.Add(new Point(px, edge));
                dc.DrawLine(zl, new Point(Math.Round(px) + 0.5, edge), new Point(Math.Round(px) + 0.5, zeroY));
            }
        }

        // линии: у «зеркала» две, иначе одна
        for (var i = 0; i < d.Rows.Count && i < (mirror ? 2 : 1); i++)
        {
            var row = d.Rows[i];
            var path = Path(d, i, row, X, Y, Mid, padL, w - PadR);
            var edge = mirror && i == 1 ? PadT + plotH : PadT;

            var fill = new LinearGradientBrush
            {
                MappingMode = BrushMappingMode.Absolute,
                StartPoint = new Point(0, edge),
                EndPoint = new Point(0, zeroY),
            };
            fill.GradientStops.Add(new GradientStop(WithAlpha(series, 0.30), 0));
            fill.GradientStops.Add(new GradientStop(WithAlpha(series, 0.03), 1));
            fill.Freeze();

            var area = new StreamGeometry();

            using (var ctx = area.Open())
            {
                ctx.BeginFigure(new Point(path[0].X, zeroY), true, true);
                ctx.LineTo(path[0], false, false);
                Curve(ctx, path);
                ctx.LineTo(new Point(path[^1].X, zeroY), false, false);
            }

            area.Freeze();
            dc.DrawGeometry(fill, null, area);

            var line = new StreamGeometry();

            using (var ctx = line.Open())
            {
                ctx.BeginFigure(path[0], false, false);
                Curve(ctx, path);
            }

            line.Freeze();
            var pen = new Pen(series, 2) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            pen.Freeze();
            dc.DrawGeometry(null, pen, line);
        }

        if (mirror)
        {
            // подписи полос — слева; если туда же попала метка пика — справа
            var right = peaks.Any(p => p.X < padL + 90) && !peaks.Any(p => p.X > w - PadR - 90);
            var a = Text(d.Rows[0].Label, 11, text2);
            var b = Text(d.Rows[1].Label, 11, text2);
            double Lx(FormattedText t) => right ? w - PadR - 6 - t.WidthIncludingTrailingWhitespace : padL + 6;
            dc.DrawText(a, new Point(Lx(a), PadT + 3));
            dc.DrawText(b, new Point(Lx(b), PadT + plotH - 3 - b.Height));
        }

        var pkPen = new Pen(card, 2);
        pkPen.Freeze();

        foreach (var p in peaks)
        {
            var diamond = new RectangleGeometry(new Rect(p.X - 3, p.Y - 3, 6, 6), 0, 0, new RotateTransform(45, p.X, p.Y));
            dc.DrawGeometry(text2, pkPen, diamond);
        }

        Hover(dc, d, padL, w - PadR, plotH, mirror, Mid, Y, strong, card, series, axis, text2);
    }

    /// <summary>
    /// Точки линии на всю ширину: где данных нет — ноль; хвост короче 2,5 шагов — последним значением до края.
    /// </summary>
    private static List<Point> Path(ChartData d, int i, ChartRow row, Func<long, double> X, Func<int, double, double> Y,
        Func<MonitorPoint, double> Mid, double left, double right)
    {
        var pts = d.Points;
        var zero = Y(i, 0);
        var stepW = X(d.From + d.Step) - X(d.From);
        var path = new List<Point>();

        if (pts.Count == 0)
        {
            path.Add(new Point(left, zero));
            path.Add(new Point(right, zero));
            return path;
        }

        if (pts[0].T - d.From > d.Step * 2.5)
        {
            path.Add(new Point(left, zero));
            path.Add(new Point(Math.Max(left, Mid(pts[0]) - stepW), zero));
        }
        else
        {
            path.Add(new Point(left, Y(i, row.Value(pts[0]))));
        }

        for (var k = 0; k < pts.Count; k++)
        {
            if (k > 0 && pts[k].T - pts[k - 1].T > d.Step * 2.5)
            {
                path.Add(new Point(Mid(pts[k - 1]) + stepW, zero));
                path.Add(new Point(Mid(pts[k]) - stepW, zero));
            }

            var x = Mid(pts[k]);

            if (x > path[^1].X + 0.01)
            {
                path.Add(new Point(x, Y(i, row.Value(pts[k]))));
            }
        }

        if (d.To - pts[^1].T > d.Step * 2.5)
        {
            path.Add(new Point(Math.Min(right, Mid(pts[^1]) + stepW), zero));
            path.Add(new Point(right, zero));
        }
        else if (path[^1].X < right)
        {
            path.Add(new Point(right, path[^1].Y));
        }

        return path;
    }

    /// <summary>
    /// Плавная кривая через точки — монотонная: между двумя замерами не выгибается выше или ниже них,
    /// то есть не рисует значений, которых не было (как в макете).
    /// </summary>
    private static void Curve(StreamGeometryContext ctx, IReadOnlyList<Point> p)
    {
        var n = p.Count;

        if (n < 3)
        {
            for (var i = 1; i < n; i++)
            {
                ctx.LineTo(p[i], true, true);
            }

            return;
        }

        var dx = new double[n - 1];
        var s = new double[n - 1];
        var t = new double[n];

        for (var i = 0; i < n - 1; i++)
        {
            dx[i] = Math.Max(1e-6, p[i + 1].X - p[i].X);
            s[i] = (p[i + 1].Y - p[i].Y) / dx[i];
        }

        t[0] = s[0];
        t[n - 1] = s[n - 2];

        for (var i = 1; i < n - 1; i++)
        {
            t[i] = s[i - 1] * s[i] <= 0 ? 0 : 3 * (dx[i - 1] + dx[i]) / ((2 * dx[i] + dx[i - 1]) / s[i - 1] + (dx[i] + 2 * dx[i - 1]) / s[i]);
        }

        for (var i = 0; i < n - 1; i++)
        {
            var k = dx[i] / 3;
            ctx.BezierTo(
                new Point(p[i].X + k, p[i].Y + k * t[i]),
                new Point(p[i + 1].X - k, p[i + 1].Y - k * t[i + 1]),
                p[i + 1],
                true,
                true);
        }
    }

    /// <summary>Наведение: ближайший шаг — перекрестие, точки на линиях и подсказка со сроком, средним и пиком.</summary>
    private void Hover(DrawingContext dc, ChartData d, double lo, double hi, double plotH, bool mirror,
        Func<MonitorPoint, double> Mid, Func<int, double, double> Y, Brush strong, Brush card, Brush series, Brush text3, Brush text2)
    {
        if (_hoverX is not { } hx || d.Points.Count == 0 || hx < lo - 4 || hx > hi + 4)
        {
            return;
        }

        var best = d.Points.MinBy(p => Math.Abs(Mid(p) - hx))!;
        var x = Mid(best);
        var rows = d.Rows.Take(mirror ? 2 : 1).ToList();

        var cross = new Pen(strong, 1);
        cross.Freeze();
        dc.DrawLine(cross, new Point(Math.Round(x) + 0.5, PadT), new Point(Math.Round(x) + 0.5, PadT + plotH));

        var ring = new Pen(card, 2);
        ring.Freeze();

        for (var i = 0; i < rows.Count; i++)
        {
            dc.DrawEllipse(series, ring, new Point(x, Y(i, rows[i].Value(best))), 4, 4);
        }

        // подсказка: срок шага, по строке на величину — подпись, среднее, пик
        var primary = Res("TextFillColorPrimaryBrush", Brushes.White);
        var head = Text(SpanText(d, best), 12, text3);
        var lines = rows.Select(r => (
            Name: Text(r.Label, 12, text2),
            Value: Text(d.Format(r.Value(best)), 12, primary, FontWeights.Medium),
            Peak: r.Peak is { } pk ? Text("пик " + d.Format(pk(best)), 12, text3) : null)).ToList();

        var nameW = lines.Max(l => l.Name.WidthIncludingTrailingWhitespace);
        var valueW = lines.Max(l => l.Value.WidthIncludingTrailingWhitespace);
        var peakW = lines.Max(l => l.Peak?.WidthIncludingTrailingWhitespace ?? 0);
        var rowH = 18.0;
        var bw = Math.Max(head.WidthIncludingTrailingWhitespace, nameW + 14 + valueW + (peakW > 0 ? 14 + peakW : 0)) + 20;
        var bh = 12 + rowH * (1 + lines.Count);
        var left = x + 12 + bw > ActualWidth ? x - 12 - bw : x + 12;
        left = Math.Max(0, Math.Min(left, ActualWidth - bw));

        var box = new Rect(left, 0, bw, Math.Min(bh, ActualHeight));
        dc.DrawRoundedRectangle(Res("SolidBackgroundFillColorBaseBrush", Brushes.Black), cross, box, 8, 8);
        dc.DrawText(head, new Point(left + 10, 6));

        for (var i = 0; i < lines.Count; i++)
        {
            var y = 6 + rowH * (i + 1);
            dc.DrawText(lines[i].Name, new Point(left + 10, y));
            dc.DrawText(lines[i].Value, new Point(left + 10 + nameW + 14 + valueW - lines[i].Value.WidthIncludingTrailingWhitespace, y));

            if (lines[i].Peak is { } peak)
            {
                dc.DrawText(peak, new Point(left + bw - 10 - peak.WidthIncludingTrailingWhitespace, y));
            }
        }
    }

    /// <summary>Срок шага: сутки — датой, часы — с датой и концом, минуты — временем.</summary>
    private static string SpanText(ChartData d, MonitorPoint p)
    {
        string T(long t, string f) => DateTimeOffset.FromUnixTimeSeconds(t).LocalDateTime.ToString(f, CultureInfo.InvariantCulture);

        return d.Step >= 86400 ? T(p.T, "dd.MM") + (d.Step > 86400 ? " — " + T(p.T + d.Step - 1, "dd.MM") : string.Empty)
            : d.Step >= 3600 ? $"{T(p.T, "dd.MM HH:mm")} — {T(p.T + d.Step, "HH:mm")}"
            : d.Step > 60 ? $"{T(p.T, d.Range == MonitorRange.Hour ? "HH:mm" : "dd.MM HH:mm")} — {T(p.T + d.Step, "HH:mm")}"
            : T(p.T, d.Range == MonitorRange.Live ? "HH:mm:ss" : "HH:mm");
    }

    private static Color WithAlpha(Brush brush, double alpha)
    {
        var c = brush is SolidColorBrush solid ? solid.Color : Colors.SteelBlue;
        return Color.FromArgb((byte)Math.Round(255 * alpha), c.R, c.G, c.B);
    }

    /// <summary>
    /// «Круглый» верх шкалы с запасом, как в макете: 1, 1.5, 2, 2.5, 3, 4, 5, 6, 8, 10 × 10ⁿ; у байтов — в своей единице,
    /// и тысяча единиц становится следующей единицей.
    /// </summary>
    public static double NiceMax(double v, bool bytes)
    {
        if (!(v > 0))
        {
            return 1;
        }

        double unit = 1;

        if (bytes)
        {
            while (v / unit >= 1024)
            {
                unit *= 1024;
            }
        }

        var x = v / unit;
        var p = Math.Pow(10, Math.Floor(Math.Log10(x)));
        double[] steps = [1, 1.5, 2, 2.5, 3, 4, 5, 6, 8, 10];
        var m = steps.FirstOrDefault(k => k * p >= x * 1.08, 10);

        if (bytes && m * p >= 1000)
        {
            return unit * 1024;
        }

        return m * p * unit;
    }
}
