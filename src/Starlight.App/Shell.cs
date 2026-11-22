using System.IO;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using H.NotifyIcon;
using Serilog;
using Starlight.Events;
using Starlight.Model;
using Starlight.Tools.Desktop;
using Starlight.Tools.Pc;
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
    private readonly RegisteredWaitHandle _showWait;
    private GlobalHotkeys? _hotkeys;
    private Microphone? _mic;
    private Recognizer? _recognizer;
    private Speaker? _speaker;
    private VoiceLoop? _voice;
    private int _pttHeld;
    private SettingsWindow? _settings;

    public Shell(App app)
    {
        _app = app;
        var paths = StarlightPaths.Default();
        _core = AssistantCore.Start(paths, new WindowApprovals());
        _core.AddTools(new PcTools(), new AppTools(new AppCatalog()));
        _model = new ModelHost(_core.Settings, _core.Events, paths);
        _core.CreateAgent(() => _model.ChatClient);
        _core.KillSwitch.RegisterCleanup("speech", () => _speaker?.Stop());

        _killItem = new MenuItem();
        _tray = new TaskbarIcon
        {
            ToolTipText = "Starlight",
            Icon = new System.Drawing.Icon(Application.GetResourceStream(new Uri("pack://application:,,,/Assets/starlight.ico"))!.Stream),
            ContextMenu = BuildMenu(),
        };
        _tray.TrayMouseDoubleClick += (_, _) => ShowSettings();
        UpdateKillItem();

        _core.Events.Subscribe<KillSwitchChanged>(_ => OnUi(UpdateKillItem));
        _core.Events.Subscribe<ModelStatus>(m => OnUi(() => _tray.ToolTipText = $"Starlight: model {m.Status.ToLowerInvariant()}"));
        _core.Events.Subscribe<Notice>(n => OnUi(() => _tray.ShowNotification("Starlight", n.Text)));
        _core.Settings.Changed += s => OnUi(() => RegisterHotkeys());

        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, Program.ShowEventName);
        _showWait = ThreadPool.RegisterWaitForSingleObject(_showEvent, (_, _) => OnUi(ShowSettings), null, Timeout.Infinite, false);
    }

    public async Task StartAsync()
    {
        _tray.ForceCreate();
        RegisterHotkeys();
        StartWithWindows.Apply(_core.Settings.Current.General.StartWithWindows);
        _ = Task.Run(StartVoice);
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
        Add("Settings", ShowSettings);
        Add("Report a problem…", ReportProblem);
        Add("Export diagnostics", ExportDiagnostics);
        menu.Items.Add(new Separator());
        _killItem.Click += (_, _) =>
        {
            if (_core.KillSwitch.IsEngaged)
            {
                _core.KillSwitch.Resume();
            }
            else
            {
                _core.KillSwitch.Engage("tray menu");
            }
        };
        menu.Items.Add(_killItem);
        menu.Items.Add(new Separator());
        Add("Quit Starlight", () => _app.Shutdown());
        return menu;
    }

    private void UpdateKillItem() =>
        _killItem.Header = _core.KillSwitch.IsEngaged ? "Resume Starlight" : $"Stop everything ({_core.Settings.Current.General.KillHotkey})";

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
            _voice = new VoiceLoop(_mic, _recognizer, _speaker, _core.Agent!, _core.Events, _core.Settings);
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

    public void ShowSettings()
    {
        if (_settings is null)
        {
            _settings = new SettingsWindow(_core, _model, ExportDiagnostics, ReportProblem);
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
        _speaker?.Dispose();
        _recognizer?.Dispose();
        _mic?.Dispose();
        _model.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5));
        _tray.Dispose();
        _core.Dispose();
    }
}
