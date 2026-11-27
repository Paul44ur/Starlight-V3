using System.Security.Cryptography;
using System.Text.Json;

namespace Starlight.Tools.Web;

/// <summary>Cookies kept after a web login, per site, encrypted for the Windows user (DPAPI).</summary>
public sealed class SessionVault(string dir, Func<byte[], byte[]>? protect = null, Func<byte[], byte[]>? unprotect = null)
{
    private readonly Func<byte[], byte[]> _protect = protect ?? (b => OperatingSystem.IsWindows() ? ProtectedData.Protect(b, null, DataProtectionScope.CurrentUser) : throw new PlatformNotSupportedException("Saved sign-ins need Windows."));
    private readonly Func<byte[], byte[]> _unprotect = unprotect ?? (b => OperatingSystem.IsWindows() ? ProtectedData.Unprotect(b, null, DataProtectionScope.CurrentUser) : throw new PlatformNotSupportedException("Saved sign-ins need Windows."));

    /// <summary>The part of a host a login belongs to: "mail.example.com" and "www.example.com" share "example.com".</summary>
    public static string SiteOf(string host)
    {
        var labels = host.ToLowerInvariant().TrimEnd('.').Split('.');
        if (labels.Length <= 2)
        {
            return string.Join('.', labels);
        }
        // Country domains with a known second level, such as co.uk or com.au (but not bbc.de or gmx.at).
        var take = labels[^1].Length == 2 && SecondLevels.Contains(labels[^2]) ? 3 : 2;
        return string.Join('.', labels[^take..]);
    }

    private static readonly HashSet<string> SecondLevels = ["co", "com", "net", "org", "gov", "edu", "ac", "ne", "or", "go", "gob", "gv", "mil", "nic", "ltd", "plc", "sch", "nhs", "lg", "ad", "ed", "gr", "info", "biz", "nom", "web"];

    public void Save(string host, IEnumerable<BrowserCookie> cookies)
    {
        var site = SiteOf(host);
        var mine = cookies.Where(c => SiteOf(c.Domain.TrimStart('.')) == site).ToList();
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(FileFor(site), _protect(JsonSerializer.SerializeToUtf8Bytes(mine)));
    }

    public IReadOnlyList<BrowserCookie> Load(string host)
    {
        var file = FileFor(SiteOf(host));
        if (!File.Exists(file))
        {
            return [];
        }
        try
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            return (JsonSerializer.Deserialize<List<BrowserCookie>>(_unprotect(File.ReadAllBytes(file))) ?? [])
                .Where(c => c.Expires <= 0 || c.Expires > now).ToList();
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or IOException)
        {
            return [];
        }
    }

    public bool Has(string host) => File.Exists(FileFor(SiteOf(host)));

    public void Delete(string host) => File.Delete(FileFor(SiteOf(host)));

    private string FileFor(string site) => Path.Combine(dir, string.Concat(site.Select(ch => char.IsLetterOrDigit(ch) || ch is '.' or '-' ? ch : '_')) + ".session");
}
