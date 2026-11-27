using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using Starlight.Events;

namespace Starlight.Overlay;

/// <summary>
/// The glowing orb (the look chosen on 22 Nov 2026). Dim and still when idle (0 fps), it breathes with
/// the microphone while listening, swirls while thinking and pulses with the voice while speaking.
/// Its own small window, because it takes a right-click for its menu; it never takes focus.
/// </summary>
internal sealed class OrbWindow : Window
{
    public const double OrbSize = 72;
    public const double WindowSize = 168;

    private readonly Ellipse _glow;
    private readonly Ellipse _core;
    private readonly Ellipse _ring;
    private readonly RotateTransform _ringTurn = new();
    private readonly ScaleTransform _scale = new(1, 1, WindowSize / 2, WindowSize / 2);
    private readonly RadialGradientBrush _coreBrush;
    private readonly RadialGradientBrush _glowBrush;
    private AssistantState _state = AssistantState.Idle;
    private double _level, _shownLevel;
    private Color _drawn;
    private Color _color, _target;
    private bool _animating;
    private DateTime _start = DateTime.UtcNow;

    public OrbWindow(ContextMenu menu)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        Width = Height = WindowSize;
        ContextMenu = menu;
        Title = "Starlight orb";

        _color = _target = ColorFor(AssistantState.Idle);
        _glowBrush = new RadialGradientBrush(
            [new GradientStop(WithAlpha(_color, 150), 0), new GradientStop(WithAlpha(_color, 60), 0.45), new GradientStop(WithAlpha(_color, 0), 1)]);
        _coreBrush = new RadialGradientBrush(
            [new GradientStop(Colors.White, 0), new GradientStop(_color, 0.55), new GradientStop(Darker(_color), 1)])
        { GradientOrigin = new Point(0.4, 0.35) };

        _glow = new Ellipse { Width = WindowSize, Height = WindowSize, Fill = _glowBrush, IsHitTestVisible = false };
        _core = new Ellipse
        {
            Width = OrbSize, Height = OrbSize, Fill = _coreBrush,
            Effect = new DropShadowEffect { Color = _color, BlurRadius = 30, ShadowDepth = 0, Opacity = 0.9 },
        };
        _ring = new Ellipse
        {
            Width = OrbSize + 18, Height = OrbSize + 18, StrokeThickness = 2.5, Opacity = 0, IsHitTestVisible = false,
            Stroke = new LinearGradientBrush(Colors.White, Color.FromArgb(0, 255, 255, 255), 0),
            RenderTransform = _ringTurn, RenderTransformOrigin = new Point(0.5, 0.5),
        };
        var canvas = new Grid { RenderTransform = _scale };
        canvas.Children.Add(_glow);
        canvas.Children.Add(_ring);
        canvas.Children.Add(_core);
        foreach (FrameworkElement e in canvas.Children)
        {
            e.HorizontalAlignment = HorizontalAlignment.Center;
            e.VerticalAlignment = VerticalAlignment.Center;
        }
        Content = canvas;
        Opacity = 0.55;
        SourceInitialized += (_, _) => Native.MakeOverlay(this, clickThrough: false);
    }

    public void SetState(AssistantState state)
    {
        _state = state;
        if (state is not (AssistantState.Listening or AssistantState.Speaking))
        {
            // The level only comes while recording or speaking; a stale one kept the frame loop running.
            _level = 0;
        }
        _target = ColorFor(state);
        Opacity = state == AssistantState.Idle ? 0.55 : 1.0;
        StartAnimating();
    }

    public void SetLevel(double level)
    {
        _level = level;
        StartAnimating();
    }

    private void StartAnimating()
    {
        if (_animating)
        {
            return;
        }
        _animating = true;
        _start = DateTime.UtcNow;
        CompositionTarget.Rendering += OnFrame;
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        var t = (DateTime.UtcNow - _start).TotalSeconds;
        _shownLevel += (_level - _shownLevel) * 0.25;
        _color = Lerp(_color, _target, 0.12);
        double scale = 1;
        double ring = 0;
        switch (_state)
        {
            case AssistantState.Listening:
                scale = 1 + 0.35 * _shownLevel;
                break;
            case AssistantState.Speaking:
                scale = 1 + 0.25 * _shownLevel;
                break;
            case AssistantState.Thinking:
            case AssistantState.Acting:
                scale = 1 + 0.05 * Math.Sin(t * 4);
                ring = 1;
                _ringTurn.Angle = t * 240 % 360;
                break;
            case AssistantState.Asking:
                scale = 1 + 0.06 * Math.Sin(t * 3);
                break;
        }
        _scale.ScaleX = _scale.ScaleY = scale;
        _ring.Opacity += (ring - _ring.Opacity) * 0.15;
        if (_color != _drawn)
        {
            Recolor();
            _drawn = _color;
        }

        // Back at rest: stop drawing frames (0 fps when idle, plan 5.4).
        var settled = _state is AssistantState.Idle or AssistantState.Paused or AssistantState.Killed
            && Math.Abs(scale - 1) < 0.01 && _ring.Opacity < 0.01 && Close(_color, _target) && _shownLevel < 0.01;
        if (settled && t > 0.5)
        {
            _scale.ScaleX = _scale.ScaleY = 1;
            _animating = false;
            CompositionTarget.Rendering -= OnFrame;
        }
        if (_state is not (AssistantState.Listening or AssistantState.Speaking) && _level > 0)
        {
            _level = 0;
        }
    }

    private void Recolor()
    {
        _glowBrush.GradientStops[0].Color = WithAlpha(_color, 150);
        _glowBrush.GradientStops[1].Color = WithAlpha(_color, 60);
        _glowBrush.GradientStops[2].Color = WithAlpha(_color, 0);
        _coreBrush.GradientStops[1].Color = _color;
        _coreBrush.GradientStops[2].Color = Darker(_color);
        ((DropShadowEffect)_core.Effect).Color = _color;
    }

    public static Color ColorFor(AssistantState s) => s switch
    {
        AssistantState.Listening => Color.FromRgb(0x5E, 0xE7, 0xFF),
        AssistantState.Thinking => Color.FromRgb(0x8C, 0x7B, 0xFF),
        AssistantState.Acting => Color.FromRgb(0x4D, 0xD8, 0xFF),
        AssistantState.Speaking => Color.FromRgb(0x6F, 0xB8, 0xFF),
        AssistantState.Asking => Color.FromRgb(0xFF, 0xB5, 0x47),
        AssistantState.Paused => Color.FromRgb(0x8A, 0x93, 0xA6),
        AssistantState.Killed => Color.FromRgb(0xFF, 0x4D, 0x4D),
        _ => Color.FromRgb(0x4D, 0xA3, 0xFF),
    };

    private static Color WithAlpha(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);
    private static Color Darker(Color c) => Color.FromRgb((byte)(c.R * 0.35), (byte)(c.G * 0.35), (byte)(c.B * 0.55));
    private static Color Lerp(Color a, Color b, double t) => Color.FromRgb(Step(a.R, b.R, t), Step(a.G, b.G, t), Step(a.B, b.B, t));

    /// <summary>Moves a channel toward its target by a fraction, but at least by one, so it always arrives.</summary>
    private static byte Step(byte from, byte to, double t)
    {
        var d = to - from;
        if (d == 0)
        {
            return to;
        }
        var move = (int)(d * t);
        return (byte)(from + (move != 0 ? move : Math.Sign(d)));
    }
    private static bool Close(Color a, Color b) => a == b;
}
