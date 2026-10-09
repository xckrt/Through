using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Tropa.Tray;

/// <summary>Minor — оборвалось, но это второстепенный хост: крестик янтарный, а не красный.</summary>
public enum TrailState { Pending, Ok, Throttled, Broken, Unknown, Minor }

/// <summary>
/// Соединение как тропа из пяти отметок: DNS, TCP, TLS, HTTP, передача.
/// Пройденные этапы закрашены сосной, на месте провала обрыв, дальше пунктир непройденного пути.
/// Узлы стоят в центрах пяти равных ячеек, поэтому совпадают с подписями этапов над списком.
/// </summary>
public sealed class TrailView : FrameworkElement
{
    public const int Stages = 5;

    public static readonly DependencyProperty StateProperty = DependencyProperty.Register(
        nameof(State), typeof(TrailState), typeof(TrailView),
        new FrameworkPropertyMetadata(TrailState.Pending, FrameworkPropertyMetadataOptions.AffectsRender, OnStateChanged));

    /// <summary>Индекс этапа, на котором оборвалось (для Broken и Unknown).</summary>
    public static readonly DependencyProperty StopAtProperty = DependencyProperty.Register(
        nameof(StopAt), typeof(int), typeof(TrailView),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Какая доля пути уже прорисована: 0..1, анимируется при появлении результата.</summary>
    private static readonly DependencyProperty RevealProperty = DependencyProperty.Register(
        "Reveal", typeof(double), typeof(TrailView),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Положение бегущей точки, пока идёт проверка.</summary>
    private static readonly DependencyProperty PhaseProperty = DependencyProperty.Register(
        "Phase", typeof(double), typeof(TrailView),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public TrailState State
    {
        get => (TrailState)GetValue(StateProperty);
        set => SetValue(StateProperty, value);
    }

    public int StopAt
    {
        get => (int)GetValue(StopAtProperty);
        set => SetValue(StopAtProperty, value);
    }

    public TrailView()
    {
        // цвета тропы берутся из темы при отрисовке: при смене темы просто перерисовываемся
        void Repaint() => InvalidateVisual();
        Loaded += (_, _) =>
        {
            ThemeManager.Changed += Repaint;
            Animate(State);
        };
        Unloaded += (_, _) =>
        {
            ThemeManager.Changed -= Repaint;
            BeginAnimation(PhaseProperty, null);
        };
    }

    private static bool Motion => SystemParameters.ClientAreaAnimation;

    private static void OnStateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((TrailView)d).Animate((TrailState)e.NewValue);

    private void Animate(TrailState state)
    {
        if (!IsLoaded) return;
        if (state == TrailState.Pending)
        {
            SetValue(RevealProperty, 0.0);
            BeginAnimation(RevealProperty, null);
            if (Motion)
                BeginAnimation(PhaseProperty, new DoubleAnimation(0, 1, TimeSpan.FromSeconds(1.6))
                {
                    RepeatBehavior = RepeatBehavior.Forever,
                    EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
                });
            return;
        }

        BeginAnimation(PhaseProperty, null);
        if (Motion)
            BeginAnimation(RevealProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(520))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            });
        else
            BeginAnimation(RevealProperty, null);
    }

    protected override Size MeasureOverride(Size available) =>
        new(double.IsInfinity(available.Width) ? 300 : available.Width, 22);

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, cy = ActualHeight / 2;
        const double r = 4.5;
        double X(int i) => w * (i + 0.5) / Stages;

        var paper = Brush("Paper");
        var fog = Brush("Fog");
        var pine = Brush("Pine");

        // основа: непройденный путь пунктиром
        var dotted = new Pen(fog, 1.6) { DashStyle = new DashStyle([0, 3.2], 0), DashCap = PenLineCap.Round };
        dc.DrawLine(dotted, new Point(X(0), cy), new Point(X(Stages - 1), cy));
        var ring = new Pen(fog, 1.5);
        for (var i = 0; i < Stages; i++)
            dc.DrawEllipse(paper, ring, new Point(X(i), cy), r, r);

        if (State == TrailState.Pending)
        {
            if (Motion)
            {
                var x = X(0) + (X(Stages - 1) - X(0)) * (double)GetValue(PhaseProperty);
                dc.DrawEllipse(Brush("Ink"), null, new Point(x, cy), 3, 3);
            }
            return;
        }

        dc.PushClip(new RectangleGeometry(new Rect(0, 0, w * (double)GetValue(RevealProperty), ActualHeight)));

        var path = new Pen(pine, 2.5) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        var passed = State switch
        {
            TrailState.Ok => Stages,
            TrailState.Throttled => Stages - 1,
            _ => Math.Clamp(StopAt, 0, Stages - 1),
        };

        for (var i = 0; i + 1 < passed; i++)
            dc.DrawLine(path, new Point(X(i), cy), new Point(X(i + 1), cy));
        for (var i = 0; i < passed; i++)
            dc.DrawEllipse(pine, null, new Point(X(i), cy), r, r);

        switch (State)
        {
            case TrailState.Throttled:
            {
                var amber = Brush("Amber");
                var slow = new Pen(amber, 2.5) { DashStyle = new DashStyle([2, 1.6], 0) };
                dc.DrawLine(slow, new Point(X(Stages - 2) + r, cy), new Point(X(Stages - 1) - r, cy));
                dc.DrawEllipse(amber, null, new Point(X(Stages - 1), cy), r, r);
                break;
            }
            case TrailState.Broken or TrailState.Unknown or TrailState.Minor:
            {
                var k = passed;
                var color = State switch { TrailState.Broken => Brush("Signal"), TrailState.Minor => Brush("Amber"), _ => fog };
                // путь доходит почти до отметки и обрывается: видно, где именно
                if (k > 0)
                    dc.DrawLine(path, new Point(X(k - 1), cy), new Point(X(k) - 11, cy));
                if (State is TrailState.Broken or TrailState.Minor)
                {
                    var cross = new Pen(color, 2.5) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
                    const double s = 4.5;
                    dc.DrawLine(cross, new Point(X(k) - s, cy - s), new Point(X(k) + s, cy + s));
                    dc.DrawLine(cross, new Point(X(k) - s, cy + s), new Point(X(k) + s, cy - s));
                }
                else
                {
                    dc.DrawEllipse(paper, new Pen(color, 2), new Point(X(k), cy), r + 1, r + 1);
                }
                break;
            }
        }

        dc.Pop();
    }

    private Brush Brush(string key) => (Brush)FindResource(key);
}
