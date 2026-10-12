using System.Windows;
using System.Windows.Media;

namespace Through.Tray;

/// <summary>
/// Фирменная сцена «Сквозь»: поток частиц идёт слева направо к кирпичной стене. Когда обход включён (On),
/// поток проходит сквозь проём (тот светится мятой, из него расходятся волны), а частицы за стеной — мятные.
/// Когда выключен — бьётся о стену и отлетает кораллом. Впереди летит искра-проводник со свечением и хвостом.
/// Рисуется вручную (OnRender) и крутится через CompositionTarget.Rendering; если анимации Windows выключены — стоит.
/// </summary>
public sealed class BreakthroughView : FrameworkElement
{
    public static readonly DependencyProperty OnProperty = DependencyProperty.Register(
        nameof(On), typeof(bool), typeof(BreakthroughView),
        new PropertyMetadata(false, (d, e) => ((BreakthroughView)d).OnChanged((bool)e.NewValue)));

    public bool On { get => (bool)GetValue(OnProperty); set => SetValue(OnProperty, value); }

    private sealed class P { public double X, Y, Vx, Vy, R, Hit, A; public bool Passed; }

    private readonly List<P> _parts = [];
    private readonly List<(double R, double A)> _rings = [];
    private P _lead = new();
    private readonly List<Point> _trail = [];
    private readonly Random _rnd = new();
    private bool _running;
    private static bool Motion => SystemParameters.ClientAreaAnimation;

    public BreakthroughView()
    {
        IsHitTestVisible = false;
        Loaded += (_, _) => { if (W > 2) Seed(); Start(); };
        Unloaded += (_, _) => Stop();
        SizeChanged += (_, _) => { if (_parts.Count == 0 && W > 2) Seed(); };
        ThemeManager.Changed += () => Dispatcher.BeginInvoke(InvalidateVisual);
    }

    protected override Size MeasureOverride(Size available)
    {
        var h = double.IsFinite(Height) ? Height : 150;
        var w = double.IsFinite(available.Width) ? available.Width : 600;
        return new Size(Math.Min(w, 4000), h);
    }

    private void OnChanged(bool on)
    {
        if (on)
        {
            _rings.Add((6, 1)); _rings.Add((0, .8));
            foreach (var p in _parts) { p.Vx = Math.Abs(p.Vx) + 1.5; p.Vy += (H * .5 - p.Y) * .006; }
            for (var k = 0; k < 20; k++) { var q = Spawn(); q.X = -5 - _rnd.NextDouble() * 30; q.Vx += 1.8; _parts.Add(q); }
            if (_parts.Count > 120) _parts.RemoveRange(0, _parts.Count - 120);
        }
        if (!Motion) InvalidateVisual();
    }

    private double W => ActualWidth;
    private double H => ActualHeight;

    private P Spawn() => new()
    {
        X = -10 - _rnd.NextDouble() * 40,
        Y = H * .5 + (_rnd.NextDouble() - .5) * H * .72,
        Vx = 1.1 + _rnd.NextDouble() * 1.3,
        Vy = (_rnd.NextDouble() - .5) * .3,
        R = 1.6 + _rnd.NextDouble() * 2.4,
    };

    private void Seed()
    {
        _parts.Clear();
        for (var i = 0; i < 64; i++) { var p = Spawn(); p.X = _rnd.NextDouble() * Math.Max(W, 1); _parts.Add(p); }
        _lead = Spawn(); _lead.X = W * .12; _lead.R = 5; _lead.Vx = 1.2; _trail.Clear();
    }

    private void Start()
    {
        if (_running) return;
        if (!Motion) { InvalidateVisual(); return; }
        _running = true;
        CompositionTarget.Rendering += Tick;
    }

    private void Stop()
    {
        if (!_running) return;
        _running = false;
        CompositionTarget.Rendering -= Tick;
    }

    private void Tick(object? s, EventArgs e)
    {
        if (W < 2 || H < 2) return;
        Step();
        InvalidateVisual();
    }

    private void Step()
    {
        var wallX = W * .5; var gap = H * .22; var cy = H * .5;
        foreach (var p in _parts)
        {
            p.A = Math.Min(1, p.A + .05);
            var inGap = p.Y > cy - gap && p.Y < cy + gap;
            if (!On) { if (p.X + p.R >= wallX - 7 && !p.Passed && !inGap) { p.Vx = -Math.Abs(p.Vx) * .5 - .3; p.Vy += (_rnd.NextDouble() - .5) * 1.2; p.Hit = 1; } }
            else { if (!(p.Y > cy - gap - 8 && p.Y < cy + gap + 8) && p.X + p.R >= wallX - 7 && !p.Passed) { p.Vx = -Math.Abs(p.Vx) * .5; p.Vy += (_rnd.NextDouble() - .5); p.Hit = 1; } if (p.X > wallX) p.Passed = true; }
            p.X += p.Vx; p.Y += p.Vy; p.Vy *= .985; p.Hit *= .9;
        }
        for (var i = 0; i < _parts.Count; i++)
            if (_parts[i].X > W + 20 || _parts[i].X < -60 || _parts[i].Y < -20 || _parts[i].Y > H + 20) _parts[i] = Spawn();

        var l = _lead;
        var lg = l.Y > cy - gap && l.Y < cy + gap;
        if (!On) { if (l.X + l.R >= wallX - 7 && !lg) { l.Vx = -Math.Abs(l.Vx) * .5 - .3; l.Vy += (_rnd.NextDouble() - .5) * 1.1; } }
        else { if (!(l.Y > cy - gap - 8 && l.Y < cy + gap + 8) && l.X + l.R >= wallX - 7 && !l.Passed) l.Vx = -Math.Abs(l.Vx) * .5; if (l.X > wallX) l.Passed = true; }
        l.X += l.Vx; l.Y += l.Vy; l.Vy *= .985;
        if (l.X > W + 30 || l.X < -70 || l.Y < -30 || l.Y > H + 30) { _lead = Spawn(); _lead.X = -10; _lead.R = 5; _lead.Vx = 1.2; _trail.Clear(); }
        else { _trail.Add(new Point(l.X, l.Y)); if (_trail.Count > 16) _trail.RemoveAt(0); }

        for (var i = _rings.Count - 1; i >= 0; i--) { var r = _rings[i]; r.R += 7; r.A -= .022; if (r.A <= 0) _rings.RemoveAt(i); else _rings[i] = r; }
    }

