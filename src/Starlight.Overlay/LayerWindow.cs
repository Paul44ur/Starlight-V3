using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Starlight.Overlay;

/// <summary>One click-through, full-monitor window: captions, the pointer line and frame, the red control frame.</summary>
internal sealed class LayerWindow : Window
{
    private readonly Canvas _canvas = new();
    private readonly Border _controlFrame;

    public LayerWindow(MonitorInfo monitor)
    {
        Monitor = monitor;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = false;
        IsHitTestVisible = false;
        Title = "Starlight overlay";
        _controlFrame = new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromArgb(200, 0xFF, 0x4D, 0x4D)),
            BorderThickness = new Thickness(4),
            Visibility = Visibility.Collapsed,
        };
        var root = new Grid();
        root.Children.Add(_canvas);
        root.Children.Add(_controlFrame);
        Content = root;
        SourceInitialized += (_, _) => Native.MakeOverlay(this, clickThrough: true);
    }

    public MonitorInfo Monitor { get; }

    public Canvas Canvas => _canvas;

    public void ShowControlFrame(bool on) => _controlFrame.Visibility = on ? Visibility.Visible : Visibility.Collapsed;

    public void Place() => Native.Place(this, Monitor.X, Monitor.Y, Monitor.Width, Monitor.Height);

    /// <summary>Draws the pointer piece for this monitor: a line from the orb and, if the target is here, a frame round it.</summary>
    public void DrawPointer(int orbX, int orbY, Starlight.Events.ScreenRect target, string label, bool exact)
    {
        ClearPointer();
        var color = Color.FromRgb(0x5E, 0xE7, 0xFF);
        var brush = new SolidColorBrush(color);
        var dash = exact ? null : new DoubleCollection([4, 3]);
        var line = OverlayGeometry.ClipLine(Monitor, orbX, orbY, target.CenterX, target.CenterY);
        if (line is { } l)
        {
            _canvas.Children.Add(new Line
            {
                X1 = l.X1, Y1 = l.Y1, X2 = l.X2, Y2 = l.Y2, Stroke = brush, StrokeThickness = 2.5, StrokeDashArray = dash, Tag = "pointer",
                Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = color, BlurRadius = 10, ShadowDepth = 0 },
            });
        }
        if (Monitor.Contains(target.CenterX, target.CenterY))
        {
            var (x, y) = OverlayGeometry.ToLocal(Monitor, target.X, target.Y);
            var frame = new Rectangle
            {
                Width = target.Width / Monitor.Scale + 8, Height = target.Height / Monitor.Scale + 8,
                Stroke = brush, StrokeThickness = 3, StrokeDashArray = dash, RadiusX = 6, RadiusY = 6, Tag = "pointer",
            };
            Canvas.SetLeft(frame, x - 4);
            Canvas.SetTop(frame, y - 4);
            _canvas.Children.Add(frame);
            if (!string.IsNullOrWhiteSpace(label))
            {
                var tag = new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb(220, 0x10, 0x16, 0x24)), CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(8, 3, 8, 3), Tag = "pointer",
                    Child = new TextBlock { Text = label, Foreground = Brushes.White, FontSize = 13 },
                };
                Canvas.SetLeft(tag, x - 4);
                Canvas.SetTop(tag, y + target.Height / Monitor.Scale + 10);
                _canvas.Children.Add(tag);
            }
        }
    }

    public void ClearPointer()
    {
        foreach (var e in _canvas.Children.OfType<FrameworkElement>().Where(e => (string?)e.Tag == "pointer").ToList())
        {
            _canvas.Children.Remove(e);
        }
    }
}
