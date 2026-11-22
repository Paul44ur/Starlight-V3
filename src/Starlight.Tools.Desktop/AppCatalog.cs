using Serilog;

namespace Starlight.Tools.Desktop;

/// <summary>Programs the user can start by name: Start menu shortcuts and Store apps. Built once, refreshed when asked.</summary>
public sealed class AppCatalog
{
    private List<AppEntry>? _apps;
    private DateTime _builtAt;
    private readonly Lock _gate = new();

    public IReadOnlyList<AppEntry> Apps
    {
        get
        {
            lock (_gate)
            {
                if (_apps is null || DateTime.UtcNow - _builtAt > TimeSpan.FromMinutes(10))
                {
                    _apps = Build();
                    _builtAt = DateTime.UtcNow;
                }
                return _apps;
            }
        }
    }

    private static List<AppEntry> Build()
    {
        var apps = new List<AppEntry>();
        foreach (var root in new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
        })
        {
            if (!Directory.Exists(root))
            {
                continue;
            }
            foreach (var lnk in Directory.EnumerateFiles(root, "*.lnk", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
            {
                var name = Path.GetFileNameWithoutExtension(lnk);
                if (name.Contains("uninstall", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                apps.Add(new AppEntry(name, lnk, AppKind.Shortcut));
            }
        }
        apps.AddRange(StoreApps());
        Log.Debug("App catalog: {Count} apps", apps.Count);
        return apps;
    }

    /// <summary>Everything in shell:AppsFolder (Store apps and desktop apps), through the Shell's COM object.</summary>
    private static List<AppEntry> StoreApps()
    {
        var list = new List<AppEntry>();
        try
        {
            var type = Type.GetTypeFromProgID("Shell.Application");
            if (type is null)
            {
                return list;
            }
            dynamic shell = Activator.CreateInstance(type)!;
            dynamic folder = shell.NameSpace("shell:::{4234d49b-0245-4df3-b780-3893943456e1}");
            foreach (dynamic item in folder.Items())
            {
                string name = item.Name;
                string id = item.Path;
                if (!string.IsNullOrWhiteSpace(name) && id.Contains('!', StringComparison.Ordinal))
                {
                    list.Add(new AppEntry(name, id, AppKind.StoreApp));
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not list Store apps");
        }
        return list;
    }
}
