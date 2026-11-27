using System.ComponentModel;
using System.Text;
using Starlight.Settings;

namespace Starlight.Tools.Network;

/// <summary>The three server tools (plan 5.9).</summary>
public sealed class NetworkTools(SettingsStore settings, INetProbe probe, ISshRunner ssh) : IToolProvider, IPermissionHint
{
    [Tool("server_check", TimeoutSeconds = 40)]
    [Description("Checks whether a server or website is up: DNS, ping, a port, HTTP and when its TLS certificate ends. Works with the user's servers by name or with any host or address.")]
    public async Task<string> Check(
        [Description("A server name from server_list, a host name, an IP address or a URL")] string target,
        [Description("A port to test, for example 22 or 443. Optional.")] int? port = null,
        CancellationToken ct = default)
    {
        var server = Find(target);
        var (host, url) = Target(target, server);
        var lines = new List<string>();
        System.Net.IPAddress[] addresses;
        try
        {
            addresses = await probe.ResolveAsync(host, ct);
            lines.Add("DNS: " + string.Join(", ", addresses.Take(3).Select(a => a.ToString())));
        }
        catch (System.Net.Sockets.SocketException)
        {
            return $"{host}: the name does not resolve (DNS). It is down or the name is wrong.";
        }
        var ping = await probe.PingAsync(host, ct);
        lines.Add(ping.Text);
        var ports = new List<int>();
        if (port is { } p)
        {
            ports.Add(p);
        }
        else if (server is not null)
        {
            ports.AddRange(server.WatchPorts);
            if (ports.Count == 0 && !string.IsNullOrWhiteSpace(server.SshUser))
            {
                ports.Add(server.SshPort);
            }
        }
        else if (url is not null)
        {
            ports.Add(url.Port);
        }
        var anyUp = ping.Ok;
        foreach (var x in ports.Distinct())
        {
            var r = await probe.PortAsync(host, x, ct);
            anyUp |= r.Ok;
            lines.Add(r.Text);
        }
        if (url is not null)
        {
            var r = await probe.HttpAsync(url, ct);
            anyUp |= r.Ok;
            lines.Add(r.Text);
        }
        var tlsPort = url?.Scheme == "https" ? url.Port : ports.Contains(443) ? 443 : (int?)null;
        if (tlsPort is { } tp)
        {
            var (days, text) = await probe.TlsAsync(host, tp, ct);
            lines.Add(days is < 14 ? "WARNING: " + text : text);
        }
        var name = server?.Display ?? host;
        return $"{name}: {(anyUp ? "up" : "not answering")}\n" + string.Join('\n', lines.Select(l => "- " + l));
    }

    [Tool("server_ssh", TimeoutSeconds = 90, UntrustedOutput = true)]
    [Description("Runs a command on one of the user's servers over SSH and returns its output. Read-only commands from the server's allow-list run at once; anything else asks the user first.")]
    public async Task<string> Ssh(
        [Description("The server's name from server_list")] string server,
        [Description("The shell command, for example \"df -h\" or \"systemctl status nginx\"")] string command,
        CancellationToken ct = default)
    {
        var s = Find(server) ?? throw new ToolException($"\"{server}\" is not one of the user's servers. Use server_list.");
        if (string.IsNullOrWhiteSpace(s.SshUser))
        {
            throw new ToolException($"SSH is not set up for {s.Display}. The user adds a user name in Settings > Servers.");
        }
        var result = await ssh.RunAsync(s, command.Trim(), ct);
        var output = result.Output.Length == 0 ? "(no output)" : result.Output;
        return $"{s.Display} $ {command.Trim()}\nexit {result.ExitCode}\n{output}";
    }

    [Tool("server_list")]
    [Description("Lists the servers the user added in Settings, with what can be done on each.")]
    public string List()
    {
        var servers = settings.Current.Servers;
        if (servers.Count == 0)
        {
            return "No servers are set up. The user adds them in Settings > Servers.";
        }
        var sb = new StringBuilder();
        foreach (var s in servers)
        {
            sb.Append("- ").Append(s.Display);
            if (!string.IsNullOrWhiteSpace(s.SshUser))
            {
                sb.Append(System.Globalization.CultureInfo.InvariantCulture, $", SSH as {s.SshUser} on port {s.SshPort}; allowed without asking: {string.Join(", ", s.AllowedCommands)}");
            }
            if (s.WatchMinutes > 0)
            {
                sb.Append(System.Globalization.CultureInfo.InvariantCulture, $", watched every {s.WatchMinutes} min");
            }
            sb.Append('\n');
        }
        return sb.ToString().TrimEnd();
    }

    public Permission? PermissionFor(string tool, IDictionary<string, object?> arguments)
    {
        if (tool != "server_ssh")
        {
            return null;
        }
        var command = ToolDispatcher.ArgumentText((arguments.TryGetValue("command", out var c) ? c : null));
        var server = Find(ToolDispatcher.ArgumentText((arguments.TryGetValue("server", out var sv) ? sv : null)));
        return server is not null && CommandPolicy.IsAllowed(command, server.AllowedCommands) ? null : Permission.Ask;
    }

    internal ServerEntry? Find(string text)
    {
        var t = text.Trim();
        var servers = settings.Current.Servers;
        return servers.FirstOrDefault(s => s.Name.Equals(t, StringComparison.OrdinalIgnoreCase))
            ?? servers.FirstOrDefault(s => s.Host.Equals(t, StringComparison.OrdinalIgnoreCase))
            ?? OnlyOne(servers.Where(s => s.Name.Length > 0 && t.Contains(s.Name, StringComparison.OrdinalIgnoreCase)).ToList());
    }

    /// <summary>A loose match counts only if it is the only one: "nas2" must not pick "nas".</summary>
    private static ServerEntry? OnlyOne(List<ServerEntry> matches) => matches.Count == 1 ? matches[0] : null;

    internal static (string Host, Uri? Url) Target(string target, ServerEntry? server)
    {
        if (server is not null)
        {
            return (server.Host, Uri.TryCreate(server.HealthUrl, UriKind.Absolute, out var h) ? h : null);
        }
        var t = target.Trim();
        if (Uri.TryCreate(t, UriKind.Absolute, out var u) && u.Scheme is "http" or "https")
        {
            return (u.Host, u);
        }
        var host = t.Split('/')[0];
        if (host.Length == 0 || host.Any(char.IsWhiteSpace))
        {
            throw new ToolException($"\"{target}\" is not a host name, address or URL.");
        }
        return (host, null);
    }
}