    private Color Col(string key, Color fallback) =>
        TryFindResource(key) is SolidColorBrush b ? b.Color : fallback;

    protected override void OnRender(DrawingContext dc)
    {
        double w = W, h = H;
        if (w < 2 || h < 2) return;
        var wallX = w * .5; var gap = h * .22; var cy = h * .5;
        var indigo = Col("Pine", Color.FromRgb(0x4B, 0x37, 0xF5));
        var mint = Col("Go", Color.FromRgb(0x10, 0xC4, 0x88));
        var coral = Col("Signal", Color.FromRgb(0xFF, 0x52, 0x30));
        var wallC = Col("Line", Color.FromRgb(0xE4, 0xDE, 0xF7));
        var mortar = Col("Surface", Colors.White);

        // прозрачный фон — чтобы ловить клики области не нужно, но нужно пространство для рисования
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));

        // стена: два скруглённых столба с кирпичными швами
        DrawWall(dc, wallX - 7, 0, 14, cy - gap, wallC, mortar);
        DrawWall(dc, wallX - 7, cy + gap, 14, h - (cy + gap), wallC, mortar);

        // портал, когда включено: мягкое мятное свечение + контур проёма
        if (On)
        {
            var glow = new RadialGradientBrush();
            glow.GradientStops.Add(new GradientStop(Alpha(mint, .55), 0));
            glow.GradientStops.Add(new GradientStop(Alpha(mint, 0), 1));
            dc.DrawEllipse(glow, null, new Point(wallX, cy), 24, gap + 12);
            dc.DrawEllipse(null, new Pen(new SolidColorBrush(Alpha(mint, .9)), 2.5), new Point(wallX, cy), 7, gap);
        }

        // частицы
        foreach (var p in _parts)
        {
            var c = p.Passed ? mint : (p.Hit > .2 ? coral : indigo);
            dc.PushOpacity(p.A * (p.Passed ? .95 : .8));
            dc.DrawEllipse(new SolidColorBrush(c), null, new Point(p.X, p.Y), p.R, p.R);
            dc.Pop();
        }

        // искра-проводник: хвост, свечение, ядро
        var lc = _lead.Passed ? mint : indigo;
        for (var t = 0; t < _trail.Count; t++)
        {
            var f = (double)t / _trail.Count;
            dc.PushOpacity(f * .45);
            dc.DrawEllipse(new SolidColorBrush(lc), null, _trail[t], _lead.R * f, _lead.R * f);
            dc.Pop();
        }
        var halo = new RadialGradientBrush { Center = new Point(.5, .5), GradientOrigin = new Point(.5, .5), RadiusX = .5, RadiusY = .5 };
        halo.GradientStops.Add(new GradientStop(Alpha(lc, .55), 0));
        halo.GradientStops.Add(new GradientStop(Alpha(lc, 0), 1));
        dc.DrawEllipse(halo, null, new Point(_lead.X, _lead.Y), 18, 18);
        dc.DrawEllipse(new SolidColorBrush(lc), null, new Point(_lead.X, _lead.Y), _lead.R, _lead.R);
        dc.PushOpacity(.9);
        dc.DrawEllipse(Brushes.White, null, new Point(_lead.X - _lead.R * .25, _lead.Y - _lead.R * .25), _lead.R * .45, _lead.R * .45);
        dc.Pop();

        // волны-прорывы
        foreach (var (r, a) in _rings)
            dc.DrawEllipse(null, new Pen(new SolidColorBrush(Alpha(mint, a * .85)), 3.5), new Point(wallX, cy), r, r);
    }

    private static void DrawWall(DrawingContext dc, double x, double y, double w, double h, Color fill, Color mortar)
    {
        if (h <= 0) return;
        var rect = new Rect(x, y, w, h);
        dc.DrawRoundedRectangle(new SolidColorBrush(fill), null, rect, 6, 6);
        dc.PushClip(new RectangleGeometry(rect));
        var pen = new Pen(new SolidColorBrush(Alpha(mortar, .6)), 2);
        for (var by = y + 11; by < y + h; by += 11)
            dc.DrawLine(pen, new Point(x - 1, by), new Point(x + w + 1, by));
        dc.Pop();
    }

    private static Color Alpha(Color c, double a) => Color.FromArgb((byte)(Math.Clamp(a, 0, 1) * 255), c.R, c.G, c.B);
}
