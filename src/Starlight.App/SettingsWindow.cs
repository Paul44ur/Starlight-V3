using System.IO;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Starlight.Diagnostics;
using Starlight.Events;
using Starlight.Model;
using Starlight.Settings;
using Starlight.Tools;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = Wpf.Ui.Controls.TextBox;

namespace Starlight.App;

/// <summary>Every setting lives here (rule R7). Changes apply on Save.</summary>
internal sealed class SettingsWindow : FluentWindow
{
    private readonly AssistantCore _core;
    private readonly ModelHost _model;
    private readonly AppSettings _draft;
    private readonly TextBlock _modelStatus = new() { TextWrapping = TextWrapping.Wrap };
    private readonly ComboBox _modelFile = new() { MinWidth = 320 };
    private readonly ComboBox _projector = new() { MinWidth = 320 };
    private readonly IDisposable _statusSub;

    public SettingsWindow(AssistantCore core, ModelHost model, Action export, Action report)
    {
        _core = core;
        _model = model;
        _draft = JsonSerializer.Deserialize<AppSettings>(core.Settings.ToJson(), SettingsJson)!;
        Title = "Starlight settings";
        Width = 760;
        Height = 600;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ExtendsContentIntoTitleBar = true;
        WindowBackdropType = WindowBackdropType.Mica;

        var tabs = new TabControl { TabStripPlacement = Dock.Left, Margin = new Thickness(12, 40, 12, 0) };
        tabs.Items.Add(Tab("General", General()));
        tabs.Items.Add(Tab("Model", ModelSection()));
        tabs.Items.Add(Tab("Permissions", Permissions()));
        tabs.Items.Add(Tab("Diagnostics", Diagnostics(export, report)));

        var save = new Button { Content = "Save", Appearance = ControlAppearance.Primary, MinWidth = 100 };
        save.Click += async (_, _) => await SaveAsync();
        var cancel = new Button { Content = "Close", MinWidth = 100, Margin = new Thickness(0, 0, 8, 0) };
        cancel.Click += (_, _) => Close();
        var bottom = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(16) };
        bottom.Children.Add(cancel);
        bottom.Children.Add(save);

        var root = new DockPanel();
        var titleBar = new TitleBar { Title = "Starlight settings" };
        DockPanel.SetDock(titleBar, Dock.Top);
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(titleBar);
        root.Children.Add(bottom);
        root.Children.Add(tabs);
        Content = root;

