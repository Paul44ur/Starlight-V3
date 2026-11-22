using Microsoft.Extensions.AI;
using Serilog;
using Starlight.Agent;
using Starlight.Diagnostics;
using Starlight.Events;
using Starlight.Settings;
using Starlight.Storage;
using Starlight.Tools;

namespace Starlight;

/// <summary>
/// The services every front end shares (the app, the developer console, the eval), wired the same way.
/// The model is passed in as a function, so the core does not depend on how it runs.
/// </summary>
public sealed class AssistantCore : IDisposable
{
    private AssistantCore(StarlightPaths paths, IApprovalService approvals)
    {
        Paths = paths;
        Events = new EventHub();
        KillSwitch = new KillSwitch(Events);
        Settings = new SettingsStore(paths.SettingsFile);
        Trace = new SessionTrace(paths.TraceDir);
        Trace.Prune();
        Db = new StarlightDb(paths.DatabaseFile);
        Tools = new ToolRegistry();
        Approvals = approvals;
        Dispatcher = new ToolDispatcher(Tools, KillSwitch, approvals, Settings, Db, Trace, Events);
        Reports = new ProblemReports(paths, Trace);
        Conversations = new Conversations(Db);
    }

    public StarlightPaths Paths { get; }
    public EventHub Events { get; }
    public KillSwitch KillSwitch { get; }
    public SettingsStore Settings { get; }
    public SessionTrace Trace { get; }
    public StarlightDb Db { get; }
    public ToolRegistry Tools { get; }
    public IApprovalService Approvals { get; }
    public ToolDispatcher Dispatcher { get; }
    public ProblemReports Reports { get; }
    public Conversations Conversations { get; }
    public AgentLoop? Agent { get; private set; }

    /// <summary>Sets up logging and crash capture, then the services. Call once per process.</summary>
    public static AssistantCore Start(StarlightPaths paths, IApprovalService approvals, bool installCrashHandler = true)
    {
        Logging.Configure(paths);
        if (installCrashHandler)
        {
            CrashHandler.Install(paths);
        }
        Log.Information("Starlight {Version} starting, data in {Data}", Logging.Version, paths.DataRoot);
        return new AssistantCore(paths, approvals);
    }

    public void AddTools(params IToolProvider[] providers)
    {
        foreach (var p in providers)
        {
            Tools.Add(p);
        }
    }

    /// <summary>Creates the turn loop once all tools are added.</summary>
    public AgentLoop CreateAgent(Func<IChatClient?> model, IEnumerable<IPromptContributor>? contributors = null, IEnumerable<ITurnObserver>? observers = null)
    {
        Log.Information("{Count} tools: {Names}", Tools.All.Count, string.Join(", ", Tools.All.Select(t => t.Name)));
        Agent = new AgentLoop(model, Tools, Dispatcher, Settings, KillSwitch, Events, Trace, Db, contributors ?? [], observers ?? []);
        return Agent;
    }

    public DiagnosticsExporter Exporter(Func<string> systemInfo) => new(Paths, Settings.ToJson, systemInfo);

    public void Dispose()
    {
        Log.Information("Starlight stopping");
        Log.CloseAndFlush();
    }
}
