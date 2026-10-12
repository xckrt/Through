using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace Through.Tray;

public enum OrbState { Off, On, Busy, Problem }

/// <summary>
/// Большая круглая кнопка «включить/выключить» на обзоре. Вокруг ядра — пунктирная тропа, которая идёт
/// по кругу (медленно, когда всё работает; быстро, пока Тропа занята), и расходящиеся волны, пока обход включён.
/// Цвета берутся из темы и переливаются при смене состояния. Если в Windows выключены анимации, кнопка стоит.
/// </summary>
public sealed class PowerOrb : Button
{
    public static readonly DependencyProperty StateProperty = DependencyProperty.Register(
        nameof(State), typeof(OrbState), typeof(PowerOrb),
        new PropertyMetadata(OrbState.Off, (d, e) => ((PowerOrb)d).Apply((OrbState)e.OldValue, animate: true)));

    public static readonly DependencyProperty DiameterProperty = DependencyProperty.Register(
        nameof(Diameter), typeof(double), typeof(PowerOrb), new PropertyMetadata(150.0, (d, _) => ((PowerOrb)d).Layout()));

    public OrbState State { get => (OrbState)GetValue(StateProperty); set => SetValue(StateProperty, value); }
    public double Diameter { get => (double)GetValue(DiameterProperty); set => SetValue(DiameterProperty, value); }

    private readonly Grid _root = new();
    private readonly Ellipse _ripple1 = new(), _ripple2 = new(), _halo = new(), _trail = new(), _core = new();
    private readonly TextBlock _glyph = new() { Text = "", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly SolidColorBrush _accent = new(Colors.Gray), _coreFill = new(Colors.Gray), _glyphFill = new(Colors.White);
    private readonly RadialGradientBrush _haloFill = new();
    private readonly RotateTransform _spin = new();
    private readonly ScaleTransform _press = new(1, 1);
    private readonly DropShadowEffect _shadow = new() { BlurRadius = 34, ShadowDepth = 0, Opacity = 0 };

    public PowerOrb()
    {
        var template = new ControlTemplate(typeof(PowerOrb)) { VisualTree = new FrameworkElementFactory(typeof(ContentPresenter)) };
        Template = template;
        Cursor = Cursors.Hand;
        Background = Brushes.Transparent;
        Focusable = true;
        if (Application.Current?.TryFindResource("FocusRing") is Style ring) FocusVisualStyle = ring;

        foreach (var r in new[] { _ripple1, _ripple2 })
        {
            r.Stroke = _accent;
            r.StrokeThickness = 2;
            r.Opacity = 0;
            r.RenderTransformOrigin = new Point(0.5, 0.5);
            r.RenderTransform = new ScaleTransform(0.6, 0.6);
            r.IsHitTestVisible = false;
        }
        _haloFill.GradientStops.Add(new GradientStop(Colors.Transparent, 0.45));
        _haloFill.GradientStops.Add(new GradientStop(Colors.Transparent, 1));
        _halo.Fill = _haloFill;
        _halo.IsHitTestVisible = false;

        _trail.Stroke = _accent;
        _trail.StrokeThickness = 3.5;
        _trail.StrokeDashCap = PenLineCap.Round;
        _trail.StrokeDashArray = [0.1, 2.4];
        _trail.RenderTransformOrigin = new Point(0.5, 0.5);
        _trail.RenderTransform = _spin;

        _core.Fill = _coreFill;
        _core.Effect = _shadow;
        _glyph.Foreground = _glyphFill;
        _glyph.FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");

        foreach (var e in new UIElement[] { _ripple1, _ripple2, _halo, _trail, _core, _glyph }) _root.Children.Add(e);
        _root.RenderTransformOrigin = new Point(0.5, 0.5);
        _root.RenderTransform = _press;
        _root.Background = Brushes.Transparent;
        Content = _root;
        Layout();

        Loaded += (_, _) => Apply(State, animate: false);
        ThemeManager.Changed += () => Dispatcher.BeginInvoke(() => Apply(State, animate: false));
        MouseEnter += (_, _) => Press(1.04);
        MouseLeave += (_, _) => Press(1.0);
        PreviewMouseLeftButtonDown += (_, _) => Press(0.94);
        PreviewMouseLeftButtonUp += (_, _) => Press(IsMouseOver ? 1.04 : 1.0);
    }

    private static bool Motion => SystemParameters.ClientAreaAnimation;

    private void Layout()
    {
        var d = Diameter;
        // волнам нужно место вокруг ядра
        Width = Height = d;
        _ripple1.Width = _ripple1.Height = _ripple2.Width = _ripple2.Height = d;
        _halo.Width = _halo.Height = d * 0.95;
        _trail.Width = _trail.Height = d * 0.74;
        _core.Width = _core.Height = d * 0.56;
        _glyph.FontSize = d * 0.17;
    }

    private void Press(double scale)
    {
        var a = new DoubleAnimation(scale, TimeSpan.FromMilliseconds(scale < 1 ? 90 : 220))
        {
            EasingFunction = new BackEase { Amplitude = 0.4, EasingMode = EasingMode.EaseOut },
        };
        _press.BeginAnimation(ScaleTransform.ScaleXProperty, a);
        _press.BeginAnimation(ScaleTransform.ScaleYProperty, a);
    }

    private Color Theme(string key, Color fallback) =>
        TryFindResource(key) is SolidColorBrush b ? b.Color : fallback;

    private void Apply(OrbState old, bool animate)
    {
        var state = State;
        var accent = state switch
        {
            OrbState.On => Theme("Pine", Colors.SeaGreen),
            OrbState.Busy => Theme("Amber", Colors.Orange),
            OrbState.Problem => Theme("Signal", Colors.IndianRed),
            _ => Theme("Fog", Colors.Gray),
        };
        var lit = state != OrbState.Off;
        var core = lit ? accent : Theme("Paper", Colors.WhiteSmoke);
        var glyph = lit ? Theme("OnAccent", Colors.White) : Theme("InkSoft", Colors.DimGray);
        var time = TimeSpan.FromMilliseconds(animate ? 450 : 0);

        Fade(_accent, accent, time);
        Fade(_coreFill, core, time);
        Fade(_glyphFill, glyph, time);
        _haloFill.GradientStops[0].Color = Color.FromArgb(lit ? (byte)0x55 : (byte)0, accent.R, accent.G, accent.B);
        _shadow.Color = accent;
        _shadow.BeginAnimation(DropShadowEffect.OpacityProperty, new DoubleAnimation(lit ? 0.55 : 0.12, time));
        _trail.BeginAnimation(OpacityProperty, new DoubleAnimation(lit ? 0.95 : 0.45, time));

        // тропа по кругу: спокойно — когда работает, бегом — пока Тропа занята
        _spin.BeginAnimation(RotateTransform.AngleProperty, null);
        if (Motion && state != OrbState.Off)
        {
            var period = state == OrbState.Busy ? 2.2 : state == OrbState.Problem ? 9 : 26;
            _spin.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(_spin.Angle, _spin.Angle + 360, TimeSpan.FromSeconds(period))
            {
                RepeatBehavior = RepeatBehavior.Forever,
            });
        }

        // волны расходятся, пока обход держится
        Ripple(_ripple1, state is OrbState.On or OrbState.Problem, TimeSpan.Zero, state == OrbState.Problem ? 1.8 : 3.2);
        Ripple(_ripple2, state is OrbState.On or OrbState.Problem, TimeSpan.FromSeconds(state == OrbState.Problem ? 0.9 : 1.6), state == OrbState.Problem ? 1.8 : 3.2);
        _halo.BeginAnimation(OpacityProperty, Motion && state == OrbState.On
            ? new DoubleAnimation(0.55, 1, TimeSpan.FromSeconds(2.6)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase() }
            : new DoubleAnimation(lit ? 1 : 0, time));

        // включилось: одна быстрая волна и «вдох» ядра
        if (animate && Motion && old != OrbState.On && state == OrbState.On) Burst();
    }

