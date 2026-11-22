using System.IO;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using H.NotifyIcon;
using Serilog;
using Starlight.Memory;
using Starlight.Security;
using Starlight.Tools.Web;
using Starlight.Tools.Network;
using Starlight.Events;
using Starlight.Model;
using Starlight.Tools.Desktop;
using Starlight.Tools.Pc;
using Starlight.Overlay;
using Starlight.Tools;
using Starlight.Voice;

namespace Starlight.App;

/// <summary>Everything Starlight.exe runs: the core, the model, hotkeys and the tray icon.</summary>
internal sealed class Shell : IDisposable
{
    private readonly App _app;
    private readonly AssistantCore _core;
    private readonly ModelHost _model;
    private readonly TaskbarIcon _tray;
    private readonly MenuItem _killItem;
    private readonly EventWaitHandle _showEvent;
    private readonly OverlayManager _overlay;
    private readonly MenuItem _orbKillItem = new();
    private readonly RegisteredWaitHandle _showWait;
    private GlobalHotkeys? _hotkeys;
    private Microphone? _mic;
    private Recognizer? _recognizer;
    private Speaker? _speaker;
    private VoiceLoop? _voice;
    private WakeWord? _wake;
    private int _pttHeld;
    private SettingsWindow? _settings;
    private ChatWindow? _chat;
    private readonly MemoryStore _memoryStore;
    private readonly CredentialManagerStore _secrets;
    private readonly CodePrompt _codePrompt;
    private readonly WebTools _web;
    private readonly NetProbe _probe;
    private readonly ServerWatcher _watcher;
    private readonly MemoryTools _memory;

