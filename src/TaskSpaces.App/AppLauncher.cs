using System.Diagnostics;
using CSharpFunctionalExtensions;
using TaskSpaces.Core.Abstractions;
using TaskSpaces.Core.Persistence;
using TaskSpaces.Core.Rehydration;

namespace TaskSpaces.App;

// The one Process.Start in the app (Core stays pure behind IAppLauncher). Launching a
// remembered app is best-effort: a moved/uninstalled/permission-denied exe returns
// None — never an exception — so one bad roster entry can't abort a Start batch or
// crash the UI (the hard-won lesson from v1's Rehydrator, whose logic this inherits).
public sealed class AppLauncher : IAppLauncher
{
    public Maybe<int> Launch(InventoryEntry entry)
    {
        try
        {
            var process = Process.Start(new ProcessStartInfo(entry.ProcessPath)
            {
                Arguments = CommandLines.ArgumentsOf(entry.CommandLine, entry.ProcessPath),
                UseShellExecute = true,
            });
            return process is null ? Maybe<int>.None : process.Id;
        }
        catch (Exception) { return Maybe<int>.None; }
    }
}
