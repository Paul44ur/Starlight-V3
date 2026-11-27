using System.Windows;
using System.Windows.Controls;
using Starlight.Events;
using Starlight.Tools;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = Wpf.Ui.Controls.TextBox;

namespace Starlight.App;

/// <summary>Asks for a short text such as a sign-in code: a small window to type it, or the user says the digits.</summary>
internal sealed class CodePrompt(EventHub events, Action<string> say) : IUserPrompt
{
    private TaskCompletionSource<string?>? _pending;

    public bool IsAsking => _pending is { Task.IsCompleted: false };

    public void AnswerByVoice(string text) => _pending?.TrySetResult(text);

    public async Task<string?> AskTextAsync(string title, string question, CancellationToken ct)
    {
        var done = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending = done;
        events.Publish(new StateChanged(AssistantState.Asking, title));
        say(question);
        var window = await Application.Current.Dispatcher.InvokeAsync(() => Show(title, question, done));
        using var reg = ct.Register(() => done.TrySetResult(null));
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        using var reg2 = timeout.Token.Register(() => done.TrySetResult(null));
        try
        {
            return await done.Task;
        }
        finally
        {
            _pending = null;
            await Application.Current.Dispatcher.InvokeAsync(window.Close);
        }
    }

    private static FluentWindow Show(string title, string question, TaskCompletionSource<string?> done)
    {
        var window = new FluentWindow
        {
            Title = title,
            Width = 400,
            SizeToContent = SizeToContent.Height,
            Topmost = true,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
        };
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) });
        panel.Children.Add(new TextBlock { Text = question, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });
        var box = new TextBox { PlaceholderText = "Code", Margin = new Thickness(0, 0, 0, 12) };
        panel.Children.Add(box);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "Cancel", MinWidth = 80 };
        cancel.Click += (_, _) => done.TrySetResult(null);
        var ok = new Button { Content = "Send", Appearance = ControlAppearance.Primary, MinWidth = 80, Margin = new Thickness(8, 0, 0, 0), IsDefault = true };
        ok.Click += (_, _) => done.TrySetResult(box.Text.Trim().Length > 0 ? box.Text.Trim() : null);
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        panel.Children.Add(buttons);
        window.Content = panel;
        window.Closed += (_, _) => done.TrySetResult(null);
        window.Show();
        window.Activate();
        box.Focus();
        return window;
    }
}
