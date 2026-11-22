using System.Diagnostics;
using System.Text;
using Starlight;
using Starlight.Model;
using Starlight.Platform;
using Starlight.Security;
using Starlight.Tools;
using Starlight.Tools.Code;
using Starlight.Tools.Desktop;
using Starlight.Tools.Network;
using Starlight.Tools.Web;

namespace Starlight.DevConsole;

/// <summary>
/// starlight-dev --selfcheck: runs every Windows-only part once, without a model and without downloads, and
/// writes self-check.md. One command for the first test on the PC; each line is a pass, a fail or a skip.
/// </summary>
internal static partial class SelfCheck
{
    private sealed record Line(string Name, string Result, string Detail, long Ms);

    public static async Task<int> RunAsync(AssistantCore core, StarlightPaths paths)
    {
        var lines = new List<Line>();
        async Task Check(string name, Func<Task<string>> run)
        {
            var sw = Stopwatch.StartNew();
            string result, detail;
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                detail = await run().WaitAsync(timeout.Token);
                result = detail.StartsWith("SKIP", StringComparison.Ordinal) ? "skip" : "pass";
            }
            catch (Exception ex)
            {
                result = "FAIL";
                detail = $"{ex.GetType().Name}: {ex.Message}";
            }
            lines.Add(new Line(name, result, detail.Replace('\n', ' '), sw.ElapsedMilliseconds));
            Console.ForegroundColor = result == "pass" ? ConsoleColor.Green : result == "skip" ? ConsoleColor.Yellow : ConsoleColor.Red;
            Console.WriteLine($"{result,-5} {name} ({sw.ElapsedMilliseconds} ms): {detail}");
            Console.ResetColor();
        }

        await Check("Credential Manager", () =>
        {
            var store = new CredentialManagerStore();
            store.Set("selfcheck", "user", "secret-ü-1");
            var back = store.Get("selfcheck");
            var listed = store.List("self").Contains("selfcheck");
            store.Delete("selfcheck");
            return Task.FromResult(back?.Secret == "secret-ü-1" && back.UserName == "user" && listed && store.Get("selfcheck") is null
                ? "write, read, list, delete" : throw new InvalidOperationException($"read back {back?.UserName}/{back?.Secret}, listed {listed}"));
        });

        await Check("DPAPI session vault", () =>
        {
            var dir = Path.Combine(paths.DataRoot, "selfcheck-sessions");
            var vault = new SessionVault(dir);
            vault.Save("www.example.com", [new BrowserCookie("sid", "42", ".example.com", "/", -1, true, true, null)]);
            var raw = File.ReadAllBytes(Directory.GetFiles(dir)[0]);
            var ok = vault.Load("example.com").SingleOrDefault()?.Value == "42" && !Encoding.UTF8.GetString(raw).Contains("sid", StringComparison.Ordinal);
            Directory.Delete(dir, true);
            return Task.FromResult(ok ? "encrypted on disk, read back" : throw new InvalidOperationException("cookie not read back or stored in clear text"));
        });

        await Check("Job Object ends children", async () =>
        {
            var child = Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 30 127.0.0.1") { CreateNoWindow = true, UseShellExecute = false })!;
            using (var job = new JobObject())
            {
                job.Add(child);
            }
            await Task.Delay(500);
            return child.HasExited ? "child ended when the job closed" : throw new InvalidOperationException("child still running");
        });

        await Check("Kill switch stops a tool", async () =>
        {
            core.Tools.Add(new SlowTool());
            var call = core.Dispatcher.InvokeAsync("selfcheck_wait", new Dictionary<string, object?>(), "selfcheck", "internal", CancellationToken.None);
            await Task.Delay(500);
            var sw = Stopwatch.StartNew();
            core.KillSwitch.Engage("self-check");
            var r = await call;
            var ms = sw.ElapsedMilliseconds;
            core.KillSwitch.Resume();
            return !r.Ok && ms < 300 ? $"stopped in {ms} ms" : throw new InvalidOperationException($"ok={r.Ok}, took {ms} ms (limit 300)");
        });

        await Check("llama-server", async () =>
        {
            var exe = ModelHost.DefaultServerExe(paths);
            if (!File.Exists(exe))
            {
                return "SKIP: not in " + exe + " (the installer brings it)";
            }
            using var p = Process.Start(new ProcessStartInfo(exe, "--version") { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true })!;
            var text = await p.StandardOutput.ReadToEndAsync() + await p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync();
            return text.Split('\n').FirstOrDefault(l => l.Contains("version", StringComparison.OrdinalIgnoreCase))?.Trim() ?? text.Trim();
        });

        await Check("UI Automation", () =>
        {
            using var snap = new UiSnapshot();
            var (title, elements) = snap.Take(GetForegroundWindow());
            return Task.FromResult($"\"{title}\": {elements.Count} controls");
        });

        await Check("Screenshot and OCR", async () =>
        {
            using var shot = ScreenCapture.Capture(ScreenCapture.VirtualDesktop());
            var lines = await Ocr.ReadAsync(shot, CancellationToken.None);
            return $"{shot.Area.Width}x{shot.Area.Height}, {lines.Count} lines of text read";
        });

        await Check("Edge, headless", async () =>
        {
            var exe = EdgeBrowser.FindExe() ?? throw new InvalidOperationException("Edge not found");
            await using var browser = new EdgeBrowser(Path.Combine(paths.DataRoot, "selfcheck-browser"));
            var (url, html) = await browser.RenderAsync(new Uri("https://example.com/"), [], CancellationToken.None);
            var page = PageText.Clean(html);
            return page.Text.Contains("Example Domain", StringComparison.Ordinal) || page.Title.Contains("Example", StringComparison.Ordinal)
                ? $"{Path.GetFileName(exe)} rendered {url.Host}" : throw new InvalidOperationException("page text: " + page.Text[..Math.Min(80, page.Text.Length)]);
        });

