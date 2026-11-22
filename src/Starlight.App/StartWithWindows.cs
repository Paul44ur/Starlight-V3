using Microsoft.Win32;

namespace Starlight.App;

/// <summary>The "start with Windows" setting, as a value in the user's Run key.</summary>
internal static class StartWithWindows
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Starlight";

    public static void Apply(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
        {
            key.SetValue(ValueName, $"\"{Environment.ProcessPath}\" --autostart");
        }
        else if (key.GetValue(ValueName) is not null)
        {
            key.DeleteValue(ValueName);
        }
    }
}
