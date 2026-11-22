using System.Security.Cryptography;
using System.Text.Json;

namespace Starlight.Tools.Web;

/// <summary>Cookies kept after a web login, per site, encrypted for the Windows user (DPAPI).</summary>
public sealed class SessionVault(string dir, Func<byte[], byte[]>? protect = null, Func<byte[], byte[]>? unprotect = null)
{
    private readonly Func<byte[], byte[]> _protect = protect ?? (b => ProtectedData.Protect(b, null, DataProtectionScope.CurrentUser));
    private readonly Func<byte[], byte[]> _unprotect = unprotect ?? (b => ProtectedData.Unprotect(b, null, DataProtectionScope.CurrentUser));

    /// <summary>The part of a host a login belongs to: "mail.example.com" and "www.example.com" share "example.com".</summary>
    public static string SiteOf(string host)
    {
        var labels = host.ToLowerInvariant().TrimEnd('.').Split('.');
        if (labels.Length <= 2)
        {
            return string.Join('.', labels);
        }
        // Country domains with a second level, such as co.uk or com.au.
        var take = labels[^2].Length <= 3 && labels[^1].Length == 2 ? 3 : 2;
        return string.Join('.', labels[^take..]);
    }

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
