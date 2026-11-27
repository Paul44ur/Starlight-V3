using System.Net;
using Starlight.Events;
using Starlight.Settings;
using Starlight.Tools;
using Starlight.Tools.Network;

namespace Starlight.Tests;

internal sealed class FakeProbe : INetProbe
{
    public HashSet<int> OpenPorts { get; } = [22, 443];
    public bool PingOk { get; set; } = true;
    public bool HttpOk { get; set; } = true;
    public int? TlsDays { get; set; } = 60;
    public bool Resolves { get; set; } = true;
    public List<string> Calls { get; } = [];

    public Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct) =>
        Resolves ? Task.FromResult(new[] { IPAddress.Parse("192.168.1.20") }) : throw new System.Net.Sockets.SocketException(11001);

    public Task<ProbeResult> PingAsync(string host, CancellationToken ct)
    {
        Calls.Add("ping " + host);
        return Task.FromResult(new ProbeResult(PingOk, PingOk ? "ping 3 ms" : "ping: TimedOut"));
    }

    public Task<ProbeResult> PortAsync(string host, int port, CancellationToken ct)
    {
        Calls.Add($"port {port}");
        return Task.FromResult(new ProbeResult(OpenPorts.Contains(port), OpenPorts.Contains(port) ? $"port {port} open" : $"port {port} closed"));
    }

    public Task<ProbeResult> HttpAsync(Uri url, CancellationToken ct)
    {
        Calls.Add("http " + url);
        return Task.FromResult(new ProbeResult(HttpOk, HttpOk ? "HTTP 200 OK" : "HTTP 502 Bad Gateway"));
    }

    public Task<(int? DaysLeft, string Text)> TlsAsync(string host, int port, CancellationToken ct) =>
        Task.FromResult((TlsDays, TlsDays is null ? "no TLS" : $"certificate ends in {TlsDays} days"));
}

internal sealed class FakeSsh : ISshRunner
{
    public List<string> Ran { get; } = [];

    public Task<SshOutput> RunAsync(ServerEntry server, string command, CancellationToken ct)
    {
        Ran.Add(command);
        return Task.FromResult(new SshOutput(0, "Filesystem  Size  Used\n/dev/sda1  100G  40G"));
    }
}

public sealed class NetworkTests
{
    [Theory]
    [InlineData("df -h", true)]
    [InlineData("df   -h", true)]
    [InlineData("systemctl status nginx", true)]
    [InlineData("journalctl -n 50 -u nginx", true)]
    [InlineData("systemctl restart nginx", false)]
    [InlineData("df -h; rm -rf /", false)]
    [InlineData("uptime && reboot", false)]
    [InlineData("systemctl status $(reboot)", false)]
    [InlineData("docker ps | sh", false)]
    [InlineData("docker ps -a", false)]
    [InlineData("", false)]
    [InlineData("systemctl status nginx --kill-whom=all", false)]
    [InlineData("journalctl -n 50 -u nginx --vacuum-time=1s", false)]
    [InlineData("systemctl status ../../x", false)]
    [InlineData("systemctl status /etc/*", false)]
    public void Only_allow_listed_commands_without_shell_tricks_run_freely(string command, bool allowed) =>
        Assert.Equal(allowed, CommandPolicy.IsAllowed(command, new ServerEntry().AllowedCommands));

    private sealed class Rig : IDisposable
    {
        public Rig()
        {
            Env = new TestEnv();
            Env.Settings.Update(s => s.Servers.Add(new ServerEntry { Name = "nas", Host = "192.168.1.20", SshUser = "admin", WatchPorts = [22, 443], HealthUrl = "https://nas.home/health" }));
            Tools = new NetworkTools(Env.Settings, Probe, Ssh);
            Env.Registry.Add(Tools);
        }

        public TestEnv Env { get; }
        public FakeProbe Probe { get; } = new();
        public FakeSsh Ssh { get; } = new();
        public NetworkTools Tools { get; }

        public Task<ToolResult> Call(IApprovalService approvals, string tool, params (string, object?)[] args) =>
            Env.Dispatcher(approvals).InvokeAsync(tool, args.ToDictionary(a => a.Item1, a => a.Item2), "t1", "chat", default);

        public void Dispose() => Env.Dispose();
    }

    [Fact]
    public async Task Check_by_name_tests_the_servers_ports_health_and_certificate()
    {
        using var rig = new Rig();
        rig.Probe.TlsDays = 5;
        var r = await rig.Call(new DenyAllApprovals(), "server_check", ("target", "nas"));

        Assert.True(r.Ok, r.Text);
        Assert.StartsWith("nas (192.168.1.20): up", r.Text);
        Assert.Contains("port 22 open", r.Text);
        Assert.Contains("HTTP 200 OK", r.Text);
        Assert.Contains("WARNING: certificate ends in 5 days", r.Text);
    }

    [Fact]
    public async Task Check_reports_a_name_that_does_not_resolve_and_a_host_that_does_not_answer()
    {
        using var rig = new Rig();
        rig.Probe.Resolves = false;
        Assert.Contains("does not resolve", (await rig.Call(new DenyAllApprovals(), "server_check", ("target", "nothere.example"))).Text);

        rig.Probe.Resolves = true;
        rig.Probe.PingOk = false;
        rig.Probe.OpenPorts.Clear();
        var r = await rig.Call(new DenyAllApprovals(), "server_check", ("target", "10.0.0.9"), ("port", 8080));
        Assert.StartsWith("10.0.0.9: not answering", r.Text);
        Assert.Contains("port 8080 closed", r.Text);
    }

