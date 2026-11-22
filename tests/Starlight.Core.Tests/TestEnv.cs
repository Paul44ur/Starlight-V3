using Starlight.Diagnostics;
using Starlight.Events;
using Starlight.Settings;
using Starlight.Storage;
using Starlight.Tools;

namespace Starlight.Tests;

/// <summary>A throwaway data folder with the core services wired up.</summary>
internal sealed class TestEnv : IDisposable
{
    public TestEnv()
    {
        Root = Path.Combine(Path.GetTempPath(), "starlight-tests", Guid.NewGuid().ToString("N"));
        Paths = new StarlightPaths(Path.Combine(Root, "data"), Path.Combine(Root, "app"));
        Events = new EventHub();
        KillSwitch = new KillSwitch(Events);
        Settings = new SettingsStore(Paths.SettingsFile);
        Trace = new SessionTrace(Paths.TraceDir);
        Db = new StarlightDb(Paths.DatabaseFile);
        Registry = new ToolRegistry();
    }

    public string Root { get; }
    public StarlightPaths Paths { get; }
    public EventHub Events { get; }
    public KillSwitch KillSwitch { get; }
    public SettingsStore Settings { get; }
    public SessionTrace Trace { get; }
    public StarlightDb Db { get; }
    public ToolRegistry Registry { get; }

    public ToolDispatcher Dispatcher(IApprovalService? approvals = null) =>
        new(Registry, KillSwitch, approvals ?? new DenyAllApprovals(), Settings, Db, Trace, Events);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(Root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

internal sealed class FixedApprovals(ApprovalAnswer answer) : IApprovalService
{
    public int Asked { get; private set; }

    public Task<ApprovalAnswer> RequestAsync(ApprovalRequest request, CancellationToken ct)
    {
        Asked++;
        return Task.FromResult(answer);
    }
}
