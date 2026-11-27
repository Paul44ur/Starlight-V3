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
using Starlight.Security;
using Starlight.Settings;
using Starlight.Tools;
using Starlight.Tools.Web;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = Wpf.Ui.Controls.TextBox;
using PasswordBox = System.Windows.Controls.PasswordBox;

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
    private readonly ISecretStore _secrets;

    public SettingsWindow(AssistantCore core, ModelHost model, ISecretStore secrets, Action export, Action report)
    {
        _core = core;
        _secrets = secrets;
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
        tabs.Items.Add(Tab("Voice", VoiceSection()));
        tabs.Items.Add(Tab("Model", ModelSection()));
        tabs.Items.Add(Tab("Web", WebSection()));
        tabs.Items.Add(Tab("Servers", ServersSection()));
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

    private StackPanel General()
    {
        var g = _draft.General;
        var p = Section("General");
        Row(p, "Assistant name", Text(g.AssistantName, v => g.AssistantName = v.Trim()), "What Starlight calls itself.");
        Row(p, "Your name", Text(g.UserName, v => g.UserName = v.Trim()), "Optional. Starlight uses it when it talks to you.");
        var autostart = new ToggleSwitch { IsChecked = g.StartWithWindows, Content = "Start Starlight when Windows starts" };
        autostart.Click += (_, _) => g.StartWithWindows = autostart.IsChecked == true;
        Row(p, "Startup", autostart);
        Row(p, "Stop-everything hotkey", Text(g.KillHotkey, v => g.KillHotkey = v.Trim()), "Stops every action at once. For example Ctrl+Alt+J.");
        var game = new ToggleSwitch { IsChecked = g.GameMode, Content = "Pause while a game runs" };
        game.Click += (_, _) => g.GameMode = game.IsChecked == true;
        Row(p, "Game mode", game, "Stops the model and speech recognition while a game runs full screen, so the graphics card is free for it.");
        Row(p, "Games", Text(string.Join(", ", g.GameProcesses), v => g.GameProcesses = [.. v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)]),
            "Optional. Programs that count as games even in a window, separated by commas. For example eldenring.exe, cs2.exe.");
        return p;
    }

    private StackPanel VoiceSection()
    {
        var v = _draft.Voice;
        var p = Section("Voice", "Hold the push-to-talk keys, speak, and let go. Press them again while Starlight talks to interrupt it.");
        var enabled = new ToggleSwitch { IsChecked = v.Enabled, Content = "Use voice (takes effect after a restart of Starlight)" };
        enabled.Click += (_, _) => v.Enabled = enabled.IsChecked == true;
        Row(p, "Voice", enabled);
        var wake = new ToggleSwitch { IsChecked = v.WakeWordEnabled, Content = "Listen for the wake phrase" };
        wake.Click += (_, _) => v.WakeWordEnabled = wake.IsChecked == true;
        Row(p, "Wake phrase", wake);
        Row(p, "Phrase", Text(v.WakePhrase, t => v.WakePhrase = t.Trim()), "Any plain English words, for example \"Hey Starlight\" or \"Hey Nova\". Takes effect after a restart of Starlight.");
        var sensitivity = new Slider { Minimum = 0, Maximum = 1, Value = v.WakeSensitivity, Width = 240, HorizontalAlignment = HorizontalAlignment.Left, TickFrequency = 0.1, IsSnapToTickEnabled = true };
        sensitivity.ValueChanged += (_, _) => v.WakeSensitivity = Math.Round(sensitivity.Value, 1);
        Row(p, "Wake sensitivity", sensitivity, "Left: wakes less often by mistake. Right: hears you more easily.");
        Row(p, "Push-to-talk keys", Text(v.PushToTalkHotkey, t => v.PushToTalkHotkey = t.Trim()), "Hold to talk. For example Ctrl+Alt+Space.");
        var speak = new ToggleSwitch { IsChecked = v.SpeakAnswers, Content = "Speak answers out loud" };
        speak.Click += (_, _) => v.SpeakAnswers = speak.IsChecked == true;
        Row(p, "Answers", speak);
        var voices = new ComboBox { Width = 200, HorizontalAlignment = HorizontalAlignment.Left, ItemsSource = Starlight.Voice.Speaker.DefaultVoices, SelectedItem = v.Voice };
        voices.SelectionChanged += (_, _) => v.Voice = voices.SelectedItem as string ?? v.Voice;
        Row(p, "Speaking voice", voices, "af = American female, am = American male, bf/bm = British.");
        var rate = new Slider { Minimum = 0.7, Maximum = 1.5, Value = v.SpeechRate, Width = 240, HorizontalAlignment = HorizontalAlignment.Left, TickFrequency = 0.1, IsSnapToTickEnabled = true };
        rate.ValueChanged += (_, _) => v.SpeechRate = Math.Round(rate.Value, 1);
        Row(p, "Speaking speed", rate);
        var mics = new ComboBox { Width = 320, HorizontalAlignment = HorizontalAlignment.Left };
        mics.Items.Add("(Windows default)");
        try
        {
            using var devices = new NAudio.CoreAudioApi.MMDeviceEnumerator();
            foreach (var d in devices.EnumerateAudioEndPoints(NAudio.CoreAudioApi.DataFlow.Capture, NAudio.CoreAudioApi.DeviceState.Active))
            {
                mics.Items.Add(d.FriendlyName);
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Could not list microphones");
        }
        mics.SelectedItem = string.IsNullOrEmpty(v.InputDevice) ? "(Windows default)" : v.InputDevice;
        mics.SelectionChanged += (_, _) => v.InputDevice = mics.SelectedIndex <= 0 ? "" : mics.SelectedItem as string ?? "";
        Row(p, "Microphone", mics, "Takes effect after a restart of Starlight.");
        return p;
    }

    private StackPanel ModelSection()
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
        _modelFile.SelectionChanged += (_, _) => { if (!_filling) { m.ChatModelFile = _modelFile.SelectedItem as string ?? ""; } };
        Row(p, "Chat model", _modelFile);
        _projector.SelectionChanged += (_, _) => { if (!_filling) { m.VisionProjectorFile = _projector.SelectedItem is string s && s != ModelFiles.None ? s : ""; } };
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

    private StackPanel WebSection()
    {
        var w = _draft.Web;
        var p = Section("Web", "How Starlight searches and which sites it may sign in to. Keys and passwords go to Windows Credential Manager, never into a file.");
        var provider = new ComboBox { ItemsSource = new[] { "auto", "searxng", "brave", "duckduckgo" }, SelectedItem = w.SearchProvider, Width = 200, HorizontalAlignment = HorizontalAlignment.Left };
        provider.SelectionChanged += (_, _) => w.SearchProvider = provider.SelectedItem as string ?? "auto";
        Row(p, "Search engine", provider, "auto: your SearXNG if set, else Brave Search if a key is saved, else DuckDuckGo.");
        Row(p, "SearXNG address", Text(w.SearxngUrl, v => w.SearxngUrl = v.Trim()), "If you run your own SearXNG, for example http://192.168.1.20:8080. It must allow the JSON format.");

        var key = new PasswordBox { Width = 320, HorizontalAlignment = HorizontalAlignment.Left };
        var keyState = new TextBlock { Opacity = 0.7, FontSize = 12, Margin = new Thickness(0, 4, 0, 0) };
        void ShowKeyState() => keyState.Text = _secrets.Get(WebTools.BraveKey) is null ? "No key saved." : "A key is saved.";
        var saveKey = new Button { Content = "Save key", Margin = new Thickness(8, 0, 0, 0) };
        saveKey.Click += (_, _) =>
        {
            if (key.Password.Trim().Length > 0)
            {
                _secrets.Set(WebTools.BraveKey, "", key.Password.Trim());
                key.Clear();
            }
            ShowKeyState();
        };
        var removeKey = new Button { Content = "Remove", Margin = new Thickness(8, 0, 0, 0) };
        removeKey.Click += (_, _) => { _secrets.Delete(WebTools.BraveKey); ShowKeyState(); };
        var keyRow = new StackPanel { Orientation = Orientation.Horizontal };
        keyRow.Children.Add(key);
        keyRow.Children.Add(saveKey);
        keyRow.Children.Add(removeKey);
        Row(p, "Brave Search key", keyRow, "Optional. A free key from brave.com/search/api gives better results than DuckDuckGo.");
        p.Children.Add(keyState);
        ShowKeyState();
        Row(p, "Requests per second", Number(w.MaxRequestsPerSecond, v => w.MaxRequestsPerSecond = Math.Min(v, 20)), "For all sites together. Each site gets at most one per second.");

        p.Children.Add(new TextBlock { Text = "Sign-ins", FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 20, 0, 4) });
        p.Children.Add(new TextBlock { Text = "Sites Starlight may sign in to when you ask (it asks first every time). It never sees the password.", Opacity = 0.7, TextWrapping = TextWrapping.Wrap });
        var list = new StackPanel { Margin = new Thickness(0, 8, 0, 8) };
        void Fill()
        {
            list.Children.Clear();
            foreach (var login in w.Logins.ToList())
            {
                var row = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
                var remove = new Button { Content = "Remove" };
                remove.Click += (_, _) =>
                {
                    _secrets.Delete(login.SecretKey);
                    w.Logins.Remove(login);
                    _core.Settings.Update(s => s.Web.Logins.RemoveAll(l => l.SecretKey == login.SecretKey));
                    Fill();
                };
                DockPanel.SetDock(remove, Dock.Right);
                row.Children.Add(remove);
                var user = _secrets.Get(login.SecretKey)?.UserName ?? "(password missing)";
                row.Children.Add(new TextBlock { Text = $"{login.Name}  ·  {user}  ·  {login.LoginUrl}", VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
                list.Children.Add(row);
            }
            if (w.Logins.Count == 0)
            {
                list.Children.Add(new TextBlock { Text = "None yet.", Opacity = 0.6 });
            }
        }
        p.Children.Add(list);
        var name = new TextBox { PlaceholderText = "Name, for example GitHub", Width = 320, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 4) };
        var url = new TextBox { PlaceholderText = "Sign-in page, for example https://github.com/login", Width = 320, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 4) };
        var userName = new TextBox { PlaceholderText = "User name or email", Width = 320, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 4) };
        var password = new PasswordBox { Width = 320, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 4) };
        var problem = new TextBlock { Foreground = System.Windows.Media.Brushes.IndianRed, TextWrapping = TextWrapping.Wrap };
        var add = new Button { Content = "Add sign-in", Margin = new Thickness(0, 4, 0, 0) };
        add.Click += (_, _) =>
        {
            problem.Text = "";
            if (!Uri.TryCreate(url.Text.Trim(), UriKind.Absolute, out var u) || u.Scheme != "https")
            {
                problem.Text = "The sign-in page must be an https address.";
                return;
            }
            if (name.Text.Trim().Length == 0 || userName.Text.Trim().Length == 0 || password.Password.Length == 0)
            {
                problem.Text = "Fill in the name, user name and password.";
                return;
            }
            var login = new WebLogin { Name = name.Text.Trim(), LoginUrl = u.ToString() };
            _secrets.Set(login.SecretKey, userName.Text.Trim(), password.Password);
            w.Logins.RemoveAll(l => l.SecretKey == login.SecretKey);
            w.Logins.Add(login);
            _core.Settings.Update(s => { s.Web.Logins.RemoveAll(l => l.SecretKey == login.SecretKey); s.Web.Logins.Add(login); });
            name.Clear();
            url.Clear();
            userName.Clear();
            password.Clear();
            Fill();
        };
        p.Children.Add(name);
        p.Children.Add(url);
        p.Children.Add(userName);
        p.Children.Add(new TextBlock { Text = "Password", Opacity = 0.7, FontSize = 12 });
        p.Children.Add(password);
        p.Children.Add(add);
        p.Children.Add(problem);
        Fill();
        return p;
    }

    private StackPanel ServersSection()
    {
        var p = Section("Servers", "Your own servers. Starlight checks them, runs allowed commands over SSH, and can watch them. SSH passwords go to Windows Credential Manager.");
        var list = new StackPanel { Margin = new Thickness(0, 8, 0, 8) };
        var name = new TextBox { PlaceholderText = "Name, for example nas", Width = 320, HorizontalAlignment = HorizontalAlignment.Left };
        var host = new TextBox { PlaceholderText = "Host name or address", Width = 320, HorizontalAlignment = HorizontalAlignment.Left };
        var user = new TextBox { PlaceholderText = "SSH user (empty: no SSH)", Width = 320, HorizontalAlignment = HorizontalAlignment.Left };
        var port = new TextBox { Text = "22", Width = 120, HorizontalAlignment = HorizontalAlignment.Left };
        var keyFile = new TextBox { PlaceholderText = "Private key file (optional)", Width = 320, HorizontalAlignment = HorizontalAlignment.Left };
        var browseKey = new Button { Content = "Browse…", Margin = new Thickness(8, 0, 0, 0) };
        browseKey.Click += (_, _) =>
        {
            var dialog = new OpenFileDialog { Title = "SSH private key" };
            if (dialog.ShowDialog(this) == true)
            {
                keyFile.Text = dialog.FileName;
            }
        };
        var password = new PasswordBox { Width = 320, HorizontalAlignment = HorizontalAlignment.Left };
        var allowed = new TextBox { AcceptsReturn = true, MinLines = 4, Width = 320, HorizontalAlignment = HorizontalAlignment.Left, Text = string.Join('\n', new ServerEntry().AllowedCommands) };
        var watchPorts = new TextBox { PlaceholderText = "Ports to watch, for example 22, 443", Width = 320, HorizontalAlignment = HorizontalAlignment.Left };
        var health = new TextBox { PlaceholderText = "Health address, for example https://nas.home/health", Width = 320, HorizontalAlignment = HorizontalAlignment.Left };
        var minutes = new TextBox { Text = "0", Width = 120, HorizontalAlignment = HorizontalAlignment.Left };
        var problem = new TextBlock { Foreground = System.Windows.Media.Brushes.IndianRed, TextWrapping = TextWrapping.Wrap };

        void Load(ServerEntry s)
        {
            name.Text = s.Name;
            host.Text = s.Host;
            user.Text = s.SshUser;
            port.Text = s.SshPort.ToString(CultureInfo.InvariantCulture);
            keyFile.Text = s.SshKeyFile;
            password.Clear();
            allowed.Text = string.Join('\n', s.AllowedCommands);
            watchPorts.Text = string.Join(", ", s.WatchPorts);
            health.Text = s.HealthUrl;
            minutes.Text = s.WatchMinutes.ToString(CultureInfo.InvariantCulture);
        }

        void Fill()
        {
            list.Children.Clear();
            foreach (var server in _draft.Servers.ToList())
            {
                var row = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
                var buttons = new StackPanel { Orientation = Orientation.Horizontal };
                var edit = new Button { Content = "Edit", Margin = new Thickness(0, 0, 4, 0) };
                edit.Click += (_, _) => Load(server);
                var remove = new Button { Content = "Remove" };
                remove.Click += (_, _) =>
                {
                    _secrets.Delete(Starlight.Tools.Network.SshRunner.SecretKey(server));
                    _draft.Servers.Remove(server);
                    Fill();
                };
                buttons.Children.Add(edit);
                buttons.Children.Add(remove);
                DockPanel.SetDock(buttons, Dock.Right);
                row.Children.Add(buttons);
                var watch = server.WatchMinutes > 0 ? $"  ·  watched every {server.WatchMinutes} min" : "";
                var sshText = server.SshUser.Length > 0 ? $"  ·  SSH {server.SshUser}" : "";
                row.Children.Add(new TextBlock { Text = server.Display + sshText + watch, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
                list.Children.Add(row);
            }
            if (_draft.Servers.Count == 0)
            {
                list.Children.Add(new TextBlock { Text = "None yet.", Opacity = 0.6 });
            }
        }

        var add = new Button { Content = "Add or update server", Margin = new Thickness(0, 8, 0, 0) };
        add.Click += (_, _) =>
        {
            problem.Text = "";
            if (host.Text.Trim().Length == 0 || host.Text.Trim().Any(char.IsWhiteSpace))
            {
                problem.Text = "Give the host name or address.";
                return;
            }
            if (!int.TryParse(port.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var sshPort) || sshPort is < 1 or > 65535)
            {
                problem.Text = "The SSH port must be a number from 1 to 65535.";
                return;
            }
            var ports = new List<int>();
            foreach (var part in watchPorts.Text.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries))
            {
                if (!int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var wp) || wp is < 1 or > 65535)
                {
                    problem.Text = $"\"{part}\" is not a port number.";
                    return;
                }
                ports.Add(wp);
            }
            if (health.Text.Trim().Length > 0 && !Uri.TryCreate(health.Text.Trim(), UriKind.Absolute, out _))
            {
                problem.Text = "The health address must be a full address such as https://nas.home/health.";
                return;
            }
            _ = int.TryParse(minutes.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var every);
            var server = new ServerEntry
            {
                Name = name.Text.Trim(),
                Host = host.Text.Trim(),
                SshUser = user.Text.Trim(),
                SshPort = sshPort,
                SshKeyFile = keyFile.Text.Trim(),
                AllowedCommands = allowed.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
                WatchPorts = ports,
                HealthUrl = health.Text.Trim(),
                WatchMinutes = Math.Clamp(every, 0, 1440),
            };
            if (password.Password.Length > 0)
            {
                _secrets.Set(Starlight.Tools.Network.SshRunner.SecretKey(server), server.SshUser, password.Password);
            }
            _draft.Servers.RemoveAll(s => s.Host.Equals(server.Host, StringComparison.OrdinalIgnoreCase));
            _draft.Servers.Add(server);
            Load(new ServerEntry());
            Fill();
        };

        p.Children.Add(list);
        Row(p, "Name", name);
        Row(p, "Host", host);
        Row(p, "SSH user", user);
        Row(p, "SSH port", port);
        var keyRow = new StackPanel { Orientation = Orientation.Horizontal };
        keyRow.Children.Add(keyFile);
        keyRow.Children.Add(browseKey);
        Row(p, "SSH key file", keyRow, "Starlight uses the key file if set, else the password.");
        Row(p, "SSH password", password, "Leave empty to keep the stored one.");
        Row(p, "Commands that run without asking", allowed, "One per line. * stands for any text. Anything else asks first, and commands with ; | & $ or redirects always ask.");
        Row(p, "Watch: ports", watchPorts);
        Row(p, "Watch: health address", health);
        Row(p, "Watch every (minutes)", minutes, "0 turns watching off. Starlight tells you when the server goes down or comes back.");
        p.Children.Add(add);
        p.Children.Add(problem);
        Fill();
        return p;
    }

    private bool _filling;

    private void FillModelLists()
    {
        var m = _draft.Model;
        IEnumerable<string> files = [];
        try
        {
            if (Directory.Exists(m.ModelsDir))
            {
                files = Directory.GetFiles(m.ModelsDir, "*.gguf").Select(f => Path.GetFileName(f)!);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            files = [];
        }
        var chosenModel = m.ChatModelFile;
        var chosenProjector = string.IsNullOrEmpty(m.VisionProjectorFile) ? ModelFiles.None : m.VisionProjectorFile;
        var (models, projectors) = ModelFiles.Lists(files, chosenModel, chosenProjector);
        // Replacing ItemsSource fires SelectionChanged with nothing selected; without this guard
        // that wrote "" into the draft and the chosen model was lost.
        _filling = true;
        try
        {
            _modelFile.ItemsSource = models;
            _modelFile.SelectedItem = models.FirstOrDefault(x => x.Equals(chosenModel, StringComparison.OrdinalIgnoreCase));
            _projector.ItemsSource = projectors;
            _projector.SelectedItem = projectors.FirstOrDefault(x => x.Equals(chosenProjector, StringComparison.OrdinalIgnoreCase)) ?? ModelFiles.None;
        }
        finally
        {
            _filling = false;
        }
    }

    private StackPanel Permissions()
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

    private StackPanel Diagnostics(Action export, Action report)
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
        // A copy: the window keeps editing its draft, which must not change the live settings before the next Save
        // (and the next Save must still see the model change).
        var copy = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(_draft, SettingsJson), SettingsJson)!;
        _core.Settings.Update(s =>
        {
            s.General = copy.General;
            s.Voice = copy.Voice;
            s.Model = copy.Model;
            s.Web = copy.Web;
            s.Servers = copy.Servers;
            s.PermissionOverrides = copy.PermissionOverrides;
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
