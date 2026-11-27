using System.ComponentModel;
using System.Diagnostics;
using Starlight;
using Starlight.Memory;
using Starlight.Security;
using Starlight.Tools.Web;
using Starlight.Tools.Network;
using Starlight.Tools.Code;
using Starlight.Skills;
using Starlight.Agent;
using Starlight.Events;
using Starlight.Model;
using Starlight.Tools;
using Starlight.Tools.Desktop;
using Starlight.Tools.Pc;

// Developer console (plan P0/P1): talk to the model and the tools without the app.
// starlight-dev [--data DIR] [--server llama-server.exe] [--model file.gguf] [--dev-tools]
// starlight-dev --selfcheck: checks every Windows-only part once, no model needed.
var argv = args.ToList();
string? Opt(string name)
{
    var i = argv.IndexOf(name);
    return i >= 0 && i + 1 < argv.Count ? argv[i + 1] : null;
}

var dataDir = Opt("--data") ?? Environment.GetEnvironmentVariable("STARLIGHT_DATA")
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Starlight");
var paths = new StarlightPaths(dataDir, AppContext.BaseDirectory);
if (Opt("--data") is null && Environment.GetEnvironmentVariable("STARLIGHT_DATA") is null)
{
    Console.WriteLine($"Note: this uses the real Starlight data in {dataDir} (settings, memory, saved logins). Pass --data <folder> for a separate one.");
}
using var core = AssistantCore.Start(paths, new ConsoleApprovals());
if (argv.Contains("--selfcheck"))
{
    return await Starlight.DevConsole.SelfCheck.RunAsync(core, paths);
}
if (Opt("--model") is { } model)
{
    core.Settings.Update(s =>
    {
        s.Model.ModelsDir = Path.GetDirectoryName(Path.GetFullPath(model))!;
        s.Model.ChatModelFile = Path.GetFileName(model);
    });
}

var memory = new MemoryTools(new MemoryStore(core.Db));
using var tracking = memory.TrackTurns(core.Events);
var secrets = new CredentialManagerStore();
await using var web = WebTools.Create(paths, core.Settings, secrets, new ConsolePrompt());
using var probe = new NetProbe();
var skillStore = new SkillStore(core.Db);
using var skillRecorder = new SkillRecorder(skillStore, core.Events, core.Dispatcher.SawUntrusted);
var skillTools = new SkillTools(skillStore) { Caller = core.Dispatcher };
core.AddTools(new PcTools(), new AppTools(new AppCatalog()), memory, web,
    new NetworkTools(core.Settings, probe, new SshRunner(secrets, Path.Combine(paths.DataRoot, "ssh-known-hosts.json"))),
    new CodeTools(new AppContainerSandbox(Path.Combine(paths.RuntimeDir, "python"), Path.Combine(paths.DataRoot, "sandbox")), paths.DataRoot),
    skillTools,
    new DesktopTools(new UiSnapshot(), core.Events, () => !string.IsNullOrWhiteSpace(core.Settings.Current.Model.VisionProjectorFile)));
core.KillSwitch.RegisterCleanup("input", Input.ReleaseAll);
if (argv.Contains("--dev-tools"))
{
    core.AddTools(new DevTools());
}
await using var host = new ModelHost(core.Settings, core.Events, paths, Opt("--server"));
var agent = core.CreateAgent(() => host.ChatClient, contributors: [memory, skillTools]);

using var hotkeys = new GlobalHotkeys([(core.Settings.Current.General.KillHotkey, () => core.KillSwitch.Engage("hotkey"))]);
var killedAt = Stopwatch.StartNew();
core.Events.Subscribe<KillSwitchChanged>(k =>
{
    if (k.Engaged)
    {
        killedAt.Restart();
    }
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine(k.Engaged ? $"\n[kill switch engaged: {k.Reason}] /resume to continue" : "[resumed]");
    Console.ResetColor();
});
core.Events.Subscribe<AnswerDelta>(d => Console.Write(d.Delta));
core.Events.Subscribe<ToolStarted>(t => Gray($"\n  → {t.Tool} {t.ArgumentsJson}"));
core.Events.Subscribe<ToolFinished>(t => Gray($"  ← {(t.Ok ? "ok" : "failed")} in {t.Took.TotalMilliseconds:0} ms: {t.Summary}"));
core.Events.Subscribe<ModelStatus>(m => Gray($"[model: {m.Status} {m.Detail}]"));
foreach (var failed in await hotkeys.Failed)
{
    Gray($"[hotkey {failed} could not be registered]");
}

