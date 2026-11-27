using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Serilog;
using Starlight.Model;
using Starlight.Voice;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using ProgressBar = System.Windows.Controls.ProgressBar;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = Wpf.Ui.Controls.TextBox;

namespace Starlight.App;

/// <summary>
/// First start (plan 5.13): the models folder, the downloads (resumable, checked), a microphone test,
/// a short voice test and "start with Windows". Opens again from the tray until everything is in place.
/// </summary>
internal sealed class SetupWindow : FluentWindow, IDisposable
{
    public void Dispose()
    {
        _running?.Dispose();
        _running = null;
    }

    private readonly AssistantCore _core;
    private readonly TextBox _folder = new() { MinWidth = 380 };
    private readonly StackPanel _downloads = new();
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
    private readonly ProgressBar _micLevel = new() { Height = 8, Maximum = 1, Width = 380, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly Button _download = new() { Content = "Download", Appearance = ControlAppearance.Primary, Margin = new Thickness(0, 8, 0, 0) };
    private CancellationTokenSource? _running;

    public SetupWindow(AssistantCore core)
    {
        _core = core;
        Title = "Set up Starlight";
        Width = 620;
        Height = 700;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ExtendsContentIntoTitleBar = true;
        WindowBackdropType = WindowBackdropType.Mica;

        var p = new StackPanel { Margin = new Thickness(24, 8, 24, 24) };
        p.Children.Add(new TextBlock { Text = "Welcome to Starlight", FontSize = 22, FontWeight = FontWeights.SemiBold });
        p.Children.Add(new TextBlock { Text = "Everything runs on this PC. Starlight needs a few model files first; they stay on your disk.", Opacity = 0.7, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 16) });

        p.Children.Add(Heading("1. Models folder"));
        var current = core.Settings.Current.Model.ModelsDir;
        _folder.Text = current.Length > 0 ? current : DefaultFolder();
        _folder.TextChanged += (_, _) => FillDownloads();
        var browse = new Button { Content = "Browse…", Margin = new Thickness(8, 0, 0, 0) };
        browse.Click += (_, _) =>
        {
            var dialog = new OpenFolderDialog { Title = "Folder for Starlight's models" };
            if (dialog.ShowDialog(this) == true)
            {
                _folder.Text = dialog.FolderName;
            }
        };
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(_folder);
        row.Children.Add(browse);
        p.Children.Add(row);
        p.Children.Add(Hint("The chat model is a GGUF file of 8 to 10 GB; put it in this folder. The voice files are downloaded here too."));

        p.Children.Add(Heading("2. Voice files"));
        p.Children.Add(_downloads);
        _download.Click += async (_, _) => await DownloadAsync();
        p.Children.Add(_download);
        p.Children.Add(_status);

        p.Children.Add(Heading("3. Microphone"));
        var micTest = new Button { Content = "Test the microphone (5 seconds)" };
        micTest.Click += async (_, _) => await TestMicAsync(micTest);
        p.Children.Add(micTest);
        p.Children.Add(_micLevel);
        p.Children.Add(Hint("Speak while the test runs; the bar should move."));

        p.Children.Add(Heading("4. Voice"));
        var voiceTest = new Button { Content = "Play a voice sample" };
        voiceTest.Click += async (_, _) => await TestVoiceAsync(voiceTest);
        p.Children.Add(voiceTest);

        p.Children.Add(Heading("5. Startup"));
        var autostart = new ToggleSwitch { IsChecked = core.Settings.Current.General.StartWithWindows, Content = "Start Starlight when Windows starts" };
        autostart.Click += (_, _) => core.Settings.Update(s => s.General.StartWithWindows = autostart.IsChecked == true);
        p.Children.Add(autostart);

        var done = new Button { Content = "Done", Appearance = ControlAppearance.Primary, MinWidth = 120, Margin = new Thickness(0, 24, 0, 0), HorizontalAlignment = HorizontalAlignment.Right };
        done.Click += (_, _) => { SaveFolder(); Close(); };
        p.Children.Add(done);

        var root = new DockPanel();
        var titleBar = new TitleBar { Title = "Set up Starlight" };
        DockPanel.SetDock(titleBar, Dock.Top);
        root.Children.Add(titleBar);
        root.Children.Add(new ScrollViewer { Content = p });
        Content = root;
        FillDownloads();
        Closed += (_, _) =>
        {
            _running?.Cancel();
            Dispose();
        };
    }

    /// <summary>True when the voice files are missing, so the first start shows this window.</summary>
    public static bool Needed(AssistantCore core) =>
        string.IsNullOrWhiteSpace(core.Settings.Current.Model.ModelsDir)
        || DownloadManifest.Voice.Any(i => !Present(i, core.Settings.Current.Model.ModelsDir));