    [Fact]
    public async Task Allowed_ssh_commands_run_without_asking_and_others_ask()
    {
        using var rig = new Rig();
        var approvals = new FixedApprovals(ApprovalAnswer.No);

        var ok = await rig.Call(approvals, "server_ssh", ("server", "nas"), ("command", "df -h"));
        Assert.True(ok.Ok, ok.Text);
        Assert.Contains("/dev/sda1", ok.Text);
        Assert.Equal(0, approvals.Asked);

        var no = await rig.Call(approvals, "server_ssh", ("server", "nas"), ("command", "sudo reboot"));
        Assert.False(no.Ok);
        Assert.Equal(1, approvals.Asked);
        Assert.Equal(["df -h"], rig.Ssh.Ran);
    }

    [Fact]
    public async Task Always_for_one_ssh_command_does_not_free_all_commands()
    {
        using var rig = new Rig();
        var approvals = new FixedApprovals(ApprovalAnswer.Always);
        await rig.Call(approvals, "server_ssh", ("server", "nas"), ("command", "systemctl restart nginx"));
        await rig.Call(approvals, "server_ssh", ("server", "nas"), ("command", "rm -rf /tmp/x"));

        Assert.Equal(2, approvals.Asked);
        Assert.False(rig.Env.Settings.Current.PermissionOverrides.ContainsKey("server_ssh"));
    }

    [Fact]
    public async Task Ssh_output_is_outside_content()
    {
        using var rig = new Rig();
        var r = await rig.Call(new DenyAllApprovals(), "server_ssh", ("server", "nas"), ("command", "uptime"));
        Assert.StartsWith(ToolDispatcher.UntrustedMark, r.Text);
    }

    [Fact]
    public async Task Ssh_to_an_unknown_server_is_refused()
    {
        using var rig = new Rig();
        var r = await rig.Call(new FixedApprovals(ApprovalAnswer.Yes), "server_ssh", ("server", "evil.example.com"), ("command", "uptime"));
        Assert.False(r.Ok);
        Assert.Empty(rig.Ssh.Ran);
    }

    [Fact]
    public void List_names_the_servers_and_what_runs_freely()
    {
        using var rig = new Rig();
        var text = rig.Tools.List();
        Assert.Contains("nas (192.168.1.20), SSH as admin", text);
        Assert.Contains("df -h", text);
    }

    [Fact]
    public async Task Watcher_says_when_a_server_goes_down_and_comes_back()
    {
        using var rig = new Rig();
        rig.Env.Settings.Update(s => s.Servers[0].WatchMinutes = 5);
        var said = new List<string>();
        using var sub = rig.Env.Events.Subscribe<Notice>(n => said.Add(n.Text));
        var watcher = new ServerWatcher(rig.Env.Settings, rig.Probe, rig.Env.Events);
        var t0 = new DateTimeOffset(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);

        Assert.Empty(await watcher.TickAsync(t0, default));
        rig.Probe.HttpOk = false;
        Assert.Empty(await watcher.TickAsync(t0.AddMinutes(2), default)); // not due yet
        var down = await watcher.TickAsync(t0.AddMinutes(5), default);
        Assert.Contains("nas (192.168.1.20) is down: HTTP 502", Assert.Single(down));
        Assert.Empty(await watcher.TickAsync(t0.AddMinutes(10), default)); // still down: said once
        rig.Probe.HttpOk = true;
        Assert.Contains("is back up", Assert.Single(await watcher.TickAsync(t0.AddMinutes(15), default)));
        Assert.Equal(2, said.Count);
    }

    [Fact]
    public void Targets_can_be_urls_hosts_or_addresses()
    {
        Assert.Equal(("example.com", new Uri("https://example.com/x")), NetworkTools.Target("https://example.com/x", null));
        Assert.Equal(("10.0.0.1", (Uri?)null), NetworkTools.Target("10.0.0.1", null));
        Assert.Throws<ToolException>(() => NetworkTools.Target("not a host", null));
    }
}

public sealed class NetworkFixTests
{
    [Fact]
    public void A_loose_server_name_counts_only_when_one_server_fits()
    {
        using var env = new TestEnv();
        env.Settings.Update(s =>
        {
            s.Servers.Add(new ServerEntry { Name = "nas", Host = "10.0.0.1" });
            s.Servers.Add(new ServerEntry { Name = "nas2", Host = "10.0.0.2" });
        });
        var tools = new NetworkTools(env.Settings, new FakeProbe(), new FakeSsh());

        Assert.Equal("10.0.0.2", tools.Find("nas2")!.Host);
        Assert.Null(tools.Find("the nas2 box")); // "nas" and "nas2" both fit
        Assert.Equal("10.0.0.1", tools.Find("NAS")!.Host);
    }

    [Fact]
    public void Damaged_saved_server_keys_stop_ssh_instead_of_trusting_again()
    {
        using var env = new TestEnv();
        var file = Path.Combine(env.Root, "known.json");
        File.WriteAllText(file, "{ broken");
        var ssh = new SshRunner(new Starlight.Security.MemorySecretStore(), file);

        var ex = Assert.Throws<ToolException>(() => ssh.Known());
        Assert.Contains("damaged", ex.Message);
    }
}
