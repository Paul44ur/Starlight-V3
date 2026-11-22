using System.Windows;
using System.Windows.Controls;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = Wpf.Ui.Controls.TextBox;

namespace Starlight.App;

/// <summary>"Report a problem": the user types what went wrong; the last minutes of trace are saved with it.</summary>
internal static class ReportProblemWindow
{
    public static string? Ask()
    {
        string? result = null;
        var window = new FluentWindow
        {
            Title = "Report a problem",
            Width = 460,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            ResizeMode = ResizeMode.NoResize,
            Topmost = true,
        };
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock { Text = "What went wrong? What did you expect?", Margin = new Thickness(0, 0, 0, 8) });
        var box = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 110, PlaceholderText = "For example: I said \"open Spotify\" and nothing happened." };
        panel.Children.Add(box);
        var save = new Button { Content = "Save report", Appearance = ControlAppearance.Primary, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        save.Click += (_, _) => { result = box.Text; window.Close(); };
        panel.Children.Add(save);
        window.Content = panel;
        window.Loaded += (_, _) => box.Focus();
        window.ShowDialog();
        return result;
    }
}