        UpdateModelStatus(model.State.ToString(), null);
        _statusSub = core.Events.Subscribe<ModelStatus>(m => Dispatcher.BeginInvoke(() => UpdateModelStatus(m.Status, m.Detail)));
        Closed += (_, _) => _statusSub.Dispose();
    }

    private static readonly JsonSerializerOptions SettingsJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    private static TabItem Tab(string header, UIElement content) =>
        new() { Header = header, Content = new ScrollViewer { Content = content, Padding = new Thickness(16, 0, 16, 0) } };

    private static StackPanel Section(string title, string? hint = null)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = title, FontSize = 20, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
        if (hint is not null)
        {
            panel.Children.Add(new TextBlock { Text = hint, Opacity = 0.7, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });
        }
        return panel;
    }

    private static void Row(Panel panel, string label, UIElement control, string? hint = null)
    {
        panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 12, 0, 4), FontWeight = FontWeights.SemiBold });
        panel.Children.Add(control);
        if (hint is not null)
        {
            panel.Children.Add(new TextBlock { Text = hint, Opacity = 0.6, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
        }
    }

    private static TextBox Text(string value, Action<string> set)
    {
        var box = new TextBox { Text = value, MinWidth = 320, HorizontalAlignment = HorizontalAlignment.Left };
        box.TextChanged += (_, _) => set(box.Text);
        return box;
    }

    private static TextBox Number(int value, Action<int> set)
    {
        var box = new TextBox { Text = value.ToString(CultureInfo.InvariantCulture), Width = 160, HorizontalAlignment = HorizontalAlignment.Left };
        box.TextChanged += (_, _) =>
        {
            if (int.TryParse(box.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n > 0)
            {
                set(n);
            }
        };
        return box;
    }

    private UIElement General()
    {
        var g = _draft.General;
        var p = Section("General");
        Row(p, "Assistant name", Text(g.AssistantName, v => g.AssistantName = v.Trim()), "What Starlight calls itself.");
        Row(p, "Your name", Text(g.UserName, v => g.UserName = v.Trim()), "Optional. Starlight uses it when it talks to you.");
        var autostart = new ToggleSwitch { IsChecked = g.StartWithWindows, Content = "Start Starlight when Windows starts" };
        autostart.Click += (_, _) => g.StartWithWindows = autostart.IsChecked == true;
        Row(p, "Startup", autostart);
        Row(p, "Stop-everything hotkey", Text(g.KillHotkey, v => g.KillHotkey = v.Trim()), "Stops every action at once. For example Ctrl+Alt+J.");
        return p;
    }

    private UIElement ModelSection()
    {
        var m = _draft.Model;
        var p = Section("Model", "The AI model runs on your graphics card. Any GGUF model works; you can swap it at any time.");
        p.Children.Add(_modelStatus);
        var folder = Text(m.ModelsDir, v => { m.ModelsDir = v.Trim(); FillModelLists(); });
        var browse = new Button { Content = "Browse…", Margin = new Thickness(8, 0, 0, 0) };
        browse.Click += (_, _) =>
        {
            var dialog = new OpenFolderDialog { Title = "Folder with your models" };
            if (dialog.ShowDialog(this) == true)
            {
                folder.Text = dialog.FolderName;
            }
        };
        var folderRow = new StackPanel { Orientation = Orientation.Horizontal };
        folderRow.Children.Add(folder);
        folderRow.Children.Add(browse);
        Row(p, "Models folder", folderRow);
        _modelFile.SelectionChanged += (_, _) => m.ChatModelFile = _modelFile.SelectedItem as string ?? "";
        Row(p, "Chat model", _modelFile);
        _projector.SelectionChanged += (_, _) => m.VisionProjectorFile = _projector.SelectedItem is string s && s != "(none)" ? s : "";
        Row(p, "Vision projector", _projector, "The mmproj file that lets the model see screenshots. Optional.");
        Row(p, "Context size (tokens)", Number(m.ContextSize, v => m.ContextSize = v), "How much of a conversation the model keeps in mind. 32768 fits a 14B model in 16 GB.");
        Row(p, "GPU layers", Number(m.GpuLayers, v => m.GpuLayers = v), "99 puts the whole model on the graphics card.");
        Row(p, "Extra server options", Text(m.ExtraServerArgs, v => m.ExtraServerArgs = v), "Advanced. Passed to llama-server as they are.");
        var restart = new Button { Content = "Restart the model", Margin = new Thickness(0, 16, 0, 0) };
        restart.Click += async (_, _) => await RestartModelAsync();
        p.Children.Add(restart);
        FillModelLists();
        return p;
    }

    private void FillModelLists()
    {
        var m = _draft.Model;
        var files = Directory.Exists(m.ModelsDir)
            ? Directory.GetFiles(m.ModelsDir, "*.gguf").Select(f => Path.GetFileName(f)!).Order(StringComparer.OrdinalIgnoreCase).ToList()
            : [];
        var models = files.Where(f => !f.Contains("mmproj", StringComparison.OrdinalIgnoreCase)).ToList();
        var projectors = files.Where(f => f.Contains("mmproj", StringComparison.OrdinalIgnoreCase)).Prepend("(none)").ToList();
        var chosenModel = m.ChatModelFile;
        var chosenProjector = string.IsNullOrEmpty(m.VisionProjectorFile) ? "(none)" : m.VisionProjectorFile;
        _modelFile.ItemsSource = models;
        _modelFile.SelectedItem = models.Contains(chosenModel) ? chosenModel : null;
        _projector.ItemsSource = projectors;
        _projector.SelectedItem = projectors.Contains(chosenProjector) ? chosenProjector : "(none)";
    }

    private UIElement Permissions()
    {
        var p = Section("Permissions", "Safe runs without asking. Ask shows a Yes/No first. Blocked never runs.");
        foreach (var tool in _core.Tools.All.OrderBy(t => t.Name))
        {
            var combo = new ComboBox { Width = 160, HorizontalAlignment = HorizontalAlignment.Left };
            combo.Items.Add($"Default ({tool.DefaultPermission})");
            foreach (var level in Enum.GetNames<Permission>())
            {
                combo.Items.Add(level);
            }
            combo.SelectedIndex = _draft.PermissionOverrides.TryGetValue(tool.Name, out var current) ? 1 + (int)current : 0;
            var name = tool.Name;
            combo.SelectionChanged += (_, _) =>
            {
                if (combo.SelectedIndex <= 0)
                {
                    _draft.PermissionOverrides.Remove(name);
                }
                else
                {
                    _draft.PermissionOverrides[name] = (Permission)(combo.SelectedIndex - 1);
                }
            };
            Row(p, tool.Name, combo, tool.Description);
        }
        return p;
    }

    private UIElement Diagnostics(Action export, Action report)
    {
        var p = Section("Diagnostics", $"Starlight {Logging.Version}. After using Starlight for a while, export diagnostics and send the zip.");
        var exportButton = new Button { Content = "Export diagnostics", Appearance = ControlAppearance.Primary, Margin = new Thickness(0, 8, 0, 0) };
        exportButton.Click += (_, _) => export();
        var reportButton = new Button { Content = "Report a problem…", Margin = new Thickness(0, 8, 0, 0) };
        reportButton.Click += (_, _) => report();
        var logs = new Button { Content = "Open the logs folder", Margin = new Thickness(0, 8, 0, 0) };
        logs.Click += (_, _) => Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_core.Paths.LogsDir}\"") { UseShellExecute = true })?.Dispose();
        p.Children.Add(exportButton);
        p.Children.Add(reportButton);
        p.Children.Add(logs);
        return p;
    }

    private void UpdateModelStatus(string status, string? detail) =>
        _modelStatus.Text = $"Status: {status.ToLowerInvariant()}{(string.IsNullOrWhiteSpace(detail) ? "" : " — " + detail)}";

    private async Task SaveAsync()
    {
        var before = _core.Settings.Current.Model;
        var modelChanged = before.ModelsDir != _draft.Model.ModelsDir || before.ChatModelFile != _draft.Model.ChatModelFile
            || before.VisionProjectorFile != _draft.Model.VisionProjectorFile || before.ContextSize != _draft.Model.ContextSize
            || before.GpuLayers != _draft.Model.GpuLayers || before.ExtraServerArgs != _draft.Model.ExtraServerArgs;
        _core.Settings.Update(s =>
        {
            s.General = _draft.General;
            s.Model = _draft.Model;
            s.PermissionOverrides = _draft.PermissionOverrides;
        });
        try
        {
            StartWithWindows.Apply(_draft.General.StartWithWindows);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            Serilog.Log.Warning(ex, "Could not change the startup entry");
        }
        if (modelChanged)
        {
            await RestartModelAsync();
        }
    }

    private async Task RestartModelAsync()
    {
        try
        {
            await _model.RestartAsync();
        }
        catch (ModelHostException ex)
        {
            UpdateModelStatus("failed", ex.Message);
        }
    }
}
