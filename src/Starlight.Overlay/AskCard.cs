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

    public AskCard()
    {
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
        buttons.Children.Add(Button("Always", ApprovalAnswer.Always, Color.FromArgb(60, 255, 255, 255)));
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

    public Task<ApprovalAnswer> AskAsync(string question)
    {
        _answer?.TrySetResult(ApprovalAnswer.No);
        _answer = new TaskCompletionSource<ApprovalAnswer>();
        _question.Text = question;
        Show();
        return _answer.Task;
    }

    /// <summary>Answers the open question (a click here, or "yes"/"no" by voice).</summary>
    public bool Answer(ApprovalAnswer answer)
    {
        var open = _answer;
        _answer = null;
        Hide();
        return open?.TrySetResult(answer) ?? false;
    }

    public bool IsAsking => _answer is not null;
}
