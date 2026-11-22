using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Win32;
using Serilog;
using Starlight.Events;
using Starlight.Settings;
using Starlight.Tools;

namespace Starlight.Overlay;

/// <summary>
/// The overlay (plan 5.4): the orb, captions, pointer, ask card and red control frame, one layer per monitor.
/// It only listens to events; it never calls tools. It also answers approvals (the ask card).
/// </summary>
public sealed partial class OverlayManager : IApprovalService, IDisposable
{
    private readonly EventHub _events;
    private readonly SettingsStore _settings;
    private readonly Dispatcher _ui;
    private readonly List<IDisposable> _subs = [];
    private readonly List<LayerWindow> _layers = [];
    private readonly OrbWindow _orb;
    private readonly AskCard _ask = new();
    private readonly DispatcherTimer _keepOnTop;
    private readonly DispatcherTimer _pointerTimer = new();
    private readonly DispatcherTimer _captionTimer = new();
    private Border? _caption;
    private TextBlock? _captionText;
    private List<MonitorInfo> _monitors = [];
    private string? _answerTurn;
    private bool _hiddenForFullScreen;

    public OverlayManager(EventHub events, SettingsStore settings, ContextMenu orbMenu)
    {
        _events = events;
        _settings = settings;
        _ui = Dispatcher.CurrentDispatcher;
        _orb = new OrbWindow(orbMenu);
        _pointerTimer.Tick += (_, _) => { _pointerTimer.Stop(); ClearPointer(); };
        _captionTimer.Tick += (_, _) => { _captionTimer.Stop(); FadeCaption(); };
        _keepOnTop = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, (_, _) => KeepOnTop(), _ui);
        SystemEvents.DisplaySettingsChanged += OnDisplayChanged;

