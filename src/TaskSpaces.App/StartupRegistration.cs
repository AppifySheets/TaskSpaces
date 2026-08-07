using Microsoft.Win32;

namespace TaskSpaces.App;

// "Start with Windows" via HKCU Run -- per-user, no admin, trivially reversible.
public static class StartupRegistration
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string Name = "TaskSpaces";

    public static bool IsEnabled =>
        Registry.CurrentUser.OpenSubKey(RunKey)?.GetValue(Name) is not null;

    public static void Enable() =>
        Registry.CurrentUser.CreateSubKey(RunKey).SetValue(Name, $"\"{Environment.ProcessPath}\"");

    public static void Disable() =>
        Registry.CurrentUser.CreateSubKey(RunKey).DeleteValue(Name, throwOnMissingValue: false);
}
