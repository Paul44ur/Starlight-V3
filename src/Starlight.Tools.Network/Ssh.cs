using System.Text.Json;
using Renci.SshNet;
using Renci.SshNet.Common;
using Starlight.Security;
using Starlight.Settings;

namespace Starlight.Tools.Network;

public sealed record SshOutput(int ExitCode, string Output);

public interface ISshRunner
{
    Task<SshOutput> RunAsync(ServerEntry server, string command, CancellationToken ct);
}

/// <summary>
/// SSH through SSH.NET. Signs in with the server's key file, else the password stored in Credential Manager.
/// The server's host key is trusted on first use and must match after that.
/// </summary>
public sealed class SshRunner(ISecretStore secrets, string knownHostsFile) : ISshRunner
{
    private readonly Lock _lock = new();

    public static string SecretKey(ServerEntry s) => "ssh/" + s.Host.ToLowerInvariant();

    public async Task<SshOutput> RunAsync(ServerEntry server, string command, CancellationToken ct)
    {
        var auth = new List<AuthenticationMethod>();
        if (!string.IsNullOrWhiteSpace(server.SshKeyFile))
        {
            if (!File.Exists(server.SshKeyFile))
            {
                throw new ToolException($"The SSH key file for {server.Display} is missing: {server.SshKeyFile}");
            }
            auth.Add(new PrivateKeyAuthenticationMethod(server.SshUser, new PrivateKeyFile(server.SshKeyFile)));
        }
        if (secrets.Get(SecretKey(server)) is { } pw)
        {
            auth.Add(new PasswordAuthenticationMethod(server.SshUser, pw.Secret));
        }
        if (auth.Count == 0)
        {
            throw new ToolException($"No SSH key or password is set for {server.Display}. The user adds one in Settings > Servers.");
        }
        var info = new ConnectionInfo(server.Host, server.SshPort, server.SshUser, [.. auth]) { Timeout = TimeSpan.FromSeconds(10) };
        using var client = new SshClient(info);
        string? mismatch = null;
        var known = Known(); // read before connecting, so a damaged file stops here with its message
        client.HostKeyReceived += (_, e) =>
        {
            var seen = e.FingerPrintSHA256;
            var key = $"{server.Host}:{server.SshPort}";
            if (known.TryGetValue(key, out var stored))
            {
                e.CanTrust = stored == seen;
                if (!e.CanTrust)
                {
                    mismatch = seen;
                }
            }
            else
            {
                known[key] = seen;
                Save(known);
                e.CanTrust = true;
            }
        };
        try
        {
            await client.ConnectAsync(ct);
        }
        catch (SshConnectionException) when (mismatch is not null)
        {
            throw new ToolException($"{server.Display} sent a different host key than before. That can mean the server was reinstalled, or someone is in between. Starlight did not connect.");
        }
        catch (SshAuthenticationException)
        {
            throw new ToolException($"{server.Display} refused the sign-in for {server.SshUser}.");
        }
        catch (Exception ex) when (ex is SshException or System.Net.Sockets.SocketException or TimeoutException)
        {
            throw new ToolException($"Could not connect to {server.Display}: {ex.Message}");
        }
        using var cmd = client.CreateCommand(command);
        cmd.CommandTimeout = TimeSpan.FromSeconds(60);
        await cmd.ExecuteAsync(ct);
        var output = (cmd.Result ?? "") + (string.IsNullOrEmpty(cmd.Error) ? "" : "\n[stderr]\n" + cmd.Error);
        client.Disconnect();
        return new SshOutput(cmd.ExitStatus ?? -1, output.TrimEnd());
    }

    internal Dictionary<string, string> Known()
    {
        lock (_lock)
        {
            try
            {
                return File.Exists(knownHostsFile)
                    ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(knownHostsFile)) ?? []
                    : [];
            }
            catch (JsonException ex)
            {
                // Starting over would trust any key again: a damaged file stops SSH until it is looked at.
                var aside = knownHostsFile + ".broken-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
                try
                {
                    File.Copy(knownHostsFile, aside, overwrite: true);
                }
                catch (IOException)
                {
                }
                Serilog.Log.Error(ex, "The saved server keys are damaged; kept a copy as {Aside}", aside);
                throw new ToolException($"The file with the saved server keys is damaged, so SSH is stopped to stay safe. A copy is in {aside}. Export diagnostics from the tray and send them.");
            }
        }
    }

    private void Save(Dictionary<string, string> known)
    {
        lock (_lock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(knownHostsFile)!);
            File.WriteAllText(knownHostsFile, JsonSerializer.Serialize(known));
        }
    }
}