        _subs.Add(events.Subscribe<StateChanged>(e => OnUi(() => _orb.SetState(e.State))));
        _subs.Add(events.Subscribe<AudioLevel>(e => OnUi(() => _orb.SetLevel(e.Level))));
        _subs.Add(events.Subscribe<UserSaid>(e => OnUi(() => ShowCaption(e.Text, user: true))));
        _subs.Add(events.Subscribe<TurnStarted>(e => OnUi(() => { _answerTurn = e.TurnId; if (e.Source != "voice") ShowCaption(e.Text, user: true); })));
        _subs.Add(events.Subscribe<AnswerDelta>(e => OnUi(() => AppendAnswer(e))));
        _subs.Add(events.Subscribe<AnswerCompleted>(e => OnUi(() => CompleteAnswer(e))));
        _subs.Add(events.Subscribe<PointAt>(e => OnUi(() => Point(e))));
        _subs.Add(events.Subscribe<ClearPointer>(_ => OnUi(ClearPointer)));
        _subs.Add(events.Subscribe<ControlActive>(e => OnUi(() => _layers.ForEach(l => l.ShowControlFrame(e.Active)))));
        _subs.Add(events.Subscribe<KillSwitchChanged>(e => OnUi(() =>
        {
            if (e.Engaged)
            {
                ClearPointer();
                _layers.ForEach(l => l.ShowControlFrame(false));
                _ask.Answer(ApprovalAnswer.No);
            }
        })));
    }

    public void Show()
    {
        if (!_settings.Current.Overlay.Enabled)
        {
            return;
        }
        Build();
        _keepOnTop.Start();
    }

    private void Build()
    {
        foreach (var l in _layers)
        {
            l.Close();
        }
        _layers.Clear();
        _monitors = Native.Monitors();
        if (_monitors.Count == 0)
        {
            Log.Warning("No monitors found for the overlay");
            return;
        }
        Log.Information("Overlay on {Count} monitors: {Monitors}", _monitors.Count,
            string.Join("; ", _monitors.Select(m => $"{m.Name} {m.Width}x{m.Height} at {m.X},{m.Y} {m.Scale * 100:0} %{(m.Primary ? " primary" : "")}")));
        foreach (var m in _monitors)
        {
            var layer = new LayerWindow(m);
            layer.Show();
            layer.Place();
            _layers.Add(layer);
        }
        var primary = _monitors.FirstOrDefault(m => m.Primary) ?? _monitors[0];
        var orbPx = (int)Math.Round(OrbWindow.WindowSize * primary.Scale);
        var (cx, cy) = OrbCenter();
        _orb.Show();
        Native.Place(_orb, cx - orbPx / 2, cy - orbPx / 2, orbPx, orbPx);
        BuildCaption();
    }

    private (int X, int Y) OrbCenter() => OverlayGeometry.OrbCenter(_monitors, OrbWindow.WindowSize, 0);

    private LayerWindow? PrimaryLayer => _layers.FirstOrDefault(l => l.Monitor.Primary) ?? _layers.FirstOrDefault();

    private void BuildCaption()
    {
        var layer = PrimaryLayer;
        if (layer is null)
        {
            return;
        }
        _captionText = new TextBlock { Foreground = Brushes.White, FontSize = 16, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, MaxWidth = 640 };
        _caption = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(200, 0x0E, 0x13, 0x20)),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(14, 8, 14, 8),
            Child = _captionText,
            Opacity = 0,
        };
        _caption.SizeChanged += (_, _) => PlaceCaption();
        layer.Canvas.Children.Add(_caption);
    }

    private void PlaceCaption()
    {
        var layer = PrimaryLayer;
        if (layer is null || _caption is null)
        {
            return;
        }
        var (cx, cy) = OrbCenter();
        var (lx, ly) = OverlayGeometry.ToLocal(layer.Monitor, cx, cy);
        Canvas.SetLeft(_caption, lx - _caption.ActualWidth / 2);
        Canvas.SetTop(_caption, ly + OrbWindow.WindowSize / 2 - 20);
    }

    private void ShowCaption(string text, bool user)
    {
        if (_caption is null || _captionText is null)
        {
            return;
        }
        _captionText.Text = user ? $"“{text}”" : text;
        _captionText.Opacity = user ? 0.75 : 1;
        _caption.BeginAnimation(UIElement.OpacityProperty, null);
        _caption.Opacity = 1;
        _captionTimer.Stop();
    }

    private void AppendAnswer(AnswerDelta e)
    {
        if (_captionText is null || e.TurnId != _answerTurn)
        {
            return;
        }
        if (_captionText.Opacity < 1)
        {
            ShowCaption("", user: false);
        }
        var text = _captionText.Text + e.Delta;
        // Keep the caption short: the last ~300 characters.
        _captionText.Text = text.Length > 300 ? "…" + text[^300..] : text;
    }

    private void CompleteAnswer(AnswerCompleted e)
    {
        if (e.Failed && _captionText is not null)
        {
            ShowCaption(e.Text, user: false);
        }
        _captionTimer.Interval = TimeSpan.FromSeconds(Math.Max(2, _settings.Current.Overlay.CaptionSeconds));
        _captionTimer.Start();
    }

    private void FadeCaption() =>
        _caption?.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(600)));

    private void Point(PointAt e)
    {
        var (ox, oy) = OrbCenter();
        foreach (var l in _layers)
        {
            l.DrawPointer(ox, oy, e.Target, e.Label, e.Exact);
        }
        _pointerTimer.Stop();
        _pointerTimer.Interval = e.Duration > TimeSpan.Zero ? e.Duration : TimeSpan.FromSeconds(6);
        _pointerTimer.Start();
    }

    private void ClearPointer() => _layers.ForEach(l => l.ClearPointer());

    public Task<ApprovalAnswer> RequestAsync(ApprovalRequest request, CancellationToken ct) =>
        _ui.InvokeAsync(async () =>
        {
            var (cx, cy) = OrbCenter();
            var primary = _monitors.FirstOrDefault(m => m.Primary) ?? _monitors.FirstOrDefault();
            var scale = primary?.Scale ?? 1;
            var task = _ask.AskAsync(request.Question);
            var w = (int)(AskCard.CardWidth * scale);
            var h = (int)(Math.Max(_ask.ActualHeight, 130) * scale);
            Native.Place(_ask, cx - w / 2, cy + (int)(OrbWindow.WindowSize / 2 * scale) + (int)(70 * scale), w, h);
            using var reg = ct.Register(() => _ui.InvokeAsync(() => _ask.Answer(ApprovalAnswer.No)));
            return await task;
        }).Task.Unwrap();

    public bool IsAsking => _ui.Invoke(() => _ask.IsAsking);

    /// <summary>A spoken "yes" or "no" while a card is open. True if it answered a card.</summary>
    public bool AnswerByVoice(ApprovalAnswer answer) => _ui.Invoke(() => _ask.IsAsking && _ask.Answer(answer));

    private void KeepOnTop()
    {
        var fullScreen = FullScreenAppRunning();
        if (fullScreen != _hiddenForFullScreen)
        {
            _hiddenForFullScreen = fullScreen;
            var v = fullScreen ? Visibility.Hidden : Visibility.Visible;
            _orb.Visibility = v;
            _layers.ForEach(l => l.Visibility = v);
            Log.Information(fullScreen ? "Full-screen app: overlay hidden" : "Overlay shown again");
        }
        if (!fullScreen)
        {
            Native.BringToTop(_orb);
            _layers.ForEach(Native.BringToTop);
        }
    }

    [System.Runtime.InteropServices.LibraryImport("shell32.dll")]
    private static partial int SHQueryUserNotificationState(out int state);

    /// <summary>A game or presentation in full screen: the overlay must not cover it.</summary>
    private static bool FullScreenAppRunning() =>
        SHQueryUserNotificationState(out var s) == 0 && s is 2 or 3 or 4; // busy (full screen), D3D full screen, presentation

    private void OnDisplayChanged(object? sender, EventArgs e) => OnUi(() =>
    {
        Log.Information("Display settings changed; rebuilding the overlay");
        Build();
    });

    private void OnUi(Action a) => _ui.BeginInvoke(a);

    public void Dispose()
    {
        SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
        _subs.ForEach(s => s.Dispose());
        _keepOnTop.Stop();
        _ui.Invoke(() =>
        {
            _orb.Close();
            _ask.Close();
            _layers.ForEach(l => l.Close());
        });
    }
}