        await Check("Network probe", async () =>
        {
            using var probe = new NetProbe();
            var ping = await probe.PingAsync("1.1.1.1", CancellationToken.None);
            var port = await probe.PortAsync("1.1.1.1", 443, CancellationToken.None);
            var (days, tls) = await probe.TlsAsync("example.com", 443, CancellationToken.None);
            return ping.Ok && port.Ok && days > 0 ? $"{ping.Text}; {port.Text}; {tls}" : throw new InvalidOperationException($"{ping.Text}; {port.Text}; {tls}");
        });

        var python = Path.Combine(paths.RuntimeDir, "python");
        var sandbox = new AppContainerSandbox(python, Path.Combine(paths.DataRoot, "selfcheck-sandbox"));
        async Task<SandboxResult> Script(string code)
        {
            var dir = Directory.CreateDirectory(Path.Combine(paths.DataRoot, "selfcheck-sandbox", "work-" + Guid.NewGuid().ToString("N")[..6])).FullName;
            var file = Path.Combine(paths.DataRoot, "selfcheck-sandbox", "s.py");
            await File.WriteAllTextAsync(file, code);
            try
            {
                return await sandbox.RunAsync(file, dir, TimeSpan.FromSeconds(30), CancellationToken.None);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }
        var hasPython = AppContainerSandbox.IsAvailable(python);
        await Check("Sandbox runs Python", async () =>
        {
            if (!hasPython)
            {
                return "SKIP: no Python in " + python + " (the installer brings it)";
            }
            var r = await Script("import sys, pathlib\npathlib.Path(sys.argv[1], 'ok.txt').write_text('hi')\nprint('hello', sys.version.split()[0])");
            return r.ExitCode == 0 && r.Output.Contains("hello", StringComparison.Ordinal) ? r.Output.Trim() : throw new InvalidOperationException($"exit {r.ExitCode}: {r.Output}");
        });
        await Check("Sandbox has no network", async () =>
        {
            if (!hasPython)
            {
                return "SKIP: no Python";
            }
            var r = await Script("import socket\nsocket.create_connection(('1.1.1.1', 80), timeout=5)\nprint('CONNECTED')");
            return r.ExitCode != 0 && !r.Output.Contains("CONNECTED", StringComparison.Ordinal) ? "connection refused in the sandbox" : throw new InvalidOperationException("the script reached the network");
        });
        await Check("Sandbox can't write outside its folder", async () =>
        {
            if (!hasPython)
            {
                return "SKIP: no Python";
            }
            var target = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "starlight-selfcheck.txt");
            var r = await Script($"open(r'{target}', 'w').write('x')\nprint('WROTE')");
            var wrote = File.Exists(target);
            if (wrote)
            {
                File.Delete(target);
            }
            return !wrote && r.ExitCode != 0 ? "write to the user folder refused" : throw new InvalidOperationException("the script wrote outside its folder");
        });
        await Check("Sandbox can't start programs", async () =>
        {
            if (!hasPython)
            {
                return "SKIP: no Python";
            }
            var r = await Script("import subprocess\nsubprocess.run(['cmd', '/c', 'echo', 'CHILD'])\nprint('STARTED')");
            return !r.Output.Contains("STARTED", StringComparison.Ordinal) ? "child process refused" : throw new InvalidOperationException("a child process started: " + r.Output);
        });

        await Check("Voice files", () =>
        {
            var dir = core.Settings.Current.Model.ModelsDir;
            if (dir.Length == 0)
            {
                return Task.FromResult("SKIP: no models folder set yet (first start)");
            }
            var missing = DownloadManifest.Voice.Where(i => !File.Exists(Path.Combine(dir, i.Target)) && !Directory.Exists(Path.Combine(dir, i.Target))).Select(i => i.Id).ToList();
            return Task.FromResult(missing.Count == 0 ? "all present in " + dir : "SKIP: missing " + string.Join(", ", missing));
        });

        foreach (var d in new[] { "selfcheck-sandbox", "selfcheck-browser" })
        {
            try
            {
                Directory.Delete(Path.Combine(paths.DataRoot, d), true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        var report = new StringBuilder($"# Starlight self-check {DateTime.Now:yyyy-MM-dd HH:mm}\n\n{Environment.OSVersion}, {Environment.ProcessorCount} threads\n\n| Check | Result | Time | Detail |\n| --- | --- | --- | --- |\n");
        foreach (var l in lines)
        {
            report.Append($"| {l.Name} | {l.Result} | {l.Ms} ms | {l.Detail.Replace("|", "/", StringComparison.Ordinal)} |\n");
        }
        var file = Path.Combine(paths.DataRoot, "self-check.md");
        await File.WriteAllTextAsync(file, report.ToString());
        var failed = lines.Count(l => l.Result == "FAIL");
        Console.WriteLine($"\n{lines.Count - failed} of {lines.Count} passed or skipped, {failed} failed. Report: {file}");
        return failed == 0 ? 0 : 1;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    private sealed class SlowTool : IToolProvider
    {
        [Tool("selfcheck_wait", TimeoutSeconds = 30), System.ComponentModel.Description("Waits 20 seconds.")]
        public async Task<string> Wait(CancellationToken ct)
        {
            await Task.Delay(TimeSpan.FromSeconds(20), ct);
            return "finished";
        }
    }
}
