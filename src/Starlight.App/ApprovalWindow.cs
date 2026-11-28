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
        if (ct.IsCancellationRequested)
        {
            return Task.FromResult(ApprovalAnswer.No);
        }
        // Continuations run off the UI thread: an approved tool must not start running inside the button click.
        var done = new TaskCompletionSource<ApprovalAnswer>(TaskCreationOptions.RunContinuationsAsynchronously);
        var closed = false;
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
        panel.Children.Add(new TextBlock { Text = request.Question, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) });
        panel.Children.Add(new TextBlock { Text = $"Tool: {request.Tool} · asked from: {request.Source}", Opacity = 0.7, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 16) });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        Button Add(string text, ApprovalAnswer answer, ControlAppearance look)
        {
            var b = new Button { Content = text, Appearance = look, Margin = new Thickness(8, 0, 0, 0), MinWidth = 80 };
            b.Click += (_, _) => { done.TrySetResult(answer); window.Close(); };
            buttons.Children.Add(b);
            return b;
        }
        // No comes first and has the focus, so a stray Enter or Space never grants anything.
        var no = Add("No", ApprovalAnswer.No, ControlAppearance.Secondary);
        no.IsDefault = true;
        no.IsCancel = true;
        Add("Yes", ApprovalAnswer.Yes, ControlAppearance.Primary);
        if (request.CanRemember)
        {
            Add("Always allow", ApprovalAnswer.Always, ControlAppearance.Secondary);
        }
        panel.Children.Add(buttons);
        window.Content = panel;
        // Runs after Show() (the dispatcher queues it), so a cancel right after showing still closes the window.
        var registration = ct.Register(() => window.Dispatcher.InvokeAsync(() =>
        {
            done.TrySetResult(ApprovalAnswer.No);
            if (!closed)
            {
                window.Close();
            }
        }));
        window.Closed += (_, _) => { closed = true; registration.Dispose(); done.TrySetResult(ApprovalAnswer.No); };
        window.Show();
        window.Activate();
        no.Focus();
        return done.Task;
    }
}

/// <summary>Forwards to the overlay's ask card, or to the window when the overlay is off.</summary>
internal sealed class SwitchableApprovals : IApprovalService
{
    public IApprovalService Target { get; set; } = new DenyAllApprovals();

    public Task<ApprovalAnswer> RequestAsync(ApprovalRequest request, CancellationToken ct) => Target.RequestAsync(request, ct);
}