    private static void Fade(SolidColorBrush brush, Color to, TimeSpan time)
    {
        if (time == TimeSpan.Zero)
        {
            brush.BeginAnimation(SolidColorBrush.ColorProperty, null);
            brush.Color = to;
            return;
        }
        brush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(to, time) { EasingFunction = new QuadraticEase() });
    }

    private static void Ripple(Ellipse ring, bool on, TimeSpan delay, double seconds)
    {
        var scale = (ScaleTransform)ring.RenderTransform;
        if (!on || !Motion)
        {
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            ring.BeginAnimation(OpacityProperty, null);
            ring.Opacity = 0;
            return;
        }
        var grow = new DoubleAnimation(0.58, 1.0, TimeSpan.FromSeconds(seconds))
        {
            BeginTime = delay, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        var fade = new DoubleAnimation(0.6, 0, TimeSpan.FromSeconds(seconds)) { BeginTime = delay, RepeatBehavior = RepeatBehavior.Forever };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        ring.BeginAnimation(OpacityProperty, fade);
    }

    private void Burst()
    {
        var a = new DoubleAnimation(1.12, TimeSpan.FromMilliseconds(180)) { AutoReverse = true, EasingFunction = new QuadraticEase() };
        var s = new ScaleTransform(1, 1);
        _core.RenderTransformOrigin = new Point(0.5, 0.5);
        _core.RenderTransform = s;
        s.BeginAnimation(ScaleTransform.ScaleXProperty, a);
        s.BeginAnimation(ScaleTransform.ScaleYProperty, a);
    }
}
