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

    // Petre, on the update flow (#71): "after a portable swap to a new file/path, that value still
    // points at the OLD exe."
    //
    // The app is portable and updating it means running a DIFFERENT FILE -- a new exe downloaded
    // next to the old one, never a replacement of the running one. So the path stored here goes
    // stale the moment a newer version is started, and the machine keeps launching the old build
    // at every login while the user is looking at the new one, wondering why the update did not
    // take.
    //
    // Called on every start rather than only after an update, because there is no reliable moment
    // that means "I was just updated": the new exe is started by the old one, by Explorer, or by
    // the Run key itself, and only the first of those is knowable. Re-asserting unconditionally
    // needs no such signal.
    //
    // Only when autostart is already ON: this must never turn it on for someone who chose off.
    // The value name is fixed, so this overwrites rather than accumulating entries -- which also
    // means whichever version runs last owns startup, and that is the intended rule for a folder
    // holding several versions side by side. Latest one started wins.
    public static void ReassertIfEnabled()
    {
        if (IsEnabled) Enable();
    }

    public static void Disable() =>
        Registry.CurrentUser.CreateSubKey(RunKey).DeleteValue(Name, throwOnMissingValue: false);
}