    private static bool Present(DownloadItem item, string dir)
    {
        var path = Path.Combine(dir, item.Target);
        return item.Kind == DownloadKind.File ? File.Exists(path) : Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any();
    }

    private static string DefaultFolder() =>
        Directory.Exists(@"D:\") ? @"D:\Starlight\models" : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Starlight", "models");

    private static TextBlock Heading(string text) => new() { Text = text, FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 16, 0, 6) };

    private static TextBlock Hint(string text) => new() { Text = text, Opacity = 0.6, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };

    private void FillDownloads()
    {
        _downloads.Children.Clear();
        var dir = _folder.Text.Trim();
        foreach (var item in DownloadManifest.Voice)
        {
            var have = dir.Length > 0 && Present(item, dir);
            _downloads.Children.Add(new TextBlock { Text = $"{(have ? "✓" : "•")} {item.Title}, about {item.ApproxBytes / 1_000_000:N0} MB", Tag = item.Id });
            _downloads.Children.Add(new ProgressBar { Height = 4, Maximum = 1, Width = 380, HorizontalAlignment = HorizontalAlignment.Left, Value = have ? 1 : 0, Tag = item.Id, Margin = new Thickness(0, 2, 0, 6) });
        }
    }

    private void SaveFolder()
    {
        var dir = _folder.Text.Trim();
        if (dir.Length > 0)
        {
            _core.Settings.Update(s => s.Model.ModelsDir = dir);
        }
    }

    private async Task DownloadAsync()
    {
        if (_running is not null)
        {
            _running.Cancel();
            return;
        }
        var dir = _folder.Text.Trim();
        try
        {
            Directory.CreateDirectory(dir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _status.Text = "That folder can't be used: " + ex.Message;
            return;
        }
        SaveFolder();
        _running = new CancellationTokenSource();
        _download.Content = "Stop";
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Starlight/1.0");
        var downloader = new Downloader(http);
        try
        {
            foreach (var item in DownloadManifest.Voice.Where(i => !Present(i, dir)))
            {
                var bar = _downloads.Children.OfType<ProgressBar>().First(b => (string)b.Tag == item.Id);
                _status.Text = $"Downloading {item.Title}…";
                var progress = new Progress<(long Done, long Total)>(x => bar.Value = x.Total > 0 ? (double)x.Done / x.Total : 0);
                await downloader.FetchAsync(item, dir, progress, _running.Token);
            }
            _status.Text = "All voice files are in place. Restart Starlight to turn voice on.";
        }
        catch (OperationCanceledException)
        {
            _status.Text = "Stopped. Press Download to go on where it stopped.";
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            Log.Warning(ex, "Setup download failed");
            _status.Text = "The download failed: " + ex.Message + " Press Download to try again; it goes on where it stopped.";
        }
        finally
        {
            _running.Dispose();
            _running = null;
            _download.Content = "Download";
            FillDownloads();
        }
    }

    private async Task TestMicAsync(Button button)
    {
        button.IsEnabled = false;
        try
        {
            using var mic = new Microphone(_core.Events, _core.Settings.Current.Voice.InputDevice);
            mic.Samples += s =>
            {
                var rms = Math.Sqrt(s.Select(x => (double)x * x).DefaultIfEmpty(0).Average());
                Dispatcher.BeginInvoke(() => _micLevel.Value = Math.Min(1, rms * 8));
            };
            mic.Open();
            await Task.Delay(5000);
            mic.Close();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Microphone test failed");
            _status.Text = "The microphone could not be opened: " + ex.Message;
        }
        finally
        {
            _micLevel.Value = 0;
            button.IsEnabled = true;
        }
    }

    private async Task TestVoiceAsync(Button button)
    {
        SaveFolder();
        var assets = new VoiceAssets(_core.Paths, _core.Settings);
        if (!File.Exists(Path.Combine(assets.KokoroDir, "model.onnx")))
        {
            _status.Text = "Download the voice files first.";
            return;
        }
        button.IsEnabled = false;
        try
        {
            using var speaker = await Task.Run(() => new Speaker(assets.KokoroDir, _core.Events) { Voice = _core.Settings.Current.Voice.Voice });
            speaker.Say($"Hello, I'm {_core.Settings.Current.General.AssistantName}. I run entirely on this PC. Ask me anything, or tell me what to do.");
            await Task.Delay(500);
            while (speaker.IsSpeaking)
            {
                await Task.Delay(200);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Voice test failed");
            _status.Text = "The voice could not play: " + ex.Message;
        }
        finally
        {
            button.IsEnabled = true;
        }
    }
}