Console.WriteLine($"Starlight developer console. Data: {paths.DataRoot}");
Console.WriteLine("Commands: /tools /kill /resume /new /report TEXT /export /quit. Anything else is said to Starlight.");
try
{
    await host.StartAsync();
}
catch (ModelHostException ex)
{
    Console.WriteLine($"Model not started: {ex.Message}");
}

var conversation = new Conversation();
while (true)
{
    Console.Write("\n> ");
    var line = Console.ReadLine();
    if (line is null || line == "/quit")
    {
        break;
    }
    switch (line.Split(' ', 2)[0])
    {
        case "":
            continue;
        case "/tools":
            foreach (var t in core.Tools.All)
            {
                Console.WriteLine($"{t.Name} [{core.Dispatcher.EffectivePermission(t)}]: {t.Description}");
            }
            continue;
        case "/kill":
            core.KillSwitch.Engage("console");
            continue;
        case "/resume":
            core.KillSwitch.Resume();
            continue;
        case "/new":
            conversation = new Conversation();
            Console.WriteLine("New conversation.");
            continue;
        case "/report":
            Console.WriteLine($"Saved {core.Reports.Save(line.Length > 8 ? line[8..] : "(no text)")}");
            continue;
        case "/export":
            Console.WriteLine($"Saved {core.Exporter(() => new PcTools([]).SystemInfo() + "\n\nModel server output:\n" + host.RecentOutput).Export(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory))}");
            continue;
    }
    var sw = Stopwatch.StartNew();
    var outcome = await agent.RunAsync(conversation, line, "chat");
    Console.WriteLine();
    if (outcome.Failed)
    {
        Console.WriteLine(outcome.Answer);
    }
    Gray($"[{sw.Elapsed.TotalSeconds:0.0} s{(core.KillSwitch.IsEngaged ? $", stopped {killedAt.ElapsedMilliseconds} ms after the kill switch" : "")}]");
}

return 0;

static void Gray(string text)
{
    Console.ForegroundColor = ConsoleColor.DarkGray;
    Console.WriteLine(text);
    Console.ResetColor();
}

/// <summary>Asks in the console: y, n or a (always).</summary>
internal sealed class ConsoleApprovals : IApprovalService
{
    public Task<ApprovalAnswer> RequestAsync(ApprovalRequest request, CancellationToken ct)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.Write($"\n  Allow {request.Question}? [y/n/a] ");
        Console.ResetColor();
        var answer = Console.ReadLine()?.Trim().ToLowerInvariant();
        return Task.FromResult(answer switch { "y" or "yes" => ApprovalAnswer.Yes, "a" or "always" => ApprovalAnswer.Always, _ => ApprovalAnswer.No });
    }
}

/// <summary>Asks for a sign-in code in the console.</summary>
internal sealed class ConsolePrompt : IUserPrompt
{
    public Task<string?> AskTextAsync(string title, string question, CancellationToken ct)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.Write($"\n  {question} ");
        Console.ResetColor();
        var answer = Console.ReadLine()?.Trim();
        return Task.FromResult(string.IsNullOrEmpty(answer) ? null : answer);
    }
}

/// <summary>Only with --dev-tools: a slow tool for the kill-switch test (P1 acceptance). Never in the app.</summary>
internal sealed class DevTools : IToolProvider
{
    [Tool("dev_wait", TimeoutSeconds = 60), Description("Waits for the given number of seconds. Only for testing.")]
    public async Task<string> Wait([Description("Seconds to wait")] int seconds, CancellationToken ct)
    {
        await Task.Delay(TimeSpan.FromSeconds(seconds), ct);
        return $"Waited {seconds} seconds.";
    }
}
