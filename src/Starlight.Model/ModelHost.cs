using System.ClientModel;
using System.Diagnostics;
using Microsoft.Extensions.AI;
using OpenAI;
using Serilog;
using Starlight.Events;
using Starlight.Platform;
using Starlight.Settings;

namespace Starlight.Model;

public enum ModelState { Stopped, Starting, Ready, Failed, Paused }

/// <summary>
/// Runs llama-server as a child process in a Job Object (it dies with Starlight), waits until it is
/// ready, and restarts it after a crash: at most 3 times in 5 minutes, then it tells the user.
/// </summary>
public sealed class ModelHost : IAsyncDisposable
{
    public const int MaxRestarts = 3;
    public static readonly TimeSpan RestartWindow = TimeSpan.FromMinutes(5);

    private readonly SettingsStore _settings;
    private readonly EventHub _events;
    private readonly StarlightPaths _paths;
    private readonly string _serverExe;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(3) };
    private readonly RestartBudget _restarts;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly LogTail _tail = new(200);
    private Process? _process;
    private JobObject? _job;
    private IChatClient? _client;
    private bool _stopping;

    public ModelHost(SettingsStore settings, EventHub events, StarlightPaths paths, string? serverExe = null, TimeProvider? time = null)
    {
        _settings = settings;
        _events = events;
        _paths = paths;
        _serverExe = serverExe ?? DefaultServerExe(paths);
        _restarts = new RestartBudget(MaxRestarts, RestartWindow, time ?? TimeProvider.System);
    }

    public ModelState State { get; private set; } = ModelState.Stopped;

    /// <summary>How long to wait for the model to load. Large models on a cold disk need a while.</summary>
    public TimeSpan StartTimeout { get; set; } = TimeSpan.FromMinutes(3);

    public Uri BaseUri => new($"http://127.0.0.1:{_settings.Current.Model.Port}/");

    /// <summary>The chat client while the model is ready, otherwise null. The agent loop reads this on every turn.</summary>
    public IChatClient? ChatClient => State == ModelState.Ready ? _client : null;

    /// <summary>The last lines llama-server printed, for errors and the diagnostics zip.</summary>
    public string RecentOutput => _tail.Text;

    public static string DefaultServerExe(StarlightPaths paths) =>
        Path.Combine(paths.RuntimeDir, "llama", OperatingSystem.IsWindows() ? "llama-server.exe" : "llama-server");

    /// <summary>The model file from Settings, as a full path. Throws a readable error if it is missing.</summary>
    public string ModelPath()
    {
        var m = _settings.Current.Model;
        if (string.IsNullOrWhiteSpace(m.ChatModelFile))
        {
            throw new ModelHostException("No model is chosen yet. Pick one in Settings.");
        }
        var path = Path.IsPathRooted(m.ChatModelFile) ? m.ChatModelFile : Path.Combine(m.ModelsDir, m.ChatModelFile);
        return File.Exists(path) ? path : throw new ModelHostException($"The model file is missing: {path}");
    }

    private string? ProjectorPath()
    {
        var m = _settings.Current.Model;
        if (string.IsNullOrWhiteSpace(m.VisionProjectorFile))
        {
            return null;
        }
        var path = Path.IsPathRooted(m.VisionProjectorFile) ? m.VisionProjectorFile : Path.Combine(m.ModelsDir, m.VisionProjectorFile);
        if (File.Exists(path))
        {
            return path;
        }
        Log.Warning("Vision projector {Path} is missing; starting without vision", path);
        return null;
    }

    /// <summary>Starts the server and waits until /health answers 200. Safe to call when already running.</summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (State is ModelState.Ready or ModelState.Starting)
            {
                return;
            }
            _stopping = false;
            await StartProcessAsync(ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // StopAsync ended the load; the state says Stopped or Paused.
        }
        finally
        {
            _gate.Release();
        }
    }

    private CancellationTokenSource? _starting;

    private async Task StartProcessAsync(CancellationToken outer)
    {
        SetState(ModelState.Starting, "Loading the model");
        Kill(); // a crashed server that is still the current one
        // StopAsync cancels this, so a stop does not wait for a model load of up to minutes.
        using var starting = CancellationTokenSource.CreateLinkedTokenSource(outer);
        _starting = starting;
        var ct = starting.Token;
        try
        {
            if (!File.Exists(_serverExe))
            {
                throw new ModelHostException($"The model server is missing: {_serverExe}. Reinstall Starlight.");
            }
            var args = ServerArguments.Build(_settings.Current.Model, ModelPath(), ProjectorPath());
            var psi = new ProcessStartInfo(_serverExe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(_serverExe)!,
            };
            foreach (var a in args)
            {
                psi.ArgumentList.Add(a);
            }
            Log.Information("Starting model server: {Exe} {Args}", _serverExe, string.Join(' ', args));
            var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, e) => _tail.Add(e.Data);
            process.ErrorDataReceived += (_, e) => _tail.Add(e.Data);
            process.Exited += (_, _) => OnExited(process);
            process.Start();
            _process = process;
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            if (OperatingSystem.IsWindows())
            {
                // Added after the start (output is redirected, so JobObject.Start can't be used). The server
                // starts no children, so nothing escapes the job in that short gap.
                try
                {
                    _job ??= new JobObject();
                    _job.Add(process);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Could not put the model server in the job object");
                }
            }

            await WaitForHealthAsync(process, ct);
            _client = CreateClient(BaseUri);
            SetState(ModelState.Ready, Path.GetFileName(ModelPath()));
            if (process.HasExited)
            {
                // It died between the last health check and Ready, when OnExited still ignored it: handle it now.
                OnExited(process);
            }
        }
        catch (ModelHostException ex)
        {
            Log.Error("Model server did not start: {Reason}", ex.Message);
            Kill();
            SetState(ModelState.Failed, ex.Message);
            throw;
        }
        catch (OperationCanceledException)
        {
            Kill();
            SetState(ModelState.Stopped, "Start cancelled");
            throw;
        }
        catch (Exception ex)
        {
            // Anything else (Windows could not start the file, a bad setting) must not leave the state at Starting.
            Log.Error(ex, "Model server did not start");
            Kill();
            SetState(ModelState.Failed, ex.Message);
            throw new ModelHostException("The model server did not start: " + ex.Message);
        }
        finally
        {
            _starting = null;
        }
    }

    private async Task WaitForHealthAsync(Process process, CancellationToken ct)
    {
        var deadline = Stopwatch.StartNew();
        var health = new Uri(BaseUri, "health");
        while (deadline.Elapsed < StartTimeout)
        {
            ct.ThrowIfCancellationRequested();
            if (process.HasExited)
            {
                throw new ModelHostException($"The model server stopped while loading (exit code {process.ExitCode}). Last output:\n{_tail.LastLines(15)}");
            }
            try
            {
                using var response = await _http.GetAsync(health, ct);
                if (response.IsSuccessStatusCode)
                {
                    Log.Information("Model server ready after {Seconds:0.0} s", deadline.Elapsed.TotalSeconds);
                    return;
                }
            }
            catch (HttpRequestException)
            {
                // Not listening yet.
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                // The health call timed out; the server is busy loading.
            }
            await Task.Delay(250, ct);
        }
        throw new ModelHostException($"The model did not load within {StartTimeout.TotalSeconds:0} seconds. Last output:\n{_tail.LastLines(15)}");
    }

    internal static IChatClient CreateClient(Uri baseUri)
    {
        var options = new OpenAIClientOptions { Endpoint = new Uri(baseUri, "v1"), NetworkTimeout = TimeSpan.FromMinutes(5) };
        // llama-server serves one model and ignores the model name and key.
        return new OpenAIClient(new ApiKeyCredential("local"), options).GetChatClient("local").AsIChatClient();
    }

    private Process? _exitHandled;

    private void OnExited(Process process)
    {
        // While starting, WaitForHealthAsync sees the exit and reports it; no restart for a model that never loaded.
        if (_stopping || State == ModelState.Starting || !ReferenceEquals(process, _process))
        {
            return;
        }
        // The Exited event and the check after Ready can both see one exit; it is handled once.
        if (ReferenceEquals(Interlocked.Exchange(ref _exitHandled, process), process))
        {
            return;
        }
        var code = SafeExitCode(process);
        Log.Error("Model server exited unexpectedly with code {Code}. Last output: {Output}", code, _tail.LastLines(30));
        _client = null;
        if (!_restarts.TryTake())
        {
            SetState(ModelState.Failed, $"The model server crashed {MaxRestarts + 1} times in {RestartWindow.TotalMinutes:0} minutes. It is in the error log.");
            _events.Publish(new Notice("My model keeps crashing, so I stopped restarting it. Export diagnostics from the tray and send them.", NoticeLevel.Error));
            return;
        }
        _ = Task.Run(async () =>
        {
            await _gate.WaitAsync();
            try
            {
                // Only if nothing else happened meanwhile: a Stop or a new Start replaced the crashed process.
                if (!_stopping && ReferenceEquals(_process, process))
                {
                    Log.Warning("Restarting the model server");
                    await StartProcessAsync(CancellationToken.None);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Model server restart failed");
            }
            finally
            {
                _gate.Release();
            }
        });
    }

    /// <summary>Stops the server (game mode, quit, or a model change).</summary>
    public async Task StopAsync(ModelState after = ModelState.Stopped)
    {
        _stopping = true;
        try
        {
            _starting?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
        await _gate.WaitAsync();
        try
        {
            _stopping = true;
            _client = null;
            Kill();
            SetState(after, after == ModelState.Paused ? "Paused for a game" : "Stopped");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Restarts with the current settings, for example after the model was changed in Settings.</summary>
    public async Task RestartAsync(CancellationToken ct = default)
    {
        await StopAsync();
        await StartAsync(ct);
    }

    private void Kill()
    {
        var p = _process;
        _process = null;
        if (p is null)
        {
            return;
        }
        try
        {
            if (!p.HasExited)
            {
                p.Kill(entireProcessTree: true);
                p.WaitForExit(5000);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Log.Warning(ex, "Could not stop the model server");
        }
        finally
        {
            p.Dispose();
        }
    }

    private static int? SafeExitCode(Process p)
    {
        try { return p.ExitCode; } catch (InvalidOperationException) { return null; }
    }

    private void SetState(ModelState state, string detail)
    {
        State = state;
        _events.Publish(new ModelStatus(state.ToString(), detail));
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _job?.Dispose();
        _http.Dispose();
        _gate.Dispose();
    }
}

public sealed class ModelHostException(string message) : Exception(message);

/// <summary>Allows N restarts within a sliding time window.</summary>
internal sealed class RestartBudget(int max, TimeSpan window, TimeProvider time)
{
    private readonly Queue<DateTimeOffset> _taken = new();
    private readonly Lock _gate = new();

    public bool TryTake()
    {
        lock (_gate)
        {
            var now = time.GetUtcNow();
            while (_taken.Count > 0 && now - _taken.Peek() > window)
            {
                _taken.Dequeue();
            }
            if (_taken.Count >= max)
            {
                return false;
            }
            _taken.Enqueue(now);
            return true;
        }
    }
}

/// <summary>Keeps the last lines of the server's output.</summary>
internal sealed class LogTail(int capacity)
{
    private readonly Queue<string> _lines = new();
    private readonly Lock _gate = new();

    public void Add(string? line)
    {
        if (line is null)
        {
            return;
        }
        lock (_gate)
        {
            _lines.Enqueue(line);
            while (_lines.Count > capacity)
            {
                _lines.Dequeue();
            }
        }
    }

    public string Text => LastLines(capacity);

    public string LastLines(int n)
    {
        lock (_gate)
        {
            return string.Join('\n', _lines.Skip(Math.Max(0, _lines.Count - n)));
        }
    }
}
