using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.AI;
using Starlight;
using Starlight.Memory;
using Starlight.Security;
using Starlight.Tools.Web;
using Starlight.Tools.Network;
using Starlight.Tools.Code;
using Starlight.Skills;
using Starlight.Agent;
using Starlight.Eval;
using Starlight.Model;
using Starlight.Tools;
using Starlight.Tools.Desktop;
using Starlight.Tools.Pc;

// Eval runner (plan section 6). Runs on the user's PC with the real model and the real tool list.
// Tools are never executed: only the model's first decision is scored, so nothing on the PC changes.
// starlight-eval --models a.gguf[,b.gguf] [--server llama-server.exe] [--cases DIR] [--out DIR] [--ctx N]
var argv = args.ToList();
string? Opt(string name)
{
    var i = argv.IndexOf(name);
    return i >= 0 && i + 1 < argv.Count ? argv[i + 1] : null;
}

var models = (Opt("--models") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
if (models.Length == 0)
{
    Console.WriteLine("Usage: starlight-eval --models a.gguf[,b.gguf] [--server llama-server.exe] [--cases DIR] [--out DIR] [--ctx N]");
    return 2;
}
var casesDir = Opt("--cases") ?? Path.Combine(AppContext.BaseDirectory, "cases");
var outDir = Opt("--out") ?? Path.Combine(Environment.CurrentDirectory, "reports");
var cases = Directory.GetFiles(casesDir, "*.json").SelectMany(EvalCase.Load).ToList();

var data = Path.Combine(Path.GetTempPath(), "starlight-eval-" + Guid.NewGuid().ToString("N")[..8]);
var paths = new StarlightPaths(data, AppContext.BaseDirectory);
using var core = AssistantCore.Start(paths, new DenyAllApprovals(), installCrashHandler: false);
core.AddTools(new PcTools(), new AppTools(new AppCatalog()), new MemoryTools(new MemoryStore(core.Db)),
    WebTools.Create(paths, core.Settings, new MemorySecretStore(), new NoUserPrompt()),
    new NetworkTools(core.Settings, new NetProbe(), new SshRunner(new MemorySecretStore(), Path.Combine(data, "known-hosts.json"))),
    new CodeTools(new AppContainerSandbox(Path.Combine(paths.RuntimeDir, "python"), Path.Combine(data, "sandbox")), data),
    new SkillTools(new SkillStore(core.Db)), new DesktopTools(new UiSnapshot(), core.Events, () => false));
var declarations = core.Tools.Declarations();
var known = core.Tools.All.Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
var runnable = cases.Where(c => c.Expect != "tool" || c.Tools.Any(known.Contains)).ToList();
Console.WriteLine($"{cases.Count} cases, {runnable.Count} runnable with the {known.Count} tools built so far.");

var report = new StringBuilder();
report.AppendLine(CultureInfo.InvariantCulture, $"# Eval report {DateTime.Now:yyyy-MM-dd HH:mm}");
report.AppendLine();
report.AppendLine(CultureInfo.InvariantCulture, $"Tools: {string.Join(", ", known.Order())}. Cases: {runnable.Count} run, {cases.Count - runnable.Count} skipped (tool not built yet).");
report.AppendLine("Thresholds for 1.0: 90 % overall, 80 % per tool.");
var summary = new List<string>();

foreach (var modelFile in models)
{
    core.Settings.Update(s =>
    {
        s.Model.ModelsDir = Path.GetDirectoryName(Path.GetFullPath(modelFile))!;
        s.Model.ChatModelFile = Path.GetFileName(modelFile);
        s.Model.VisionProjectorFile = "";
        if (Opt("--ctx") is { } ctx)
        {
            s.Model.ContextSize = int.Parse(ctx, CultureInfo.InvariantCulture);
        }
    });
    await using var host = new ModelHost(core.Settings, core.Events, paths, Opt("--server"));
    var load = Stopwatch.StartNew();
    try
    {
        await host.StartAsync();
    }
    catch (ModelHostException ex)
    {
        report.AppendLine().AppendLine(CultureInfo.InvariantCulture, $"## {Path.GetFileName(modelFile)}").AppendLine().AppendLine("Did not start: " + ex.Message);
        continue;
    }
    var loadSeconds = load.Elapsed.TotalSeconds;
    var client = host.ChatClient!;
    var results = new List<(EvalCase Case, EvalVerdict Verdict, double Seconds, double? TokensPerSecond)>();
    foreach (var c in runnable)
    {
        var system = SystemPrompt.Build(core.Settings.Current, DateTimeOffset.Now, [], c.Source);
        var options = new ChatOptions { Tools = declarations, ToolMode = ChatToolMode.Auto, Temperature = (float)core.Settings.Current.Model.Temperature, AllowMultipleToolCalls = false };
        var sw = Stopwatch.StartNew();
        EvalVerdict verdict;
        double? tps = null;
        try
        {
            var response = await client.GetResponseAsync([new ChatMessage(ChatRole.System, system), new ChatMessage(ChatRole.User, c.Say)], options);
            verdict = EvalScorer.Score(c, response);
            if (response.Usage?.OutputTokenCount is > 0 and var n)
            {
                tps = n / sw.Elapsed.TotalSeconds;
            }
        }
        catch (Exception ex)
        {
            verdict = new EvalVerdict(false, $"error: {ex.GetType().Name}: {ex.Message}");
        }
        results.Add((c, verdict, sw.Elapsed.TotalSeconds, tps));
        Console.WriteLine($"{(verdict.Pass ? "PASS" : "FAIL")} {c.Id,-12} {sw.Elapsed.TotalSeconds,5:0.0}s  {verdict.Why}");
    }
    await host.StopAsync();

    var passed = results.Count(r => r.Verdict.Pass);
    var rate = results.Count == 0 ? 0 : 100.0 * passed / results.Count;
    var medianSeconds = Median(results.Select(r => r.Seconds));
    var tpsValues = results.Where(r => r.TokensPerSecond is not null).Select(r => r.TokensPerSecond!.Value).ToList();
    var medianTps = tpsValues.Count == 0 ? 0 : Median(tpsValues);
    summary.Add($"| {Path.GetFileName(modelFile)} | {rate:0} % ({passed}/{results.Count}) | {medianSeconds:0.00} s | {medianTps:0} | {loadSeconds:0} s |");

    report.AppendLine().AppendLine(CultureInfo.InvariantCulture, $"## {Path.GetFileName(modelFile)}").AppendLine();
    report.AppendLine(CultureInfo.InvariantCulture, $"Pass rate {rate:0.0} % ({passed}/{results.Count}). Median time per case {medianSeconds:0.00} s. Load time {loadSeconds:0} s.");
    report.AppendLine().AppendLine("| Expected | Pass rate |").AppendLine("| --- | --- |");
    foreach (var g in results.GroupBy(r => r.Case.Expect == "tool" ? r.Case.Tools.First() : "(" + r.Case.Expect + ")").OrderBy(g => g.Key))
    {
        report.AppendLine(CultureInfo.InvariantCulture, $"| {g.Key} | {100.0 * g.Count(r => r.Verdict.Pass) / g.Count():0} % ({g.Count(r => r.Verdict.Pass)}/{g.Count()}) |");
    }
    report.AppendLine().AppendLine("| Case | Result | Time | Detail |").AppendLine("| --- | --- | --- | --- |");
    foreach (var r in results)
    {
        report.AppendLine(CultureInfo.InvariantCulture, $"| {r.Case.Id} | {(r.Verdict.Pass ? "pass" : "**fail**")} | {r.Seconds:0.0} s | {r.Verdict.Why.Replace('|', '/')} |");
    }
}

report.Insert(report.ToString().IndexOf("Thresholds", StringComparison.Ordinal),
    "| Model | Pass rate | Median time | Tokens/s | Load |\n| --- | --- | --- | --- | --- |\n" + string.Join('\n', summary) + "\n\n");
Directory.CreateDirectory(outDir);
var file = Path.Combine(outDir, $"eval-{DateTime.Now:yyyyMMdd-HHmm}.md");
File.WriteAllText(file, report.ToString());
Console.WriteLine($"Report: {file}");
try { Directory.Delete(data, true); } catch (IOException) { }
return 0;

static double Median(IEnumerable<double> values)
{
    var v = values.Order().ToList();
    return v.Count == 0 ? 0 : v.Count % 2 == 1 ? v[v.Count / 2] : (v[v.Count / 2 - 1] + v[v.Count / 2]) / 2;
}