    public Shell(App app)
    {
        _app = app;
        var paths = StarlightPaths.Default();
        var approvals = new SwitchableApprovals();
        _core = AssistantCore.Start(paths, approvals);
        _secrets = new CredentialManagerStore();
        _codePrompt = new CodePrompt(_core.Events, text => _speaker?.Say(text));
        _web = WebTools.Create(paths, _core.Settings, _secrets, _codePrompt);
        _probe = new NetProbe();
        _watcher = new ServerWatcher(_core.Settings, _probe, _core.Events);
        var network = new NetworkTools(_core.Settings, _probe, new SshRunner(_secrets, Path.Combine(paths.DataRoot, "ssh-known-hosts.json")));
        _memoryStore = new MemoryStore(_core.Db);
        _memory = new MemoryTools(_memoryStore);
        _memory.TrackTurns(_core.Events);
        _core.AddTools(new PcTools(), new AppTools(new AppCatalog()), _memory, _web, network,
            new DesktopTools(new UiSnapshot(), _core.Events, () => !string.IsNullOrWhiteSpace(_core.Settings.Current.Model.VisionProjectorFile)));
        _core.KillSwitch.RegisterCleanup("input", Input.ReleaseAll);
        _core.KillSwitch.RegisterCleanup("pointer", () => _core.Events.Publish(new ClearPointer()));
        _model = new ModelHost(_core.Settings, _core.Events, paths);
        _core.CreateAgent(() => _model.ChatClient, contributors: [_memory]);
        _core.KillSwitch.RegisterCleanup("speech", () => _speaker?.Stop());

        _overlay = new OverlayManager(_core.Events, _core.Settings, BuildOrbMenu());
        approvals.Target = _core.Settings.Current.Overlay.Enabled ? _overlay : new WindowApprovals();

        _killItem = new MenuItem();
        _tray = new TaskbarIcon
        {
            ToolTipText = "Starlight",
            Icon = new System.Drawing.Icon(Application.GetResourceStream(new Uri("pack://application:,,,/Assets/starlight.ico"))!.Stream),
            ContextMenu = BuildMenu(),
        };
        _tray.TrayMouseDoubleClick += (_, _) => ShowChat();
        UpdateKillItem();

        _core.Events.Subscribe<KillSwitchChanged>(_ => OnUi(UpdateKillItem));
        _core.Events.Subscribe<ModelStatus>(m => OnUi(() => _tray.ToolTipText = $"Starlight: model {m.Status.ToLowerInvariant()}"));
        _core.Events.Subscribe<Notice>(n => OnUi(() => _tray.ShowNotification("Starlight", n.Text)));
        _core.Settings.Changed += s => OnUi(() => RegisterHotkeys());

        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, Program.ShowEventName);
        _showWait = ThreadPool.RegisterWaitForSingleObject(_showEvent, (_, _) => OnUi(ShowChat), null, Timeout.Infinite, false);
    }

    public async Task StartAsync()
    {
        _tray.ForceCreate();
        _overlay.Show();
        RegisterHotkeys();
        StartWithWindows.Apply(_core.Settings.Current.General.StartWithWindows);
        _ = Task.Run(StartVoice);
        _watcher.Start();
        if (string.IsNullOrWhiteSpace(_core.Settings.Current.Model.ChatModelFile))
        {
            ShowSettings();
            _tray.ShowNotification("Starlight", "Choose a model in Settings to get started.");
            return;
        }
        try
        {
            await _model.StartAsync();
        }
        catch (ModelHostException ex)
        {
            _tray.ShowNotification("Starlight", ex.Message);
        }
    }

    private ContextMenu BuildMenu()
    {
        var menu = new ContextMenu();
        void Add(string header, Action onClick)
        {
            var item = new MenuItem { Header = header };
            item.Click += (_, _) => onClick();
            menu.Items.Add(item);
        }
        Add("Open Starlight", ShowChat);
        Add("Settings", ShowSettings);
        Add("Report a problem…", ReportProblem);
        Add("Export diagnostics", ExportDiagnostics);
        menu.Items.Add(new Separator());
        _killItem.Click += (_, _) => ToggleKill("tray menu");
        menu.Items.Add(_killItem);
        menu.Items.Add(new Separator());
        Add("Quit Starlight", () => _app.Shutdown());
        return menu;
    }

    private ContextMenu BuildOrbMenu()
    {
        var menu = new ContextMenu();
        void Add(string header, Action onClick)
        {
            var item = new MenuItem { Header = header };
            item.Click += (_, _) => onClick();
            menu.Items.Add(item);
        }
        Add("Open chat", ShowChat);
        _orbKillItem.Click += (_, _) => ToggleKill("orb menu");
        menu.Items.Add(_orbKillItem);
        menu.Items.Add(new Separator());
        Add("Settings", ShowSettings);
        Add("Report a problem…", ReportProblem);
        menu.Items.Add(new Separator());
        Add("Quit Starlight", () => _app.Shutdown());
        return menu;
    }

    private void ToggleKill(string source)
    {
        if (_core.KillSwitch.IsEngaged)
        {
            _core.KillSwitch.Resume();
        }
        else
        {
            _core.KillSwitch.Engage(source);
        }
    }

    private void UpdateKillItem()
    {
        var header = _core.KillSwitch.IsEngaged ? "Resume Starlight" : $"Stop everything ({_core.Settings.Current.General.KillHotkey})";
        _killItem.Header = header;
        _orbKillItem.Header = header;
    }

    /// <summary>Loads the voice models. Missing files are reported once; the rest of Starlight runs without voice.</summary>
    private void StartVoice()
    {
        var settings = _core.Settings.Current.Voice;
        if (!settings.Enabled)
        {
            return;
        }
        try
        {
            var assets = new VoiceAssets(_core.Paths, _core.Settings);
            var missing = assets.Missing();
            if (missing.Count > 0)
            {
                Log.Warning("Voice is off, missing: {Missing}", string.Join("; ", missing));
                _core.Events.Publish(new Notice("Voice is off: some voice files are missing. Details are in the log.", NoticeLevel.Warning));
                return;
            }
            _mic = new Microphone(_core.Events, settings.InputDevice);
            _recognizer = new Recognizer(assets.WhisperModel, assets.VadModel);
            _speaker = new Speaker(assets.KokoroDir, _core.Events);
            _voice = new VoiceLoop(_mic, _recognizer, _speaker, _core.Agent!, _core.Events, _core.Settings)
            {
                ApprovalPending = () => _overlay.IsAsking,
                AnswerApproval = _overlay.AnswerByVoice,
                CodePending = () => _codePrompt.IsAsking,
                AnswerCode = _codePrompt.AnswerByVoice,
                ConversationFor = _core.Conversations.ForVoice,
            };
            if (settings.WakeWordEnabled)
            {
                try
                {
                    _wake = new WakeWord(assets.KwsDir, settings.WakePhrase, settings.WakeSensitivity);
                    _voice.EnableWakeWord(_wake);
                }
                catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or ArgumentException)
                {
                    Log.Warning(ex, "Wake phrase is off");
                    _core.Events.Publish(new Notice("The wake phrase is off: " + ex.Message, NoticeLevel.Warning));
                }
            }
            Log.Information("Voice ready; push-to-talk is {Key}", settings.PushToTalkHotkey);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Voice could not start");
            _core.Events.Publish(new Notice("Voice could not start. It's in the error log.", NoticeLevel.Error));
        }
    }

    private void PushToTalk()
    {
        if (_voice is null || Interlocked.Exchange(ref _pttHeld, 1) == 1)
        {
            return;
        }
        _voice.StartListening();
        var (_, key) = HotkeyParser.Parse(_core.Settings.Current.Voice.PushToTalkHotkey);
        _ = Task.Run(async () =>
        {
            await KeyState.WaitForReleaseAsync(key, TimeSpan.FromSeconds(60));
            Interlocked.Exchange(ref _pttHeld, 0);
            _voice.StopListening();
        });
    }

    private void RegisterHotkeys()
    {
        _hotkeys?.Dispose();
        _hotkeys = new GlobalHotkeys(
        [
            (_core.Settings.Current.General.KillHotkey, () => _core.KillSwitch.Engage("hotkey")),
            (_core.Settings.Current.Voice.PushToTalkHotkey, PushToTalk),
        ]);
        _ = _hotkeys.Failed.ContinueWith(t =>
        {
            foreach (var key in t.Result)
            {
                _core.Events.Publish(new Notice($"The hotkey {key} is used by another program. Pick another one in Settings.", NoticeLevel.Warning));
            }
        }, TaskScheduler.Default);
        UpdateKillItem();
    }

    public void ShowChat()
    {
        if (_chat is null)
        {
            _chat = new ChatWindow(_core, _memoryStore, ShowSettings);
            _chat.Closed += (_, _) => _chat = null;
        }
        _chat.Show();
        if (_chat.WindowState == WindowState.Minimized)
        {
            _chat.WindowState = WindowState.Normal;
        }
        _chat.Activate();
    }

    public void ShowSettings()
    {
        if (_settings is null)
        {
            _settings = new SettingsWindow(_core, _model, _secrets, ExportDiagnostics, ReportProblem);
            _settings.Closed += (_, _) => _settings = null;
        }
        _settings.Show();
        _settings.Activate();
    }

    private void ReportProblem()
    {
        var text = ReportProblemWindow.Ask();
        if (!string.IsNullOrWhiteSpace(text))
        {
            _core.Reports.Save(text);
            _tray.ShowNotification("Starlight", "Thanks. It's saved and will be in the next diagnostics export.");
        }
    }

    private void ExportDiagnostics()
    {
        try
        {
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var zip = _core.Exporter(SystemInfo).Export(desktop);
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{zip}\"") { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Diagnostics export failed");
            _tray.ShowNotification("Starlight", "The export failed: " + ex.Message);
        }
    }

    private string SystemInfo() =>
        new PcTools([]).SystemInfo()
        + $"\nModel: {_core.Settings.Current.Model.ChatModelFile} ({_model.State})"
        + "\n\nModel server output (last lines):\n" + _model.RecentOutput;

    private static void OnUi(Action action) => Application.Current?.Dispatcher.BeginInvoke(action);

    public void Dispose()
    {
        _showWait.Unregister(null);
        _showEvent.Dispose();
        _hotkeys?.Dispose();
        _voice?.Dispose();
        _overlay.Dispose();
        _speaker?.Dispose();
        _wake?.Dispose();
        _recognizer?.Dispose();
        _mic?.Dispose();
        _watcher.Dispose();
        _probe.Dispose();
        _web.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(3));
        _model.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5));
        _tray.Dispose();
        _core.Dispose();
    }
}
