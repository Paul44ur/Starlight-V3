using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Starlight.Tools;

namespace Starlight.Overlay;

/// <summary>The Yes/No card under the orb. The only overlay part that takes clicks besides the orb; it never takes focus.</summary>
internal sealed class AskCard : Window
{
    private readonly TextBlock _question = new() { Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap, FontSize = 14, Margin = new Thickness(0, 0, 0, 12) };
    private TaskCompletionSource<ApprovalAnswer>? _answer;

    public const double CardWidth = 380;

    private readonly System.Windows.Controls.Button _always;
    private bool _canRemember = true;

    public AskCard()
    {
        _always = Button("Always", ApprovalAnswer.Always, Color.FromArgb(60, 255, 255, 255));
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.Height;
        Width = CardWidth;
        Title = "Starlight asks";

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(_always);
        buttons.Children.Add(Button("No", ApprovalAnswer.No, Color.FromArgb(60, 255, 255, 255)));
        buttons.Children.Add(Button("Yes", ApprovalAnswer.Yes, Color.FromRgb(0xFF, 0xB5, 0x47), dark: true));
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = "May I do this?", Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xB5, 0x47)), FontWeight = FontWeights.SemiBold, FontSize = 13, Margin = new Thickness(0, 0, 0, 4) });
        panel.Children.Add(_question);
        panel.Children.Add(buttons);
        Content = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(235, 0x12, 0x18, 0x26)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(160, 0xFF, 0xB5, 0x47)),
            BorderThickness = new Thickness(1.5),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(16),
            Child = panel,
        };
        SourceInitialized += (_, _) => Native.MakeOverlay(this, clickThrough: false);
    }

    private System.Windows.Controls.Button Button(string text, ApprovalAnswer answer, Color background, bool dark = false)
    {
        var b = new System.Windows.Controls.Button
        {
            Content = text, MinWidth = 72, Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(10, 5, 10, 5),
            Background = new SolidColorBrush(background), Foreground = dark ? Brushes.Black : Brushes.White, BorderThickness = new Thickness(0),
            Focusable = false,
        };
        b.Click += (_, _) => Answer(answer);
        return b;
    }

    /// <summary>Opens the card. The caller places it; call <see cref="Measure"/> first for its height.</summary>
    public Task<ApprovalAnswer> AskAsync(string question, bool canRemember = true)
    {
        _answer?.TrySetResult(ApprovalAnswer.No);
        _answer = new TaskCompletionSource<ApprovalAnswer>();
        _question.Text = question;
        _canRemember = canRemember;
        _always.Visibility = canRemember ? Visibility.Visible : Visibility.Collapsed;
        Show();
        return _answer.Task;
    }

    /// <summary>The height the card needs for <paramref name="question"/>, before it is shown.</summary>
    public double HeightFor(string question, bool canRemember)
    {
        _question.Text = question;
        _always.Visibility = canRemember ? Visibility.Visible : Visibility.Collapsed;
        Measure(new Size(CardWidth, double.PositiveInfinity));
        return DesiredSize.Height;
    }

    /// <summary>Answers the open question (a click here, or "yes"/"no" by voice). With
    /// <paramref name="only"/>, answers only if that question is still the open one, so a late
    /// cancel never closes a newer card.</summary>
    public bool Answer(ApprovalAnswer answer, Task<ApprovalAnswer>? only = null)
    {
        var open = _answer;
        if (open is null || (only is not null && open.Task != only))
        {
            return false;
        }
        if (answer == ApprovalAnswer.Always && !_canRemember)
        {
            answer = ApprovalAnswer.Yes;
        }
        _answer = null;
        Hide();
        return open.TrySetResult(answer);
    }

    public bool IsAsking => _answer is not null;
}
