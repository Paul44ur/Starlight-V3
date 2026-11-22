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
    private SettingsWindow? _settings;

    public Shell(App app)
    {
        _app = app;
        var paths = StarlightPaths.Default();
        _core = AssistantCore.Start(paths, new WindowApprovals());
        _core.AddTools(new PcTools(), new AppTools(new AppCatalog()));
        _model = new ModelHost(_core.Settings, _core.Events, paths);
        _core.CreateAgent(() => _model.ChatClient);

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

    private void RegisterHotkeys()
    {
        _hotkeys?.Dispose();
        _hotkeys = new GlobalHotkeys([(_core.Settings.Current.General.KillHotkey, () => _core.KillSwitch.Engage("hotkey"))]);
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
        _model.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5));
        _tray.Dispose();
        _core.Dispose();
    }
}
