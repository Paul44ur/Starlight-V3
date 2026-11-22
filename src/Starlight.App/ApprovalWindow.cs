using System.Windows;
using System.Windows.Controls;
using Starlight.Tools;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using TextBlock = System.Windows.Controls.TextBlock;

namespace Starlight.App;

/// <summary>Yes / No / Always for an Ask tool. A small topmost window until the overlay's ask card (P4) replaces it.</summary>
internal sealed class WindowApprovals : IApprovalService
{
    public Task<ApprovalAnswer> RequestAsync(ApprovalRequest request, CancellationToken ct) =>
        Application.Current.Dispatcher.InvokeAsync(() => Show(request, ct)).Task.Unwrap();

    private static Task<ApprovalAnswer> Show(ApprovalRequest request, CancellationToken ct)
    {
        var done = new TaskCompletionSource<ApprovalAnswer>();
        var window = new FluentWindow
        {
            Title = "Starlight asks",
            Width = 420,
            SizeToContent = SizeToContent.Height,
            Topmost = true,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            ShowInTaskbar = true,
        };
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock { Text = "May I do this?", FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) });
        panel.Children.Add(new TextBlock { Text = request.Question, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 16) });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        void Add(string text, ApprovalAnswer answer, ControlAppearance look)
        {
            var b = new Button { Content = text, Appearance = look, Margin = new Thickness(8, 0, 0, 0), MinWidth = 80 };
            b.Click += (_, _) => { done.TrySetResult(answer); window.Close(); };
            buttons.Children.Add(b);
        }
        Add("Always allow", ApprovalAnswer.Always, ControlAppearance.Secondary);
        Add("No", ApprovalAnswer.No, ControlAppearance.Secondary);
        Add("Yes", ApprovalAnswer.Yes, ControlAppearance.Primary);
        panel.Children.Add(buttons);
        window.Content = panel;
        window.Closed += (_, _) => done.TrySetResult(ApprovalAnswer.No);
        ct.Register(() => window.Dispatcher.InvokeAsync(window.Close));
        window.Show();
        window.Activate();
        return done.Task;
    }
}
